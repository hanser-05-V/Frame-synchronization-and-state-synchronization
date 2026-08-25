# Raw UDP Minimal Frame-Sync Demo Design

> 状态：2026-08-20 由帅老大逐段批准；本文件锁定独立 Raw UDP 最小 Demo 的设计输入，不代表生产代码、测试或实施计划已经完成。
>
> 上游边界：[P2-F UDP/KCP Transport Design](2026-08-17-p2f-udp-kcp-transport-design.md)、[P2-F Task 7 delivery](../../handoffs/2026-08-20-p2f-task7-delivery.md)。原 P2-F Task 8 继续暂停。

## 1. 结论

采用“现有 Route C envelope + 独立 Raw UDP 消息 + 纯滑动窗口输入冗余”的固定 1v1 Demo：

- 默认冗余窗口 `N=6`，最大 `N=16`；
- 不使用 ACK、选择性补发、拥塞控制或可靠交付承诺；
- 每个业务输入继续是既有 8-byte little-endian `raw:uint32 + frameID:int32`；
- 客户端和服务端都通过有界不可变历史重建最近 N 帧；
- 重复和乱序幂等接受，同帧不同 raw 是完整性故障；
- 缺帧越过冗余窗口和有界乱序宽限后，双方确定性终止本局；
- `FrameInputLedger` 继续是 Actual 输入唯一事实源；
- TCP、KCP client 和 Raw UDP 三种 transport 独立保留。

Raw UDP 交付不等于 KCP 或完整 P2-F 完成。未来仍从原 P2-F Task 8 恢复 KCP server、bounded resume 和 interval matrix。

## 2. 已验证基线与设计边界

开始本设计门时只读确认：

- 分支为 `delivery/route-c`；
- 工作区存在大量未提交和未跟踪成果，没有可用的 Task 7 Git fixed point，不得用 `HEAD` 冒充基线；
- Task 7 relevant `192/192`、full EditMode `535/535`、Unity compile 0 errors；
- net48/C# 7.3 为 28 sources、exit 0、54,272 bytes；
- TCP options、ownership、lifecycle、Lab、Barrier、timing 均 PASS；
- Task 7 Standards/Spec 交付记录均为 0 actionable findings；
- 当前没有 Raw UDP 或原 Task 8 KCP server 生产文件。

现有代码边界继续成立：

- `IFrameTransportClient` 是 Unity 与具体传输之间的唯一接口；
- `NetworkClient` 只持有一个 concrete transport；
- `GameController` 通过现有异步 Start 和 transport event 路径启动/终止网络会话；
- `FrameInputLedger.RecordActual` 已支持同值幂等重复，并把同帧不同 raw 标记为完整性故障；
- `KcpUdpClientTransport`、`TcpClientTransport`、Route C codec/state machine 和 KCP vendor 成果必须保留。

## 3. 方案比较与裁定

### 3.1 方案 A：纯滑动窗口冗余（采用）

复杂度最低，不实现 ACK 或补发调度器。N=6 时，一个输入正常出现在 6 个连续发送机会中，可恢复任意最多 5 个连续丢失的数据报；6 个与该输入发送机会完全对齐的连续丢失可能令最老输入不可恢复。

拟定 RawInput 为 `28-byte envelope + 12-byte raw header + N*8-byte entries`：默认 88 bytes，最大 168 bytes。30 逻辑帧/秒时默认应用层为 2,640 B/s/单向；加 IPv4+UDP 的 28-byte 头后约 3,480 B/s/单向，不含以太网开销。最大 N=16 时分别为 5,040 B/s 和 5,880 B/s/单向。

### 3.2 方案 B：冗余 + piggyback ACK + 有界选择性补发（否决）

它需要 ACK bitmap、发送历史、补发次数、超时和终止状态，实质上重新实现一部分简化 ARQ。它能在反馈及时且历史仍可用时恢复超过 N 的丢失，但仍不能把 UDP 变成可靠通道；未来切回 KCP 时大部分自定义可靠性逻辑会废弃。

