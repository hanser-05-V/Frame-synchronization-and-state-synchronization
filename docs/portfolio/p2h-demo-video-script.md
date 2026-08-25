# Route C P2-H 约 5 分钟演示视频脚本

> 目标受众：Unity 客户端、网络同步和游戏逻辑岗位面试官。
>
> 叙事顺序：问题 → 输入事实 → 三世界 → 失配回滚 → 表现分离 → 真实传输 → 安全边界 → 证据与限制。
>
> 录制只使用真实画面、命令和现有证据，不制造假日志、假 Hash、假延迟或假网络故障。

## 录制前准备

- 从仓库根目录执行所有 PowerShell 命令。
- 打开 `Assets/Scenes/SampleScene.unity`。允许在进入 Play 前临时修改 Inspector，但不得保存场景；退出后若 Unity 询问保存，选择丢弃本次临时改动。
- TCP 镜头：进入 Play 前把 Editor 的 `NetworkClient` 临时设为 `Tcp`，服务器 `127.0.0.1:8888`，并开启 `GameController` 的 `P2-E Network Diagnostics`。
- KCP 镜头：进入 Play 前把 Editor 的 `NetworkClient` 临时设为 `KcpUdp`，服务器 `127.0.0.1:8888`；当前版本化场景的序列化值是 `RawUdp`，必须人工确认。
- 录制终端时遮蔽用户名和无关目录；不得展示 Token、Nonce、原始 SessionID 或 ResumeAttemptID。
- 每次只启动本镜头需要的服务端；结束时只关闭自己刚启动的进程，不按进程名批量结束。

## 0:00–0:25｜先展示问题

**画面**：标题页与双端篮球运行画面快速切换。

**旁白**：

> 帧同步的矛盾是：远端输入会迟到，但本地操作不能等。只等真实输入会增加响应延迟，直接预测又会在猜错时产生回滚。这个项目的重点不是堆玩法，而是把输入事实、预测世界、确认世界和最终画面拆开，并用真实 Socket 和可追溯证据验证边界。

**PASS**：25 秒内同时交代“为什么预测”和“为什么需要回滚”，不先堆测试数字。

## 0:25–1:05｜架构与术语落地

