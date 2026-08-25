# Windows 构建与双端演示

## 固定配置

```text
Scene: Assets/Scenes/SampleScene.unity
Platform: PC, Mac & Linux Standalone
Target Platform: Windows
Architecture: x86_64
Server: Project/NetworkServer/NetworkServer.exe
Endpoint: 127.0.0.1:8888
Client pairing: Unity Editor = player 0, Windows build = player 1
```

## 构建

1. 用 Unity Hub `2022.3.62f2` 打开 `Project/Frame Synchronization`。
2. 确认 Build Settings 中只有启用的 `Assets/Scenes/SampleScene.unity`。
3. 选择 `PC, Mac & Linux Standalone`，目标为 `Windows`，架构为 `x86_64`。
4. Task 12 的已验证输出是 `Builds/P2FTask12/RouteC.exe`。`Builds/` 是本地产物目录，不进入版本控制。
5. 构建完成后检查 Console，要求没有脚本编译错误。

## 启动顺序

1. 在仓库根目录的 PowerShell A 启动 KCP 服务端并保持前台运行：

   ```powershell
   & '.\Project\NetworkServer\NetworkServer.exe' --transport kcp --kcp-interval-ms 10
   ```

2. 在 Unity Editor 打开 `SampleScene`。版本化场景当前序列化为 `RawUdp`；进入 Play 前临时把 `NetworkClient` 改成 `KcpUdp`，确认地址 `127.0.0.1`、端口 `8888`，再进入 Play Mode。它先连接并取得 player 0。临时 Inspector 修改不得保存到场景。
3. 在仓库根目录另开 PowerShell B，启动 Windows 客户端；它后连接并取得 player 1：

   ```powershell
   & '.\Project\Frame Synchronization\Builds\P2FTask12\RouteC.exe' --transport kcp --server-ip 127.0.0.1 --server-port 8888
   ```

4. 服务器等待两端均已连接后共同放行，双方从规范帧 0 开始。
5. 演示结束后依次退出 Windows 客户端、停止 Editor Play Mode，再在 PowerShell A 用 `Ctrl+C` 结束自己启动的前台服务端；不得按进程名批量结束进程。

不要先让单个客户端独立跑局，也不要同时启动第二个服务器实例。端口固定为 `127.0.0.1:8888`。

## P2-E 网络实验室

自动化实验可跳过交互选择，直接传入显式参数：

```powershell
& '.\Project\NetworkServer\NetworkServer.exe' --transport tcp --delay-ms 70 --jitter-ms 25 --reorder-percent 35 --reorder-delay-ms 60 --duplicate-percent 25 --loss-percent 30 --loss-recovery-ms 90 --seed 771 --decision-trace decision.jsonl --timing-trace timing.jsonl
```

`--loss-percent` 表示 TCP 式“丢失后恢复”，即增加恢复延迟并阻塞同发送方后续可靠消息，不会永久删除 8 字节输入。`decision.jsonl` 用于同种子复现，不能包含运行时间；`timing.jsonl` 用于统计入队、到期、实际发送和迟到量，不要求两次逐字节相同。

作品集录制固定 100ms 预测/回滚时使用 TCP。先在 PowerShell A 启动并保持服务端前台运行：

```powershell
& '.\Project\NetworkServer\NetworkServer.exe' --transport tcp --delay-ms 100
```

Editor 端在进入 Play 前临时选择 `Tcp` 并开启 `P2-E Network Diagnostics`，进入 Play 后再在 PowerShell B 启动 Windows 客户端：

```powershell
& '.\Project\Frame Synchronization\Builds\P2FTask12\RouteC.exe' --transport tcp --server-ip 127.0.0.1 --server-port 8888 -p2eDiagnostics
```

Editor 端也应选择 `Tcp` 并开启 `P2-E Network Diagnostics`。这里的 `100ms` 才是人工注入网络延迟。

要输出客户端诊断，在 Editor 的 `GameController` 勾选 `P2-E Network Diagnostics`，或给 Windows 构建增加 `-p2eDiagnostics`。日志前缀为 `[RouteC][P2E]`，包含 receive、drain、logic、rollback 和 remoteRender 事件。诊断默认关闭，且只读观察现有边界。

## P2-F KCP 默认实验通道

以下命令均从仓库根目录执行。PowerShell A 启动版本化服务端并保持前台运行：

```powershell
& '.\Project\NetworkServer\NetworkServer.exe' --transport kcp --kcp-interval-ms 10
```

