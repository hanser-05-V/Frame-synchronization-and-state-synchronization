# P2-B FrameInputLedger 与确认帧头实施计划

> **执行前置：** 本文只规划 P2-B。必须等帅老大明确批准后，才能修改运行时代码或测试。实施时按任务使用 TDD；不得提前实现 P2-C 双世界、P2-D ViewWorldBuilder 或 ECS。

**目标：** 用唯一 `FrameInputLedger` 统一双方逐帧输入事实、既有预测语义、连续 `ConfirmedThroughFrame` 与最早预测失配，使正常推进、迟到 Actual、回滚重演和稳定帧消费者不再各自保存或比较输入。

**推荐架构：** 所有输入在进入 Ledger 前只映射一次到 canonical frame；Ledger 以 `(canonicalFrame, playerIndex)` 为唯一键保存 `Missing / Predicted / Actual`。网络层只交付包，`FrameBuffer` 只记录当次 Step 使用值，`PredictionSystem` 只保留世界快照，`FrameReplaySystem` 只消费 Ledger 预检生成的重演计划。P2-B 仍只有 P2-A 的单一逻辑世界。

**技术栈：** Unity 2022.3.62f2、C#、NUnit EditMode、现有 8 字节输入协议与 `DeterministicWorld.Step`。

---

## 1. 工作区保护与已验证基线

- 分支必须仍为 `delivery/route-c`，基线 HEAD 必须仍为 `2bbb1193b81893b66ded937f52b8ad02197b43a4`。
- 当前工作区含未提交的 P1-G、P2-A 代码、测试、文档和日志，全部视为已有成果；禁止 reset、checkout 回退、clean、删除未跟踪文件、自动 commit/merge/push。
- `TestResults-p2a-complete.xml` 当前证据为 `268/268 passed`；`p2a-complete-compile.log` 以 `Exiting batchmode successfully now!` 和 return code 0 结束。
- 规划审计时存在 Unity PID 31920，但未发现本项目 `Temp` 锁文件；因进程命令行无权限读取，是否占用本项目尚未验证。实施前必须重新检查；若项目被占用，不得同时启动 batchmode Unity。
- 每个任务前后都运行下列只读保护检查并比较；不清理任何差异：

```powershell
git branch --show-current
git rev-parse HEAD
git status --short
git diff --check
Get-Process -Name Unity,UnityHub,UnityCrashHandler64 -ErrorAction SilentlyContinue |
    Select-Object Id,ProcessName,StartTime
Get-ChildItem -LiteralPath 'E:\帧同步_RouteC\Project\Frame Synchronization\Temp' -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'lock|UnityLockfile' }
```

## 2. 当前行为与代码证据

### 2.1 帧域与输入路径

- `FrameEngine.ExecuteOneFrame` 先取得本地逻辑 `frameID`，再请求输入并写 `FrameBuffer`；`GameController.ReadInputs` 的 `CurrentFrame - 1` 就是同一待执行帧：`Project/Frame Synchronization/Assets/Scripts/FrameSync/FrameEngine.cs:120`、`Project/Frame Synchronization/Assets/Scripts/GameController.cs:192`。
- 当前联网会话共同从帧 0 起跑，`NetworkFrameTimeline.RemoteFrameOffset == 0` 且 `RemoteFrameForLocal` 为 identity：`Project/Frame Synchronization/Assets/Scripts/FrameSync/NetworkFrameTimeline.cs:4`。
- 概念仍须区分：包内键是发送端 local/接收端 remote frame；预测历史与 `FrameBuffer` 键是接收端 local frame；canonical 映射是 Player0 `local`、Player1 `local - offset`：`Project/Frame Synchronization/Assets/Scripts/FrameSync/CanonicalFrame.cs:10`。当前仅因 offset=0 三者数值相同。
- 本地瞬时动作按 render frame 捕获一次，首个逻辑帧消费后清空；持续输入每逻辑帧重读：`Project/Frame Synchronization/Assets/Scripts/Input/LocalFrameActionBuffer.cs:10`、`Project/Frame Synchronization/Assets/Scripts/GameController.cs:591`。

### 2.2 当前多事实源与重复语义

- `NetworkClient` 以 remote frame 保存 Actual；同帧包无条件字典覆盖，所以同值重复无感，冲突重复为静默 last-write-wins：`Project/Frame Synchronization/Assets/Scripts/Network/NetworkClient.cs:125`。
- `PredictionSystem` 只保存远端的 `_lastRemoteInput` 与 local-frame `PredictionEntry`，同时负责查 Actual、预测、逐帧比较、最早失配和清历史：`Project/Frame Synchronization/Assets/Scripts/FrameSync/PredictionSystem.cs:17`、`:48`。
- `FrameBuffer` 每帧只保存 `FrameInput[]`，无法区分本地 Actual、远端 Actual 和远端 Predicted：`Project/Frame Synchronization/Assets/Scripts/FrameSync/FrameBuffer.cs:14`。
- `FrameReplaySystem` 又从 `FrameBuffer + NetworkClient dict + correctedRaw` 拼输入，并在回滚后缀内再次实现 `ToPredictionInput`：`Project/Frame Synchronization/Assets/Scripts/Gameplay/FrameReplaySystem.cs:121`。
- 因而冲突重复可能静默改写后续回滚读取的远端事实；已经验证并删除预测历史的旧帧还可能永远不再比较。这是 P2-B 必须明确修正的错误语义，不是协议改造。

