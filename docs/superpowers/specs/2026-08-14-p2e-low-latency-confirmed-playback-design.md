# P2-E 低延迟 Confirmed 表现播放控制设计

> 状态：设计已由帅老大于 2026-08-14 确认；尚未实施，尚未重新完成自动化与双端人工验收
>
> 上位订正依据：[P2-E 街篮2式 Confirmed 表现播放：16 项订正与主对话交接](../../architecture/p2e-streetball2-16-item-correction-handoff.md)
>
> 实施计划：[P2-E Low-Latency Confirmed Playback Implementation Plan](../plans/2026-08-14-p2e-low-latency-confirmed-playback-plan.md)

## 1. 结论

P2-E 保持已经确定的三世界表现边界：本地玩家读取 Predicted，远端玩家读取 Confirmed，篮球按持球与出手语义在 Predicted/Confirmed 间仲裁，View World 只读且不参与确定性模拟。

本次订正只重写远端 Confirmed 表现轨的播放控制。默认采用 `TargetReadyBacklog = 0` 的低延迟模式：确认区间一旦完整到达即可开始播放，不再固定等待后续一帧。积压时使用分数积压、平滑倍速和迟滞追赶；容量达到第 9 个完整待播区间时进行显式、原子的表现重定位；缺失快照时进入 `PresentationFault`，不得静默拼接。

Route C 的默认水位 `0`、可选水位 `1`、容量 `8`、追赶增益 `0.15`、最大倍速 `1.5`、迟滞阈值和 `0.10s` 平滑时间均是本项目的初始实验参数，不是《街篮2》已验证参数，也不是行业标准。

## 2. 当前问题与事实边界

旧 P2-E 游标已经缓解了多包同一渲染帧排空时的周期性前跳，并保持了 Confirmed 帧的单调播放。但它把启动缓冲固定为一帧，并规定每个渲染帧最多消费一个 Confirmed 区间。固定 100ms 单向延迟下，人工体验结果是：周期性卡顿明显缓解，但远端动作在 Actual 已到达后仍增加约一个逻辑帧的固定等待，双端表现延迟被进一步拉大。

这不意味着双端必须显示完全相同的临时画面。街篮2式双世界表现的正确目标仍是：本地输入立即反馈，远端只显示 Confirmed 轨，网络恢复后逻辑状态和 Hash 收敛，表现侧以受控方式消化到达抖动。逻辑帧编号一致、临时画面一致、回滚正确、画面平滑和最终 Hash 收敛是不同验收维度。

当前精确状态统一写为：

> P2-E 自动化门禁通过；固定 100ms 的远端表现人工验收未通过。旧周期跳步已缓解后出现固定播放附加延迟，当前等待低延迟 Confirmed 播放控制订正与重新验收。

在本设计实施并通过双端人工矩阵前，不得写“P2-E 画面验收完成”“已达到街篮2体验”或进入 P2-F。

## 3. 术语与不变量

| 名称 | 定义 | 不等于 |
|---|---|---|
| `ConfirmedHead` | 当前最新完整 Confirmed 逻辑帧 | 当前屏幕已经显示的帧 |
| 活跃区间 | `ActiveFromFrame -> ActiveToFrame` | 待播队列 |
| `Alpha` | 活跃区间内的连续播放进度，范围 `[0, 1]` | 逻辑世界时间 |
| `ReadyBacklog` | `ConfirmedHead - ActiveToFrame`，只计算活跃区间之后的完整待播区间 | 快照容量、网络队列长度 |
| `FractionalBacklog` | `ConfirmedHead - (ActiveFromFrame + Alpha)` | 只看整数帧头差 |
| `TargetReadyBacklog` | 希望常驻的完整待播区间数 | 最大容量、固定网络延迟 |
| `MaxReadyBacklog` | 允许连续保留的最大完整待播区间数 | Ledger 或快照容量 |
| Underflow | 当前区间播完，但满足目标水位的下一连续区间不可用 | 网络断开 |
| Overflow | `ReadyBacklog > MaxReadyBacklog` | 快照已经淘汰、Ledger 容量不足 |
| `PresentationFault` | 请求的连续 Confirmed 端点不可用，表现轨停止推进并输出缺失范围 | 逻辑世界暂停或自动重连 |

