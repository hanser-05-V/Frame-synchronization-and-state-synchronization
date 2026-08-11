# P1-E 远端渲染缓冲设计

> 日期：2026-08-07  
> 状态：方案已获帅老大批准，等待文档确认  
> 工作区：`E:\帧同步_RouteC`  
> 分支基线：`delivery/route-c`  
> HEAD 基线：`37260b437260c7712c658d5d0e05cbdd183accfb`

## 目标

在不改变确定性逻辑的前提下，为远端玩家增加一个逻辑帧的额外表现缓冲，使远端总显示延迟由当前约一个逻辑帧增加到约两个逻辑帧，即约 `66ms`。这样可以减少远端预测失败时已经展示出去的错误路径长度，从源头降低回滚纠正的可见幅度。

本地玩家继续使用现有表现路径，不增加输入显示延迟。现有 `PresentationCorrectionSmoother` 保留在缓冲输出之后，作为缓冲仍未覆盖全部预测错误时的第二层纠错保护。

## 非目标

- 不修改逻辑帧率、输入采集、远端输入预测、回滚触发或回滚重演。
- 不修改 `FrameSnapshot` 字段、SnapshotBuffer 容量或 WorldHash schema。
- 不修改服务器协议、网络帧映射或 canonical frame 计算。
- 不实现动态网络抖动、自适应缓冲深度、时间伸缩或外推。
- 不让本地玩家跟随远端额外缓冲。
- 不重新实现已经完成的逻辑帧与渲染帧分离、斜向归一化或速度连续回滚纠错。
- 不处理动画、旋转、摄像机、Rigidbody 插值或场景资源。
- 不修改 `E:\帧同步`，不自动 commit、merge、push、reset、clean 或 checkout 覆盖。

## 当前表现基线

当前 `PresentationFrameInterpolator` 保存最近两个完整逻辑帧。设最新完成逻辑帧为 `N`，渲染比例为 `alpha`：

```text
当前所有对象 = Lerp(端点 N-1, 端点 N, alpha)
```

所以本地玩家、远端玩家和篮球目前统一落后逻辑头约一个逻辑帧。`GameController.SyncPresentationFromLogic` 在 `LateUpdate` 中先求出插值基准，再叠加球员和篮球的 `PresentationCorrectionSmoother`，最终只写一次 Transform。

当前 `ReplaceAfterRollback` 会把前后端点同时压成纠正后的最终帧 `N`。这对两端点插值足够，但 P1-E 需要保留纠正后的 `N-2/N-1/N` 历史，否则远端缓冲会在回滚后短暂失效。

## 方案比较

### 方案 A：深化现有表现插值模块为三端点缓冲（采用）

扩展现有 `PresentationFrameInterpolator`，让它固定保存三个完整表现端点，并在一次求值中输出本地与远端的混合时间线。模块内部同时保存篮球状态、持有者和 Held 挂点偏移。

优点：只有一份端点历史；帧顺序、球权时间线和回滚替换集中在同一模块；调用方接口小；容易通过同一接口测试。代价：需要扩展现有模块内部结构和回滚替换接口。

### 方案 B：在现有插值器旁增加独立远端缓冲（拒绝）

新缓冲只保存远端玩家及篮球历史，现有插值器继续保存本地世界。

表面改动较少，但两套模块会重复保存世界端点。正常推帧、Reset、回放和回滚必须同时更新两套状态，一旦有一步遗漏就会产生帧号或球权不一致，长期维护风险较高。

### 方案 C：全世界统一再延迟一帧（拒绝）

所有玩家和篮球都改为 `N-2 → N-1`。

实现最简单，世界时间线也最统一，但本地玩家会新增约 `33ms` 显示延迟，直接违反本地手感优先的目标。

动态自适应缓冲不进入首轮候选，因为它还需要缓冲深度切换、时间伸缩和抖动控制，范围明显超过 P1-E。

## 时间模型

模块固定保存三个严格递增的完整表现端点：

```text
最旧端点       前一端点       当前端点
   N-2  ─────────  N-1  ─────────  N
       远端插值区间      本地插值区间
```

同一个 `alpha` 同时驱动两条时间线：