### 2.3 预测、确认和历史边界

- Actual 原 raw 完整保留；预测只通过 `ToPredictionInput()` 清 `0x61`，即 `shootReleased`、`pickupPressed`、`endMatchPressed`，保留移动、pass/steal/block/sprint 和高位：`Project/Frame Synchronization/Assets/Scripts/FrameSync/FrameInput.cs:25`、`:111`；现有契约在 `FrameInputTests.cs:39` 与 `PredictionSystemResolveRemoteTests.cs:9`。
- 当前最早失配由遍历历史 predicted 条目并显式取最小 frame 得出；GameController 先聚合请求，LateUpdate 才回滚，失败会重试：`PredictionSystem.cs:60`、`RollbackRequestBuffer.cs:14`、`GameController.cs:961`。
- 当前不存在真正的 `ConfirmedThroughFrame`。`LatestDrainedRemoteFrameID` 与 `StableRemoteFrameGate` 只取最大水位，不验证从 0 开始逐帧无缺口：`GameController.cs:206`、`StableRemoteFrameGate.cs:7`。它们不得继续被描述为输入确认。
- 当前窗口互不一致：远端 Actual 约保留 120 帧、prediction history 约 120 帧、`FrameBuffer` 256 槽、世界快照默认 512 槽：`GameController.cs:989`、`PredictionSystem.cs:148`、`FrameBuffer.cs:12`、`FrameSnapshot.cs:176`。
- 回滚已具备重要安全契约：先预检所有输入，再 Restore/reset/Step；缺输入时不得部分修改世界：`FrameReplaySystem.cs:91`、`:106`，对应 `FrameReplaySystemTests.cs:398`、`:430`。

## 3. 精确术语、数据模型与不变量

### 3.1 术语

- **canonical frame：** Ledger 唯一接受的帧域；local/remote frame 必须由调用者先通过现有映射转换。
- **历史容量：** Ledger 最多保留多少完整 canonical frame 的事实；本计划为 256，与当前可重演输入窗口对齐。
- **预测跨度：** 当前执行帧领先 `ConfirmedThroughFrame` 的距离；P2-B 不新增硬上限，容量耗尽时显式失败，不把它与容量混为一谈。
- **`ConfirmedThroughFrame`：** 在 Ledger 健康期间，从 `StartFrame=0` 起双方 slot 都已接受首个 Actual 的最大连续前缀；初值 `-1`，只增不减。若后来出现冲突重复，数值保留为故障发生前的诊断事实，但 `HasIntegrityFault=true` 时禁止继续推进、消费或发布该确认头。
- **稳定世界发布水位：** P1-G 在回滚成功后才能发布给精彩回放的水位；它可阶段性落后输入确认头，但不能自行计算另一套确认事实。
- **`FirstRetainedFrame`：** Ledger 仍可逐格查询的历史下界；它不是确认头。

### 3.2 `FrameInputLedger` 顶层模型

新增一个顶层 public `FrameInputLedger`；为遵守“一文件一个 public 类型”，以下枚举/值对象作为其嵌套只读类型：

```csharp
public sealed class FrameInputLedger
{
    public enum InputState { Missing, Predicted, Actual }
    public enum ActualDisposition
    {
        Accepted,
        PredictionMatched,
        PredictionMismatched,
        IdempotentDuplicate,
        ConflictingDuplicate,
        HistoryUnavailable,
        CapacityExceeded
    }

    public int StartFrame { get; }                 // 0
    public int ConfirmedThroughFrame { get; }      // 初始 -1，单调
    public int FirstRetainedFrame { get; }
    public int LastObservedFrame { get; }
    public bool HasIntegrityFault { get; }

    public ActualArrival RecordActual(
        int canonicalFrame, int playerIndex, FrameInput input);
    public ResolvedFrame ResolveForSimulation(int canonicalFrame);
    public bool TryGetRecord(
        int canonicalFrame, int playerIndex, out InputRecord record);
    public bool TryGetActualFrame(
        int canonicalFrame, out ResolvedFrame inputs);
    public bool TryGetEarliestMismatch(out InputMismatch mismatch);
    public ReplayPlanResult TryBuildReplayPlan(
        int fromFrame, int throughFrame, out ReplayInputPlan plan);
    public void CommitReplay(in ReplayInputPlan plan);
    public PruneResult TryPruneBefore(int firstFrameToKeep);
}
```