必须保持以下不变量：

1. Confirmed/Predicted 世界、Ledger、快照和 Hash 行为不因表现播放速度而改变。
2. 远端球员、远端确认持球的篮球和持球关系使用同一个 Confirmed 表现相位。
3. 本地玩家与本地预测持球/出手继续使用 Predicted 相位。
4. View World、播放游标、表现修正和诊断均不得写回逻辑世界。
5. 回滚不回退或重写 Confirmed 表现游标；回滚后只重新计算当前积压对应的速度状态和篮球来源。
6. 表现容量只约束 `ReadyBacklog`，不新建第三个可运行世界，也不复制一套篮球状态机。

## 4. 锁定参数

新增独立、可序列化的 `ConfirmedPlaybackSettings`，由 `GameController` 持有或引用，并在初始化时验证：

| 参数 | 默认值 | 约束 | 含义 |
|---|---:|---:|---|
| `TargetReadyBacklog` | `0` | `0..MaxReadyBacklog` | 低延迟默认水位；`1` 是稳定优先可选模式 |
| `MaxReadyBacklog` | `8` | `>= 1` | 最大完整待播区间数 |
| `CatchUpGainPerFrame` | `0.15` | `>= 0` | 每个超额分数帧增加的目标倍速 |
| `MaximumPlaybackSpeed` | `1.5` | `>= 1` | 追赶倍速上限 |
| `SpeedSmoothingSeconds` | `0.10s` | `> 0` | 从当前倍速向目标倍速移动的时间尺度 |
| `CatchUpEnterExcessFrames` | `0.25` | `> CatchUpExitExcessFrames` | 进入追赶的迟滞上阈值 |
| `CatchUpExitExcessFrames` | `0.10` | `>= 0` | 退出追赶的迟滞下阈值 |
| `OverflowCorrectionMaximumSeconds` | `0.20s` | `> 0` | 溢出重定位的最长屏幕修正时间 |

若 Inspector 值非法，初始化必须输出一次明确警告并恢复整组安全默认值；不得在每帧静默夹取，也不得允许 `TargetReadyBacklog > MaxReadyBacklog`、进入阈值不大于退出阈值或最大倍速小于 `1`。

## 5. 正常播放与追赶控制

### 5.1 启动与水位

- 水位 `0`：当 `ConfirmedHead = F` 且 `F-1 -> F` 端点完整存在时，立即激活该区间，不等待 `F+1`。
- 水位 `1`：只有激活后仍有一个完整待播区间时才开始，因而允许并明确显示约一个逻辑帧的额外稳定延迟。
- 活跃区间未播完时，不因 ConfirmedHead 前进而替换它。
- 活跃区间播完且下一连续区间不可用时，保持最后 Confirmed 端点；不重播旧区间，不注入 Predicted 远端数据。

### 5.2 分数积压

控制器每次更新计算：

```text
FractionalBacklog = ConfirmedHead - (ActiveFromFrame + Alpha)
Excess = max(0, FractionalBacklog - (TargetReadyBacklog + 1))
```

`TargetReadyBacklog + 1` 包含正在播放的正常活跃区间。这样，水位 `0` 下正常播放最新完整区间时，`FractionalBacklog` 从 `1` 连续降到 `0`，不会被误判为需要追赶；只有活跃区间之后仍出现额外积压时，`Excess` 才大于零。

### 5.3 目标速度、迟滞和平滑

```text
if not CatchingUp and Excess > 0.25: CatchingUp = true
if CatchingUp and Excess < 0.10: CatchingUp = false

RawTargetSpeed = clamp(1 + 0.15 * Excess, 1, 1.5)
TargetSpeed = CatchingUp ? RawTargetSpeed : 1
```

实际倍速不瞬间跳到目标值，而是在 `0.10s` 时间尺度内限速趋近；从 `1.0` 到 `1.5` 的最大变化时间不得短于该配置。诊断必须同时输出目标倍速与实际倍速。

每次渲染更新的顺序固定为：先用更新开始时的 `ConfirmedHead/ActiveFrom/Alpha` 计算目标速度，再将实际速度向目标限速移动一次，然后用该次实际速度消费本渲染帧时间并顺序跨越区间，最后基于最终 Alpha 重算用于诊断和下一次更新的积压/目标速度。不得在同一个长渲染帧的每个跨越区间内反复切换速度档位。

