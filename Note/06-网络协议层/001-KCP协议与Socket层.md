# 001 — KCP 协议与 Socket 层

> 📊 **本篇配图：** 无独立配图（协议层总览）

## 一句话定位

街篮2 的网络层使用 **KCP（可靠 UDP）作为首选协议，TCP 作为降级**。KCP 在 UDP 之上实现了 ARQ（自动重传请求），比 TCP 延迟更低（牺牲 10-20% 带宽换取 30-40% 延迟降低），非常适合帧同步场景下大量小包（每帧几百字节）的实时传输。

---

## 设计意图

### 为什么不用纯 TCP？

TCP 的问题：
- **队头阻塞**：丢一个包，后续所有包都要等重传
- **拥塞控制过于保守**：丢包就降速，实时游戏帧同步不能接受突发的延迟尖峰
- **三次握手 + 四次挥手开销**

KCP 的改进：
- 可配置的重传策略（快重传）
- 不降速（RTO 不翻倍，只有 ×1.5）
- 选择性重传（只重传丢的包，不等后面）

---

## 接口层

```csharp
// Framework/Core/Net/KcpClient.cs
public class KcpClient : SocketClient
{
    + Connect(host, port) → bool
    + Send(data, length) → bool
    + Recv() → (data, length)
    + Update() — 驱动 KCP 状态机（IKCPUpdate）
}

// Framework/Core/Net/TcpSocketClient.cs
public class TcpSocketClient : SocketClient
{
    + Connect(host, port) → bool
    + Send(data) → bool
    + Recv() → (data)
}

// Framework/Core/Net/kcp/kcp.cs — 内嵌 KCP 实现
// 核心: IKCPUpdate 驱动重传/确认/窗口滑动
```

---

## 设计逻辑

### KCP 的核心配置

```
KCP 模式：无拥塞控制 (nodelay=1, interval=10ms, resend=2, nc=1)
  - nodelay=1: 启用快速模式
  - interval=10ms: 内部更新间隔
  - resend=2: 快速重传（2次ACK触发）
  - nc=1: 关闭拥塞窗口（不降速）
```

### 为什么帧同步适合 KCP？

帧同步每帧只发几百字节（6个玩家 × 4字节 = 24字节 + 协议头），包体积小但频率高（30个/秒）。KCP 的"牺牲带宽换延迟"策略在这种场景下几乎没带宽负担（每秒才 720 字节），但延迟降低效果显著。

---

## ⚠️ 实践注意

1. **KCP 需要手动 Update**——不像 TCP 在内核态自动处理，KCP 是用户态协议，需要在网络线程中持续调 `KcpClient.Update()`。
2. **RemoteBattleNetworkController 封装了 KCP**——游戏层不直接操作 `KcpClient`，通过 `RemoteBattleNetworkController` 收发帧数据。

---

## 关联笔记

- [[002-Protobuf帧数据协议]] — KCP 传输的包体是 Protobuf 序列化的 PVPFrame
- [[003-FrameEngine帧循环引擎]] — 网络线程驱动 KCP 的 Update

---

*记录时间：2026-06-30*
