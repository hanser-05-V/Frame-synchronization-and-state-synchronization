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
4. 输出到项目下 `Builds/Windows/`。该目录是本地产物目录，不进入版本控制。
5. 构建完成后检查 Console，要求没有脚本编译错误。

## 启动顺序

1. 在仓库根目录启动 `Project/NetworkServer/NetworkServer.exe`。
2. 在 Unity Editor 打开 `SampleScene` 并进入 Play Mode；它作为 player 0 连接。
3. 启动 Windows 构建；它作为 player 1 连接。
4. 服务器等待两端均已连接后共同放行，双方从规范帧 0 开始。

不要先让单个客户端独立跑局，也不要同时启动第二个服务器实例。端口固定为 `127.0.0.1:8888`。

## 演示验收

1. 两端分别移动，确认本地响应及时、远端移动平滑。
2. 靠近自由球后按 `E`，确认只有一个确定持球者。
3. 持球时按住 `Space`，面板显示准备投篮；松开后篮球进入空中。
4. 在网络延迟环境下观察预测与纠正日志，确认回滚后双方 WorldHash 收敛。
5. 任一端按 `F9`，双方等待精彩片段收尾，并在同一终局帧进入回放。
6. 使用 `P`、`[`、`]`、`R` 验证播放控制；最后用 `Esc` 退出。

## 常见问题

- 客户端一直等待：检查服务器是否已启动，以及另一客户端是否已连接。
- 连接失败：确认没有残留服务器占用 8888 端口。
- 场景缺失：确认仓库包含 `Assets/Scenes/SampleScene.unity` 及其 `.meta`。
- 日志中的帧号不同：先区分网络帧与 Canonical Frame，再比较同一语义的帧。