- 内部按完整 canonical frame 保存两个固定玩家 slot；slot 包含 `FrameInput value + InputState state`。`Missing` 的 value 必须为 default。
- `ResolvedFrame` 同时返回两名玩家的 value 与来源状态，禁止仅返回裸数组后丢失来源。
- `InputMismatch` 固定保存 canonical frame、playerIndex、predicted raw、actual raw 和 generation；最早顺序先 frame、同帧再 playerIndex。
- `ReplayInputPlan` 是预检完成的不可变帧序列和 generation token；Replay 不得自行查询网络、猜测预测或改写 Ledger。
- 淘汰完整帧时为每名玩家保存“`FirstRetainedFrame` 之前最近 Actual 的预测种子摘要”，保证保留区间首帧重演仍与原预测语义一致；摘要不是可查询历史事实。

### 3.3 唯一状态转换和输入裁决

1. `Missing -> Predicted`：只由当前帧 `ResolveForSimulation` 或重演计划生成；冷启动为 default，否则取同玩家严格早于该帧的最近 Actual，再调用现有 `ToPredictionInput()`。
2. `Missing -> Actual`：返回 `Accepted`。
3. `Predicted -> Actual` 且 raw 相同：返回 `PredictionMatched`，不产生 mismatch。
4. `Predicted -> Actual` 且 raw 不同：返回 `PredictionMismatched`，保存唯一 mismatch；slot 最终保存 Actual。
5. `Actual -> 相同 Actual`：返回 `IdempotentDuplicate`，完全幂等，不产生第二事件。
6. `Actual -> 不同 Actual`：返回 `ConflictingDuplicate`；首个 Actual 仍是 Ledger 唯一保存值，禁止覆盖；Ledger 锁存全局 integrity fault。`ConfirmedThroughFrame` 数值不倒退但立即失去可消费/可发布资格，且不得再推进；GameController 暂停联网逻辑并显式报错。冲突是完整性故障，不把首个 Actual 改写成第二份“真值”。
7. `< FirstRetainedFrame` 的任何迟到包或查询：返回 `HistoryUnavailable`，不得伪装成 Missing、不得重建旧格、不得改动确认头。
8. 任意重复 Resolve 必须返回同值且不重复制造 mismatch；预测种子只能来自 preceding Actual，不能链式把瞬时动作或到达顺序污染到后缀。

### 3.4 确认头、乱序和历史不变量

1. 每次成功 `RecordActual` 后，只从 `ConfirmedThroughFrame + 1` 向前扫描；该帧两名玩家均为无冲突 Actual 才继续，否则遇首个洞立即停止。
2. 高帧先到允许落账，但最大到达帧、预测命中、`FrameBuffer` 有值、WorldHash 收敛、固定播放延迟或回滚成功均不能越洞推进确认头。
3. 输入确认头可在世界回滚前推进；它只证明双方输入事实齐全，不代表 P2-C 的 Confirmed World 已存在。
4. Ledger 采用 256 个完整 frame 的有界保留窗口；只有 `frame <= ConfirmedThroughFrame`、低于调用者提交的安全下界、无 pending/in-flight mismatch、且 Ledger 健康时，完整帧才能淘汰。
5. 窗口将覆盖未确认或仍需回滚的事实时返回 `CapacityExceeded`，暂停逻辑推进并报告；禁止静默环形覆盖。
6. 淘汰后 `ConfirmedThroughFrame` 保持单调，`FirstRetainedFrame` 单调；最早 mismatch 不得指向已淘汰帧。
7. 回滚范围、预测种子或恢复所需事实落到保留边界前时，返回 `HistoryUnavailable`，并在改动世界或 Ledger 前失败。唯一例外是：Ledger 仍完整保留 `StartFrame=0` 到目标帧的 plan 且 `resetWorld` 可用，此时允许无快照 reset 后从 0 完整重演。
8. 世界快照仍由 `PredictionSystem` 的 `SnapshotBuffer` 保留 512 槽；输入容量与快照容量是不同概念，不合并、不宣称一致。

### 3.5 淘汰所有权与可执行安全下界

- `GameController` 是唯一 prune 调用者，Ledger 是最终裁决者；`NetworkClient`、`PredictionSystem`、`FrameBuffer` 和表现/回放组件都不得自行删除输入事实。
- prune 只在 LateUpdate 中按以下顺序尝试：先处理 pending/in-flight rollback，再处理截至已发布稳定水位的 highlight 帧，最后计算 `firstFrameToKeep` 并调用 `TryPruneBefore`；发生 rollback/fault 或任一消费者失败时本轮不 prune。
- `firstFrameToKeep` 取所有仍需输入的最小 canonical frame：① in-flight `ReplayInputPlan.FromFrame`；② earliest pending mismatch 对应的安全恢复快照之后第一帧（由 `PredictionSystem.TryGetWorldSnapshot` 向前查得；无安全快照则为 0）；③ `_highlightStableCursor.LastProcessedFrame + 1`；④任何显式注册的当前消费者游标。没有 pending/in-flight rollback 时该项不限制。
- GameController 只提交候选 floor；Ledger 还必须以内部 pending/in-flight mismatch、generation 和 integrity fault 再校验并拒绝不安全 prune。候选 floor 不得绕过 Ledger 状态。
- 若 highlight 游标落后将阻止容量释放，先按现有逐帧规则消费；只有既有快照/输入已经过期时才沿用显式 `RebaseAt` 并记录丢失区间，不能静默跳过。仍无法安全释放时返回 `CapacityExceeded` 并暂停，不覆盖事实。