### 3.3 方案 C：继续 KCP、关闭 Resume（否决）

现有客户端可复用，但仍需要原 Task 8 的 KCP server session、router、调度和 live 验证。它迁移到完整 P2-F 的成本最低，却不是 Raw UDP 最小 Demo，并会实质提前进入当前明确暂停的 Task 8。

## 4. 总体架构与事实源

```text
Unity main thread
  -> NetworkClient facade
     -> RawUdpInputTransport
        -> bounded local-input command queue
        -> one UDP worker / one Socket
        -> one remote-arrival queue
        -> one transport-event queue

RawUdpRelayServer
  -> one UDP Socket / one worker
  -> P0/P1 endpoint + session slots
  -> decode / validate / deduplicate
  -> bounded immutable relay copies per player
  -> rebuild RawInput datagrams for the peer

remote arrival
  -> GameController
  -> FrameInputLedger.RecordActual()
```

Raw UDP server 只校验和转发输入，不执行 gameplay、预测、回滚、快照、表现或 WorldHash 推演。客户端和服务端保存的历史都是不可变传输副本，不得覆盖首次接受值，也不是第二个可修改 Actual 事实源。

Unity 主线程不得访问 Socket、Endpoint、UDP bytes、PacketSequence 窗口或 worker 内部历史。

## 5. Route C envelope 与消息类型

复用现有 28-byte `RCF2` version 1 envelope：

| 偏移 | 长度 | 字段 | 编码 |
|---:|---:|---|---|
| 0 | 4 | Magic | ASCII `RCF2` |
| 4 | 1 | Version | `1` |
| 5 | 1 | MessageType | byte |
| 6 | 2 | PayloadLength | unsigned big-endian |
| 8 | 16 | SessionID | 128-bit raw bytes |
| 24 | 4 | Generation | unsigned big-endian |
| 28 | N | Payload | 必须与 PayloadLength 完全一致 |

Raw UDP 建立后 Generation 固定为 `1`。v1 不提供 Generation 切换、Endpoint 迁移或 Resume。

新增且与 KCP 控制负载隔离的消息值：

| 值 | 消息 |
|---:|---|
| 13 | `RawHello` |
| 14 | `RawWelcome` |
| 15 | `RawReady` |
| 16 | `RawStart` |
| 17 | `RawInput` |
| 18 | `RawFault` |

Route C 总数据报上限继续是 1200 bytes。RawInput v1 的合法上限更严格，为 168 bytes。

## 6. 控制负载

| 消息 | 精确负载 |
|---|---|
| `RawHello` | `ClientNonce(16)`；外层 SessionID 全零、Generation=0 |
| `RawWelcome` | `EchoNonce(16) + PlayerIndex(1) + WindowSize(1)`；外层携带分配的 SessionID、Generation=1 |
| `RawReady` | empty payload |
| `RawStart` | `CanonicalStartFrame:int32(be)`；v1 只能为 0 |
| `RawFault` | `Reason:uint16(be) + FaultPlayerIndex:uint8 + Reserved:uint8=0 + FrameID:int32(be) + ObservedLatestFrameID:int32(be)` |

`FaultPlayerIndex=255` 表示无法归属单一玩家。没有适用帧号时两个帧字段均为 `-1`。

RawHello 必须使用全零 SessionID 和 Generation=0；MatchFull 握手拒绝也使用这组外层值。RawWelcome 及建立会话后的 RawReady、RawStart、RawInput、RawFault 必须使用非零 SessionID 和 Generation=1。所有控制负载必须精确匹配表中长度；未知 fault reason、非法 WindowSize、非零 Reserved 或错误消息方向均按拒绝矩阵处理。

## 7. RawInput 二进制布局

| 偏移 | 长度 | 字段 | 编码与规则 |
|---:|---:|---|---|
| 0 | 1 | PlayerIndex | 只能为 0 或 1 |
| 1 | 1 | InputCount | `1..16` |
| 2 | 2 | Reserved | v1 必须为零 |
| 4 | 4 | PacketSequence | uint32 big-endian |
| 8 | 4 | LatestFrameID | int32 big-endian，必须非负 |
| 12 | `InputCount*8` | InputEntries | 按 FrameID 严格连续递增 |