这个控制器用于消化短期 Confirmed 到达积压，不用于掩盖长期吞吐不足。连续追赶时间过长、频繁进入/退出追赶或经常撞到 `1.5x` 都属于需要诊断的异常体验，而不是成功状态。

## 6. 长渲染帧、欠载与溢出

### 6.1 长渲染帧

一个 Unity 渲染帧的 `deltaTime` 可能跨过两个或更多 Confirmed 区间。播放时钟必须按连续顺序推进经过的区间，并只向 Transform 提交本渲染帧的最终样本。被跨过的中间逻辑区间应计入 `CrossedIntervalCount` 和 `RenderFrameDropCount`。

这属于渲染帧丢失后的显式降级，不能描述成“每个视觉帧都播放了”，也不能继续使用旧规则“每个 `LateUpdate` 最多消费一个区间”而人为积压。

### 6.2 Underflow

当活跃区间到达 `Alpha = 1`，但激活下一连续区间会使 `ReadyBacklog` 低于目标水位时：

- 远端玩家和 Confirmed 篮球保持当前端点；
- 目标速度回到 `1.0`，实际速度平滑稳定；
- 记录 Underflow 开始、持续时间与恢复帧；
- 下一连续区间满足水位后，从保持端点继续，不重放旧区间。

### 6.3 Overflow

正常范围为 `ReadyBacklog <= 8`。第 9 个完整待播区间进入时触发 Overflow，并计算：

```text
NewActiveToFrame = ConfirmedHead - TargetReadyBacklog
NewActiveFromFrame = NewActiveToFrame - 1
DroppedRange = [OldActiveToFrame + 1, NewActiveToFrame - 1]
```

重定位必须在同一个表现事务中完成：

- 更新远端球员区间；
- 更新 Confirmed 篮球区间；
- 重新计算 holder/attachment 和篮球来源；
- 将游标相位放到新端点；
- 清除旧迟滞/倍速状态并从 `1.0x` 重新计算；
- 从当前屏幕位置启动最长 `0.20s` 的视觉修正；
- 记录旧区间、新区间和完整丢弃范围。

水位 `0` 时重定位至 `ConfirmedHead`；水位 `1` 时重定位至 `ConfirmedHead - 1`。溢出是可观察的表现降级，不得伪装成正常连续播放。

## 7. 快照缺失与回滚

### 7.1 快照缺失

如果请求的 `ActiveFromFrame` 或 `ActiveToFrame` 不存在：

- 进入显式 `PresentationFault`；
- 暂停 Confirmed 表现轨并保持最后已知屏幕状态；
- 输出所需区间与缺失范围；
- 不静默使用单端点拼接，不跳到新帧继续播放；
- 不暂停或修改 Confirmed/Predicted 逻辑世界。

生产级 resync/reconnect 不属于 P2-E，本阶段只保留明确故障与诊断。

### 7.2 回滚

Predicted 回滚后：

- Confirmed 游标的 `ActiveFromFrame`、`ActiveToFrame` 和 `Alpha` 不回退；
- 根据当前 `ConfirmedHead`、水位和 `Alpha` 重新计算 `Excess`、迟滞状态、目标倍速和实际倍速，不能沿用与当前积压不符的旧控制状态；
- 本地 Predicted 玩家和本地持球/出手来源可以更新；
- 远端 Confirmed 玩家和球保持同相位；
- holder、球来源与修正基线在一次表现更新内原子重建。

## 8. 诊断契约

`RuntimeNetworkDiagnostics` 继续是默认关闭的只读侧车，不拥有 Ledger、世界、快照或 Transform。开启后至少输出：

- Actual 接收时间到该帧首次进入可见区间的毫秒数；
- `ReadyBacklog`、`FractionalBacklog`、活跃区间与 `Alpha`；
- 目标倍速、实际倍速、速度档位切换次数和连续追赶时长；
- 单渲染帧跨越区间数与累计 `RenderFrameDropCount`；
- 远端位移、最大位移、反向位移和静止时长；
- Underflow 开始、持续时间和恢复；
- Overflow 重定位帧和完整丢弃范围；
- 球来源、holder、attachment 的切换帧；
- 回滚最早失配帧、恢复帧、重演数与 Confirmed Hash；
- `TargetReadyBacklog` 当前模式及其预期附加延迟说明。

