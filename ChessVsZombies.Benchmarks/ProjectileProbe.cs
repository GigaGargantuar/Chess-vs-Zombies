using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ChessVsZombies.Core;

internal static class ProjectileProbe
{
    private sealed record Fixture(GameSession Game, Action<float> Update);
    private sealed record VerifiedSample(int Sample, string State, IReadOnlyList<ProjectileContact> Contacts);

    public static int Run(string[] args, Func<GameSession, string> snapshot)
    {
        int warmup = Number(args, "--warmup", 5), samples = Number(args, "--ticks", 20);
        int shots = Number(args, "--count", 1000), seed = Number(args, "--seed", 12345);
        string? verifyPath = Value(args, "--verify-output");
        bool counts = args.Contains("--work-counts") || verifyPath != null;
        const float duration = .05f;
        if (warmup < 0 || samples <= 0 || shots <= 0) throw new ArgumentException("Invalid probe sample counts.");
        Console.WriteLine($"CLOUD SYNTHETIC projectile kernel; .NET {Environment.Version}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Not the unavailable wave-95 checkpoint. seed={seed}; actors=40; shots={shots}; flyingBlocks=8; warmup={warmup}; samples={samples}; simulatedSecondsPerSample={duration:R}; diagnostics={counts}");
        Console.WriteLine("Each sample starts from an identical fresh fixture. Only production UpdateProjectiles is timed; no turret/movement/navigation/render work. All measured fixtures are constructed before timing and a full GC. Verification and work-count runs are separate from representative timing runs.");
        for (int i = 0; i < warmup; i++)
        {
            var warm = Create(seed, shots);
            warm.Update(duration);
        }
        var fixtures = Enumerable.Range(0, samples).Select(_ => Create(seed, shots)).ToArray();
        string setup = snapshot(fixtures[0].Game);
        foreach (var fixture in fixtures)
            if (counts) fixture.Game.ProjectileDiagnostics = new() { CaptureContacts = verifyPath != null };
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var elapsed = new List<double>(samples);
        var verified = new List<VerifiedSample>();
        var work = new List<object>();
        long allocated = 0;
        int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        for (int i = 0; i < samples; i++)
        {
            var fixture = fixtures[i];
            long before = GC.GetTotalAllocatedBytes(true), start = Stopwatch.GetTimestamp();
            fixture.Update(duration);
            elapsed.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            allocated += GC.GetTotalAllocatedBytes(true) - before;
            if (fixture.Game.ProjectileDiagnostics is { } d)
                work.Add(new { d.ForecastRequests, d.ForecastEvaluations, d.PendingSorts, d.BodyQueries, d.CoverQueries,
                    d.FlightQueries, d.Slices, Contacts = d.Contacts.Count });
            if (verifyPath != null) verified.Add(new(i, snapshot(fixture.Game), fixture.Game.ProjectileDiagnostics!.Contacts));
        }
        for (int i = 0; i < 3; i++) collections[i] = GC.CollectionCount(i) - collections[i];
        elapsed.Sort();
        double Percentile(double p) => elapsed[(int)Math.Ceiling(elapsed.Count * p) - 1];
        Console.WriteLine(FormattableString.Invariant($"ms/update mean={elapsed.Average():F4} median={Percentile(.5):F4} p95={Percentile(.95):F4} p99={Percentile(.99):F4} max={elapsed[^1]:F4}; allocatedBytes/update={allocated / samples}; GC={string.Join('/', collections)}"));
        Console.WriteLine($"setupSha256={setup}; finalSha256={snapshot(fixtures[^1].Game)}; remainingShots={fixtures[^1].Game.Projectiles.Count}; liveActors={fixtures[^1].Game.Zombies.Count(z => z.BodyHp > 0)}; flyingBlocks={fixtures[^1].Game.MegaProjectiles.Count}");
        if (work.Count > 0) Console.WriteLine("work=" + JsonSerializer.Serialize(work));
        if (verifyPath != null)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { Setup = setup, Duration = duration, Samples = verified });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(verifyPath))!);
            File.WriteAllBytes(verifyPath, bytes);
            Console.WriteLine($"exactContactAndStateTraceSha256={Convert.ToHexString(SHA256.HashData(bytes))}; trace={verifyPath}");
        }
        return 0;
    }

    private static Fixture Create(int seed, int shotCount)
    {
        var game = new GameSession(seed);
        var heldType = typeof(GameSession).GetNestedType("HeldKiloBlock", BindingFlags.NonPublic)!;
        var owned = (IDictionary)typeof(GameSession).GetField("_kiloBlocks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        for (int row = 0; row < 10; row++)
            for (int column = 0; column < 4; column++)
            {
                int index = row * 4 + column;
                var size = index < 4 ? ZombieSize.Kilo : index < 8 ? ZombieSize.Mega : ZombieSize.Standard;
                var zombie = new Zombie { Id = 1000 + index, WaveIndex = -1, Size = size, Tier = GearTier.Osmium,
                    Kind = row >= 8 && column == 0 ? ZombieKind.Basic : ZombieKind.ArmedArmored,
                    Position = new Vector3(9 + column * 5, 24.5f, 3.5f + row * 3), Attack = 1 };
                zombie.FaceToward(-Vector3.UnitX);
                game.Zombies.Add(zombie);
                if (!zombie.IsGiant) continue;
                var blocks = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(heldType))!;
                for (int indexInHand = 0; indexInHand < (size == ZombieSize.Mega ? 4 : 2); indexInHand++)
                {
                    var block = Activator.CreateInstance(heldType, [indexInHand, Material.Stone, 500f, false])!;
                    var offset = size == ZombieSize.Mega
                        ? new Vector3(-1, indexInHand / 2 == 0 ? -.5f : .5f, indexInHand % 2 == 0 ? -.5f : .5f)
                        : new Vector3(-.5f, 0, indexInHand == 0 ? -.5f : .5f);
                    heldType.GetProperty("Offset")!.SetValue(block, offset);
                    blocks.Add(block);
                }
                owned[zombie.Id] = blocks;
            }
        var flights = Enumerable.Range(0, 8).Select(row => new SavedMegaWeaponFlight
        {
            OwnerId = 2000 + row, Formation = MegaWeaponFormation.Stick, IsDebris = true,
            X = 6.5f, Y = 24.5f, Z = 3.5f + row * 3, VelocityZ = row % 2 == 0 ? .2f : -.2f,
            Age = 0, Damage = 13.5f, Blocks = [new() { BlockIndex = 0, Material = Material.Stone, Hp = 500 }]
        }).ToArray();
        typeof(GameSession).GetMethod("RestoreMegaWeaponFlights", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, [flights]);
        for (int i = 0; i < shotCount; i++)
        {
            int row = i % 10;
            float offset = ((i / 10) % 3 - 1) * .1f;
            game.Projectiles.Add(new Projectile
            {
                Origin = new Vector3(1, 24.5f + offset, 3.5f + row * 3 + offset),
                Target = new Vector3(30, 24.5f + offset, 3.5f + row * 3 + offset),
                Kind = i % 19 == 0 ? ShotKind.Lob : ShotKind.Pellet,
                Damage = i % 37 == 0 ? 5 : 1, Duration = .08f + i % 4 * .02f, TargetId = 1000 + row * 4
            });
        }
        return new(game, typeof(GameSession).GetMethod("UpdateProjectiles", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<float>>(game));
    }

    private static string? Value(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    private static int Number(string[] args, string name, int fallback) => Value(args, name) is { } value
        ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
}