**画面**：打开[作品集架构图](p2h-portfolio-index.md#项目解决的问题)，依次高亮 Ledger、Confirmed、Predicted、View、Transport。

**旁白要点**：

1. `FrameInputLedger` 让每帧每名玩家只有 Missing、Predicted、Actual 三种输入事实。
2. Confirmed World 只消费双方连续 Actual，Predicted World 允许暂时预测。
3. View World 不运行第三份逻辑，只让本地读 Predicted、远端读 Confirmed、篮球按球权仲裁。
4. 服务器只转发输入，Confirmed 不是服务器权威篮球状态。

**PASS**：在使用“确认帧头”“回滚基线”等术语前先解释其含义；明确 View World 是只读表现组合。

## 1:05–2:05｜TCP 100ms：制造并解释预测失配

**启动顺序**：

PowerShell A（服务端，保持前台运行）：

```powershell
& '.\Project\NetworkServer\NetworkServer.exe' --transport tcp --delay-ms 100
```

服务端启动后，确认 Editor 已在进入 Play 前临时选择 `Tcp` 并开启诊断，然后进入 Play。再打开 PowerShell B（Windows 客户端）：

```powershell
& '.\Project\Frame Synchronization\Builds\P2FTask12\RouteC.exe' --transport tcp --server-ip 127.0.0.1 --server-port 8888 -p2eDiagnostics
```

**操作与画面**：

- Editor 端同样选择 `Tcp` 并开启诊断。
- 两端持续移动后突然反向或停止，制造远端 Actual 与预测的差异。
- 并排录制游戏画面和 Console/Player 日志。
- 定格一条 `[RouteC][Rollback]`，放大 `errorFrame`、`restoredFrame`、`replayed`。
- 再展示对应 confirmed frame 的最终 WorldHash。

**旁白**：

> 真实输入和预测不同后，系统不是直接修 Transform，而是找到最早错误帧，从它之前的完整确认快照恢复，再用正常推进的同一个 Step 重演。最终 Hash 一致证明确认状态收敛，但不表示中间从未预测分叉。

**PASS**：日志中的三个帧字段真实可见；没有拿设计文档中的示例 Hash 冒充运行结果。

## 2:05–2:40｜逻辑正确不等于画面自动平滑

**画面**：保持 TCP 100ms 双端，展示持续移动、主动反向、持球与投篮/脱手；同时短暂切回 View 来源矩阵。

**旁白**：

> 回滚正确只回答逻辑能否恢复。画面是否平滑是另一项验收：本地优先 Predicted，远端读取 Confirmed 并插值，篮球按持球与离手语义选择来源。表现纠正不能写回逻辑世界，也不能改变 WorldHash。

**PASS**：同时展示回滚日志存在和 Transform 没有明显原样倒退；若现场画面不满足，应如实重录，不能用自动化 PASS 替代。

**TCP 镜头清理**：两个 TCP 镜头都录完后，退出刚启动的 Windows 客户端，停止 Editor Play Mode，再在 PowerShell A 用 `Ctrl+C` 结束自己启动的前台服务端；确认 8888 已释放后再进入 KCP 镜头。不得按进程名批量结束进程。

## 2:40–3:20｜KCP 正常局与玩法路径

**启动顺序**：

PowerShell A（服务端，保持前台运行）：

```powershell
& '.\Project\NetworkServer\NetworkServer.exe' --transport kcp --kcp-interval-ms 10
```

服务端启动后，确认 Editor 已在进入 Play 前临时选择 `KcpUdp`，然后进入 Play。再打开 PowerShell B（Windows 客户端）：

```powershell
& '.\Project\Frame Synchronization\Builds\P2FTask12\RouteC.exe' --transport kcp --server-ip 127.0.0.1 --server-port 8888
```

**操作与画面**：

- 展示双方共同 Ready/Start，只从 canonical frame 0 启动一次。
- 两端移动、反向、拾球/持球并投篮。
- 屏幕角标写清：`KCP interval = 10ms`，`人工注入网络延迟 = 0ms`。

**旁白**：

> KCP、TCP 和 Raw UDP 搬运的是同一 8 字节业务输入。这里的 10ms 是 KCP 更新间隔，不是注入 10ms 网络延迟；当前 KCP 服务端不会应用 NetworkLab 的 delay 参数。

**PASS**：不把 KCP 正常局说成 KCP 100ms；不混用不同传输客户端。

**镜头清理**：退出刚启动的 Windows 客户端，停止 Editor Play Mode，再在 PowerShell A 用 `Ctrl+C` 结束自己启动的前台服务端；确认 8888 已释放后再运行 probe。

## 3:20–3:55｜2% UDP drop 与 interval 裁定

**建议画面**：打开 [`p2f-kcp-interval-summary.json`](../../p2f-kcp-interval-summary.json)，依次高亮：

- `rawRunCount: 12`；
- 1/5/10/20ms 各 3/3；
- 每轮双方 900/900；
- 合计 21,600 条业务输入，0 missing、0 duplicate；
- 10ms p50/p95/p99 `15.9993/30.8883/53.3266ms`，CPU `2.680308%`。

需要现场复验单轮时可使用：

```powershell
& '.\Project\NetworkServer\KcpLiveSocketProbe.ps1' -ServerPath '.\Project\NetworkServer\NetworkServer.exe' -Scenario clean -IntervalMs 10 -FramesPerClient 900 -DropPercent 2
```

**旁白**：

> Task 11 按预声明三轮中位数选择 10ms，但 Task 12 fresh 样本中 5ms 的 p99 和 CPU 更低。这个结果依赖当前机器、Windows loopback 和调度条件，所以 10ms 是工程裁定，不是普遍定律。

**PASS**：明确 TCP recovered-loss 与 KCP UDP datagram drop 不是同一故障模型，不直接宣称谁固定快多少。

## 3:55–4:35｜受控续连成功与安全拒绝

**命令**：

```powershell
& '.\Project\NetworkServer\KcpLiveSocketProbe.ps1' -ServerPath '.\Project\NetworkServer\NetworkServer.exe' -Scenario reconnect -IntervalMs 10
& '.\Project\NetworkServer\KcpLiveSocketProbe.ps1' -ServerPath '.\Project\NetworkServer\NetworkServer.exe' -Scenario grace-expired -IntervalMs 10
& '.\Project\NetworkServer\KcpLiveSocketProbe.ps1' -ServerPath '.\Project\NetworkServer\NetworkServer.exe' -Scenario history-expired -IntervalMs 10
```

**画面**：

- reconnect：`endpointChanged: true`、Generation `1→2`、`replayedFrames: 8`、双方 `Running`。
- grace：`ResumeGraceExpired`。
- history：`UnsafeResume`。

**旁白**：

> 这是同客户端进程、同服务端进程、服务端状态仍在时的有界局内续连。它校验身份、进度和不可变历史；不安全时明确拒绝。它不等于客户端或服务端重启续局，也不代表 Unity endpoint 迁移画面已经演示。

**PASS**：成功和拒绝路径都展示；不把协议 probe 扩写为商业级断网恢复。

## 4:35–5:00｜证据、交付与诚实限制

**画面**：作品集证据表，依次高亮：

- focused `257/257`、full `682/682`；
- KCP protocol `11/11`；
- matrix `12/12`；
- Windows x64 Player 与 net48 Release 服务端；
- P2-F Task 12 独立只读审查 0 Critical、0 Important、2 Minor；
- 限制页。

**收尾旁白**：

> 这个项目最终交付的不是“两个方块能移动”，而是一条从输入事实、预测失配、快照恢复、表现仲裁到真实传输的可核查证据链。我主动保留了服务器非权威模拟、KCP 无固定延迟注入、生产 Socket 不自动重建、续连只覆盖同进程短宽限，以及 ECS 本轮延后的边界。

**PASS**：最后一页至少停留 5 秒；没有把未实现能力写成未来承诺式成果。

## 成片最终检查

- [ ] 总时长约 5 分钟，问题与设计早于测试数字出现。
- [ ] 架构、运行画面、真实日志、Hash、Socket 证据和限制页均出现。
- [ ] `interval != latency`。
- [ ] `protocol resume != commercial reconnect`。
- [ ] `最终 Hash 收敛 != 中间从未预测分叉`。
- [ ] `自动化 PASS != 画面一定平滑`。
- [ ] 没有 Token、Nonce、SessionID 或个人隐私泄漏。
- [ ] 没有假日志、示例 Hash 冒充实测、KCP 100ms 或已实现 ECS 声明。
- [ ] 由帅老大实际观看并宣布 P2-H 是否最终完成。