```text
本地玩家位置 = Lerp(N-1.player, N.player, alpha)
远端玩家位置 = Lerp(N-2.player, N-1.player, alpha)
```

这保留了当前本地约一帧的显示延迟，同时让远端稳定增加一个逻辑帧，总显示延迟约为两个逻辑帧。

`alpha` 仍由 `FrameEngine.RenderInterpolationAlpha` 提供，只服务表现层，不写入输入、实体、快照、重演或 WorldHash。

## 表现端点内容

每个端点只保存从完整逻辑世界复制出的表现数据：

- 逻辑帧号。
- 所有球员的 Unity 表现坐标。
- 篮球的 Unity 表现坐标。
- 篮球确定性状态。
- 篮球持有者索引。
- Held 时篮球相对持有者表现基准的挂点偏移。

端点不保存实体引用，不反向修改 `PlayerEntity`、`BallEntity` 或 `FrameSnapshot`。

构造时一次性分配三个球员坐标数组。正常 `PushLogicFrame`、`Evaluate`、回滚历史替换和 `LateUpdate` 不产生托管分配。

## 模块与接口

### PresentationFrameInterpolator

继续使用现有类作为表现世界缓冲的深模块，不再创建并列表现缓存。建议外部接口：

```csharp
public sealed class PresentationFrameInterpolator
{
    public PresentationFrameInterpolator(int playerCount);
    public bool IsReady { get; }

    public void Reset(
        int frameID,
        PlayerEntity[] players,
        BallEntity ball);

    public void PushLogicFrame(
        int frameID,
        PlayerEntity[] players,
        BallEntity ball);

    public void ReplaceHistoryAfterRollback(
        FrameSnapshot newest,
        FrameSnapshot? previous,
        FrameSnapshot? oldest);

    public void Evaluate(
        int localPlayerIndex,
        float alpha,
        Vector3[] playerBasePositions,
        out PresentationBallSample ballSample);
}
```

接口约束：

- `PushLogicFrame` 只接受严格大于当前端点的帧号。
- `Reset` 将三个槽都填成同一端点，使初始化和生命周期切换立即稳定。
- `ReplaceHistoryAfterRollback` 一次性验证并替换全部历史，不能让渲染读取到一半新、一半旧的端点。
- `Evaluate` 校验本地索引、输出数组长度和初始化状态，并将非法 `alpha` 钳制到 `[0,1]`。
- `Evaluate` 隐藏本地/远端时间线选择和篮球球权规则，`GameController` 不自行索引三个端点。

### PresentationBallSample

新增一个独立文件中的只读表现值类型：

```csharp
public readonly struct PresentationBallSample
{
    public Vector3 BasePosition { get; }
    public int AttachedPlayerIndex { get; }
    public bool IsAttached => AttachedPlayerIndex >= 0;
}
```

`AttachedPlayerIndex == -1` 表示篮球当前按自由世界显示。该值是表现层的附着结果，不参与确定性球权，也不写入快照。

### PresentationTargetResolver

解析器接收 `PresentationBallSample`、本次求值产生的球员基准位置，以及球员最终显示位置：

- 未附着：返回 `BasePosition`。
- 已附着：返回 `BasePosition + (displayedHolder - baseHolder)`。`BasePosition - baseHolder` 就是模块从同一时间线算出的 Held 挂点偏移，因此篮球既保留同帧挂点，又继承持有者的回滚表现纠错。
- 附着索引对任一数组无效时，安全返回 `BasePosition` 并由调用方报告球权不变量问题。

解析器不得再使用实时 `_ballEntity.state` 去解释较旧的缓冲坐标。这样可以避免“坐标来自 N-1、球权却来自 N”的跨帧混合。

### GameController

`GameController` 只负责编排：

1. 每个完整正常逻辑帧结束后推入一个端点。
2. `LateUpdate` 请求一次混合时间线结果。
3. 对球员基准叠加现有回滚纠错。
4. 根据 `PresentationBallSample` 求出篮球最终目标。
5. 处理一次性的附着关系变化。
6. 统一写入 Transform 和调试显示。

帧选择、三槽轮转、篮球时间线策略和回滚历史补齐均留在表现缓冲模块内部。

## 玩家时间线规则

