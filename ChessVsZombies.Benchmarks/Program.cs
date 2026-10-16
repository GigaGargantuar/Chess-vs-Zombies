using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChessVsZombies.Core;

return Run(args);

static int Run(string[] arguments)
{
    try
    {
        if (arguments.Contains("--projectile-probe")) return ProjectileProbe.Run(arguments, Snapshot);
        var options = Options.Parse(arguments);
        if (options.Help)
        {
            Console.WriteLine("Usage: dotnet run -c Release --project ChessVsZombies.Benchmarks -- [--scenario marching|defended|mixed|all] [--save-path checkpoint.json --turrets 409 --count 300] [--warmup 60] [--ticks 180] [--seed 12345] [--verify] [--verify-output trace.json] [--profile]; or --projectile-probe [--count 1000 --warmup 5 --ticks 20 --work-counts --verify-output trace.json]");
            return 0;
        }

        Console.WriteLine($"Horde benchmark: .NET {Environment.Version}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; Stopwatch={Stopwatch.Frequency} Hz");
        Console.WriteLine("One production GameSession.Update(1/60f) per tick; setup, spawning, warmup and snapshots excluded. No rendering.");
        Console.WriteLine($"count={options.Count}, warmup={options.Warmup}, measuredTicks={options.Ticks}, seed={options.Seed}");
        if (options.SavePath != null)
            Console.WriteLine($"checkpointApproximation={Path.GetFullPath(options.SavePath)}; towers={options.Turrets}; destroyed tower positions approximated by nearest legal supported cells; later arrivals held");
        if (options.Verify) Console.WriteLine("Per-tick verification enabled; hashes and file output are outside update timings. Use a separate run for representative allocation/GC measurements.");
        foreach (var scenario in options.SavePath != null ? new[] { "save-horde" } : options.Scenario == "all" ? new[] { "marching", "defended", "mixed" } : [options.Scenario])
            Measure(scenario, options);
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Benchmark failed: {error.Message}");
        return 1;
    }
}

static void Measure(string scenario, Options options)
{
    const float dt = 1f / 60f;
    var game = CreateScenario(scenario, options);
    if (options.Profile) game.Profile = new SimulationProfile();
    int spawned = game.Zombies.Count(zombie => zombie.Hp > 0);
    List<TickSnapshot>? trace = options.Verify ? [] : null;
    void RecordTick(string stage, int tick)
    {
        if (trace == null) return;
        trace.Add(new TickSnapshot(stage, tick, Snapshot(game), game.Zombies.Count(z => z.Hp > 0),
            game.Turrets.Count(t => t.Hp > 0), game.Projectiles.Count, game.CoreHp, game.Phase));
    }
    RecordTick("setup", 0);
    for (int i = 0; i < options.Warmup; i++)
    {
        RequireCombat(game, scenario, "warmup", i);
        game.Update(dt);
        RecordTick("warmup", i + 1);
    }
    int liveBegin = game.Zombies.Count(zombie => zombie.Hp > 0);
    if (options.Profile)
    {
        game.Profile!.Reset();
        game.ProjectileDiagnostics = new();
    }
    string initialHash = Snapshot(game);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var elapsed = new double[options.Ticks];
    long allocated = 0;
    int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    int completed = 0;
    for (int i = 0; i < options.Ticks; i++)
    {
        RequireCombat(game, scenario, "measurement", i);
        long allocationStart = GC.GetTotalAllocatedBytes(precise: true);
        long start = Stopwatch.GetTimestamp();
        game.Update(dt);
        long stop = Stopwatch.GetTimestamp();
        allocated += GC.GetTotalAllocatedBytes(precise: true) - allocationStart;
        elapsed[i] = (stop - start) * 1000d / Stopwatch.Frequency;
        completed++;
        RecordTick("measurement", i + 1);
    }
    for (int generation = 0; generation < collections.Length; generation++)
        collections[generation] = GC.CollectionCount(generation) - collections[generation];
    int liveEnd = game.Zombies.Count(zombie => zombie.Hp > 0);
    string finalHash = Snapshot(game);
    Array.Sort(elapsed);
    double median = elapsed.Length % 2 == 0
        ? (elapsed[elapsed.Length / 2 - 1] + elapsed[elapsed.Length / 2]) / 2
        : elapsed[elapsed.Length / 2];
    double p95 = elapsed[(int)Math.Ceiling(elapsed.Length * .95) - 1];
    Console.WriteLine(FormattableString.Invariant($"scenario={scenario} spawned={spawned} liveBegin={liveBegin} liveEnd={liveEnd} completed={completed}/{options.Ticks} simulatedSeconds={(options.Warmup + completed) * (double)dt:F3} coreHp={game.CoreHp:R} phase={game.Phase} towers={game.Turrets.Count(t => t.Hp > 0)}"));
    Console.WriteLine(FormattableString.Invariant($"  ms/update mean={elapsed.Average():F3} median={median:F3} p95={p95:F3} max={elapsed[^1]:F3}; allocatedBytes/update={allocated / (double)completed:F0}; GC={collections[0]}/{collections[1]}/{collections[2]}"));
    Console.WriteLine($"  initialSha256={initialHash}");
    Console.WriteLine($"  finalSha256={finalHash}");
    if (options.Profile)
    {
        Console.WriteLine("  inclusiveStages=" + JsonSerializer.Serialize(game.Profile!.Report(completed)));
        var d = game.ProjectileDiagnostics!;
        Console.WriteLine("  projectileWork=" + JsonSerializer.Serialize(new { d.ForecastRequests, d.ForecastEvaluations,
            d.PendingSorts, d.BodyQueries, d.CoverQueries, d.FlightQueries, d.Slices }));
    }
    if (trace != null)
    {
        byte[] traceBytes = JsonSerializer.SerializeToUtf8Bytes(trace);
        Console.WriteLine($"  snapshotVersion=2 verifiedStates={trace.Count} traceSha256={Convert.ToHexString(SHA256.HashData(traceBytes))}");
        if (options.VerifyOutput != null)
        {
            string path = Path.GetFullPath(options.VerifyOutput);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, traceBytes);
            Console.WriteLine($"  trace={path}");
        }
    }
}

