# P0 最小投篮闭环设计

## 目标

在现有 30 FPS 确定性帧循环中交付一条可运行、可录制的纵向闭环：P0 初始持球，移动时篮球跟随固定逻辑挂点，按下投篮后同帧释放球权并进入 `Airborne`，最终进入 `Scored` 或 `Free`。

## 范围

- 初始球权固定归 P0。
- 首版按下即出手，不做蓄力、动画或随机命中。
- 自动瞄准篮筐逻辑命中点，使用固定飞行帧数反算初速度。
- 持球、出手和物理只读取 `PlayerEntity`、`BallEntity`、`FixedInt` 与 `FixedVector3`。
- 修复篮球穿越篮筐平面时的上一位置判定。
- 建立 EditMode 自动化测试基线。

## 非目标

- 不实现完整篮球快照、回滚重放或篮球 Hash；这些属于 P1。
- 不实现传球、抢断、盖帽、篮板、自动重置或美术替换。
- 不修改网络协议、预测帧对齐和旧项目 `E:\帧同步`。

## 模块边界

### `BallPossessionSystem`

负责 Held 状态下的持球不变量校验与固定挂点跟随。只有同时满足 `player.hasBall=true`、`ball.state=Held`、`ball.holderPlayerIndex=player.playerIndex` 才更新 `ball.position` 并清零 `ball.velocity`；校验失败时返回 false 且不修改状态。

### `BallShotSystem`

负责验证投篮前置条件、驱动 `ShootReady → Shooting`、计算确定性出手点和初速度，并作为一次事务同步写入：

```text
player.hasBall = false
ball.holderPlayerIndex = -1
ball.state = Airborne
```

固定帧数轨迹匹配当前半隐式欧拉顺序：

```text
v.x = delta.x / (N * dt)
v.z = delta.z / (N * dt)
v.y = delta.y / (N * dt) - Half * gravity * dt * (N + 1)
```

### `BallPhysicsSystem`

积分前保存 `previousPosition`，积分后用 `previousPosition.y >= HoopY && currentPosition.y < HoopY` 判定从上向下穿越篮筐平面。水平距离必须位于篮筐半径内。

### `GameController`

只负责编排：玩家逻辑移动 → 处理投篮或 Held 跟随 → 后置球物理 → 表现同步。下一逻辑帧把 `Shooting` 恢复到 `Idle` 后再处理移动，避免移动输入令状态永久卡在 `Shooting`。

## 状态与帧顺序

```text
初始化：P0.hasBall=true，P1.hasBall=false，ball=Held，holder=0
每帧：读取 FrameInput
→ 更新玩家逻辑位置和 facing
→ 若 holder 的 shoot=true：执行 TryShoot
→ 否则 Held 球更新固定挂点
→ OnPostFrameUpdate 推进 Airborne/Free 物理
→ 将逻辑坐标写入 Transform
```

投篮失败（无球、球不是 Held、持有者不匹配、非法状态、无效帧数或 `dt<=0`）时不得部分修改玩家或篮球状态。

## 测试与验收

- Held 跟随成功时位置等于固定挂点、速度归零。
- 球权不一致时 Held 跟随失败且篮球状态不变。
- 合法投篮同帧释放球权、进入 Airborne，并在第 N 次积分后到达目标点的固定点容差范围。
- 无球投篮不改变玩家和篮球状态。
- 篮球从篮筐上方穿到下方且位于半径内时进入 Scored。
- 偏离篮筐的篮球落地后进入 Free。
- Unity 编译无错误，EditMode 测试全部通过，本地场景运行 60 秒无新增错误并可完成一次投篮。