- `localPlayerIndex` 指向的球员使用 `N-1 → N`。
- 其余球员使用 `N-2 → N-1`。
- 两条时间线使用同一个 `alpha`，不会发生渲染节奏漂移。
- 玩家回滚纠错器在各自缓冲基准之上运行。
- 客户端本地索引为 0 或 1 时必须对称工作，不能写死 P1/P2。

## 篮球时间线与球权规则

篮球采用“本地球权即时、远端球权缓冲”的非对称表现策略。

### 本地 Held

如果最新端点 `N` 表示篮球由本地玩家持有：

- 篮球使用本地 `N-1 → N` 时间线。
- 篮球基准由本地持有者基准加 Held 挂点偏移得到。
- 最终目标由本地持有者最终显示位置加同一挂点偏移得到。
- 篮球因此继承本地玩家的表现延迟和回滚纠错，不增加额外一帧。

若区间两端都由同一名本地玩家持有，可插值两个 Held 挂点偏移；若在新端点刚进入 Held，则直接使用新端点挂点偏移，优先保证球贴手。

### 远端 Held

只有当远端区间的新端点 `N-1` 已经表示篮球由远端玩家持有时，表现篮球才附着到远端玩家：

- 篮球使用 `N-2 → N-1` 时间线。
- 持有者也使用同一个远端区间。
- 篮球最终目标继承远端持有者的最终显示位置及回滚纠错。

远端逻辑帧 `N` 刚发生持球时，不允许用 `N` 的球权提前解释 `N-2 → N-1` 的坐标。远端球权在缓冲端点到达后再显现。

### Free / Airborne / Scored

三种非附着状态统一使用 `N-2 → N-1` 的篮球绝对坐标。状态标签变化不会改变时间线，也不会单独重启表现纠错。

### 本地球权离开与转移

本地 Held 的进入和离开按最新端点立即生效：

- 本地获得球权时立即切入本地 Held 路径。
- 本地投篮或失去球权时立即退出本地附着，篮球改用缓冲世界基准。
- 如果远端尚未在缓冲端点中获得球权，中间允许出现一个短暂的“未附着表现段”，禁止继续错误地挂在旧本地持有者身上。
- 远端获得或释放球权仍按远端缓冲端点显现。

这种非对称规则保证本地操作响应，同时避免远端球权提前使用尚未到达的历史坐标。

## 篮球状态切换连续性

表现层使用 `AttachedPlayerIndex` 作为附着键。只有以下变化属于附着关系变化：

- `-1 → holder`：进入 Held。
- `holder → -1`：离开 Held。
- `holderA → holderB`：持有者变化。

`Airborne → Free`、`Airborne → Scored`、`Scored` 落地等非附着状态变化不改变附着键，因此不重启篮球纠错器。

切换规则：

- 离开 Held：从上一渲染帧实际篮球位置启动篮球纠错，目标为新的缓冲世界基准，避免离手闪跳。
- 进入 Held：目标直接由持有者最终显示位置和同帧挂点组成。确定性球权系统必须保证获得球权时球已处于合法接触范围；若偏差超过表现容差，优先贴手并报告球权不变量问题，不允许长期显示人球分离。
- 持有者变化：按进入 Held 处理，并要求确定性球权切换端点满足接触不变量。
- 稳定 Held：篮球不运行独立世界时间线；持有者纠错器就是 Held 篮球的第二层保护。
- 稳定非 Held：篮球继续使用自己的 `PresentationCorrectionSmoother` 处理回滚剩余误差。

这样可以同时避免状态切换硬跳、每帧重复纠错和持球期间的独立拖尾。

## 正常帧数据流

1. `FrameEngine` 执行逻辑帧。
2. `FrameSimulationSystem.Step` 更新球员、球权和篮球。
3. `PredictionSystem.TakeWorldSnapshot` 和正常 WorldHash 仍在现有时点完成。
4. `OnPostFrameUpdate` 将完整逻辑世界复制为表现端点。
5. 三槽缓冲轮转为新的 `N-2/N-1/N`。
6. `LateUpdate` 用本地索引和 `alpha` 求值混合时间线。
7. 球员纠错器分别追踪自己的本地或远端基准。
8. 篮球解析器根据附着键生成最终目标。
9. 一个渲染帧只写一次球员和篮球 Transform。

