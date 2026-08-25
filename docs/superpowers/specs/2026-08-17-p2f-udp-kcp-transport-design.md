# P2-F UDP/KCP Transport Design

> 状态：2026-08-17 由帅老大逐节批准；本文件是 P2-F 的实现约束，不代表代码已经完成或验收通过。
>
> 实施计划：[P2-F UDP/KCP Transport Implementation Plan](../plans/2026-08-17-p2f-udp-kcp-transport-plan.md)

## 1. 结论

P2-F 将 Route C 的默认实验传输从 TCP 改为 UDP/KCP，同时保留 TCP 作为回退通道和 A/B 对照。上层继续只收发既有的 8 字节帧输入；`FrameInputLedger`、Canonical Frame 映射、确定性世界、预测、回滚和表现层不得知道 KCP、Conv、UDP Endpoint 或重连令牌。

本阶段新增的是传输抽象、独立网络线程、幂等会话协议、1v1 KCP 中继、有界局内续连、两层故障实验和 live Socket 证据。P2-F 不引入 Protobuf，不进行世界状态同步，也不宣称账号鉴权、加密、NAT 穿透、DDoS 防护或任意规模房间服务。

阶段顺序调整为：

1. P2-F：UDP/KCP 传输与证据闭环；
2. P2-G：轻量 ECS；
3. P2-H：作品集与证据收口。

## 2. 已验证事实与来源边界

### 2.1 当前 Route C

当前 Unity `NetworkClient` 使用 `TcpClient`/`NetworkStream`，服务器使用 `TcpListener`，双方均启用 `NoDelay`。P2-E 的 recovered-loss/HOL 模型是 TCP/应用语义实验，不是 UDP 丢包，也不是 KCP。

### 2.2 街篮2参考边界

街篮2只作为以下思路的参考：

- 传输抽象与 TCP/KCP 切换；
- KCP 由调用方主动 Update；
- 快速模式的配置思路。

街篮2参考源码调用值明确记录为 `NoDelay(1, 1, 2, 1)`。Route C 不复制其 `KcpClient`、`SocketClient` 或关联生产依赖，不把该调用值写成 Route C 已验证参数，也不把 10ms 写成街篮2的精确有效值。

### 2.3 独立 KCP 核心

