# CullExpiredFix

Server-side Harmony mod for 7 Days to Die dedicated server **2.6 (b14)**.
Cuts the cost of `RegionFileManager.CullExpiredChunks`. No client download.

Port of the 3.x build. `CullExpiredChunks`, its call site in `DoSaveChunks` and the
`List<long>.Contains` in its loop are identical in 2.6; only the chunk-grouping types differ.

| 3.x | 2.6 |
|---|---|
| `LongSetGroups chunkGroups`, `TryGetGroup`, `Dictionary<Group, uint> groupTimestamps` | `Dictionary<long, HashSetLong> groupsByChunkKey`, `Dictionary<HashSetLong, uint> groupTimestamps` |
| `groupTimestampsDirty` + `UpdateGroupTimestamps()` inside the sweep | neither exists: `SetChunkTimestamp` keeps group timestamps current on every save |
| `RemoveChunks(expired, true, false)` | `RemoveChunks(expired, true)` |

Nothing else changed. The numbers below were measured on the 3.1.0 server; 2.6 has not been
measured yet.

## Problem

`RegionFileManager.thread_SaveChunks` runs `DoSaveChunks()` once per saved chunk, and every
call unconditionally does:

```csharp
protectionLevelsDirty = true;
CullExpiredChunks();          // full O(N) sweep of chunksInSaveDir, under saveLock + chunksInSaveDir
```

That is byte-for-byte the same in 2.6.

So the whole save directory is walked once per saved chunk. Measured on a live server
(4 vCPU, 13 players, 105k saved chunks):

| | value |
|---|---|
| sweeps | 17.7 / s |
| cost per sweep | 26.3 ms |
| SaveChunks thread | 47% of one core, 93% of it in the sweep |
| chunks actually removed | 0.19 per sweep |
| scan time per chunk removed | 139 ms |
| `isChunkInSaveDir` callers blocked on the lock | 278 ms / s |

In-game benchmark (`proflog cullbench`, 104667 keys, 137 pending reset requests):

```
raw iterate             0.75 ms    7 ns/key
V0 vanilla replica     20.17 ms  193 ns/key
V1 +HashSet resetReq    4.77 ms   46 ns/key
V2 +KVP no relookup     3.60 ms   34 ns/key
```

76% of the sweep is `resetRequestedChunks.Contains(key)`. That field is a `List<long>`, so the
loop is O(keys x pending requests) — 14.3M comparisons per sweep, 254M per second.

## What the mod does

Replaces `CullExpiredChunks` with a prefix (`Priority.Last`, returns `false`). Two independent
switches:

- **fastScan** — same loop, three changes: `HashSet` instead of `List.Contains`, iteration over
  `KeyValuePair` so the timestamp comes from the enumerator instead of a second dictionary
  lookup, and the group-timestamp override applied inline. Expected 20.2 ms -> 3.6 ms.
- **throttle** — skips the sweep unless `intervalSeconds` have passed since the last one.
  At 30 s this turns 17.7 sweeps/s into 0.03/s.

Semantics are unchanged: same lock order, same 10000-chunk cap, same protection handling, same
`RemoveChunks(expired, true)` call, same `maxChunkAge < 0` branch. The only observable
difference is that an expired chunk may be removed up to `intervalSeconds` late — against a
`MaxChunkAge` of 7 in-game days (~7 real hours) that is a 0.12% delay.

`RequestChunkReset` and a `MaxChunkAge` game-pref change both force the next sweep to run
immediately, so admin `resetregion` commands are not delayed.

## Install

Copy `CullExpiredFix.dll`, `ModInfo.xml` and `Config.xml` to `Mods/CullExpiredFix/`.
Requires `0_TFP_Harmony`.

**Stop the server before replacing the DLL.** Overwriting it while the process runs leaves Mono
with a memory-mapped assembly whose not-yet-JITted methods read different bytes, which surfaces
as `BadImageFormatException: Method has zero rva` and kills the SaveChunks thread.

## Config.xml

| attribute | default | meaning |
|---|---|---|
| `fastScan` | `true` | use the mod scan loop |
| `throttle` | `true` | enforce a minimum gap between sweeps |
| `intervalSeconds` | `30` | that gap, 0..3600 |
| `logEverySeconds` | `600` | counter line in the server log, 0 disables; emitted in every mode |

## Commands

```
cullfix                  status and counters
cullfix reset            zero the counters
cullfix interval <sec>   minimum seconds between sweeps
cullfix throttle on|off
cullfix fastscan on|off
cullfix log <sec>        counter line interval, 0 disables
cullfix run              run one sweep on the next save cycle
cullfix stuck [n]        list reset requests that can never complete
cullfix save             write Config.xml
```

Setting `throttle off fastscan on` measures the scan fix alone; that is the useful A/B, because
with throttling on the scan cost stops being visible and a future growth of the pending-request
list would go unnoticed.

## Counter line

Written to the server log every `logEverySeconds`, and printed by `cullfix`:

```
[CullExpiredFix] fastScan=on throttle=on interval=30s logEvery=600s calls=144663 skipped=0 (0.0%)
scans=144663 vanillaScans=0 scanTotal=856068ms mean=5.92ms max=1030.41ms perKey=69ns removed=27883 fallbacks=0
```