## 回滚数据流

回滚前继续保存当前肉眼显示的所有球员位置和篮球位置。

回滚逻辑保持不变。现有 `FrameReplaySystem.Replay` 会在每个成功重演帧调用 `PredictionSystem.TakeWorldSnapshot`，因此重演成功后，最后三个可用帧的快照已经是纠正后的完整世界。

成功回滚后的顺序：

1. 只读取得 `N`、`N-1`、`N-2` 的纠正快照。
2. 调用 `ReplaceHistoryAfterRollback` 原子替换三个表现端点。
3. 立即按当前 `alpha` 求出新的本地、远端和篮球基准。
4. 球员纠错器从回滚前实际显示位置开始追向各自新基准。
5. 非 Held 篮球纠错器从回滚前实际显示位置追向新的缓冲篮球基准。
6. Held 篮球跟随已纠错的持有者显示位置，不建立独立错误时间线。
7. 下一正常逻辑帧继续正常轮转三槽缓冲。

回滚失败时：

- 不替换任何表现端点。
- 不启动新的球员或篮球纠错。
- 不改变已记录的表现附着键。

历史不足时：

- 启动最初不足三帧时，复制最早可用的正确端点补齐较旧槽位。
- 若意外缺少某个历史快照，复制最近的已验证正确旧端点，禁止保留旧预测端点。
- 降级结果可以短暂停住，但不能显示已知错误的预测历史。

表现模块只读取快照字段生成副本，不修改快照、SnapshotBuffer 或 WorldHash 输入。

## 生命周期

以下路径使用 `Reset`，把三个槽填成同一完整世界，并清除所有表现纠错与附着历史：

- 初始化。
- 手动 Reset/Clear。
- 回放进入和退出。
- 回放自动结束。
- 世界对象重新创建。
- 表现模块尚未准备完成时的恢复。

暂停时不推入逻辑端点，`alpha` 和纠错时间都不推进，保持最后显示结果。恢复后从冻结状态继续。

一次 Unity Update 追赶多个逻辑帧时，按顺序推入每一个完整逻辑帧；最终三个槽必须是最后三个递增端点。一个 Unity 渲染帧仍只执行一次表现求值和 Transform 写入。

## 参数与错误处理

- 玩家数量必须大于零。
- 本地玩家索引必须位于输出数组范围内。
- 输出数组长度必须与构造玩家数量一致。
- 正常推帧必须严格递增；重复或倒退帧明确抛出参数异常。
- 回滚替换快照必须有效且顺序可验证。
- 非法 `alpha`：NaN、负数和负无穷按 0；正无穷和大于 1 按 1。
- Held 持有者索引无效时，按未附着缓冲篮球处理并报告不变量问题，不能访问越界数组。
- 所有浮点坐标和纠错速度仅存在于表现层。

## 自动测试

### 三端点与玩家时间线

- Reset 后三个槽一致，任意 `alpha` 返回同一世界。
- 推入连续三帧后，本地严格插值 `N-1 → N`。
- 远端严格插值 `N-2 → N-1`。
- `alpha=0/0.5/1` 得到精确端点和中点。
- 本地索引 0 和 1 时结果对称。
- 多帧追赶后只保留最后三个端点。
- 重复、倒退帧和非法参数符合约定。

### 篮球时间线

- 本地 Held 使用本地持有者时间线和同帧挂点。
- 远端 Held 使用远端缓冲持有者时间线和同帧挂点。
- 远端逻辑刚进入 Held、但缓冲端点尚未进入 Held 时不得提前附着。
- Free、Airborne、Scored 都使用缓冲篮球端点。
- 本地 Held → Airborne 立即退出本地附着，并从上一显示位置连续接入缓冲目标。
- 远端 Held → Airborne 在缓冲端点到达时才退出附着。
- Airborne → Free、Airborne → Scored 和 Scored 落地不重复启动纠错。
- 本地到远端球权转移期间不继续挂在旧本地持有者身上。
- Held 最终目标始终等于持有者最终显示位置加挂点偏移。

### 回滚替换

