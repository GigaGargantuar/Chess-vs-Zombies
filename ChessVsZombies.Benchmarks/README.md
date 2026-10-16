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

## Projectile processing experiments, 2026-10-16 (cloud)

The source baseline is `b55426794e386b6d6271d8e61cc99a2d61faf31d`. The user-reported frozen wave-95 save (180 Bishops, 371 queued enemies, tower attack reduced to one) and its newer native profiling harness are **not present in this checkout**. The older `SaveHordeScenario` above changes the encounter and approximates tower positions; it cannot establish equivalence with that report. No native wave-95 speedup or FPS result is claimed here.

The cloud probe is a separately labeled deterministic stress fixture: 40 actors (including eight giant cover carriers), 1,000 actual pellet/lob projectiles, and eight moving material blocks. It includes nonlethal impacts, two body deaths and 906 ordered contacts. Every sample starts from the same fresh state and calls production `UpdateProjectiles(0.05f)` once. Actor movement, turret firing, navigation and drawing are excluded from this kernel measurement. Setup is completed before timing all measured fixtures, followed by a full GC. Production gameplay has no actor cap or reduced update/decision rate.

```sh
DOTNET_TieredCompilation=0 dotnet run -c Release --project ChessVsZombies.Benchmarks -- --projectile-probe --count 1000 --warmup 5 --ticks 20
DOTNET_TieredCompilation=0 dotnet run -c Release --project ChessVsZombies.Benchmarks -- --projectile-probe --count 1000 --warmup 1 --ticks 1 --verify-output artifacts/projectile-performance/contacts.json
DOTNET_TieredCompilation=0 dotnet run -c Release --project ChessVsZombies.Benchmarks -- --scenario mixed --count 500 --warmup 60 --ticks 180 --verify-output artifacts/projectile-performance/mixed-trace.json
DOTNET_TieredCompilation=0 dotnet run -c Release --project ChessVsZombies.Benchmarks -- --scenario mixed --count 500 --warmup 60 --ticks 180 --profile
```

Use built binaries for timing after a separate build. `--work-counts`, `--verify-output` and `--profile` are separate diagnostic runs; do not compare their timing/GC results with ordinary runs. Profile stage timings are inclusive and must not be added together. Contact records identify each original shot in append order, its physical target/part, age at contact, damage and durability before/after. Per-tick state verification also captures individual body/armor/material HP, exact positions and trajectories, death/order state, terrain, liquid, inventory and RNG state; it does not accept aggregate HP totals or floating-point tolerances as equivalence.

Environment: Debian 13 x64 cloud VM, AMD EPYC 9V74, five exposed vCPUs, .NET runtime 8.0.32 / SDK 8.0.426, Release, `DOTNET_TieredCompilation=0`, seed 12345. All timing variants include the optional work-counter/contact hooks with diagnostics disabled. Their source versions are `177fc8d` (original algorithm plus harness), `bd9e11e` (liveness only), and `b645bef` (ordering/frame reuse). Runs were sequential, with no concurrent build, test or renderer. Two ordinary kernel runs per variant used five warmup samples and 20 measured samples each (40 measured samples per variant); the order was baseline/liveness/candidate, then candidate/baseline/liveness. Values below show the range of those two run summaries, not pooled percentiles.

| Kernel variant | Mean ms | Median ms | p95 ms | Maximum ms | Bytes/update |
| --- | ---: | ---: | ---: | ---: | ---: |
| Baseline | 1866.7–1929.6 | 1861.5–1937.0 | 1919.3–2063.9 | 1958.7–2075.5 | 13,019,709 |
| Body-liveness checks only | 1173.1–1269.4 | 1160.8–1260.9 | 1265.9–1325.4 | 1283.2–1356.8 | 13,019,709 |
| Liveness + stable order/frame predictions | 57.1–58.6 | 56.8–58.2 | 62.0–62.9 | 63.6–73.7 | 1,017,569 |

The liveness change replaces total-HP sums only where body survival is the question. Actual HP arithmetic, armor coverage and damage remain unchanged. The second change retains the original stable sorted suffix after HP-only impacts. Body death, held/flying part destruction, reactive hand motion, terrain or topology changes invalidate it. Exact repeated forecast queries can also share a result within the same flying-block frame; keys include projectile age, duration and scene revisions. Nothing is retained across body movement, guard placement, flying-block advancement or a different frame interval. The existing projectile paths, subdivision times, boundary tolerance, hit tests and tie rules are unchanged.

| Work per kernel sample | Baseline | Candidate |
| --- | ---: | ---: |
| Forecast evaluations | 513,601 | 18,961 |
| Pending sorts | 928 | 24 |
| Body / held-cover / flight queries (each) | 1,286,565 | 53,361 |
| Chronological slices | 22 | 22 |
| Resolved contacts | 906 | 906 |

All three variants have byte-identical ordered contact/state traces: SHA-256 `6F043A7969B4FC3C9BDF590CC1E02A8E5FAB226FA73F6D66B2E8D37D371F3F8A`. The 241 setup/warmup/measured states of the separate 500-actor mixed scenario also match exactly: trace SHA-256 `059777B1D2B901BDD119B85B56F3DB5A859025616C63F62245F0B48893F1A72F`. The kernel's individual contacts are flying-block, body and boundary contacts; held-cover/terrain changes and piercing are covered by focused invalidation and existing physical-contact regressions.

One ordinary full-update mixed run per variant (60 warmup + 180 measured updates, four simulated seconds total) measured baseline/liveness/candidate means of 16.850 / 14.298 / 14.084 ms, medians of 4.828 / 3.739 / 3.675 ms, and p95 of 86.726 / 84.293 / 86.918 ms. Maximums were 185.506 / 139.355 / 139.868 ms. This broader scenario retained 491 living actors and 21 towers identically. Its tail hitch remains; the much larger kernel gain must not be extrapolated to whole-game FPS. Further body/cover/flight candidate work remains (53,361 queries each in the dense probe). No new spatial index or simplified hitbox was introduced.

A separate instrumented candidate run measured inclusive mean stage costs of 5.03 ms in zombie movement, 4.67 ms in projectiles, and 3.94 ms in navigation rebuilds (21 rebuilds). Navigation time overlaps its callers; these values must not be summed. This identifies remaining work in the cloud mixed scenario, without assigning those costs to the unavailable native fixture.

The unmodified baseline has 1,665 passing tests and eight failing `CombatOverlayPerformanceTests` after main changed its production FPS thresholds. The final Release suite has 1,675 passing tests (ten added cases), with exactly the same eight failures and no new failing case. Its build has zero warnings/errors. Local raw measurements, exact traces, failure logs and the final validation summary are under `artifacts/projectile-performance/`; that folder is ignored by git. The earlier unpublished Mega/mobile-cover branch is preserved separately rather than replacing current main's newer game code.
