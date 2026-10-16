# Live horde benchmark

Run from the repository root:

```powershell
dotnet run -c Release --project ChessVsZombies.Benchmarks -- --scenario all --count 500 --warmup 60 --ticks 180
```

The project references the production Core project and has no package dependencies. `--seed` defaults to `12345`; `--help` lists options. Use identical arguments, runtime, configuration and machine for comparisons. Run without a debugger or concurrent builds, and repeat runs to distinguish sustained improvements from timing noise.

To reproduce the checkpoint-based 409-turret / 300-zombie rendered smoke scenario headlessly:

```powershell
dotnet run -c Release --project ChessVsZombies.Benchmarks -- --save-path artifacts/save-horde/checkpoint.json --turrets 409 --count 300 --warmup 30 --ticks 150
```

Both frontends use `SaveHordeScenario.Create`. The checkpoint is loaded without saving it. Missing turrets copy the saved tower mix, HP, upgrade allocations and jump patterns into the nearest legal supported cells; their historical positions are unknown. The checkpoint's next five levels supply the combined campaign encounter and equipment, production spawning creates exactly 300 initial enemies, and later arrivals are held. `--seed` applies to the authored scenarios; checkpoint loading retains the save loader's production seed behavior.

For an exact trace comparison between two assemblies, run the same command separately with `--verify --verify-output artifacts/perf409/baseline-trace.json` (and a distinct optimized path). This hashes setup and every warmup/measured tick. Equal trace hashes establish equal captured state at every observed tick. Hashing is excluded from update timings, but its allocations and collections affect process GC totals and may influence subsequent updates; use verification runs only for behavior comparison and separate ordinary runs for timing.

Each scenario starts a real authored wave, then binds the production private `Spawn` method once to spawn the complete roster during setup. This removes gradual spawn ramp-up from the measurement without replacing navigation, combat, physics, equipment, timers or spawning rules. The benchmark fails if the roster cannot spawn or combat ends before all requested ticks run.

- `marching`: 500 durable Standard/Milli armored zombies approaching an undefended core.
- `defended`: the same horde against 24 legally placed Rooks/Knights and 16 physical dirt cover blocks.
- `mixed`: the defended setup with up to 10 Kilos and 5 Megas included in the requested total. Counts scale down for smaller `--count` values.

The default measurement covers three simulated seconds after one second of warmup. Armor uses ordinary Osmium equipment, keeping the horde alive through this short observation window. Longer runs can reach the core or defeat the defenders, so check the reported live counts and core HP. `--count` is the total roster size, including giants.

The harness measures only `GameSession.Update(1f / 60f)`, excluding setup, warmup and snapshots. It reports mean, median, p95 and maximum milliseconds per update; process-wide managed bytes allocated during each update, including navigation workers; collection counts; live population before and after; and completed simulation ticks. These are headless simulation CPU costs, not rendered FPS.

The initial and final SHA-256 snapshots include zombie positions, facing, next cells, movement progress, body and equipment durability, carried equipment, lanes and cooldowns; wave/tower/projectile state; private movement, group, progression, retreat, construction and ability state; giant equipment/projectiles; falling block and actor motion tracks; complete terrain materials and durability; liquids and buckets; economy/inventory/upgrade batch state; list iteration order; and opaque RNG state. Snapshot version 2 extends the original format, so its hashes must be compared with other version-2 runs on the same .NET runtime. The snapshot deliberately excludes navigation caches, feedback overlays and diagnostic counters so implementations with different caches can be compared. Equal hashes provide a focused behavior comparison for these deterministic scenarios, not a proof that all gameplay states are equivalent.

## Measured optimization results, 2026-10-15

Windows 10 x64, .NET 8.0.31, Release, seed 12345, 500 simultaneous spawns, 60 warmup updates and 180 measured updates. The baseline is the Core assembly captured before the AI changes. These are individual sequential runs on the same machine; timing varies between runs.

| Scenario | Mean ms before / after | p95 ms before / after | Allocated bytes per update before / after |
| --- | ---: | ---: | ---: |
| Marching | 13.341 / 2.726 | 21.143 / 3.986 | 13,097,041 / 54,313 |
| Defended | 15.712 / 1.957 | 21.697 / 2.568 | 14,034,320 / 62,456 |
| Mixed giants | 26.551 / 12.185 | 78.417 / 69.558 | 17,397,316 / 991,427 |

All six initial/final state hashes match the baseline. Marching and defended scenarios keep all 500 alive throughout; the mixed scenario ends with 493, identically before and after. The mixed case still has navigation rebuild spikes when terrain changes; its maximum measured update was 180.216 ms after optimization, compared with 149.645 ms in this baseline run. The improvements do not establish a fixed frame-time guarantee.

The changes stop wave-population checks after the five-member decision is known, remove HP and dry-liquid query allocations, retain navigation workspaces, avoid redundant geometry checks, and build the four independent standard approach fields in parallel. Exact movement, collision, breach costs, route tie choices and simulation time remain covered by state comparisons and complete navigation regression fields. The full suite passes 1,365 tests.