Actual 到首次可见必须基于映射后的 canonical frame 关联接收时间；重复 Actual 只记录首次有效到达，不能用后来的重复包缩短延迟。

## 9. 测试与验收

### 9.1 自动化矩阵

必须覆盖：

1. 水位 `0/1`、积压 `0/1/8/9`、Alpha `0/0.5/1` 的边界；
2. 水位 `0` 的到达即播放，以及水位 `1` 的固定额外等待；
3. 积压 `2..8` 的比例追赶、`1.5x` 上限、平滑和迟滞；
4. 30 秒稳定到达下无周期性 `1.0 <-> 1.15` 速度脉冲；
5. Underflow 保持与连续恢复；
6. Overflow 原子重定位、丢弃范围、速度重置与 `0.20s` 修正；
7. Confirmed 快照缺失触发 `PresentationFault`；
8. 回滚保持游标但重算速度和篮球来源；
9. 本地/远端持球、抢断、脱手和投篮的玩家—球来源矩阵；
10. `30/60/144 FPS` 相位一致性；
11. 长渲染帧跨越两个以上区间，只提交最终相位且记录 RenderFrameDrop；
12. Actual 接收到首次可见延迟；
13. `GameController` 真实接线，而不是只测纯游标；
14. Confirmed Hash 不变，所有表现字段排除在规范状态与 Hash 外。

所有行为修改遵循 TDD：先保存失败测试证据，再做最小实现，随后运行专项、全量 EditMode、独立编译、服务端 lab/barrier 和 Windows x64 构建。不得用旧的 `368/368` 证据代替订正后的新结果。

### 9.2 双端人工矩阵

- 网络面板明确区分单向 `0/100/200ms` 与 RTT；本轮固定 100ms 指单向注入延迟。
- 自动化覆盖 `30/60/144 FPS`；人工基线执行 `60 FPS` 和 `144 FPS`。
- 每个场景连续至少 10 秒，覆盖启动、持续直线移动、停止、八方向、持球、抢断和投篮。
- 本地必须立即响应；远端允许网络与 Confirmed 延迟，但水位 `0` 不得再人为增加完整一帧常驻等待。
- 不允许周期性停走、跳步、回抽、持续快速播放或人球错位。
- 每轮同时保存主观观察和诊断数据。

只有双端矩阵通过后，才能更新 P2-E 为画面验收完成并讨论 P2-F。

## 10. 修改范围

允许修改：

- `Assets/Scripts/Presentation/ConfirmedPresentationCursor.cs`
- 新增 `Assets/Scripts/Presentation/ConfirmedPlaybackSettings.cs`
- `Assets/Scripts/Presentation/ViewWorldBuilder.cs`
- `Assets/Scripts/Presentation/PresentationFrameInterpolator.cs`
- `Assets/Scripts/Presentation/PresentationCorrectionSmoother.cs` 的必要接线，不改其通用数学契约
- `Assets/Scripts/Presentation/RuntimeNetworkDiagnostics.cs`
- `Assets/Scripts/GameController.cs`
- 对应 EditMode/必要 PlayMode 测试、架构文档和验收记录

禁止修改：

- 网络 8 字节协议和服务器转发语义；
- `FrameInputLedger` 的事实定义；
- Confirmed/Predicted 世界推进、确定性 Step、快照规范状态和 WorldHash；
- 篮球玩法规则；
- P2-F ECS/DOTS/Jobs/Burst。

## 11. 失败策略

- 30 秒稳定到达仍出现周期性倍速切换：停在控制器任务，不进入 GameController 接线。
- Overflow 无法原子保持人球关系：停在表现集成任务，不以玩家位置单测通过代替。
- Actual 到可见时间无法按 canonical frame 关联：诊断门不通过，不进行人工体验归因。
- 自动化、编译、服务端或构建任一失败：不开始双端人工验收。
- 人工固定 100ms 仍出现明显固定附加帧、停走或持续追赶：保持 P2-E 未通过，回到参数与轨迹证据分析，不进入 P2-F。