每个 InputEntry 精确保留既有业务布局：

```text
raw:uint32 little-endian + frameID:int32 little-endian
```

结构约束：

- `InputCount = min(WindowSize, LatestFrameID + 1)`；发送方不得主动缩短窗口；
- 第一项 FrameID 必须等于 `LatestFrameID-InputCount+1`；
- 最后一项 FrameID 必须等于 LatestFrameID；
- 相邻 FrameID 必须恰好相差 1；
- 默认 N=6 时总数据报 88 bytes；最大 N=16 时总数据报 168 bytes；
- v1 没有 ACK 字段、累计 ACK、确认 bitmap 或隐含送达确认。

PacketSequence 从 0 开始，每发送一个 RawInput 数据报递增并允许 uint32 自然回绕。比较采用半范围串行数规则：`a != b && (uint)(a-b) < 0x80000000` 表示 a 比 b 新；达到半范围的跳变无法安全排序并按协议违规处理。

PacketSequence 的作用域是 `(receiver SessionID, direction, payload PlayerIndex)`：客户端到服务端时外层 SessionID 属于发送玩家；服务端到客户端时外层 SessionID 属于接收玩家，而 payload PlayerIndex 表示输入的原始玩家。

LatestFrameID 是“本数据报窗口的末帧”，不是必须随 PacketSequence 单调增长的全局确认值。客户端正常上行在 Sequence 顺序下必须非递减（尾部副本允许相等）；服务端为了补发迟到恢复窗口，可以用更新的下行 PacketSequence 发送较旧的 LatestFrameID。接收端必须单独维护 HighestObservedLatestFrameID，不能仅因合法数据报的 LatestFrameID 下降而拒绝它。

## 8. 幂等握手与状态转换

控制重试周期固定为 50ms，握手总上限为 3000ms：

```text
Client                           Server
  |-- RawHello(nonce) ----------->|
  |<-- RawWelcome ----------------|  allocate Session/Player/N
  |-- RawReady ------------------->|
  |                                |  wait until P0/P1 Ready
  |<-- RawStart(frame=0) ----------|
  |-- RawInput(includes frame 0) ->|  implicit Start acknowledgement
```

规则：

- 首次握手幂等键为 `Endpoint + ClientNonce`；
- 相同 Endpoint+Nonce 只重发完全相同的 Welcome；
- SessionID 使用密码学安全随机 128-bit 值，两个活动玩家不得相同；
- Welcome 必须回显 Nonce；客户端只接受第一次完全匹配的 Welcome；
- Ready 和 Start 重复均幂等，不得重复创建会话或启动 FrameEngine；
- 客户端只在第一次收到匹配 Session/Generation/server Endpoint 的 Start(frame=0) 时启动；
- 服务端收到该玩家第一份包含 frame 0 的合法 RawInput 后，停止向该 endpoint 重发 Start；
- 两名玩家都 Ready 后即可转发输入；尚未收到 Start 的客户端最多暂存 256 个已校验远端输入，Start 前不得发布给 gameplay；Start 后按 FrameID 升序发布预启动缓存；
- 握手超时、预启动缓存满或冲突输入均终止本局。

客户端状态使用现有枚举的子集：

```text
Disconnected -> Handshaking -> AwaitingReady -> Running -> Terminated
```

服务端比赛状态：

```text
WaitingForPlayers -> WaitingForReady -> Starting -> Running -> Terminated
```

Raw UDP 从不进入 Reconnecting 或 Resuming。Running 后连续 3000ms 没有合法控制或业务流量时触发 ConnectionTimedOut。

## 9. Endpoint 与玩家绑定

