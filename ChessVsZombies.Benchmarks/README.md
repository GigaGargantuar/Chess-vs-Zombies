# Live horde benchmark

Run from the repository root:

```powershell
dotnet run -c Release --project ChessVsZombies.Benchmarks -- --scenario all --count 500 --warmup 60 --ticks 180
```

The project references the production Core project and has no package dependencies. `--seed` defaults to `12345`; `--help` lists options. Use identical arguments, runtime, configuration and machine for comparisons. Run without a debugger or concurrent builds, and repeat runs to distinguish sustained improvements from timing noise.

Each scenario starts a real authored wave, then binds the production private `Spawn` method once to spawn the complete roster during setup. This removes gradual spawn ramp-up from the measurement without replacing navigation, combat, physics, equipment, timers or spawning rules. The benchmark fails if the roster cannot spawn or combat ends before all requested ticks run.

- `marching`: 500 durable Standard/Milli armored zombies approaching an undefended core.
- `defended`: the same horde against 24 legally placed Rooks/Knights and 16 physical dirt cover blocks.
- `mixed`: the defended setup with up to 10 Kilos and 5 Megas included in the requested total. Counts scale down for smaller `--count` values.

The default measurement covers three simulated seconds after one second of warmup. Armor uses ordinary Osmium equipment, keeping the horde alive through this short observation window. Longer runs can reach the core or defeat the defenders, so check the reported live counts and core HP. `--count` is the total roster size, including giants.

The harness measures only `GameSession.Update(1f / 60f)`, excluding setup, warmup and snapshots. It reports mean, median, p95 and maximum milliseconds per update; process-wide managed bytes allocated during each update, including navigation workers; collection counts; live population before and after; and completed simulation ticks. These are headless simulation CPU costs, not rendered FPS.

The initial and final SHA-256 snapshots include zombie positions, facing, next cells, movement progress, body and equipment durability, carried equipment, lanes and cooldowns; wave/tower/projectile state; selected private movement and ability state; giant equipment/projectiles; falling blocks; complete terrain materials and durability; liquids and buckets. The snapshot deliberately excludes navigation caches and diagnostic counters so implementations with different caches can be compared. Equal hashes provide a focused behavior comparison for these deterministic scenarios, not a proof that all gameplay states are equivalent.

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