Editor 端在进入 Play 前临时选择 `KcpUdp`，进入 Play 后再从 PowerShell B 启动 Windows 构建：

```powershell
& '.\Project\Frame Synchronization\Builds\P2FTask12\RouteC.exe' --transport kcp --server-ip 127.0.0.1 --server-port 8888
```

源码字段默认值是 `KcpUdp`，但版本化 `SampleScene` 当前序列化值仍是 `RawUdp`，会覆盖源码默认。因此 Editor 端必须在进入 Play 前临时选择 `KcpUdp`，服务器地址填 `127.0.0.1`、端口填 `8888`；两端必须连接同一 KCP 服务端。TCP 回退需要三端都显式选择 TCP。先在 PowerShell A 启动并保持 TCP 服务端前台运行：

```powershell
& '.\Project\NetworkServer\NetworkServer.exe' --transport tcp --delay-ms 0
```

Editor 端在进入 Play 前临时改为 `Tcp`，进入 Play 后再在 PowerShell B 启动 Windows 客户端：

```powershell
& '.\Project\Frame Synchronization\Builds\P2FTask12\RouteC.exe' --transport tcp --server-ip 127.0.0.1 --server-port 8888
```

KCP/TCP 演示结束后均按 Windows 客户端 → Editor Play Mode → PowerShell A 前台服务端的顺序停止；确认 8888 释放后再切换传输。

不要在同一局混用 KCP、TCP 或 Raw UDP 客户端。`10ms` 是 Task 11 基于当前 Route C 机器、Windows loopback 和预声明样本作出的工程选择，不是《街篮2》参数，也不保证在其他机器上始终最低延迟或最低 CPU；Task 12 fresh 样本中 5ms 的 p99 和 CPU 就低于 10ms。kcp2k 固定为 commit `66efda6686f649838d42f078fbaabf56ac449de4` 的纯 Core；许可见 [LICENSE](../../Project/Frame%20Synchronization/Assets/ThirdParty/kcp2k/LICENSE)，引入边界见 [NOTICE](../../Project/Frame%20Synchronization/Assets/ThirdParty/kcp2k/NOTICE.md)，Route C interval clamp 补丁见 [PATCHES](../../Project/Frame%20Synchronization/Assets/ThirdParty/kcp2k/PATCHES.md)。

`--kcp-interval-ms 10` 只控制 KCP 更新间隔，不注入 10ms 网络延迟。当前 KCP 服务端构造路径不消费 NetworkLab 的 `--delay-ms`，所以 KCP Unity 正常局的人工注入延迟是 `0ms`；不得把 TCP 100ms 结果写成 KCP 100ms 结果。

可在仓库根目录用真实 Socket 探针复验三条续连边界：

```powershell
& '.\Project\NetworkServer\KcpLiveSocketProbe.ps1' -ServerPath '.\Project\NetworkServer\NetworkServer.exe' -Scenario reconnect -IntervalMs 10
& '.\Project\NetworkServer\KcpLiveSocketProbe.ps1' -ServerPath '.\Project\NetworkServer\NetworkServer.exe' -Scenario grace-expired -IntervalMs 10
& '.\Project\NetworkServer\KcpLiveSocketProbe.ps1' -ServerPath '.\Project\NetworkServer\NetworkServer.exe' -Scenario history-expired -IntervalMs 10
```

`reconnect` 应显示客户端端点变化、Generation 增加、缺口输入回放且双方回到 Running；`grace-expired` 必须显式拒绝为 `ResumeGraceExpired`；`history-expired` 必须显式拒绝为 `UnsafeResume`。这些探针验证协议、真实 Socket、endpoint 变化和进程边界，不替代 Unity 双端画面与手感观察。

当前生产 `KcpUdpClientTransport` 没有供人工强制重建 UDP Socket/更换 endpoint 的注入入口。因此本次固定 Unity 双端只验 KCP 正常局的画面、平滑度与手感；endpoint 变化续连、grace/history 拒绝及异常终止由上面三条真实 Socket probe 输出供帅老大人工复核。当前不宣称“Unity 画面重连已验”；若要增加可交互的 Unity 重连演示入口，属于后续生产能力，必须另行设计和授权。

## Raw UDP 最小双端演示

Raw UDP 与上面的 TCP/KCP 实验是独立模式。在 PowerShell A 从仓库根目录启动服务端并保持前台运行：

```powershell
& '.\Project\NetworkServer\NetworkServer.exe' --transport raw-udp --raw-window 6
```