- 相同 Endpoint、相同 Nonce：幂等 Welcome；
- 已绑定 Endpoint 使用新 Nonce：归属到该合法 endpoint 的协议违规并终止本局；
- 相同 Nonce 来自不同 Endpoint：丢弃，不迁移 Session；
- 第三个独立 Endpoint：返回一次 MatchFull 握手拒绝，不分配会话；
- Welcome 后业务包必须同时匹配 `SessionID + Generation(1) + Endpoint + PlayerIndex`；
- Session 正确但 Endpoint 错误的包只丢弃和计数，不得成为外部杀局开关；
- v1 不支持 Endpoint 更新；客户端 Endpoint 变化最终通过合法流量静默超时终止。

该 Session 仅用于本进程内会话隔离，不等于账号认证、内容加密或抗链路攻击方案。

## 10. 发送、冗余与尾部行为

客户端 worker 使用单调时钟，正常发送由新逻辑输入驱动并保持 30Hz 逻辑节奏，不依赖 Unity render FPS：

1. 本地 FrameID 必须从 0 连续递增；跳帧、倒退或同帧不同 raw 是 outbound history fault；
2. 每接受一个新本地输入，依次生成包含最近 N 个不可变输入的 RawInput；
3. catch-up 一次入队多个逻辑帧时仍按 FrameID 升序生成各自窗口，允许有界发送突发；
4. 至少有 frame 0 后，暂时没有新帧时按 30Hz 发送当前窗口副本，以承担存活检测和尾部冗余；
5. 本地输入结束后至少继续产生 N-1 个尾部发送机会；
6. 每个副本使用新的 PacketSequence。

客户端、服务端每玩家的不可变输入历史均为 256 帧。本地输入命令、预启动远端缓存和远端 arrival 队列上限也各为 256。

## 11. 服务端重新生成下行冗余

服务端不得直接转发客户端数据报。它解码、校验并保存首次接受的不可变输入，然后使用接收玩家自己的 SessionID 和独立 PacketSequence 重新编码下行 RawInput。

正常顺序输入：每个唯一上行发送机会生成一个下行窗口，未来窗口自然让当前帧累计 N 个下行发送机会。

迟到恢复需要额外处理。例如 N=6 时服务端先收到 `[F+1..F+6]`，后收到乱序窗口 `[F..F+5]`，此时才首次恢复 F。若只转发一次迟到窗口，F 在下行只有一次机会。因此：

- 服务端为保留帧记录“已生成的下行发送机会数”；该计数不是 ACK，也不表示对端收到；
- 对第一次通过迟到窗口恢复的帧，服务端使用新的下行 PacketSequence 有界重复这份合法窗口，直至该迟到帧累计获得 N 个下行发送机会；
- 同一上行包最多触发 N 个下行包：默认 6，最大 16；
- clean 稳态仍为每个正常上行发送机会对应一个下行发送机会；
- 迟到恢复产生的额外副本单独计入 `LateRecoveryRelayCopies`；
- 上行尾部重复由服务端逐次重建并转发。

该修正确保上行最后一次机会才恢复的输入不会在下行退化为单次机会，同时仍保持无 ACK 和单轮 64 数据报预算。

## 12. 去重、乱序和历史

业务去重键为 `PlayerIndex + FrameID`：

- 同帧同 raw：幂等丢弃，不发布第二次 arrival；
- 同帧不同 raw：保留首次值，标记完整性故障并终止双方；
- RawInput entries 可因数据报乱序晚于更高帧到达，首次有效输入仍可发布给 Ledger；
- 下行迟到恢复窗口可以使用更新 PacketSequence 和较低 LatestFrameID；gap proof 使用独立的 HighestObservedLatestFrameID，不能被较低窗口倒退；
- arrival queue 保存 worker 的单调 ReceiveSequence 和单调时间戳，不把网络到达顺序伪装成 FrameID 顺序。

每个 PacketSequence 域保留最近 64 个 Sequence 及内容指纹：

- 窗口内乱序正常处理；
- 相同 Sequence、相同内容是数据报重复；
- 相同 Sequence、不同内容是协议完整性故障；
- 落后最高 Sequence 超过 64 的包是 PacketTooOld；
- 半范围以上的 Sequence 跳变是 ProtocolViolation。