- 纠正后的 `N-2/N-1/N` 一次性替换全部预测端点。
- 本地求值使用纠正后的 `N-1/N`，远端求值使用纠正后的 `N-2/N-1`。
- 任一旧预测端点都不能泄漏到回滚后的输出。
- 失败回滚不改变缓冲和附着键。
- 启动早期历史不足时复制正确端点，不访问负帧或无效快照。
- 回滚后附着关系未变化、进入 Held、离开 Held和持有者变化分别符合规则。

### 隔离与回归

- 表现求值前后，`PlayerEntity`、`BallEntity` 和输入快照字段完全不变。
- 同一快照在表现求值前后的 WorldHash 相同。
- 预热后正常推帧和求值不产生托管分配。
- 现有 `PresentationFrameInterpolatorTests`、`PresentationTargetResolverTests`、`PresentationCorrectionSmootherTests` 全部保持通过。
- 现有 Replay、Prediction、Snapshot、FrameSimulation 和 WorldHash 测试全部保持通过。
- 当前预期 `153` 项 Unity EditMode 回归加新增 P1-E 用例全部通过，零失败、零跳过；最终数量以 Unity Test Runner 实际发现为准。

## 独立代码审查重点

- 三槽帧号和轮转顺序是否始终严格递增。
- 本地与远端是否分别只读取规定的两个端点。
- 是否存在实时球权解释旧缓冲坐标的跨帧混合。
- Held 挂点是否与持有者来自同一时间线。
- 回滚是否原子替换全部历史并清除旧预测端点。
- 生命周期路径是否全部重置三槽和附着历史。
- 是否出现每帧数组、闭包、集合或临时对象分配。
- 是否有任何表现字段进入实体、快照或 WorldHash。

## 人工验收标准

由帅老大在 Unity Editor 与打包客户端双端执行，AI 不控制 Unity 界面。

1. 停止 Play Mode，等待脚本编译完成，Console 无红色错误。
2. 运行完整 EditMode，全部发现用例通过。
3. 双端持续直线、斜向、启动、停止和连续快速变向，普通移动无新增抖动。
4. 两端分别操作本地玩家，确认本地手感没有新增一帧延迟。
5. 观察远端连续快速变向，回到变向点的可见距离明显小于 P1-D 当前版本。
6. 分别验证本地 Held 与远端 Held，篮球稳定跟手，无持续人球分离。
7. 验证 Held → Airborne、Airborne → Free、Airborne → Scored 和 Scored 落地，无闪跳和二次纠正。
8. 验证 Reset、暂停恢复、录制回放进入和退出，无异常跳变。
9. 触发回滚后无首帧冻结、明显目标穿越或二次回抽。
10. 最终双端逻辑位置一致，同一 canonical frame 的 WorldHash 一致。

## 风险与取舍

### 球权跨时间线切换

这是最高风险点。本地球权即时而远端球权延迟，意味着本地到远端的球权转移可能出现一个短暂未附着表现段。该段必须连续显示缓冲篮球，不能继续挂在旧持有者，也不能提前挂到尚未到达的远端端点。

### 本地投篮后的篮球延迟

本地玩家和本地 Held 篮球保持当前时间线，但篮球离手成为 Airborne 后转入缓冲世界，因此飞行部分相对 Held 阶段会增加约一个逻辑帧延迟。这是换取 Free/Airborne/Scored 全局时间一致性的明确代价。

### 回滚历史不足

启动早期或异常缺失快照时，缓冲可能短暂停住。设计优先使用重复的正确端点，不允许继续展示旧预测端点。

### 超低渲染帧率

固定缓冲只能改善时间模型，无法掩盖严重性能卡顿。低渲染帧率下仍可能看到大步进。

## 完成定义

P1-E 只有在以下条件全部满足后才可标记完成：

- 设计文档经帅老大确认。
- 独立 TDD 实施计划经确认并按 RED→GREEN 顺序执行。
- 新增自动测试和全部现有回归通过。
- 独立代码审查 Critical 0、Important 0。
- Route C 分支、HEAD 和旧项目状态指纹复核通过。
- Unity 全量测试及双端人工验收通过。
- 人工验收通过后，才在 Windows 临时目录生成阶段总结和下一窗口交接文件。
