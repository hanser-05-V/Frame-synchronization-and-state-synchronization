# P1-A 完整世界快照设计

## 目标

建立可独立验证的完整同步状态快照能力：保存两名玩家与篮球的全部动态确定性状态，随后即使这些对象被任意修改，也能按指定帧恢复为逐字段完全一致的值。

P1-A 只提供并测试基础能力，不接入 `GameController` 的正常帧或回滚流程，避免当前残缺回滚重演覆盖完整快照。运行时接入、保存时序统一和完整重演属于 P1-B。

## 方案比较与选择

### 方案 A：扩展现有扁平 `FrameSnapshot`（采用）

在已有双玩家固定字段上补齐玩家 Y 坐标与朝向，继续使用现有玩家状态、球权和篮球字段。新增完整世界快照 API，保留旧 XZ API。

- 优点：改动最小，不影响 P0；不引入数组引用或额外分配；与当前固定两名玩家结构一致。
- 缺点：字段较多，玩家数量暂时固定为两名。

### 方案 B：嵌套 `PlayerSnapshot` 与 `BallSnapshot`

结构更清晰，但需要迁移现有字段和旧 API，扩大本阶段改动与兼容风险。

### 方案 C：玩家快照数组

便于扩展玩家数量，但数组是引用类型，必须额外处理深拷贝、分配和别名问题，不适合当前最小确定性闭环。

## 同步状态边界

每名玩家保存：

- `position.x/y/z`
- `facing.x/y/z`
- `state`
- `hasBall`

篮球保存：

- `position.x/y/z`
- `velocity.x/y/z`
- `state`
- `holderPlayerIndex`

`playerIndex` 是初始化后不改变的身份配置，不属于逐帧动态状态，不写入或恢复。Unity `Transform`、材质、调试 UI、墙钟时间和网络连接状态均不进入快照。

## API 与行为

在 `PredictionSystem` 新增：

```csharp
public void TakeWorldSnapshot(
    int frameID,
    PlayerEntity[] players,
    BallEntity ball)

public bool RestoreWorldSnapshot(
    int frameID,
    PlayerEntity[] players,
    BallEntity ball)
```

规则：

1. `players` 必须包含两名非空玩家，`ball` 不得为空；无效参数立即抛出 `ArgumentException`。
2. 捕获时逐字段复制值类型数据，不保存玩家或篮球对象引用。
3. 指定帧不存在时，恢复返回 `false`，不得修改任何传入状态。
4. 指定帧存在时，恢复全部动态同步字段并返回 `true`。
5. 旧 `TakeSnapshot/RestoreSnapshot` 保持不变，P1-A 不改变当前运行时行为。

## 测试与验收

新增 `PredictionSystemSnapshotTests`：

1. 构造两名具有不同位置、朝向、状态和球权的玩家，以及一颗带位置、速度、状态和持有者的篮球。
2. 捕获快照后，修改所有可恢复字段。
3. 恢复快照，逐个比较全部 `FixedInt._raw`、枚举、布尔值和持有者。
4. 确认玩家 `playerIndex` 未被恢复或改变。
5. 请求不存在的帧，确认返回 `false` 且玩家和篮球不发生变化。
6. 全部现有 EditMode 测试继续通过，Unity 编译错误为零。

## P1-B 接缝

P1-B 将删除运行时对旧 XZ 快照 API 的依赖，把完整快照统一保存到球物理完成之后，并让回滚重演复用与正常逻辑帧相同的玩家、投篮和篮球物理流程。P1-A 不提前实现这些行为。