输入历史按最高已见 FrameID 保留最近 256 帧。早于历史且不再参与缺口恢复的包计为 InputTooOld；新 Latest 跨度超过历史能力则终止为 CapacityExceeded。

## 13. 不可恢复缺口

缺失帧 F 的最后正常携带机会是 LatestFrameID=`F+N-1` 的窗口。接收端看到一个合法窗口，其最早 FrameID 已大于 F 时：

1. 记录“冗余窗口已越过 F”的证据 PacketSequence；
2. 允许 16 个更新的唯一 PacketSequence 作为乱序宽限；
3. 宽限内迟到窗口补回 F，则清除该缺口；
4. 宽限耗尽仍缺 F，触发 `UnrecoverableInputGap(F)`；
5. 若没有后续合法包推进证据，则由 3000ms ConnectionTimedOut 终止。

该 16 包宽限只容忍有界乱序，不增加 F 的发送冗余次数，也不把 UDP 变成可靠通道。终局故障由服务端向双方以 `50ms * 6` 次有界发送 RawFault；通知本身不被宣称可靠。客户端发现下行故障时向服务端发送合法 RawFault，服务端验证会话后将比赛转为终局并通知双方。对于 HandshakeTimeout、ConnectionTimedOut、ProtocolViolation、ConflictingInput、UnrecoverableInputGap 和 CapacityExceeded，服务端必须把原始 reason/player/frame 原样广播给双方，不能把具体原因降级成 PeerFault；只有客户端自身 worker 发生无法归入上述协议原因的本地终局故障时才使用 PeerFault。

## 14. RawFault 原因码

| 值 | 原因 | 语义 |
|---:|---|---|
| 1 | `HandshakeTimeout` | 3000ms 内未完成 Start |
| 2 | `ConnectionTimedOut` | Running 后 3000ms 无合法流量 |
| 3 | `ProtocolViolation` | 已绑定会话发送结构或状态非法的数据 |
| 4 | `ConflictingInput` | 同一 Player/Frame 出现不同 raw |
| 5 | `UnrecoverableInputGap` | 缺帧越过冗余和乱序宽限 |
| 6 | `CapacityExceeded` | 队列、历史或跨度超过硬上限 |
| 7 | `PeerFault` | 客户端本地 worker 报告无法归入 1..6 的终局故障 |
| 8 | `MatchFull` | 固定 1v1 的两个玩家槽位已占用 |

MatchFull 使用全零 SessionID、Generation=0、FaultPlayerIndex=255 和两个 `-1` 帧字段，只对有效 RawHello 返回一次。

## 15. 严格拒绝矩阵