## 4. 最终 File map

### 新增

- `Project/Frame Synchronization/Assets/Scripts/FrameSync/FrameInputLedger.cs` + `.meta`：唯一输入事实、预测、确认头、失配、重演计划与安全淘汰。
- `Project/Frame Synchronization/Assets/Tests/EditMode/FrameInputLedgerTests.cs` + `.meta`：状态转换、确认、重复/冲突、预测和历史边界。
- `Project/Frame Synchronization/Assets/Tests/EditMode/FrameInputLedgerReplayTests.cs` + `.meta`：最早失配、重演计划、提交/失败原子性与后缀重建。

### 修改

- `Project/Frame Synchronization/Assets/Scripts/FrameSync/PredictionSystem.cs`：删除输入 history/last input；仅保留 P2-A 世界快照；迁移期旧输入 API 只能立即转发同一 Ledger，最终移除无调用门面。
- `Project/Frame Synchronization/Assets/Scripts/Network/NetworkClient.cs`：保留 socket、8 字节协议、接收线程、FIFO 与到达诊断；移除 remote Actual 字典及 120 帧事实清理。
- `Project/Frame Synchronization/Assets/Scripts/GameController.cs`：唯一完成帧映射、双方 Actual 登记、当前帧解析、最早 mismatch 调度、安全淘汰 floor、稳定水位 staging 和故障暂停。
- `Project/Frame Synchronization/Assets/Scripts/Gameplay/FrameReplaySystem.cs`：保留快照选择、恢复/reset、唯一 Step 与快照重建；改为只消费 `ReplayInputPlan`。
- `Project/Frame Synchronization/Assets/Scripts/FrameSync/FrameBuffer.cs`：仅更新职责注释为“当次已执行输入日志/调试环”，不再作为 Actual/Predicted/Confirmed 权威事实。
- `Project/Frame Synchronization/Assets/Tests/EditMode/PredictionSystemResolveRemoteTests.cs`：先迁移旧行为契约到 Ledger；临时门面删除后移除该 fixture 与 `.meta`。
- `Project/Frame Synchronization/Assets/Tests/EditMode/FrameReplaySystemTests.cs`：改用 Ledger，保留恢复点、缺输入零部分修改、瞬时动作、后续错误和 Hash 收敛契约。
- `Project/Frame Synchronization/Assets/Tests/EditMode/PredictionSystemSnapshotTests.cs`：保持原测试，证明快照职责未被 Ledger 污染。
- `Project/Frame Synchronization/Assets/Tests/EditMode/StableRemoteFrameGateTests.cs`：补充“只延迟发布 Ledger 确认头、不跨洞自算确认”的接线契约。
- `docs/architecture/route-c-frame-sync.md`、`docs/architecture/streetball2-minimal-frame-sync-v2.md`、`Project/路线规划_街篮帧同步最小实现.md`：仅在所有验收通过后同步 P2-B 已验证事实，继续明确 P2-C/P2-D/ECS 未实现。

## 5. TDD 实施顺序

### Task 1：独立建立 Ledger 核心

**Files:** 新增 `FrameInputLedger.cs`、`FrameInputLedgerTests.cs`；复用 `FrameInput.ToPredictionInput()`，不改输入位语义。

- [ ] **RED 1：** 初始 `ConfirmedThrough=-1`；frame0 单玩家 Actual 不推进；双方 Actual 才到 0。
- [ ] **RED 2：** 先到 frame2 不推进；补 frame0 只到 0；补 frame1 一次连续推进到 2。
- [ ] **RED 3：** Missing→Predicted；冷启动 default；preceding Actual 移动/持续位保留且 `0x61` 清除。
- [ ] **RED 4：** Predicted→Actual 命中不报错；失配记录 predicted/actual；多个乱序失配稳定返回最早 frame/player。
- [ ] **RED 5：** 同值重复幂等；冲突重复首值不变、返回 Conflict、锁存 fault、确认不再推进。
- [ ] **RED 5b：** 已确认帧后来收到冲突重复时，确认头数值不倒退，但 `HasIntegrityFault` 立即使其不可消费/不可发布，后续 Actual 也不能继续推进。
- [ ] **RED 6：** 非法 frame/player、重复 Resolve、`ResolvedFrame` 不暴露可变数组别名。
- [ ] **GREEN：** 用最小固定双 slot frame entry 实现上述状态机与连续扫描；不得接 GameController。
- [ ] **VERIFY：** 运行 `FrameInputLedgerTests`，再运行原 268 基线；此时运行时行为应完全未变。