采用 [MirrorNetworking/kcp2k](https://github.com/MirrorNetworking/kcp2k) 的纯 KCP 核心，固定到提交：

`66efda6686f649838d42f078fbaabf56ac449de4`

只引入：

- `AckItem.cs`
- `Kcp.cs`
- `Pool.cs`
- `Segment.cs`
- `Utils.cs`

不引入 kcp2k 的 high-level client/server、Socket、会话、线程或 Mirror 集成。仓库必须保留原始 MIT `LICENSE`、来源、固定提交、文件清单、校验值和 Route C 补丁说明。

### 2.4 低于 10ms 的事实纠正

该固定提交的 `Kcp.SetNoDelay` 与 `Kcp.SetInterval` 会把低于 10ms 的 interval 夹到 10ms。若不处理，`1/5/10/20ms` 实验会实际变成 `10/10/10/20ms`，证据无效。

因此 Route C 对 vendored `Kcp.cs` 使用一个最小、可审计补丁：仅把两个 interval 下限从 `10` 改为 `1`，不修改 KCP 算法、重传逻辑、窗口逻辑或业务层。`PATCHES.md` 必须记录原始提交、精确差异、原因和校验方式。诊断必须同时记录请求值、核心实际值、真实调用间隔和线程唤醒误差，防止再次出现“配置值不等于生效值”的误报。

## 3. 总体架构

```text
Unity 主线程
  -> NetworkClient 门面
     -> IFrameTransportClient
        -> KcpUdpClientTransport（默认实验通道）
        -> TcpClientTransport（回退/A-B 对照）
  -> ConcurrentQueue<8-byte local input>

KCP 客户端网络线程（唯一所有者）
  -> UDP Socket
  -> Client handshake/reconnect state
  -> KcpSession
  -> ConcurrentQueue<decoded remote input / control event>

服务器主程序
  -> --transport kcp：KcpUdpRelayServer
       -> 一个 UDP Socket
       -> 一个网络线程
       -> Player0 KcpServerSession
       -> Player1 KcpServerSession
  -> --transport tcp：保留 P2-E TcpRelayServer
```

统一抽象只承诺“启动、提交本地 8 字节输入、读取远端输入、读取状态、请求停止”。它不假装 TCP 和 KCP 具有相同的故障语义，也不把 KCP 控制信息暴露给 GameController。

## 4. 客户端线程所有权

每个 KCP 客户端由一个独立网络工作线程单线程拥有：

- UDP Receive；
- 外层协议校验；
- KCP `Input/Update/Output/Receive`；
- handshake、heartbeat、timeout、reconnect、resume 状态；
- KCP 实例和 UDP Socket 的创建与销毁。

Unity 主线程只能：

- 向线程安全队列提交 8 字节输入；
- 读取已经解码的 `NetworkPacketArrival`；
- 读取不可变状态快照；
- 提交 `ResumeReadiness`；
- 请求停止。

首次 Start 到达前逻辑帧引擎保持停止。Start 之后发生短暂断线或进入 Resuming 时，客户端仍保持“网络会话模式”：继续把本地输入记录为 Ledger Actual、对缺失远端输入使用既有预测并生成快照，绝不能退回本地双人输入分支。若断线持续到历史或快照越界，恢复协议将拒绝本局。只有 Terminated 才停止该网络会话。

主线程不得直接访问 KCP 实例或 UDP Socket。网络线程使用 `Stopwatch` 单调时钟，通过 `Socket.Select` 等待 UDP 可读或最近 KCP deadline；新出站输入最迟在当前 interval 对应的下一轮被取走，不使用依赖 Unity FPS 的 `Update`，也不使用 1ms 无条件忙轮询。

客户端单轮顺序固定为：

1. 获取待发送消息；
2. 接收并校验 UDP 数据报；
3. 执行 KCP `Input`；
4. 对到期 KCP 执行 `Update`；
5. 循环执行 `PeekSize/Receive`；
6. 发布不可变状态和诊断快照。

停止由主线程设置停止请求；worker 在不超过当前 interval 加固定关闭余量的有限时间内退出，并由 worker 自己释放 KCP 和 Socket。

## 5. 服务器所有权与公平性

当前 1v1 比赛采用一个 UDP Socket、一个网络线程。该线程串行拥有两名玩家各自独立的 `KcpServerSession`。每个 Session 独立保存：

- 稳定玩家索引；
- `SessionID`；
- `Generation`；
- `Conv`；
- 远端 Endpoint；
- KCP 状态与下一次 Update deadline；
- 会话状态、最后有效流量时间和续连尝试；
- 该玩家的有界 8 字节 Actual 历史。

服务器从玩家 A 的 KCP Session 解出完整 8 字节输入后，校验并记录业务输入，再调用玩家 B 的 KCP Session 发送同一 8 字节业务值。禁止直接转发 A 的 KCP 数据报。

同一时刻到期的 Session 按稳定玩家索引 `0 -> 1` 处理。每轮使用以下初始硬预算：

- 最多接收 64 个 UDP 数据报；
- 每个 Session 最多解出 64 个 KCP 消息；
- live worker 单轮最多连续工作约 2ms，随后重新检查另一个 Session 和 Socket。

预算是防饥饿边界，不是性能结论；命中次数必须进入诊断。未来多比赛按 MatchID 分片到多个网络 Worker，不进入 P2-F。

## 6. UDP 外层信封

固定头长度为 28 字节：

| 偏移 | 长度 | 字段 | 编码 |
|---:|---:|---|---|
| 0 | 4 | Magic | ASCII `RCF2` (`52 43 46 32`) |
| 4 | 1 | Version | `1` |
| 5 | 1 | MessageType | 枚举值 |
| 6 | 2 | PayloadLength | 网络字节序，无符号 |
| 8 | 16 | SessionID | 128 位原始值；首次 Hello 全零 |
| 24 | 4 | Generation | 网络字节序；首次 Hello 为 0 |
| 28 | N | Payload | 必须与 PayloadLength 完全一致 |

UDP 数据报上限为 1200 字节，KCP MTU 设置为 `1200 - 28 = 1172`。`KcpData` 的 Payload 是完整 KCP 数据报，外层不重复携带 Conv。KCP 头至少 24 字节；其前 4 字节 Conv 按 kcp2k 的小端格式解析并与 Session 预期值比对。

以下情况必须在进入 KCP 前显式丢弃并分别计数：

- Magic 或 Version 错误；
- 未知 MessageType；
- PayloadLength 不一致或超过上限；
- Session 不存在；
- Generation 过期；
- Endpoint 错误；
- KCP Payload 过短；
- KCP 头 Conv 错误。

## 7. 消息和负载

外层消息类型：

- `Hello`
- `Welcome`
- `Ready`
- `Start`
- `KcpData`
- `Heartbeat`
- `Disconnect`
- `ResumeProbe`
- `ResumeState`
- `ResumeAccepted`
- `ResumeComplete`
- `ResumeRejected`

控制负载采用固定二进制布局：

| 消息 | Payload |
|---|---|
| Initial Hello | `Kind(1)=0 + ClientNonce(16)` |
| Reconnect Hello | `Kind(1)=1 + NewClientNonce(16) + OldReconnectToken(32)`；头携带旧 SessionID/Generation |
| Welcome | `EchoNonce(16) + PlayerIndex(1) + Conv(4) + NewReconnectToken(32) + HeartbeatMs(4) + TimeoutMs(4) + ResumeRequired(1)` |
| Ready | `LastContiguousRemoteFrameID(4) + EarliestRecoverableCanonicalFrame(4) + LatestLocalFrameID(4)` |
| Start | `CanonicalStartFrame(4)`，P2-F 固定为 0 |
| Heartbeat | 空 Payload |
| Disconnect | `Reason(2)` |
| ResumeProbe | `ResumeAttemptID(16)` |
| ResumeState | `ResumeAttemptID(16) + 三个 Ready 边界字段(12)` |
| ResumeAccepted | `ResumeAttemptID(16) + 三组闭区间 From/Through(24)` |
| ResumeComplete | `ResumeAttemptID(16) + UploadedLocalThrough(4) + ReceivedRemoteThrough(4)` |
| ResumeRejected | `ResumeAttemptID(16) + Reason(2)` |

控制负载中的多字节字段使用网络字节序。空区间统一编码为 `From > Through`。KCP 内业务消息继续使用现有 8 字节小端布局：`raw:uint32 + localFrameID:int32`。

## 8. 首次握手与幂等

1. 客户端生成 128 位密码学安全 `ClientNonce`，发送 Initial Hello。
2. 服务器以 `Endpoint + ClientNonce` 作为首次握手幂等键。
3. 首次有效处理分配 128 位 SessionID、玩家索引、Generation=1、无冲突非零 Conv 和 256 位 ReconnectToken。
4. Welcome 必须回显 ClientNonce。
5. 相同 Endpoint+Nonce 的重试只重发同一 Welcome，不再创建 Session。
6. 两个 Session 都 Ready 后，服务器向双方幂等重发同一 Start；双方只在收到匹配 Session/Generation 的 Start 后共同从 Canonical Frame 0 起跑。

控制消息使用状态幂等和 250ms 初始重发周期，不把 UDP 当成可靠通道。Heartbeat 只在业务静默满 1000ms 时发送；任何有效业务或控制数据都刷新活跃时间。连续 3000ms 没有有效流量时判定断线事实，Session 继续保留到最后有效流量后的 5000ms 重连宽限截止点。`Disconnect` 仅是提示，不是断线事实。

## 9. 受控重连

重连幂等键是：

`SessionID + 旧 ReconnectToken + 新 ClientNonce`

首次有效重连必须在服务器 worker 内原子执行：

1. Generation 递增；
2. 生成新 Conv；
3. 生成新 ReconnectToken；
4. 替换 Endpoint；
5. 建立新的 KCP 状态；
6. 立即使旧 Generation、旧 Conv、旧 Token 和旧 Endpoint 失效。

相同新 Nonce 的重试只重发同一 Welcome，不能再次切换代次。Nonce、Token、SessionID 和 Conv 均使用密码学安全随机源；Conv 必须非零并检查活动 Session 冲突。Token 不写普通日志，诊断最多记录不可逆短指纹。

这只是最小会话持有证明，不是账号鉴权、内容加密或抗链路攻击方案。

## 10. 有界局内续连

### 10.1 唯一事实源

`FrameInputLedger` 仍是客户端本地输入唯一事实源。网络层仅允许保存：

- 待发送 FrameID 范围；或
- 从 Ledger 提交时取得的不可变 8 字节发送副本。

初始容量：客户端不可变出站历史 256 帧，服务器每玩家 Actual 历史 256 帧。它们是传输恢复材料，不是第二个可修改 Ledger。

服务器按 `PlayerIndex + FrameID` 保存历史：同帧同 raw 幂等；同帧不同 raw 是完整性错误，向双方 ResumeRejected 并终止本局。

### 10.2 安全边界交换

重连 Welcome 后 Session 进入 `Resuming`。重连客户端用 Ready 上报：

- `LastContiguousRemoteFrameID`
- `EarliestRecoverableCanonicalFrame`
- `LatestLocalFrameID`

服务器向在线客户端发送 ResumeProbe，在线客户端用 ResumeState 上报同样的三个边界。Heartbeat 只证明活跃，不携带正确性边界。

服务器必须同时验证：

- 仍在 5000ms 宽限内；
- 服务端历史覆盖所需远端 Actual；
- 重连端不可变出站历史覆盖所需本地 Actual；
- 两端缺失纠正起点均不早于各自 `EarliestRecoverableCanonicalFrame`；
- 范围长度不超过容量和处理预算。

### 10.3 冻结与补流

验证通过后，服务器先冻结三组闭区间：

1. 重连端补交本地 Actual；
2. 服务器向重连端补发远端 Actual；
3. 在线端等待补齐的对端 Actual。

冻结完成才发送 ResumeAccepted。冻结后到达的新输入可继续进入有界历史，但必须进入 live tail，不能插入基础恢复批次。

重连端从 Ledger/不可变副本补交本地 Actual；服务器校验、去重并通过在线端自己的 KCP Session 发送。服务器同时向重连端重放远端 Actual。双方分别用 ResumeComplete 报告上传和连续接收上界。只有两端都越过冻结上界，服务器才把 Session 切回 Running，并按 FrameID 接续 live tail。

### 10.4 拒绝条件

以下任一条件触发 ResumeRejected 给双方并终止本局：

- 超过宽限期；
- 任一历史范围已不可用；
- 任一客户端快照安全边界不覆盖最早纠正帧；
- 重连端无法提供必要本地 Actual；
- 同帧不同 raw；
- 恢复期间再次发生无法安全合并的代次切换；
- 恢复队列或处理预算超过硬上限。

P2-F 不做世界状态重同步，不能用猜测状态继续比赛。

当前 Route C 共享起始帧且 `RemoteFrameOffset=0`，所以业务 FrameID 当前与 Canonical Frame 数值一致。服务端历史仍按“发送玩家 + 业务 FrameID”保存；未来若引入非零映射，客户端负责映射，服务器不得猜测。

## 11. KCP 参数与调度

Route C 初始候选配置为：

```text
MTU = 1172
SetWindowSize(32, 128)
SetNoDelay(1, interval, 2, false)
interval in {1, 5, 10, 20} ms
```

这里 `nocwnd=false`，因此不是直接继承街篮2的 `NoDelay(1,1,2,1)`。这些仍然只是 Route C 实验配置，不是已验证默认值。实现完成后以 10ms 作为首个 smoke 值，再完整运行 1/5/10/20ms 矩阵。

每个 Session 使用同一单调时间域调用 `Update(nowMs)`，并用 `Check(nowMs)`计算下一次 deadline。诊断分别记录：

- requested interval；
- KCP core effective interval；
- 实际 Update 调用间隔；
- 计划唤醒点与真实唤醒点误差；
- Update 次数、KCP output 数、重传、WaitSnd、队列高水位；
- CPU 时间、数据报数和字节数。

## 12. 两层故障模型

### 12.1 UDP 数据报层

位于 KCP output 与对端 KCP input 之间，对真实 KCP 数据段和 ACK 同等生效：

- delay；
- jitter；
- drop；
- reorder；
- duplicate。

KCP loss 必须是真实数据报丢弃，不能用 recovery delay 代替。

### 12.2 既有业务/TCP层

保留 P2-E 的业务消息 reorder/duplicate 和 TCP recovered-loss/HOL 语义，明确标注其不是 UDP 数据报丢包。

TCP/KCP 可以共享业务输入脚本、随机种子和名义场景，但报告必须标注不同故障语义，不能把两类数字直接当成同一种网络事件。

## 13. 两类验收证据

### 13.1 确定性虚拟链路

使用虚拟单调时钟和内存数据报队列，证明：

- 协议状态机与幂等；
- 数据报丢失后的 KCP 重传；
- Session/Generation/Endpoint/Conv 隔离；
- 有界续连冻结、补交、补发、尾部接续和拒绝；
- 相同种子得到相同事件轨迹。

虚拟链路不证明真实线程、Socket、CPU或系统调度。

### 13.2 live Socket

使用真实客户端/服务器 worker 与 UDP Socket，证明：

- Socket 和线程所有权；
- deadline 等待、实际 Update 间隔和唤醒误差；
- 有限关闭、无死锁、无 1ms 无条件忙轮询；
- CPU、带宽、重传和队列水位；
- 真实 Endpoint 切换和续连。

live测试不代替确定性协议证明。两类证据缺一不可，也不能据此预设 KCP 一定快多少。

## 14. interval裁定规则

对 `1/5/10/20ms` 使用相同业务脚本、运行时长、故障种子和机器环境，至少三次独立 live 运行。分别报告 p50/p95/p99 relay latency、重传、总字节、worker CPU、requested/effective/actual interval 和 wakeup error。

裁定顺序：

1. 淘汰任何正确性、稳定性、关闭或调度证据失败的候选；
2. 在剩余候选中列出 latency、bandwidth、CPU、retransmission 和 scheduling precision 的 Pareto 前沿；
3. 若多个候选没有单一支配者，选择更低 CPU/带宽且尾延迟没有明显恶化的较大 interval；
4. 保存完整表格和选择理由，不写“KCP必然快 X%”。

在矩阵完成前，10ms 只是 smoke 初值，不是最终默认值。

## 15. P2-F通过条件

- 8 字节业务格式和 Ledger 语义不变；
- KCP/UDP 可作为默认实验通道，TCP仍可回退；
- 非法 Version、Length、Generation、Endpoint 和 Conv 均被拒绝并分类计数；
- 服务器从不直接转发客户端 KCP 数据报；
- 握手、重连和续连在丢包、重排、重复下保持幂等；
- 超出安全边界时双方一致终止；
- KCP 和 UDP Socket 均满足单线程所有权；
- worker 有限关闭且不存在 Unity FPS 驱动或 1ms 无条件轮询；
- kcp2k 来源、MIT许可、固定提交和最小补丁均可审计；
- 确定性虚拟链路与 live Socket 两类证据分别通过；
- 1/5/10/20ms 的 requested、effective 和 actual 值均能被证明；
- 最终性能陈述仅适用于实际测量环境。

最终双端人工体验验收仍由帅老大执行；自动化通过不能替代该人工验收。