static GameSession CreateScenario(string scenario, Options options)
{
    if (options.SavePath != null) return SaveHordeScenario.Create(options.SavePath, options.Turrets, options.Count);
    var groups = new List<ZombieGroup>();
    int kilos = scenario == "mixed" ? Math.Min(10, options.Count / 30) : 0;
    int megas = scenario == "mixed" ? Math.Min(5, options.Count / 60) : 0;
    int ordinary = options.Count - kilos - megas;
    int millis = ordinary / 5;
    void Add(int count, ZombieSize size, ZombieKind kind, GearTier tier, bool boss = false)
    {
        if (count > 0) groups.Add(new ZombieGroup { Count = count, Size = size, Kind = kind, Tier = tier, IsBoss = boss });
    }
    // Exact authored quotas avoid budget rounding and guarantee the requested live population.
    // Durable armor keeps the defended benchmark focused on sustained live AI, without synthetic HP.
    Add(ordinary - millis, ZombieSize.Standard, ZombieKind.ArmedArmored, GearTier.Osmium);
    Add(millis, ZombieSize.Milli, ZombieKind.Armored, GearTier.Osmium);
    Add(kilos, ZombieSize.Kilo, ZombieKind.ArmedArmored, GearTier.Osmium, boss: true);
    Add(megas, ZombieSize.Mega, ZombieKind.ArmedArmored, GearTier.Osmium, boss: true);
    var catalog = new LevelCatalog();
    catalog.Set(new LevelDefinition
    {
        Number = 1,
        Name = $"{options.Count}-zombie benchmark: {scenario}",
        Waves = [new WaveDefinition { SpawnInterval = .28f, Groups = groups }]
    });
    var game = new GameSession(options.Seed, catalog);
    if (scenario != "marching")
    {
        // A reproducible legal defense built through the same APIs as normal play.
        foreach (int z in new[] { 5, 10, 21, 26 })
            foreach (int x in new[] { 3, 7, 11, 20, 24, 28 })
            {
                var kind = (x + z) % 2 == 0 ? PieceKind.Rook : PieceKind.Knight;
                if (!game.Deploy(kind, new Cell(x, 16, z))) throw new InvalidOperationException(game.Message);
            }
        game.Inventory.Add(Material.Dirt, 32);
        foreach (int axis in new[] { 6, 12, 19, 25 })
            foreach (var cell in new[] { new Cell(5, 16, axis), new Cell(26, 16, axis), new Cell(axis, 16, 5), new Cell(axis, 16, 26) })
                if (!game.BuildBarrier(cell)) throw new InvalidOperationException(game.Message);
    }
    if (!game.StartLevel()) throw new InvalidOperationException(game.Message);
    // Bind the real spawn operation once. Setup fills the whole authored wave without
    // advancing the simulation or changing combat timers, positions or AI decisions.
    var spawn = typeof(GameSession).GetMethod("Spawn", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<Func<WaveState, bool>>(game);
    var wave = game.Waves.Single();
    while (wave.Spawned < wave.TotalCount)
        if (!spawn(wave)) throw new InvalidOperationException($"Only {wave.Spawned}/{wave.TotalCount} zombies could spawn in {scenario}.");
    if (game.Zombies.Count != options.Count) throw new InvalidOperationException("Authored roster does not match --count.");
    return game;
}

static void RequireCombat(GameSession game, string scenario, string stage, int tick)
{
    if (game.Phase != GamePhase.Combat)
        throw new InvalidOperationException($"{scenario} ended during {stage}, before tick {tick}. Reduce --ticks/--warmup to measure a live horde.");
}

static string Snapshot(GameSession game)
{
    var terrain = new List<object>(WorldGrid.Size * WorldGrid.Size * WorldGrid.Size);
    for (int x = 0; x < WorldGrid.Size; x++)
        for (int y = 0; y < WorldGrid.Size; y++)
            for (int z = 0; z < WorldGrid.Size; z++)
            {
                var cell = new Cell(x, y, z);
                terrain.Add(new { cell, Material = game.World.GetMaterial(cell), NaturalMaterial = game.World.GetNaturalMaterial(cell), Hp = game.World.GetTerrainHp(cell), Barrier = game.World.GetBarrier(cell), Obsidian = game.World.GetObsidian(cell) });
            }
    // Explicit behavior fields exclude implementation caches and profiling counters.
    // Positions, next cells, movement progress, facing, all body/armor HP, carried
    // equipment, buckets, lane, and cooldown are captured by Zombie's public state.
    string[] behaviorFields =
    [
        "_seed", "_nextId", "_stepOrigins", "_jumpHeights", "_fallStartHeights", "_liquidDestinations", "_liquidOrigins",
        "_coverWaiting", "_coverAdvanceOrigins", "_coverGroups", "_coverRally", "_kiloActionCooldowns", "_megaActionCooldowns",
        "_hurledAllies", "_kiloBlocks", "_megaFlights", "_megaGuardMemory", "_retreating", "_guardedKilos",
        "_retreatObservedPositions", "_strandedSeconds", "_retreatNavigationTime", "_zombieProgress", "_builderYields",
        "_returningZombies", "_pendingBlockDrops", "_liquidInventory", "_sourceLevelNumbers", "_nextSourceLevelNumbers",
        "_allInOnePlan", "_wavePlans"
    ];
    var behavior = behaviorFields.ToDictionary(name => name, name => Canonical(typeof(GameSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)));
    var snapshot = new
    {
        SnapshotVersion = 2,
        game.CoreHp, game.Phase, game.LevelNumber, game.MergeFiveLevels, game.AllInOne,
        Economy = game.Economy.ExportState(),
        Inventory = game.Inventory.ExportState(),
        game.UpgradeTemplate, game.QueuedUpgradeBatch,
        ZombieOrder = game.Zombies.Select(z => z.Id).ToArray(),
        TurretOrder = game.Turrets.Select(t => t.Id).ToArray(),
        Zombies = game.Zombies.OrderBy(z => z.Id).Select(z => new
        {
            State = z, Hiding = game.IsZombieHiding(z), Hurled = game.IsZombieHurled(z), Retreating = game.IsZombieRetreating(z),
            Private = SelectedFields(z, "_guardFacing", "_carriedBlocks", "_carriesBucket")
        }).ToArray(),
        Turrets = game.Turrets.OrderBy(t => t.Id).ToArray(),
        game.Waves, game.Projectiles, game.KiloCovers, game.MegaWeapons, game.MegaProjectiles,
        Falls = SelectedFields(game.Falls, "_blocks", "_turrets", "_zombies"),
        Liquids = game.Liquids.ExportState(),
        Buckets = game.Buckets.ExportState(),
        Terrain = terrain,
        Behavior = behavior,
        Random = PrivateState(typeof(GameSession).GetField("_random", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game))
    };
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions { IncludeFields = true });
    return Convert.ToHexString(SHA256.HashData(bytes));
}