### Task 2：实现重演计划与历史边界

**Files:** 修改 `FrameInputLedger.cs`；新增 `FrameInputLedgerReplayTests.cs`。

- [ ] **RED 1：** `TryBuildReplayPlan` 对 `[from, through]` 先完整预检，Actual 原样、原 Predicted 统一按最近 preceding Actual 重算。
- [ ] **RED 2：** 含瞬时纠正的后缀不重复 `0x61`；乱序 Actual 改变后缀种子但不反向影响更早帧。
- [ ] **RED 3：** plan 构建失败、未 Commit、过期 generation Commit 均不改变 slot/mismatch。
- [ ] **RED 4：** Commit 原子替换 plan 内 Predicted 后缀并只清除该 generation 已纳入的 mismatch；期间新增更早 mismatch 不得误清。
- [ ] **RED 5：** 256 首次环绕、精确 `FirstRetainedFrame`、安全 prune、pending/in-flight/conflict 拒绝 prune、淘汰种子摘要。
- [ ] **RED 6：** `< FirstRetainedFrame`、跨度超容量和 replay 越界分别稳定返回 `HistoryUnavailable/CapacityExceeded`，不得退化为 Missing。
- [ ] **GREEN：** 实现不可变 plan + generation token + 显式 prune；禁止静默覆盖未安全历史。
- [ ] **VERIFY：** 运行两组 Ledger 测试并执行 `git diff --check`。

### Task 3：把输入预测职责从 PredictionSystem 迁入同一 Ledger

**Files:** 修改 `PredictionSystem.cs`、`PredictionSystemResolveRemoteTests.cs`、`PredictionSystemSnapshotTests.cs`。

- [ ] **RED 1：** 把现有 Actual 保真、瞬时预测清除、历史 truth 命中/失配、冷启动和最早一次错误契约逐项移植到 Ledger 测试。
- [ ] **RED 2：** 临时 `PredictionSystem.ResolveRemote` 门面与直接 Ledger 调用必须得到同一 resolved input、同一 earliest mismatch、同一确认头；第二次调用不得双报告。
- [ ] **RED 3：** 所有 P2-A `Take/TryGet/RestoreWorldSnapshot` 测试原样通过。
- [ ] **GREEN：** `PredictionSystem.Init` 接收唯一 Ledger；删除 `_lastRemoteInput`、`_predictionHistory`、`_correctCount` 和 120 帧 prediction 清理。临时输入门面只立即转发，不私存。
- [ ] **GREEN 2：** 运行时迁移完成后删除已无调用的 `ResolveRemote/RecordReplayRemote` 门面及旧 fixture；`PredictionSystem` 最终只拥有快照。
- [ ] **VERIFY：** Ledger、snapshot、simulation 和原 prediction 契约测试全部通过。

### Task 4：让 FrameReplaySystem 只消费 Ledger 计划

**Files:** 修改 `FrameReplaySystem.cs`、`FrameReplaySystemTests.cs`、`FrameReplayResult.cs`（仅在需要新增明确失败原因时）。

- [ ] **RED 1：** 新 overload 从错误帧前最近快照恢复。只有 plan 从 Ledger `StartFrame=0` 完整覆盖到目标帧且 `resetWorld` 可用时，找不到错误帧前快照才允许 reset 后从 0 重演；每帧仍只调用 `DeterministicWorld.Step`。
- [ ] **RED 2：** Replay 的双方输入只来自 `ReplayInputPlan`；测试故意让 FrameBuffer/网络旧值不同，结果仍以 plan 为唯一事实。
- [ ] **RED 3：** 所有可预检失败——`HistoryUnavailable`、`MissingInput`、过期 generation，以及“plan 不能从 StartFrame=0 完整重演且不存在可用恢复快照”的 `MissingSnapshot`——必须在 Restore/reset/Step/写快照前返回；已选中的快照若在预检后异常消失也返回 `MissingSnapshot`。世界、快照、Ledger、mismatch 均零修改。
- [ ] **RED 4：** 成功后逐帧重建快照并 Commit plan；最终逐字段状态与 Hash 等于权威序列。
- [ ] **RED 5：** 纠正瞬时动作不延续；重建后缀的另一处迟到 truth 仍能成为下一次 earliest mismatch。
- [ ] **GREEN：** 保留“找快照→预检→恢复/reset→Step→快照”编排；移除 `correctedRemoteRaw`、remote resolver、`remoteFrameOffset`、FrameBuffer 输入依赖、Replay 内 `ToPredictionInput` 与 `RecordReplayRemote`。
- [ ] **GREEN 1b：** 原子性严格限定为上述可预检失败与 Ledger plan commit；live world 的 Step 或快照写入发生未预期运行时异常时不承诺事务回退，必须暂停会话并报告。不得把该限定误写成“任意异常下全事务原子”。
- [ ] **GREEN 2：** 旧 overload 在迁移期只能构造/接收同一个 Ledger 后立即转发；所有调用和测试迁移后删除，不能保留第二个 replay loop。
- [ ] **VERIFY：** `FrameReplaySystemTests`、`DeterministicWorldTests`、`WorldStateCodecTests` 和 WorldHash 相关测试通过。