| 情况 | 处理 | 终止合法比赛 |
|---|---|---|
| Magic/Version/MessageType/外层 Length 非法 | 分类丢弃，不回复 | 否 |
| 数据报 >1200B 或 RawInput >168B | 丢弃；可归属到绑定 endpoint 时记 ProtocolViolation | 仅可归属时 |
| 未知 Session | 丢弃，不回复 | 否 |
| RawHello 的 SessionID 非零或 Generation != 0 | 丢弃并计数 | 否 |
| 建立会话后的消息 Generation != 1 | 丢弃并计数 | 否 |
| Session 正确、Endpoint 错误或旧 Endpoint | 丢弃，不回复 | 否 |
| Session/Endpoint 正确、PlayerIndex 错误 | ProtocolViolation | 是 |
| Hello Endpoint+Nonce 完全重复 | 重发同一 Welcome | 否 |
| 已绑定 Endpoint 使用新 Nonce | ProtocolViolation | 是 |
| 相同 Nonce 来自其他 Endpoint | 丢弃 | 否 |
| 第三个独立 Endpoint | 单次 MatchFull | 否 |
| Ready/Start 重复 | 幂等接受 | 否 |
| 控制负载长度、WindowSize、fault reason、Reserved 或消息方向非法 | 可归属到绑定 endpoint 时 ProtocolViolation，否则丢弃 | 仅可归属时 |
| 客户端在合法 Start 前发送 RawInput | ProtocolViolation | 是 |
| 已 Ready 客户端在 Start 前收到 server RawInput | 校验后进入预启动缓存 | 否 |
| Reserved/Count/窗口长度/Latest/Frame 连续性非法 | ProtocolViolation | 是 |
| Sequence 窗口内乱序 | 正常处理 | 否 |
| 同 Sequence、同内容 | 幂等丢弃 | 否 |
| 同 Sequence、不同内容 | ProtocolViolation | 是 |
| Sequence 落后超过 64 | PacketTooOld | 否 |
| Sequence 半范围歧义 | ProtocolViolation | 是 |
| 同帧同 raw | 幂等丢弃 | 否 |
| 同帧不同 raw | ConflictingInput | 是 |
| Latest 跨度超过 256 | CapacityExceeded | 是 |
| 输入早于保留历史且不参与恢复 | InputTooOld | 否 |
| 缺帧耗尽 N 与 16 包宽限 | UnrecoverableInputGap | 是 |
| 本地命令/预启动/arrival/历史满 | CapacityExceeded | 是 |
| 绑定客户端发送合法 RawFault | 转为比赛终局并通知双方 | 是 |
| 未绑定 Endpoint 发送 RawFault | 丢弃，不回复 | 否 |

只有能由 SessionID+Endpoint 归属到合法参与者的协议违规才允许结束比赛；外部垃圾包不得成为远程杀局开关。

## 16. 客户端 worker 与队列所有权

RawUdpInputTransport worker 单线程拥有：

- UDP Socket 和 configured server Endpoint；
- handshake/session state；
- PacketSequence 和 64 包乱序窗口；
- 256 帧本地不可变发送历史；
- 256 帧预启动远端缓存；
- receive/send buffers 和 diagnostics accumulator。

主线程只能提交 immutable `{raw, frameID}`，读取唯一 remote-arrival queue、唯一 event queue 和原子替换的 diagnostics snapshot，并请求停止。`SubmitResumeReadiness` 在 Raw UDP 中是明确 no-op，Raw transport 永远不发布 ResumeRequired。

每轮顺序：

1. 检查停止；
2. 排空有界命令；
3. 处理到期握手/发送；
4. Socket.Select 等待可读或最近 deadline；
5. 最多接收 64 个数据报；
6. 解码、校验、去重；
7. 发布不可变 arrival/event/diagnostics。

Select 最长单次等待 10ms，以及时发现新出站命令和停止。它是阻塞等待，不是忙轮询。禁止 Thread.Sleep(1)、Unity Update 驱动网络或无条件 1ms 唤醒。

## 17. 服务端 worker、公平性与停止

一个 RawUdpRelayServer worker 拥有唯一 UDP Socket、两个玩家槽位、两套输入/Sequence/转发历史、两个下行 Sequence 域、握手、故障通知和停止状态。

同轮双方到期时按稳定玩家索引 0 -> 1。单轮初始硬预算：

- 最多 64 个入站数据报；
- 每玩家最多接受或重建 64 个业务输入/数据报工作项；
- 连续工作约 2ms 后重新检查另一玩家、Socket 和停止。

客户端和服务端的 RequestStop 只设置停止请求；主线程/Program 不关闭 worker Socket。worker 最迟在 10ms Select 上限后观察停止，并在 finally 中释放 Socket 和会话状态。

WaitForStop 正常上限为 260ms（10ms 响应 + 现有 250ms 关闭余量）。超时发布 WorkerStopTimedOut；禁止 Thread.Abort 或外部强制关闭 worker Socket。未启动、握手中、Running、故障后和重复停止都必须幂等。

## 18. Transport 配置兼容

保留现有枚举数值并追加：

```text
KcpUdp = 0
Tcp    = 1
RawUdp = 2
```

NetworkClient 仍只持有一个 IFrameTransportClient，factory 增加第三分支，不修改接口。Unity 新增 `ConnectConfigured()`，配置优先级固定为：

