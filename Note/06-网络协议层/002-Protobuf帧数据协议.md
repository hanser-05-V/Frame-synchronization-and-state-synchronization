# 002 — Protobuf 帧数据协议

> 📊 **本篇配图：** 无独立配图（协议定义总览）

## 一句话定位

街篮2 使用 **Protocol Buffers (Protobuf)** 序列化所有网络帧数据。与 JSON/XML 相比，Protobuf 是二进制格式（体积小 3-10 倍），且有强类型 IDL（`.proto` 文件），改动协议时编译器自动检查一致性。主要协议在 `Common/Battle/proto_cs/` 下的 `.cs` 文件中（由 `.proto` 编译生成）。

---

## 设计意图

### 为什么不用 JSON？

JSON 在帧同步场景的问题：
- 字符串序列化慢（每帧都要序列化/反序列化）
- 体积大（字段名重复传输）
- 无类型检查（改协议容易漏改另一端的解析代码）

Protobuf 的优势：
- 二进制紧凑编码（varint 变长整数）
- 自动生成 C# 代码（改 `.proto` → 编译 → 两端同步更新）
- 向后兼容（新增字段不破坏旧协议）

---

## 协议结构

```csharp
// E:\帧同步\Common\Battle\proto_cs\pvp.cs
message PVPFrame {           // 服务器下发的一帧
    int32 frame;              // 帧序号
    repeated GamerInput inputs; // 所有玩家的输入
}

message GamerInput {         // 单个玩家的输入
    int32 playerId;          // 玩家ID
    int32 yaw;               // 移动方向
    int32 buttons;           // 按键bitmask
    int32 flag;              // 标记
    int32 emojiId;           // 表情
    int32 giveup;            // 投降
}

message GamerPVPPingC2S {    // 心跳/延迟测量
    int64 timeStamp;
}

message GamerPVPResultC2S {  // 比赛结果上报
    int32 result;
    int64 md5;               // MD5校验值
}
```

---

## 设计逻辑

### PVPFrame 到 FrameBuffer.Frame 的转换

```
服务器 Protobuf 包
  │
  ▼
RemoteBattleNetworkController.TryRecivePackages()
  ├── 解包 → PVPFrame 对象
  ├── 遍历 GamerInput → 转化为 FrameBuffer.Input
  │     Input._raw = PackBits(pos, yaw, btn, flag, emoji, giveup)
  ├── 组装 FrameBuffer.Frame {frame, playerCount, i0..i6}
  └── _frameQueue.Enqueue(frame)
```

---

## ⚠️ 实践注意

1. **`.proto` 文件不在本仓库**——只有编译后的 `.cs` 文件。修改协议需要找到原始 `.proto` 并重新编译。
2. **协议是客户端和服务器共享的**——`Common/` 下的 proto 代码编译后供两边使用。

---

## 关联笔记

- [[001-KCP协议与Socket层]] — Protobuf 包的传输层
- [[001-RemoteBattleController联网控制器]] — 怎么消费 PVPFrame

---

*记录时间：2026-06-30*