| field | meaning |
|---|---|
| `calls` | entries into `CullExpiredChunks` |
| `skipped` | entries the throttle rejected |
| `scans` | sweeps that actually ran |
| `vanillaScans` | of those, how many ran the original method (`fastScan=off`) |
| `mean` / `max` / `perKey` | sweep cost, covering both implementations |
| `removed` | chunks removed; marked `(fast path only)` when `vanillaScans > 0`, because the count is not observable on the vanilla path |

`mean` and `perKey` include `UpdateChunkProtectionLevels`, which this mod does not change and
which is the larger share once `fastScan` is on.

## `cullfix stuck`

A chunk in `resetRequestedChunks` that carries only soft protection (`NearOfflinePlayer`,
`NearQuestObjective`, `OfflinePlayer`, `QuestObjective`, `CurrentlySynced`) is never reset and
never dropped from the list, because `UpdateResetRequest` only clears entries that have some
other protection. Those entries accumulate and each one adds a comparison per saved chunk per
sweep. The measured server had 137 of them. This is vanilla behaviour; the mod reports them but
does not remove them.

## Failure behaviour

- Missing field or method at init: nothing is patched, vanilla behaviour is kept, one `ERR` line
  names the member. `Init.Verify` checks 9 fields, 5 methods, the two-argument `RemoveChunks`
  overload and `GameUtils.WorldTimeToTotalMinutes` before any patch is applied.
- Exception inside the scan: logged once, the mod disables itself for the rest of the session
  and the vanilla method takes over from the next call. `cullfix` then reports `DISABLED` and
  `fallbacks=1`.

## Interaction with MaxChunkAgeDeadlockFix

That mod puts a `Prefix` + `Finalizer` on the same method for its reset logging. This mod's
prefix uses `Priority.Last`, so the marker prefix runs first and the finalizer still runs,
in either registration order. Verified against Harmony 2.13.

Because sweeps get batched, `[MaxChunkAgeDeadlockFix] Chunk reset` lines become fewer and each
lists more chunks. Total chunks reset is unchanged.

## Measured effect

**Measured on the 3.1.0 server, not on 2.6.** Carried over because the code path is the same;
treat it as the expected shape, not as a 2.6 result.

Live server, 4 vCPU, 90k-133k saved chunks. Vanilla 4.2 h, fastScan 12.1 h, throttle 3.5 h,
both 5.4 h. All figures are taken at a matched 9-10 players, because the sessions ran at
different populations and raw aggregates put the population difference on the mod.

| | sweeps/s | ms/sweep | ns/key | share of one core | `isChunkInSaveDir` blocked |
|---|---|---|---|---|---|
| vanilla | 17.7 | 26.3 | 250 | 46.6% | 278 ms/s |
| fastScan only | 10.9 | 6.3 | 53 | 6.9% | 12.5 ms/s |
| throttle only | 0.03 | 29-39 | 302-355 | 0.1% | 0.5 ms/s |
| both | 0.03 | 16.9 | 127 | 0.055% | 0.19 ms/s |

At 9-10 players the four modes give 51.450% / 5.794% / 0.100% / 0.055% of one core and
320.67 / 9.67 / 0.34 / 0.19 ms/s of blocking. Both switches together: 928x less CPU and 1715x
less lock blocking than vanilla.

| at 9-10 players | frame | fps |
|---|---|---|
| vanilla | 54.08 ms | 18.49 |
| fastScan only | 51.40 ms | 19.45 |
| throttle only | 51.52 ms | 19.41 |
| both | 51.86 ms | 19.28 |

The frame difference between the three mod modes is inside the noise of world state; all three
remove `CullExpiredChunks` from the frame and all three give ~2 ms over vanilla. The reason to
run both is what one sweep is made of:

| | keys | scan | ns/key | UpdateProtection | RemoveChunks | total |
|---|---|---|---|---|---|---|
| fastScan only | 90064 | 4.70 ms | 52 | 1.54 ms | 0.06 ms (0.3 chunks) | 6.31 ms |
| both | 132724 | 8.23 ms | 62 | 3.63 ms | 3.59 ms (67.0 chunks) | 16.89 ms |

Less than half of a throttled sweep is the scan. The rest is `UpdateChunkProtectionLevels`,
which this mod does not change, and `RemoveChunks`, which is the same removal work batched
instead of spread over 10k calls per hour. The `max` outliers, up to 1186 ms, are almost
entirely `UpdateChunkProtectionLevels`.

The two switches are orthogonal: `throttle` cuts how often a sweep happens, `fastScan` cuts what
one sweep costs and, more importantly, decouples that cost from the size of the save directory
and the pending-reset list. Over the combined run `resetRequestedChunks` grew from 103 to 125;
on the vanilla loop each entry adds one comparison per key per sweep (16.6M comparisons per
sweep at 133k keys), on the fast loop it is a `HashSet` probe and the growth is free. Throttling
alone does not defuse that, it only steps on it less often.

Reset throughput is not reduced by throttling. Catalogue balance per hour at matched population:
vanilla +1935, fastScan -1296, throttle only +9458, both +295. With both switches the directory
oscillates (123366 -> 138903 -> 124956 over the run) instead of running away. A sweep removes
~67 chunks against a 10000-chunk cap, so the cap never binds; throttling adds latency, not a
ceiling.

The counter line's `mean` and `perKey` divide the whole of `Run()` — scan plus
`UpdateChunkProtectionLevels` plus `RemoveChunks` — by the key count, so with `throttle` on they
read high (14.81 ms / 109 ns) against a scan that is actually 62 ns/key.