1. 命令行；
2. NetworkClient Inspector；
3. NetworkConfig defaults。

Unity 命令行：

```text
--transport tcp|kcp|raw-udp
--server-ip 127.0.0.1
--server-port 8888
```

Inspector 复用 Server IP/Port 并新增 Transport 下拉。GameController 只把固定默认 endpoint 的 Connect 调用替换为 ConnectConfigured；不增加 Raw UDP gameplay 分支。旧 `Connect(ip, port)` 保留给测试和旧调用者。

服务端显式命令：

```text
NetworkServer.exe --transport raw-udp --raw-window 6
```

- `--raw-window` 只接受 1..16，默认 6，且只对 raw-udp 合法；
- 无参数运行仍为 legacy interactive TCP；
- 显式参数未给 transport 时继续默认 KCP 并按 Task 7 fail fast；
- tcp Lab/Barrier/framing/fault labels 不变；
- kcp 仍保留 Task 7 fail fast；
- 每个 server process 只运行一种 transport。

Raw Start 继续发布现有 SessionStarted+Running event。Raw terminal fault 映射为新增 transport event reason 并复用 GameController 现有 match-terminal fault 入口。

## 19. 诊断与敏感信息边界

客户端 immutable diagnostics 至少包含：state/player/window/session fingerprint、sent/received Sequence high water、datagrams/bytes、分类 drops、latest local/highest remote/contiguous remote、pending gap 和 grace、direct/redundancy/reorder recovery、queue values/high-water、worker/select/budget/stop metrics。

服务端按玩家增加 handshake retries、accepted/idempotent/conflicting inputs、relay windows、tail copies、LateRecoveryRelayCopies、endpoint/session/player mismatch 和 fault repeats。

默认日志禁止输出：

- 完整 SessionID；
- ClientNonce；
- 完整 IP/port；
- raw UDP datagram bytes；
- 每帧 raw input value。

关联只使用 SHA-256 后截取的短指纹。普通 trace 只记录方向、消息类型、长度、Sequence、Frame range、decision 和 fault reason。固定测试值可以在断言中检查完整字节，但不得进入普通运行日志。

Raw、KCP、TCP 诊断与报告必须分开。Raw UDP 数据不得写成 KCP 性能证据。

## 20. 确定性 Raw UDP 故障模型

实验层位于完整 Raw UDP datagram output 与对端 input 之间，支持 delay、jitter、drop、reorder 和 duplicate：

- 使用虚拟单调时钟和固定 seed；
- 每类决策使用独立稳定 salt；
- 禁止 wall clock、System.Random 和 thread arrival order 参与决定；
- 决策身份包含 direction、session fingerprint、message type、copy index，以及 RawInput 的 PacketSequence；控制消息没有 PacketSequence，改用故障链路内部的 per-direction/per-message send ordinal；
- drop 永久删除，不生成 synthetic recovery；
- duplicate 最多产生一个额外副本；
- control 和 RawInput 同样经过 fault model；
- 相同 seed/script/input 必须产生 byte-identical decision trace；
- percentages 为 0..100，delay/jitter/reorder-extra 为 0..5000ms；
- 标签使用 `raw-udp-datagram-*`，禁止复用 TCP recovered-loss/HOL 标签。

该故障模型只作用于传输实验，不修改 gameplay、Ledger、prediction、rollback 或 presentation time。

确定性虚拟链路必须注入固定 ClientNonce、SessionID 和 endpoint lane identity；live Socket 使用真实随机 SessionID，其不同运行之间不要求 trace 字节一致。

## 21. 自动化测试矩阵

