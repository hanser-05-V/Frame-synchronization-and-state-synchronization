# Route C kcp2k Patches

Upstream repository: `https://github.com/MirrorNetworking/kcp2k`

Pinned commit: `66efda6686f649838d42f078fbaabf56ac449de4`

## Original SHA-256

| File | Raw Git blob SHA-256 |
|---|---|
| `AckItem.cs` | `38202E24E2BF8D5D36A7478FDE42C1D90ED84210018720B005E5FA79BB02617A` |
| `Kcp.cs` | `3C493FAA7981074899EE0283174350004BB25FBE294F0BB2DA16E2D4C4DC7F0C` |
| `Pool.cs` | `8B7F44E2E7FCC4DE9C12964178545E3350532939C09101632982909B42ADF1D5` |
| `Segment.cs` | `A62E0B9B5C824D4728F697A68DAA21816716623427EBFEB555B44BA82E87EBA7` |
| `Utils.cs` | `26853EC81350DA5402E9D281A977CC92DAC711BA9FB02A1BA9137FE60311E339` |
| `LICENSE` | `1A12B7192346C004F7C3E5CE90D652750AA0FE7A29DF67CAC5F6271433808FE0` |

## Interval patch

Patched file: `Core/Kcp.cs`

Patched SHA-256:
`3100D01FCD9DAFA8EDD7807A8B55E4B21F5D8E21663BB023C79E6CE068510D50`

Exactly two methods are changed:

- `Kcp.SetInterval`
- `Kcp.SetNoDelay`

In both methods the minimum interval clamp changes from 10 milliseconds to 1
millisecond. The adjacent comments identify the Route C experiment patch. No
KCP retransmission, congestion, window, segmentation, acknowledgement, or
business-layer logic is changed.

This patch is required because the pinned upstream core otherwise turns the
Route C `1/5/10/20ms` experiment into an effective `10/10/10/20ms` matrix.
`KcpVendorProvenanceTests` verifies that 1ms and 5ms remain observable through
the real `Update`/`Check` behavior.

## Verification

Hash the raw Git blobs at the pinned commit before applying the patch. After
applying it, verify that every unmodified source and `LICENSE` retains its
original SHA-256, `Core/Kcp.cs` has the patched SHA-256 above, and a source diff
contains only the two documented clamp/comment blocks.