### Task 5：接入网络与 GameController，消除第二 Actual 账本

**Files:** 修改 `NetworkClient.cs`、`GameController.cs`、`FrameBuffer.cs`；补充 Ledger 映射/接线测试。

- [ ] **RED 1：** 用既有 `CanonicalFrameTests/NetworkFrameTimelineTests` 加 Ledger 集成用例，证明本地输入以 `(LocalPlayerIndex, current localFrame)` 映射、远端包以 `(RemotePlayerIndex, packet.remoteFrameID)` 映射，且都在写入前完成；Ledger 内只见 canonical frame，当前 offset=0 结果不变。
- [ ] **RED 2：** 收到 0、2 时确认只到 0；补 1 后到 2；同值重复不触发回滚；冲突重复不改首值并进入显式 fault。
- [ ] **RED 3：** 当前逻辑帧仍组装“本地 Actual + 远端 Actual/Predicted”，FrameBuffer 记录的执行值与 Ledger resolved frame 相同但不是权威来源。
- [ ] **GREEN 1：** GameController 初始化唯一 Ledger。单机模式将两名本地输入都登记 Actual；联网模式登记本地 Actual，并循环消费现有 `TryGetRemoteInput(out raw,out frame)`，按远端玩家映射 canonical 后登记 Actual。
- [ ] **GREEN 2：** 每帧只从 Ledger `ResolveForSimulation` 组装 Step 输入；网络协议、发送 frame、玩法、System 顺序不变。
- [ ] **GREEN 3：** `NetworkClient` 删除 `_remoteInputDict`、`DrainQueueToDict`、`TryGetRemoteInputAt`、`CleanupRemoteInputs` 和 `LatestDrainedRemoteFrameID` 的事实职责；保留 FIFO、线程、发送和到达 gap 指标。
- [ ] **GREEN 4：** `GameController` 不再调用 `PredictionSystem.ResolveRemote`，不再读取网络字典，不再清 120 帧 Actual；Conflict/CapacityExceeded/HistoryUnavailable 均暂停联网逻辑并输出 frame/player/raw/floor。
- [ ] **GREEN 5：** LateUpdate 在 rollback 与 highlight 消费完成后，按 3.5 节计算唯一 `firstFrameToKeep` 并请求 Ledger prune；Ledger 拒绝时不得由其他组件绕过或自行清理。
- [ ] **VERIFY：** timeline、transport、local action、simulation、rollback 与 match-flow 测试通过；服务器屏障测试仍通过，证明协议未改。

### Task 6：用 Ledger 最早错误与确认头驱动既有消费者

**Files:** 修改 `GameController.cs`、`StableRemoteFrameGateTests.cs`、相关 highlight 测试。

- [ ] **RED 1：** 多个逻辑帧在同一 LateUpdate 产生 mismatch 时，只处理 Ledger 的最早 frame/player；失败后 mismatch 留存并重试，成功 Commit 后才转向下一处。
- [ ] **RED 2：** `StableRemoteFrameGate` stage 的输入来自 `ledger.ConfirmedThroughFrame`；高帧乱序不能跨缺口，回滚失败不能发布 staged 水位。
- [ ] **RED 3：** `TryGetStableActualInputs` 只从 Ledger 取双方 Actual；不得 clone FrameBuffer 后再用网络字典覆盖远端。
- [ ] **RED 4：** 正常连续输入下精彩片段采集、终局请求与回放进入行为不变；预测正确也只有在 Actual 已落账后才算确认。
- [ ] **GREEN 1：** 停止用 `_rollbackRequests` 保存输入纠正事实；LateUpdate 从 Ledger 取 earliest mismatch，Replay 成功后 Commit，失败不清除。旧 `RollbackRequestBuffer` 可保留为未使用的通用类，但运行时不得再形成第二 earliest/correctRaw 来源。
- [ ] **GREEN 2：** P1-G gate 只负责“纠正完成后发布”，不再计算确认；它可落后 Ledger，但不能超过 Ledger。
- [ ] **VERIFY：** highlight、postgame、stable gate、rollback 与 P1-G 表现测试全部通过；不改表现采样和纠正策略。

### Task 7：清理双职责、全量验证并同步文档

**Files:** 清理上述运行时/测试门面；验证通过后修改三份架构/路线文档。