| 层级 | 必须证明 |
|---|---|
| Codec | 六种消息精确字节、endianness、N=1/6/16、88/168B 边界和非法布局 |
| Handshake | Hello/Welcome/Ready/Start loss/duplicate/reorder/idempotence；FrameEngine 只启动一次 |
| Session | unknown Session、Generation、Endpoint、Player、third client 和 old endpoint isolation |
| Sequence | increment、uint32 wrap、64-packet reorder、duplicate、too-old、conflict |
| Input | idempotent same raw、conflicting raw terminal、contiguous frames、256 capacity |
| Redundancy | N=6 时连续 drop 1..5 recover；aligned 6 drop 明确 terminal |
| Server relay | upstream final-opportunity recovery still receives N bounded downstream opportunities |
| Tail | final input receives at least N send opportunities |
| Reorder grace | gap recovered within 16 packets；same frame fault after grace expires |
| Fault model | same seed identical trace；selected different seeds diverge over sufficient samples |
| Ownership | facade/GameController contain no Socket/Endpoint/UDP bytes/second arrival queue |
| Lifecycle | all states stop within 260ms and release worker-owned resources |
| Capacity | queues/history/64 datagrams/2ms/1200B/168B hard limits |

## 22. Live dual-client acceptance

每个客户端至少发送 900 个逻辑输入，覆盖：

1. clean Hello -> Start -> bidirectional input；
2. first Hello/Welcome/Ready/Start loss and retry；
3. isolated drop and five consecutive drops recover at N=6；
4. aligned six-drop burst on uplink and downlink terminates both；
5. delay+jitter+reorder+duplicate combination；
6. wrong Session/Player/Endpoint injection cannot kill the legal match；
7. bound client same-frame different-raw conflict terminates both；
8. endpoint change and 3000ms silence do not reconnect；
9. N=16 maximum 168-byte window；
10. bounded stop while handshaking, running, and terminated。

Acceptance gates：

- 双端都从 Canonical Frame 0 开始，RemoteFrameOffset=0；
- recoverable cases publish each remote Actual to Ledger once；
- recoverable terminal Confirmed/Predicted WorldHash converges，不宣称中间从未 prediction fork；
- unrecoverable cases agree on fault reason/player/frame；
- default clean RawInput is 88 bytes；实际 pps/bytes/queue/budget metrics are saved separately；
- Raw focused/relevant/full EditMode all 0 failed and 0 skipped；
- standalone Unity import/compile 0 C# errors；
- server net48/C# 7.3 compile passes；
- existing TCP Lab/Barrier/timing and KCP client focused tests remain PASS；
- static audit confirms no server gameplay/world simulation and Ledger remains sole Actual truth；
- delivery clearly says Raw UDP minimal Demo, not KCP/P2-F completion。

## 23. 明确范围外

- rooms、matchmaking、spectators、mid-match join、more than two players；
- account authentication、encryption、NAT traversal、DDoS protection；
- ACK、selective retransmission、congestion control、reliable ordered semantics；
- reconnect、endpoint migration、world-state resynchronization；
- server gameplay/prediction/rollback/snapshot/presentation；
- changes to Ledger、Canonical Frame、gameplay rules、presentation or terminal semantics；
- using Raw UDP numbers as TCP/KCP evidence。

## 24. KCP 恢复点

Raw UDP 阶段完成后停止。未来从原 P2-F Task 8 恢复：

- 保留现有 KcpUdpClientTransport、Route C KCP codec/state machine、RouteCKcpSession 和 vendored kcp2k；
- 不重做 Tasks 1..7；
- 实现 KCP server/session router、bounded resume、deterministic KCP fault lab 和 live interval matrix；
- Raw sliding window、PacketSequence、reorder grace 和 gap fault logic 不进入 KCP；
- envelope、IFrameTransportClient、single-worker ownership、monotonic clock、server transport selector 和 diagnostics style 可以继续复用；
- TCP、Raw UDP、KCP 保持独立 transport 和独立证据。

## 25. 本设计门停止边界

本设计门只允许写本设计、设计门报告和下一窗口 planning prompt。不得创建 `.cs`、`.ps1`、Unity tests、server artifacts 或 Raw UDP implementation plan；不得调用 writing-plans 或 implement。

下一窗口只使用 cold-stored writing-plans 根据本文件生成独立实施计划。计划再次批准后，另开 implementation window。