static object? Canonical(object? value)
{
    if (value is null || value is string || value.GetType().IsPrimitive || value.GetType().IsEnum || value is decimal or Vector3 or Cell) return value;
    if (value is IDictionary dictionary)
        return dictionary.Keys.Cast<object>().OrderBy(key => Convert.ToString(key, CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .Select(key => new { Key = Canonical(key), Value = Canonical(dictionary[key]) }).ToArray();
    if (value is IEnumerable sequence)
    {
        var items = sequence.Cast<object?>().Select(Canonical).ToArray();
        return value.GetType().IsGenericType && value.GetType().GetGenericTypeDefinition() == typeof(HashSet<>)
            ? items.OrderBy(item => JsonSerializer.Serialize(item), StringComparer.Ordinal).ToArray() : items;
    }
    var members = new SortedDictionary<string, object?>(StringComparer.Ordinal);
    foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
    {
        if (!property.CanRead || property.GetIndexParameters().Length != 0 || property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always) continue;
        // Gathering routes are a recomputable implementation cache. The group's
        // leader, threat, anchor and assembled time are actual decision state.
        if (value.GetType().Name == "CoverGroup" && property.Name == "RoutesByScale") continue;
        members[property.Name] = Canonical(property.GetValue(value));
    }
    foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
        members[field.Name] = Canonical(field.GetValue(value));
    return members;
}

static object SelectedFields(object value, params string[] names) => names.ToDictionary(name => name,
    name => PrivateState(value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)));

// Used only for opaque RNG state and physical fall tracks. No properties are
// evaluated, and no random value or simulation time is consumed by a snapshot.
static object? PrivateState(object? value)
{
    if (value is null || value is string || value.GetType().IsPrimitive || value.GetType().IsEnum || value is decimal or Vector3 or Cell) return value;
    if (value is IDictionary dictionary)
        return dictionary.Keys.Cast<object>().OrderBy(key => Convert.ToString(key, CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .Select(key => new { Key = PrivateState(key), Value = PrivateState(dictionary[key]) }).ToArray();
    if (value is IEnumerable sequence) return sequence.Cast<object?>().Select(PrivateState).ToArray();
    return value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .OrderBy(field => field.Name, StringComparer.Ordinal)
        .ToDictionary(field => field.Name, field => PrivateState(field.GetValue(value)));
}

sealed record TickSnapshot(string Stage, int Tick, string Sha256, int Zombies, int Turrets, int Projectiles, float CoreHp, GamePhase Phase);

sealed record Options(string Scenario, int Count, int Warmup, int Ticks, int Seed, bool Help,
    string? SavePath = null, int Turrets = 409, bool Verify = false, string? VerifyOutput = null, bool Profile = false)
{
    public static Options Parse(string[] args)
    {
        var options = new Options("all", 500, 60, 180, 12345, false);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--help" or "-h") return options with { Help = true };
            if (args[i] == "--verify") { options = options with { Verify = true }; continue; }
            if (args[i] == "--profile") { options = options with { Profile = true }; continue; }
            if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[i]}.");
            string name = args[i++], value = args[i];
            int Number() => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
                ? result : throw new ArgumentException($"Invalid integer for {name}: {value}");
            options = name switch
            {
                "--scenario" => options with { Scenario = value.ToLowerInvariant() },
                "--count" => options with { Count = Number() },
                "--warmup" => options with { Warmup = Number() },
                "--ticks" => options with { Ticks = Number() },
                "--seed" => options with { Seed = Number() },
                "--save-path" => options with { SavePath = value },
                "--turrets" => options with { Turrets = Number() },
                "--verify-output" => options with { VerifyOutput = value, Verify = true },
                _ => throw new ArgumentException($"Unknown option {name}.")
            };
        }
        if (options.Scenario is not ("marching" or "defended" or "mixed" or "all")) throw new ArgumentException("--scenario must be marching, defended, mixed or all.");
        if (options.Count <= 0 || options.Warmup < 0 || options.Ticks <= 0) throw new ArgumentException("--count and --ticks must be positive; --warmup must be nonnegative.");
        if (options.Turrets <= 0) throw new ArgumentException("--turrets must be positive.");
        if (options.VerifyOutput != null && options.SavePath == null && options.Scenario == "all")
            throw new ArgumentException("--verify-output requires one scenario or a --save-path, so its trace cannot be overwritten by another scenario.");
        return options;
    }
}
