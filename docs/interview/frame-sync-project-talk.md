# Route C 帧同步项目面试讲解提纲

> 面向 Unity 客户端、网络同步与游戏逻辑岗位。
>
> 完整简历正文和证据表见[职业材料](../career/街篮2式帧同步框架_最终目标与简历材料.md)，作品集入口见[P2-H 作品集索引](../portfolio/p2h-portfolio-index.md)。

## 一句话介绍

我在 Unity/C# 中实现了一套确定性 1v1 篮球帧同步框架：用 InputLedger 管理输入事实，用 Confirmed/Predicted 双世界和完整快照处理预测失配，再通过只读 View World 隔离逻辑回滚与画面表现，并以 TCP/KCP/Raw UDP、真实 Socket 和逐帧 Hash 建立可核查证据链。

## 推荐讲解顺序

1. 先说问题：远端输入迟到，但本地操作不能等待。
2. 再说输入事实：每帧每名玩家只能是 Missing、Predicted 或 Actual。
3. 引出双世界：Confirmed 提供稳定事实，Predicted 提供即时响应。
4. 解释失配恢复：从确认快照恢复，通过唯一 Step 重演。
5. 解释表现层：逻辑回滚正确不代表 Transform 自然平滑。
6. 最后给证据：自动化、真实 Socket、人工画面分别证明不同结论。

## 高频追问

### 1. 为什么需要 Confirmed、Predicted 和 View 三层？

Predicted World 允许临时使用预测远端输入，解决本地响应；Confirmed World 只消费双方连续 Actual，提供恢复基线、稳定结果和 Hash；View World 不运行第三份业务模拟，只为本地玩家、远端玩家和篮球选择表现来源。三者分离后，预测可以回滚，但稳定事实和表现策略不会被同一份可变状态污染。

### 2. 确认帧头为什么不是缓冲区最旧帧？

确认帧头表达“从起点到这里，双方 Actual 连续完整”。缓冲容量只表示能保存多少历史；播放水位表示表现落后多少。即使后续帧已经到达，只要中间有输入洞，确认头就不能越过。因此缓冲容量、确认进度和播放延迟是三个概念。

### 3. 预测失配后为什么不能只修正位置？

早期输入还会影响朝向、玩家状态、持球标记、篮球速度、篮球状态和持有者。只补位置会留下隐藏状态分叉。正确做法是找到最早失配，恢复其前面的完整确认快照，替换 Actual 输入，再通过正常推进使用的同一个确定性 Step 重演未确认后缀。

### 4. 为什么最终 Hash 一致不能证明中间从未出错？

最终 Hash 只证明同一 confirmed canonical frame 的规范化世界最终收敛。Predicted World 中间可能分叉并触发回滚，表现层也可能出现回抽或积压。因此还要记录 `errorFrame`、`restoredFrame`、`replayed`、三条帧头和表现轨迹。

### 5. 为什么本地、远端和篮球不能读取同一个世界？

本地玩家需要即时反馈，优先读取 Predicted；远端玩家若读取未确认预测，会把纠错直接暴露为回抽，因此读取 Confirmed 并插值；篮球跨实体共享球权，需要按持球、离手和投篮语义仲裁，否则会提前附着、延迟解绑或残留在旧持有者手上。

### 6. TCP、KCP 和 Raw UDP 各自解决什么？

- TCP 是可靠字节流，也是当前固定 100ms NetworkLab 预测/回滚表现入口。
- KCP 在 UDP 数据报上提供 ACK、重传、窗口和受控局内续连。
- Raw UDP 通过最近 N 帧冗余窗口演示不可靠数据报边界，没有 ACK、重传、拥塞控制或续连。

三种传输搬运同一 8 字节业务输入，但故障语义和能力不能互换。

### 7. 为什么选择 KCP 10ms？

`10ms` 是 KCP 更新间隔，不是网络延迟。Task 11 在当前 Windows loopback、33ms 逻辑节拍、每端 900 帧、2% 上行 UDP datagram drop 的预声明三轮中位数上选择 10ms。Task 12 fresh 样本中 5ms 的 p99 和 CPU 反而更低，说明它只是当前实验条件下的工程裁定，不是跨机器定律，也不是《街篮2》的默认参数。

### 8. 受控续连到底解决了什么？

它解决的是同一客户端进程、同一服务端进程、服务器状态仍在时的短暂局内通信中断：约 3 秒无有效流量进入 Reconnecting，从最后有效通信起约 5 秒总宽限内，使用 Session、Generation、Token、进度证明和不可变历史安全补齐缺口。live probe 已验证 endpoint 变化、Generation `1→2`、8 帧回放和双方回到 Running。

它不解决客户端关闭重启、服务端重启、生产 UDP Socket 自动重建、任意 Wi-Fi/网线切换或商业级移动网络恢复。宽限过期返回 `ResumeGraceExpired`，历史不足返回 `UnsafeResume`。

### 9. 自动化、真实 Socket 和人工画面分别证明什么？

- 自动化证明状态机、边界、回滚契约和确定性模型。
- 真实 Socket 证明真实端口、worker、CLI、数据报丢失、进程清理和 endpoint 变化。
- 人工画面证明正常局的平滑度、手感、人球关系与操作观感。

它们是互补证据，不能相互替代。协议 probe 通过不等于 Unity endpoint 迁移画面已经演示；最终 Hash 收敛也不等于画面一定平滑。

### 10. 为什么没有做 ECS/DOTS？

ECS 只改变确定性模拟内部的数据组织，不会自动解决确认头、预测失配、回滚恢复或表现回抽。P2-G 是可选深化，本轮主动延后，以避免为了简历扩大范围。当前不能声称实现了 ECS、DOTS、Jobs、Burst、Archetype 或 Chunk。

## 可直接引用的证据

- Unity EditMode：focused `257/257`，full `682/682`。
- KCP pure protocol：`11/11`。
- interval matrix：`1/5/10/20ms × 3`，21,600 条业务输入，0 missing、0 duplicate。
- KCP clean loopback：p50/p95/p99 为 `16.1202/17.2516/31.9569ms`。
- 10ms fresh drop matrix：p50/p95/p99 为 `15.9993/30.8883/53.3266ms`，worker CPU 中位为单核 `2.680308%`。
- 受控续连：endpoint changed、Generation `1→2`、8 帧回放、双方 Running。
- P2-F Task 12 独立只读审查：0 Critical、0 Important、2 Minor；该结果不等于当前 P2-H 八文档审查。

## 禁止夸大的回答

- 不说“服务器权威篮球模拟”；服务器只转发输入。
- 不说“完全不会回滚”；正确表述是回滚可恢复、可验证，并与表现层解耦。
- 不说“最终 Hash 证明全过程无分叉”。
- 不说“KCP 固定比 TCP 快 X%”。
- 不说“10ms 是注入延迟”或“街篮2原项目默认”。
- 不说“KCP 100ms Unity 人工验收通过”。
- 不说“商业级断网重连”或“客户端/服务端重启续局”。
- 不说已实现 ECS/DOTS/Jobs/Burst。
