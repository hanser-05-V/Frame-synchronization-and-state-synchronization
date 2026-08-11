# Route C 帧同步篮球 Demo

## 项目简介

两名客户端通过 8 字节输入帧协议驱动同一个确定性篮球世界。客户端执行输入预测、完整世界快照、回滚重演、WorldHash 对比、远端表现缓冲，以及由稳定帧生成的赛后精彩片段回放。

## 核心能力

- 定点数与整数帧驱动的确定性模拟
- `Free → Held → Airborne / Scored → Free` 篮球状态闭环
- 零输入预测纠错与完整世界回滚重演
- Canonical Frame 与 FNV-1a 64 位 WorldHash
- 本地低延迟、远端固定一帧额外表现缓冲
- 从已纠正稳定快照生成赛后精彩片段
- 双端共享终局帧，进入回放前恢复到相同世界状态

## 环境

- Unity `2022.3.62f2`
- Windows x86_64
- .NET Framework 4.8 转发服务器

## 快速开始

按照 [Windows 构建与双端演示](docs/demo/windows-build-and-demo.md) 操作：先启动服务器，再启动 Unity Editor 客户端和 Windows 客户端。交付场景为 `Assets/Scenes/SampleScene.unity`。

## 操作

- `WASD`：移动本机球员
- `E`：靠近自由球时拾取
- `Space`（松开）：持球时投篮
- `F9`：同步请求结束比赛，待精彩片段收尾后进入回放
- `P`：播放或暂停精彩片段
- `[` / `]`：上一段或下一段
- `R`：重播当前片段
- `Esc`：退出

## 架构

模块职责、帧数据流和确定性边界见 [Route C 帧同步架构](docs/architecture/route-c-frame-sync.md)。

## 验证证据

- Runtime、Editor、EditMode 三套程序集已通过离线编译。
- 25 个 EditMode 夹具的离线回归结果为 `231 passed / 0 failed / 12 skipped`；跳过项均依赖 Unity 原生日志接口，需在 Unity Test Runner 内复核。
- Unity 2022.3.62f2 EditMode 全量结果为 `243 passed / 0 failed / 0 skipped`，测试日志无编译错误。
- 服务端屏障测试通过：首个客户端不会提前放行，双向分片 frame-0 均完整转发。
- 纯源码导出包含 928 个文件；395 个 Unity 资产零缺失 `.meta`，且不包含 Library、Temp、Logs、obj、Builds 或遗留 `Assets/Scenes/Tests`。
- Windows x86_64 构建成功，临时客户端 SHA-256 为 `5CC71BF032F440AC46A5D24BB4101050A37001DD8F1B8325AEC1067EA0D1FF52`。
- 本次冻结后的双端演示已通过：零延迟与 100ms 延迟模式均完成移动、拾取、投篮、回滚收敛、F9 共同结束及精彩回放验收。
- 100ms 下远端球员在持续输入变化时可能出现一次短暂回退纠正；本地响应和最终 Hash 收敛正常，登记为后续表现层优化项。
- `NetworkServer.exe` 作为演示必需产物纳入交付，其源码和屏障测试脚本同时保留。

## 项目边界

当前版本不包含比分、倒计时、AI、犯规、动态缓冲、录像持久化和专用回放摄像机。它聚焦于最小篮球闭环、预测回滚正确性和可讲解的双端演示链路。

## 演示与面试材料

- [3–5 分钟演示脚本](docs/demo/demo-script-3-5min.md)
- [面试讲解提纲](docs/interview/frame-sync-project-talk.md)
- [P1-F 交付状态](docs/roadmap/p1f-delivery-status.md)
