# P1-F 成果冻结与展示交付设计

日期：2026-08-10  
阶段名称：P1-F-8 成果冻结与展示交付  
工作目录：`E:\帧同步_RouteC`

## 目标

把 P0 至 P1-F-1 的累计未提交成果整理成可复现、可审查、可构建、可录屏和可讲解的 Unity 帧同步作品集，同时关闭冻结审查发现的功能与版本管理缺口。

本阶段完成后，新工作区应能仅依靠版本库内容导入 Unity 2022.3.62f2、打开 `SampleScene`、运行自动测试并构建 Windows 客户端。两端应在同一纠正后世界帧结束正式比赛，再进入只读精彩回放。

## 当前基线

- 分支：`delivery/route-c`
- HEAD：`37260b437260c7712c658d5d0e05cbdd183accfb`
- 批准前状态：86 条
- 批准前状态指纹：`E3A439986EEB5599E68D296B10EAE2B22E378526BE48A5637812FFA62645583C`
- 原仓库 `E:\帧同步` 保持只读。
- 禁止自动 commit、merge 或 push；`git push` 始终由帅老大执行。

## 范围

### 纳入范围

1. Unity `.meta` 与启动场景的可复现版本管理。
2. F9 赛后切换使用共同、稳定、纠正后的正式世界终止帧。
3. 按住 Space 的纯本地投篮准备提示，以及篮球状态和持球者显示。
4. 捡球预测失败、不同预测持球者收敛和投篮纠正重演的专项测试。
5. 一文件一个 public 类型的明确违规修复。
6. 对实际模块目录规则进行文字对齐，不在冻结期搬迁大量源码。
7. README、架构图、阶段地图、Windows 构建说明、3～5 分钟演示脚本和面试讲解稿。
8. 完整自动验证、Unity 人工验证和新工作区导入验证。

### 明确不做

- 不增加比分、计时、胜负、AI、犯规或完整比赛流程。
- 不增加动态网络缓冲、录像持久化、分享、慢动作、回放摄像机或镜头评分。
- 不改变现有 8 字节网络输入协议。
- 不大规模拆分 `GameController`，只记录其架构债务。
- 不删除或重建未知成果，不处理 `E:\帧同步` 的文件。
- 不把 Windows 客户端构建目录或测试结果文件提交到版本库。

## 方案选择

采用“可复现源码冻结”。不采用只追踪 52 个新资产 `.meta` 的最小修补，因为旧资产 GUID 仍会在新工作区重建；也不采用只交付 Windows 压缩包的产物方案，因为它无法证明源码可审查和可复现。

## 版本库与 Unity 资产

### `.meta` 规则

移除 `.gitignore` 中的全局 `*.meta` 忽略规则。纳入所有已跟踪资产、新增资产及其祖先目录对应的 `.meta`。不得纳入没有对应受控资产的孤立 `.meta`。

当前磁盘存在 409 个 `.meta`，其中 406 个位于 `Assets/Scenes/` 之外。实施时根据“目标资产已跟踪或将在本阶段纳入”重新计算精确集合，不能仅按总数盲目添加。

### 启动场景

`ProjectSettings/EditorBuildSettings.asset` 已引用 `Assets/Scenes/SampleScene.unity`，因此需要从 `Assets/Scenes/` 的整体忽略中精确放行：

- `Assets/Scenes/SampleScene.unity`
- `Assets/Scenes/SampleScene.unity.meta`

继续忽略并保留磁盘上的 `Assets/Scenes/Tests/Tests.asmdef` 及对应 `.meta`，不删除、不提交；它是需要另行判断的历史遗留物，不属于本次交付。

### 生成物

- `Library/`、`Temp/`、`Logs/`、`obj/`、`.idea/`、Unity 生成的工程文件、测试结果和 Windows 客户端构建继续忽略。
- `Project/NetworkServer/NetworkServer.exe` 是既有受控工具，继续与 `NetworkServer.cs` 和 `NetworkServerBarrierTests.ps1` 同组保留。

## 共同赛后终止帧

### 问题

两个客户端会识别同一个稳定 F9 输入帧，但当前实现会在各自扫描到该输入时暂停各自的当前逻辑头。若两端逻辑头不同，正式世界可能冻结在不同帧。

### 终止帧规则

`HighlightReplayRecorder` 暴露只读的共同终止帧：

- 没有待收尾片段时，终止帧等于首次稳定 F9 请求帧。
- 有待收尾片段时，终止帧等于最后一个待收尾片段完成的稳定帧。
- 终止帧只能设置一次，必须来自按顺序处理的稳定、纠正后帧。

当 recorder 可以进入赛后阶段时，`GameController` 必须先从 `PredictionSystem` 取得并恢复终止帧的完整世界快照，再暂停 `FrameEngine`、清空本地瞬时动作并创建回放控制器。恢复失败时不得部分切换阶段，应保留 Playing 状态并输出包含终止帧的明确错误。

该流程恢复正式世界状态，但不重置联网逻辑帧编号，不发送新操作，不重新触发预测或回滚。回放继续只写表现对象。

## 本地投篮准备与状态显示