- [ ] **RED/审计：** 全仓扫描不得再出现运行时 `_remoteInputDict`、`_predictionHistory`、Replay 内 `ToPredictionInput`、`TryGetRemoteInputAt`、以 `LatestDrainedRemoteFrameID` 推确认、120 帧输入事实清理。
- [ ] **RED/审计 2：** Replay/稳定输入路径不得再引用 `FrameBuffer.PeekFrame`；运行时不得再引用 `_rollbackRequests` 作为 earliest/correctRaw 事实源。
- [ ] **GREEN：** 删除临时门面和陈旧注释；保留唯一 `FrameInput.ToPredictionInput` 规则、唯一 Ledger 比较、唯一 Ledger 确认头和唯一 Ledger earliest mismatch。
- [ ] **GREEN 2：** 文档只写实际已验证的 P2-B；明确单世界仍存在，P2-C Confirmed/Predicted Worlds、P2-D ViewWorldBuilder、P2-F ECS 均未实现。
- [ ] **VERIFY：** 局部矩阵、全量 EditMode、独立编译、Windows 构建、0ms/100ms 双端冒烟、工作区保护和 `git diff --check` 全部通过。

## 6. 必测矩阵

| 场景 | 预期 slot / 返回 | `ConfirmedThroughFrame` | mismatch / 故障 |
|---|---|---:|---|
| 初始 | 全 Missing | -1 | 无 |
| frame0 仅 P0 Actual | P0 Actual、P1 Missing | -1 | 无 |
| frame0 双 Actual | 双 Actual | 0 | 无 |
| 先 frame2、后 0、后 1 | 乱序保存 Actual | -1→0→2 | 无 |
| Missing 后 Resolve | Predicted | 不推进 | 无 |
| Predicted 与 Actual 相同 | Actual / PredictionMatched | 仅连续时推进 | 无 |
| Predicted 与 Actual 不同 | Actual / PredictionMismatched | 仅连续时推进 | 记录 predicted+actual |
| 多个乱序失配 | 各自转 Actual | 按连续事实推进 | 稳定返回最早 frame/player |
| 同值重复 Actual | IdempotentDuplicate | 不变 | 不重复事件 |
| 冲突重复 Actual | ConflictingDuplicate，首值保留 | 数值不倒退，但 fault 下不可消费/发布并冻结后续推进 | integrity fault，暂停 |
| 已确认旧帧后到冲突 | 首个 Actual 仍唯一保存 | 已有数值仅作故障前诊断，不再发布 | integrity fault，暂停 |
| Actual 含 `0x61` | Actual 原样 | 按连续事实推进 | 无 |
| 由该 Actual 预测下一帧 | 清 `0x61`，保留持续位 | 不推进 | 无 |
| 冷启动预测 | default Predicted | 不推进 | 无 |
| 回滚 plan 成功 | Actual 保留、Predicted 后缀原子替换 | 不由回滚推进 | 清本 generation 已处理 mismatch |
| 无快照但 plan 完整覆盖 StartFrame=0 | reset 后从 0 完整重演 | 不由回滚推进 | 成功后提交 plan |
| plan 非从 0 完整覆盖且无恢复快照 | MissingSnapshot，Restore/reset/Step 前零修改 | 不变 | mismatch 保留、显式失败 |
| 回滚 plan/输入可预检缺失 | Restore/reset/Step 前零修改 | 不变 | mismatch 保留、显式失败 |
| Replay 中未预期运行时异常 | 不承诺 live world 事务回退 | 不再发布 | 暂停会话并完整报错 |
| 256 边界安全淘汰 | 完整确认旧帧淘汰、种子摘要保留 | 不倒退 | 无悬空 mismatch |
| 未确认/pending/conflict 将被覆盖 | CapacityExceeded | 不变 | 暂停，禁止静默覆盖 |
| `FirstRetainedFrame-1` | HistoryUnavailable | 不变 | 不重建、不预测 |
| `FirstRetainedFrame` / `+255` | 正常查询窗口边界 | 不变 | 依 slot 状态返回 |
| 首次需要 `FirstRetainedFrame+256` | 先安全 prune，否则 CapacityExceeded | 不变 | 禁止静默覆盖 |
| 固定延迟/最大到达水位领先 | 仅保存已到 Actual | 不跨洞 | 水位不等于确认 |

## 7. 验证命令与双端冒烟

### 7.1 基线与局部 EditMode

实施前先关闭占用本项目的 Unity。每个 RED 必须确认是预期契约失败，不得把编译错误冒充 RED。

```powershell
$ErrorActionPreference = 'Stop'
$unity = 'C:\Unity\unity2022\Editor\Unity.exe'
$project = 'E:\帧同步_RouteC\Project\Frame Synchronization'

& $unity -projectPath $project -batchmode -runTests -runSynchronously `
  -testPlatform EditMode -testFilter 'FrameSyncDemo.Tests.FrameInputLedgerTests' `
  -testResults 'E:\帧同步_RouteC\TestResults-p2b-ledger.xml' `
  -quit -logFile 'E:\帧同步_RouteC\p2b-ledger-tests.log'
if ($LASTEXITCODE -ne 0) { throw "Ledger EditMode failed: $LASTEXITCODE" }