The native `--smoke-horde --capture-frame 180` check also kept 500 actors alive. After 30 startup frames, core updates averaged 3.823 ms (p95 13.534 ms), while rendered-frame intervals averaged 50.514 ms. Presentation/rendering is a separate remaining bottleneck; headless update gains do not imply 60 rendered FPS. A capture writes an adjacent `.metrics.json` with the timings and population. Raw local measurements are in `artifacts/ai-baseline.txt`, `artifacts/ai-optimized-final.txt`, and `artifacts/horde-500-final.metrics.json`.

## Checkpoint profiling, 2026-10-16

The 409-turret/300-zombie case uses the frozen checkpoint with SHA-256 `14D409B167421B112C7ABDD5A07817F7D3A5E928BD405D2C2CD807EA42F70A53`. It retains 297 saved Bishops and their upgrades, then places 112 supported copies near the defense. This approximates the missing pre-battle towers; their historical positions were not saved. The map contains 184 liquid cells. The staged battle peaks at 432 simultaneous shots.

Collect native CPU stage timings with:

```powershell
dotnet run -c Release --project ChessVsZombies -- --smoke-save-horde --save-path artifacts/save-horde/checkpoint.json --profile-smoke --capture-frame 240 --capture artifacts/perf409/profile.png
```

The adjacent metrics file separates simulation stages and renderer work. Simulation stages are inclusive: navigation called from zombie movement also appears in `NavigationRebuild`, so those rows must not be added together. Renderer timings include CPU geometry and submission work; rendered-frame intervals include presentation and frame limiting. Setup and the first 30 frames are excluded. Run timings separately from tests, state hashing, geometry verification and diagnostic tracing.

Sampling traces confirmed repeated linear turret lookup in footing/support checks as the largest initial managed-code hotspot. Other measured costs were repeated giant aim scans, exact projectile tests against every zombie, navigation rebuilds after terrain changes, and rebuilding/copying turret geometry each frame. The optimized path shares exact cell lookups within an update, retains first-living-match and public mutation behavior, reuses the selected giant aim and upgrade ranges, rejects distant projectile bodies before unchanged exact tests, and skips empty navigation scan rows while retaining the original breach/cost order. The renderer caches exact turret geometry, reuses upload arrays and armor snapshots, and combines vertex color/position passes.

For byte-level renderer verification, add `--verify-render-geometry` to a smoke run. It regenerates the uncached turret mesh and original separate vertex transform/color loops and rejects any byte mismatch. This mode is intentionally excluded from timing. `--uncached-turrets` disables the turret cache only in smoke runs for native A/B captures. The focused cache tests cover selection, upgrades, membership/order, world changes and continuously falling positions.

Windows 10 x64, .NET 8.0.31, Release, same frozen checkpoint, 240 native frames with 30 startup frames excluded; individual sequential runs without concurrent builds/tests:

| Measured work | Before | After |
| --- | ---: | ---: |
| Mean rendered-frame interval | 49.34 ms | 31.96 ms |
| Median rendered-frame interval | 37.61 ms | 20.80 ms |
| p95 rendered-frame interval | 101.96 ms | 78.35 ms |
| Mean simulation update in native run | 29.36 ms | 18.79 ms |
| Mean renderer CPU work/submission | 12.50 ms | 6.17 ms |
| Turret geometry generation/cache check | 2.04 ms | 0.019 ms |
| Dynamic vertex array copy | 2.50 ms | 0.36 ms |
| Dynamic geometry rebuilt per frame | 442,755 vertices | 233,271 vertices |

The frame interval improvement is about 35%. A separate headless run (30 warmup, 210 measured updates) changed mean update time from 29.73 to 19.17 ms and median from 18.55 to 9.37 ms. Headless managed allocations remained approximately 3.25 MB/update; liquid volume/surface queries still allocate, so no large simulation allocation reduction is claimed. Bucket route storage is retained across rebuilds rather than discarded.

All 241 setup/warmup/measurement state hashes match the baseline exactly: trace SHA-256 `84E0E4AF86F80AACAD69E03C73196114BE7FFB4E34080CF9C078574A008D3CB4`. The capture PNG is byte-identical before/after, and a separate 90-frame run passed 46,890 raw vertex verification checks. The Release suite passes 1,425 tests, including exact navigation fields, scalar/masked sweep costs, exhaustive/projectile broad-phase comparisons, public turret mutations, collapse support and repeated liquid-route rebuilds. The save checkpoint hash remains unchanged.

These improvements do not establish 60 FPS or remove all hitches: the final native run still has a 234 ms maximum update, down from 261 ms, with large navigation/liquid-route rebuilds remaining expensive after terrain changes. Update cadence, routes, target/contact tie order, floating-point hit calculations, damage, collision and simulation time were preserved. Raw local captures/metrics, traces, baseline assemblies and verification files are under `artifacts/perf409/`.
