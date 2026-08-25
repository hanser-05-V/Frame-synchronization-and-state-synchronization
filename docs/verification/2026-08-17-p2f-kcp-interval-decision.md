# P2-F Task 11 — KCP Interval Decision

## Decision

Select **10 ms** as the Task 11 KCP interval recommendation for Route C.

The former 1 ms recommendation is invalidated and replaced by the evidence generated on 2026-08-24. The former matrix did not prove the KCP core's actual interval independently and its deterministic drop sequence injected zero drops. The replacement matrix reads the real kcp2k core interval on all server and client sessions, launches the server executable through `--transport kcp --kcp-interval-ms`, and requires a non-zero number of actual UDP datagram drops in every run.

Task 11 records the decision only. Applying 10 ms as a product default belongs to the separately approved follow-on task.

## Test conditions and gates

- Same Windows machine and same temporary server candidate for all runs.
- Intervals: 1, 5, 10, and 20 ms.
- Three balanced-order runs per interval; 12/12 runs passed.
- Every run launched the real server executable with the requested KCP CLI interval.
- Two real UDP clients, 900 eight-byte business inputs per client, and 33 ms logical cadence.
- Deterministic 2% client-to-server UDP datagram loss with seed `20260824`; every run recorded 41–58 intentional drops.
- Every run required 900/900 delivery on each client, strict ascending order, zero missing inputs, and zero duplicate inputs.
- Requested interval and independently read kcp2k core interval had to equal the candidate on all four sessions.
- Every 1 ms and 5 ms run required sub-10 ms observed update samples in every server and client session.
- Every run retained a summary plus a chronological 1,800-event JSONL trace, stopped its exact owned server process, and released port 8888.

## Median results across three runs

| Interval | Relay p50 / p95 / p99 (ms) | UDP datagrams / bytes | KCP outputs / updates | Retransmitted segments | WaitSnd high-water | CPU (% one core) | Actual update gap p50 / p95 / p99 (ms) |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 ms | 15.83 / **17.44** / 62.12 | 4,887 / 338,628 | 4,946 / 10,290 | **53** | 4 | 2.57 | **15.21** / **16.09** / **16.21** |
| 5 ms | 16.07 / 30.85 / 61.45 | 4,010 / 315,080 | 4,061 / **8,244** | 75 | 4 | 1.76 | 15.57 / 16.14 / **16.26** |
| **10 ms** | **16.06 / 30.83 / 49.18** | 3,994 / 314,376 | 4,045 / 8,326 | 67 | **3** | **1.56** | 15.54 / **16.12** / 16.27 |
| 20 ms | 16.40 / 47.52 / 63.80 | **3,806 / 309,552** | **3,856** / 10,337 | 80 | 5 | 1.71 | 15.17 / 31.34 / 31.98 |

All four candidates passed correctness. The choice is therefore a latency/overhead tradeoff, not a correctness distinction.

The UDP datagram/byte columns are client-observed successful Socket send/receive totals. KCP output counters are reported separately and can differ because handshake envelopes and intentionally dropped output attempts are not the same measurement boundary.

## Tradeoff analysis

The 10 ms candidate produced the best median p99 relay latency: 49.18 ms. Compared with 1 ms, it reduced p99 by about 20.8%, UDP datagrams by about 18.3%, bytes by about 7.2%, KCP update calls by about 19.1%, and measured worker CPU by about 39.1%. The 1 ms candidate did retain the best p95 and fewest retransmitted segments, so it is not dominated on every metric; however, its much higher wire/update/CPU cost did not buy the lowest measured tail.

Compared with 5 ms, 10 ms reduced measured CPU by about 11.4%, datagrams and bytes slightly, retransmissions from 75 to 67, and WaitSnd high-water from 4 to 3. Its KCP update-call median was about 1.0% higher, but p50/p95 were effectively equal and median p99 improved by about 20.0%. Individual p99 ranges overlapped—10 ms measured 48.74/61.99/49.18 ms and 5 ms measured 61.45/62.13/47.64 ms—so the decision rests on the predeclared three-run median plus lower aggregate overhead, not a claim that every 10 ms run is faster.

The 20 ms candidate saved about 4.7% of datagrams and 1.5% of bytes relative to 10 ms, but its median p99 was about 29.7% higher, its observed update-gap tail roughly doubled, and it used more CPU, updates, retransmissions, and queue depth. That is a material tail/stability degradation, so the formal preference for a larger interval does not extend from 10 ms to 20 ms.

The 10 ms candidate is therefore the measured balance: lowest median p99, lower overhead than 1/5 ms, and no material tail sacrifice for choosing the larger value over 5 ms.

## Scheduler interpretation

Requested interval, KCP core interval, KCP `Check` deadline, and observed worker update gap are separate measurements:

- Requested interval is the CLI/configuration value.
- Core interval is read from the actual kcp2k object and proves that the session accepted the candidate.
- `Check` delta is a dynamic next-deadline value and is not used as a substitute for the core interval.
- Observed update gap includes Windows scheduling and timer coalescing.

The roughly 15–16 ms median update gaps for 1, 5, and 10 ms show that Windows frequently coalesced worker wakeups on this machine. The required per-session sub-10 ms samples prove that 1 ms and 5 ms were not merely relabelled 10 ms sessions, but the absolute measurements remain machine-specific. Remote-network and other-OS measurements are still needed before treating these figures as production capacity guarantees.

## TCP control

The refreshed TCP control passed 1,800/1,800 deliveries with zero missing, zero duplicates, and strict ascending order. Under the deterministic recovered-loss application profile, its relay latency was 15.55 / 62.58 / 110.09 ms at p50 / p95 / p99.

This is not an apples-to-apples transport-loss comparison. The KCP matrix drops real UDP datagrams; the TCP control models recovered loss by delaying application delivery. The evidence labels the fault semantics separately, and TCP numbers were not used to select the KCP interval.

## Evidence

- `p2f-kcp-interval-summary.json`
- `p2f-kcp-interval-{1|5|10|20}ms-run{1|2|3}-summary.json`
- `p2f-kcp-interval-{1|5|10|20}ms-run{1|2|3}.jsonl`
- `p2f-kcp-live-clean.log`
- `p2f-kcp-live-reconnect.log`
- `p2f-kcp-live-resume-rejected.log`
- `p2f-tcp-control-summary.json`