& $unity -projectPath $project -batchmode -runTests -runSynchronously `
  -testPlatform EditMode -testFilter 'FrameSyncDemo.Tests.FrameInputLedgerReplayTests' `
  -testResults 'E:\帧同步_RouteC\TestResults-p2b-replay.xml' `
  -quit -logFile 'E:\帧同步_RouteC\p2b-replay-tests.log'
if ($LASTEXITCODE -ne 0) { throw "Ledger replay EditMode failed: $LASTEXITCODE" }
```

### 7.2 全量、独立编译、协议屏障与构建

```powershell
& $unity -projectPath $project -batchmode -runTests -runSynchronously `
  -testPlatform EditMode `
  -testResults 'E:\帧同步_RouteC\TestResults-p2b-complete.xml' `
  -quit -logFile 'E:\帧同步_RouteC\p2b-complete-tests.log'
if ($LASTEXITCODE -ne 0) { throw "Full EditMode failed: $LASTEXITCODE" }

& $unity -projectPath $project -batchmode -quit `
  -logFile 'E:\帧同步_RouteC\p2b-complete-compile.log'
if ($LASTEXITCODE -ne 0) { throw "Compile verification failed: $LASTEXITCODE" }

& 'E:\帧同步_RouteC\Project\NetworkServer\NetworkServerBarrierTests.ps1'
if ($LASTEXITCODE -ne 0) { throw "Server barrier tests failed: $LASTEXITCODE" }

& $unity -projectPath $project -batchmode -quit -buildWindows64Player `
  'E:\帧同步_RouteC\Project\Frame Synchronization\Builds\RouteC-P2B-Smoke\FrameSynchronization.exe' `
  -logFile 'E:\帧同步_RouteC\p2b-build.log'
if ($LASTEXITCODE -ne 0) { throw "Windows build failed: $LASTEXITCODE" }
```

要求：全量零失败；编译与构建 return code 0 且 Console 无编译错误；屏障测试通过；新增测试数应使总数高于 P2-A 的 268，不能硬写未经运行证实的最终数量。

### 7.3 双端人工冒烟

按 `docs/demo/windows-build-and-demo.md`：Editor 作为 player0，Windows build 作为 player1，共同从帧 0 放行。分别执行两轮：

1. 启动 `Project/NetworkServer/NetworkServer.exe`，输入 `0`（0ms）；再启动 Editor Play Mode 与 P2-B build。
2. 重启服务器，输入 `1`（固定 100ms）；重新启动两端，禁止复用上一轮残留进程。
3. 两端分别移动、停止、反向；验证本地输入即时、远端最终状态一致。
4. 验证捡球、持球移动、投篮松开、自由球、F9 结束请求和精彩回放；玩法/System 顺序不得变化。
5. 观察并记录：canonical frame、Ledger `ConfirmedThroughFrame`、FirstRetained、Actual/Predicted 来源、earliest mismatch、回滚起点/长度、HistoryUnavailable/Conflict/CapacityExceeded（正常冒烟应为 0）。
6. 0ms 与 100ms 均要求两端同一 canonical frame 的最终世界/Hash 收敛；100ms 的 P1-G 已知画面回抽可继续存在，P2-B 不得宣称解决表现问题。

## 8. P2-B 完成门槛与停止点

- [ ] 每个 canonical frame、每个玩家只有一个 Ledger slot 和一个状态。
- [ ] 本地/远端 Actual、预测 used value、重复裁决、连续确认头和 earliest mismatch 只有 Ledger 一个事实源。
- [ ] 相同重复幂等；冲突重复首值不变并显式故障；乱序填洞只连续推进确认头。
- [ ] `ConfirmedThroughFrame`、历史容量、预测跨度、稳定发布水位和 `FirstRetainedFrame` 可明确区分。
- [ ] Actual 原样；预测仍只清 `0x61`，没有第二套预测算法。
- [ ] Replay 只消费预检 plan；所有可预检失败都在修改世界、快照、Ledger 或 mismatch 前返回；未预期 Step/快照运行时异常明确暂停且不虚构事务回退；成功仍走唯一 `DeterministicWorld.Step`。
- [ ] `PredictionSystem` 保留 P2-A 世界快照职责且 snapshot tests 全过；不再保存输入 history。
- [ ] `NetworkClient` 不再保存第二份 Actual；`FrameBuffer` 只作为执行日志，不再作为重演事实。
- [ ] P1-G 稳定发布只读取 Ledger 确认头和 Actual，回滚失败不发布；表现采样/纠正策略未改。
- [ ] 全量 EditMode、独立编译、协议屏障、Windows 构建、0ms/100ms 双端冒烟和工作区保护全部通过。
- [ ] P1-G/P2-A 既有未提交成果完整保留；没有自动 commit、merge 或 push。
- [ ] 文档只标记 P2-B 的已验证事实；P2-C/P2-D/ECS 仍明确为未实现。

**强制停止点：** 本计划提交后立即停止并等待帅老大批准。批准后也只执行 P2-B；完成上述门槛并再次汇报前，不开始 P2-C。