客户端逐字段接受命令行、Inspector、默认值三级覆盖。Editor 端在进入 Play 前临时选择 `RawUdp`，进入 Play 后再从 PowerShell B 启动 Windows 构建：

```powershell
& '.\Project\Frame Synchronization\Builds\P2FTask12\RouteC.exe' --transport raw-udp --server-ip 127.0.0.1 --server-port 8888
```

Editor 端服务器地址填 `127.0.0.1`、端口填 `8888`；也可用同样的 Unity 命令行参数启动 Editor。两端必须选择同一 Raw UDP 服务端。第二个客户端完成 Ready 屏障后，双方只在 `SessionStarted + Running` 时从 canonical frame 0 启动。演示结束后依次退出 Windows 客户端、停止 Editor Play Mode，再在 PowerShell A 用 `Ctrl+C` 结束自己启动的前台服务端。

默认窗口 N=6，稳定输入数据报为 88 字节。边界演示可把服务端改为 `--raw-window 16`，对应最大 168 字节数据报；客户端从 Welcome 接受该窗口，不需要另传客户端窗口参数。允许范围为 `1..16`。

Raw UDP 不可靠：没有 ACK、重传、拥塞控制、重连、断线恢复或端点迁移。窗口机会不是送达确认；对齐连续丢失 6 个机会会按协议终止，而不是静默等待或切换到 KCP。不要把 Raw UDP 的测试结果写成 KCP/P2-F 完成证据。

## 演示验收

1. 两端分别移动，确认本地响应及时、远端移动平滑。
2. 靠近自由球后按 `E`，确认只有一个确定持球者。
3. 持球时按住 `Space`，面板显示准备投篮；松开后篮球进入空中。
4. 在网络延迟环境下观察预测与纠正日志，确认回滚后双方 WorldHash 收敛。
   固定 100ms 时还应连续移动至少十秒并主动反向，记录 FIFO 单次排空量、Confirmed 增量、远端单帧位移和最大反向位移；“到达间隔约 33ms”不能单独代替画面验收。
5. 任一端按 `F9`，双方等待精彩片段收尾，并在同一终局帧进入回放。
6. 使用 `P`、`[`、`]`、`R` 验证播放控制；最后用 `Esc` 退出。

KCP 的 Unity 双端人工验收观察项为：两端从 canonical frame 0 只启动一次；持续移动、主动反向、持球、抢断/holder 转换、投篮/脱手时没有周期性停走、前跳或持续快进。三条 probe 则复核续连没有重复输入或时间线跳接，宽限/历史过期时明确终止且不伪装为成功恢复。2026-08-25，帅老大明确宣布 `P2-F 验收完成`；该结论与自动化、真实 Socket probe 共同完成 P2-F 验收，但 probe 仍不等于 Unity endpoint 迁移画面已经演示。

Task 12 的主要机器证据位于仓库根目录：`p2f-full.log`、`p2f-build-summary.json`、`p2f-server-build-summary.json`、`p2f-kcp-protocol.log`、`p2f-kcp-live-clean-summary.json`、`p2f-kcp-live-reconnect-summary.json`、`p2f-kcp-live-grace-expired-summary.json`、`p2f-kcp-live-history-expired-summary.json`、`p2f-kcp-interval-summary.json` 与 `p2f-versioned-kcp-smoke-summary.json`。证据文件名本身只证明机器证据存在；P2-F 人工结论来自帅老大 2026-08-25 的明确验收声明，不由文件名代替。

Raw UDP 人工验收还应分别使用 N=6 与 N=16：确认两端只启动一次、双方从帧 0 放行、移动/持球/投篮仍走同一玩法路径；随后停止任一端发送至少 3 秒，确认该局终止且不会自动重连。自动化 live 矩阵只能证明协议、数据和有界停止，不能代替画面平滑度与手感观察。

## 常见问题

- 客户端一直等待：检查服务器是否已启动，以及另一客户端是否已连接。
- 连接失败：确认没有残留服务器占用 8888 端口。
- 场景缺失：确认仓库包含 `Assets/Scenes/SampleScene.unity` 及其 `.meta`。
- 日志中的帧号不同：先区分网络帧与 Canonical Frame，再比较同一语义的帧。
- KCP 客户端被拒绝续连：先区分 `ResumeGraceExpired` 与 `UnsafeResume`；前者是宽限过期，后者是安全回放所需历史已不足，两者都不应自动拼接旧局。