`GameController` 在 Playing 阶段按渲染更新计算纯表现状态：本地玩家实际持球且 Space 正在按住时，设置 `IsPreparingShot`。它不进入 `FrameInput`、快照、回滚世界或 `WorldHash`。

`RuntimeControlOverlay` 在比赛阶段显示：

- 当前篮球状态。
- 持球者编号；无持球者时显示“自由球”。
- 本地持球且按住 Space 时显示“准备投篮，松开出手”。

暂停、失去球权、进入赛后或重置后提示必须立即消失。

## 模块与规范对齐

拆出独立 `HighlightPresentationSample.cs`，让它与 `HighlightSnapshotInterpolator.cs` 各自只声明一个 public 类型。

两份 `AGENTS.md` 的目录说明同步明确：

- `FrameSync/`：通用帧时间线、定点数、快照、预测、回滚协调和同步帧缓冲。
- `Gameplay/`：确定性篮球领域模拟、球权、投篮和精彩事件识别。
- `Input/`：Unity 渲染更新到逻辑帧之间的本地输入边沿捕获。
- `Presentation/`：不进入同步世界的插值、纠正、回放采样和运行时操作面板。

不移动现有已批准模块，避免冻结期扩大 Unity GUID、命名空间和引用风险。

## 测试设计

### F9 终止帧

- 无待收尾片段时，共同终止帧等于稳定 F9 帧。
- 有待收尾片段时，在片段结束前不能进入赛后，结束后共同终止帧等于片段尾帧。
- 两个 recorder 接收相同稳定帧，但调用方逻辑头不同，仍得到同一终止帧和同一终止快照 Hash。
- 终止快照缺失时不暂停、不切换阶段、不部分修改世界。
- 重复 F9 不改变首次请求帧或共同终止帧。

### 篮球预测与回滚

- 预测捡球成功、权威输入无捡球时，重演后篮球恢复 Free，所有玩家 `hasBall` 为 false。
- 两端曾预测不同持球者时，同一权威输入重演后持球者、位置、状态和 `WorldHash` 一致。
- 投篮边沿纠正后，轨迹、状态、球权和最终 Hash 与权威世界一致。

### 表现状态

- 只有本地实际持球且 Space 按住时显示准备提示。
- 松开、失去球权、暂停、重置和赛后阶段均隐藏提示。
- 状态文案正确显示 Free、Held、Airborne、Scored 和持球者。
- 表现状态不改变输入 raw、快照或 `WorldHash`。

### 资产与交付

- 每个受控 Unity 资产都有受控 `.meta`，且不存在提交集合内的孤立 `.meta`。
- 新工作区导入后脚本 GUID、场景引用和三个程序集保持稳定。
- `SampleScene` 位于 Build Settings 且可以构建 Windows x86_64 客户端。

## 交付文档

- `README.md`：项目定位、核心能力、按键、快速启动、测试证据、限制和文档导航。
- `docs/architecture/route-c-frame-sync.md`：输入、预测、模拟、快照、回滚、表现缓冲和精彩回放数据流。
- `docs/roadmap/p1f-delivery-status.md`：P0 至 P1-F-8 的实际完成度与剩余项。
- `docs/demo/windows-build-and-demo.md`：服务端、Editor、Windows 客户端双端启动与验收步骤。
- `docs/demo/demo-script-3-5min.md`：按时间段安排最小篮球闭环、预测回滚、Hash 和精彩回放展示。
- `docs/interview/frame-sync-project-talk.md`：项目背景、关键取舍、故障案例、测试证据和常见追问。

## 验证顺序

1. 新增专项测试并确认 RED。
2. 完成最小实现并确认定向 GREEN。
3. 编译 Runtime、Editor、EditMode 三个程序集。
4. 运行全部非 Unity 原生回归与服务端屏障/分片测试。
5. 运行 `git diff --check`，复核 Route C 与原仓库状态。
6. 在独立目录验证版本化资产集合和 Unity 新导入。
7. 由帅老大在 Unity Test Runner 运行全部 EditMode 测试。
8. 由帅老大构建 Windows x86_64 客户端，并在 0ms 与 100ms 服务端模式下完成 Editor + Build 双端验收。
9. 验证两端共同终止帧、最终世界 Hash、精彩片段范围和回放操作一致。
10. 录制 3～5 分钟演示视频。

## 失败处理

- 任何资产身份或场景引用不确定时停止添加并报告，不删除原文件。
- 共同终止快照缺失时保持 Playing，不进入不完整赛后状态。
- Unity 正在占用 Route C 时不并发启动无头 Unity；改由帅老大关闭或执行编辑器验证。
- 自动验证出现真实失败时转入系统化诊断，不用绕过、跳过或放宽断言完成冻结。

## 完成标准

- 冻结审查中的功能、测试和资产阻塞全部关闭。
- 新工作区可导入、打开场景、运行测试并构建。
- 两端从同一纠正后正式世界帧进入赛后回放。
- Windows 构建、README、架构图、演示脚本和面试讲解稿齐备。
- 所有验证证据记录清楚，工作树无未知新增物。
- 未自动 commit、merge 或 push。
