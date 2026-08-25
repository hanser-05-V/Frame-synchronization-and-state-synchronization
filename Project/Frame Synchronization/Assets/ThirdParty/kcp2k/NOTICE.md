# kcp2k Core Notice

Route C vendors the pure KCP core from
[MirrorNetworking/kcp2k](https://github.com/MirrorNetworking/kcp2k) at commit
`66efda6686f649838d42f078fbaabf56ac449de4` under the upstream MIT license.

Imported files:

- `kcp2k/kcp2k/kcp/AckItem.cs`
- `kcp2k/kcp2k/kcp/Kcp.cs`
- `kcp2k/kcp2k/kcp/Pool.cs`
- `kcp2k/kcp2k/kcp/Segment.cs`
- `kcp2k/kcp2k/kcp/Utils.cs`
- `LICENSE`

The kcp2k high-level client, server, connection, socket, threading, Mirror
integration, tests, and `AssemblyInfo.cs` are intentionally excluded. Route C
provides its own transport, session, socket-ownership, and scheduling layers.

Original SHA-256 values are recorded in `PATCHES.md` and match the raw Git
blobs at the pinned commit before the Route C interval patch is applied.
