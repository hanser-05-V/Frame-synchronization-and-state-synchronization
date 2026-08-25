# P2-E 低延迟 Confirmed 播放验收记录

> 日期：2026-08-17
>
> 工作区：`E:\帧同步_RouteC`
>
> 分支：`delivery/route-c`
>
> 结论：P2-E 已完成 Route C 验收；本次未进入 P2-F

## 1. 验收边界

本次只验收低延迟 Confirmed 表现播放订正。它不改变 8 字节网络协议、服务端转发语义、FrameInputLedger、Confirmed/Predicted 确定性世界、快照规范状态、WorldHash 或篮球玩法，也不包含 P2-F 轻量 ECS。

锁定参数为：默认 `TargetReadyBacklog = 0`，水位 `1` 可选，`MaxReadyBacklog = 8`，追赶增益 `0.15`，最大倍速 `1.5`，平滑时间 `0.10s`，进入/退出阈值 `0.25/0.10`，Overflow 纠正最长 `0.20s`。这些数值仅是 Route C 初始实验参数，不代表《街篮2》真实参数。

## 2. 自动化证据

| 门禁 | 结果 | 证据 |
|---|---|---|
| P2-E focused EditMode | `152/152`，失败 `0`，跳过 `0` | `E:\帧同步_RouteC\TestResults-p2e-low-latency-focused.xml` |
| 全量 EditMode | `410/410`，失败 `0`，跳过 `0` | `E:\帧同步_RouteC\TestResults-p2e-low-latency-full.xml` |
| 独立 Unity 导入/编译 | 返回码 `0`，无 C# error/warning | `E:\帧同步_RouteC\p2e-low-latency-independent-compile.log` |
| Windows x64 构建 | `Build Finished, Result: Success` | `E:\帧同步_RouteC\p2e-low-latency-build.log` |
| 服务端实验室 | PASS | `E:\帧同步_RouteC\p2e-server-lab-tests.log` |
| 服务端 barrier/双向分片 frame-0 | PASS | `E:\帧同步_RouteC\p2e-server-barrier.log` |

收口当天尝试重新执行全量 EditMode 三次，但 Unity 授权客户端均在发现测试用例之前拒绝 IPC 连接，并以内部返回码 `199` 退出；因此没有生成新的测试 XML，也不能把这三次启动记作测试通过或失败。对应环境日志为：

- `E:\帧同步_RouteC\p2e-low-latency-acceptance-final.log`
- `E:\帧同步_RouteC\p2e-low-latency-acceptance-final2.log`
- `E:\帧同步_RouteC\p2e-low-latency-acceptance-final3.log`

只读时序核对显示，最近一次已完成的全量 `410/410` XML 写入后，当前 Unity `Assets`、`Packages` 和 `ProjectSettings` 中没有任何文件更新；本次收口只修改文档。因此 `410/410` 仍覆盖当前代码与 Unity 配置，但本记录同时保留“2026-08-17 因授权 IPC 环境问题未能重新执行”的限制，不把旧 XML 冒充当天新跑结果。

独立验证副本保留在：

`C:\Users\Administrator\AppData\Local\Temp\route-c-p2e-low-latency-86670ed0d50940d0bdb6f2860e0d76e9`

其中 Windows 玩家为：

`C:\Users\Administrator\AppData\Local\Temp\route-c-p2e-low-latency-86670ed0d50940d0bdb6f2860e0d76e9\Builds\Windows\FrameSyncDemo.exe`

当前机器只有 .NET Runtime，没有 .NET SDK，因此原定 `dotnet build Project/NetworkServer/NetworkServer.csproj` 命令不可用。补充门禁使用 Unity 随附的 Mono/Roslyn，以 C# 7.3 编译该 csproj 的相同源文件并成功生成临时 `NetworkServer.exe`。这项结果是等价源文件编译证据，不冒充原 `dotnet build` 命令通过。

## 3. 覆盖的行为矩阵

自动化覆盖：30/60/144 FPS 稳态追踪；30 秒无周期性速度脉冲；目标水位 0/1；待播容量 0/1/8/9；Alpha 0/0.5/1；Underflow、Overflow、长渲染帧、快照缺失、PresentationFault 与回滚；远端/本地持球、释放和 holder 转换；Actual 到首次可见、GameController 接线、诊断只读性及 WorldHash 排除。

最终人工验收由帅老大执行。报告的通过范围为固定 `100ms` 单向注入延迟、60/144 FPS 双端、每场至少 10 秒，覆盖启动、持续单向移动、停止、八方向变化、持球移动、抢断/holder 转换及投篮/脱手。通过判据包括：本地立即响应；默认水位 `0` 不增加完整逻辑帧等待；无周期性停走、前跳、回滚后退或持续快进；人球关系正确；稳定固定 100ms 场景无意外 PresentationFault/Overflow。

人工原始诊断文件及 Actual-to-visible p50/p95、最大水位、倍速切换、最长连续追赶、RenderFrameDrop、最大/反向位移等具体数值未随最终结论提供。因此本记录只保存帅老大的最终“通过”结论，不虚构缺失的数值，也不声称完成了参数真实性复刻。

## 4. 最终裁定

P2-E 自动化、独立编译、Windows 构建、服务端 lab/barrier 门禁通过；固定 100ms 的远端表现由帅老大完成最终人工验收并报告通过。P2-E 至此完成 Route C 验收。

P2-F 尚未实现，本次没有开始、修改或验收 P2-F，也没有执行 commit、merge 或 push。
