using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class SizeNavigationPerformanceTests
{
    // Captured from the original full slab-sweep navigation builder. Hash both
    // historical fields above Y=0, including all underground breach paths and
    // tie choices. Bottom-layer routes have separate behavioral coverage.
    [Theory]
    [InlineData(0, 15358, "DDBAE052CE224AC8CD403E02EAAB58C00F6FF47918E6080BE474A5CAD606EE31", "1A3B71F37D9A0E2B0305DE7C8AB9B89984E20849C6C111C2E319A067FC9D8DB5")]
    [InlineData(1, 15426, "A47F58564F8F7F9A85136FC8DF03DB7A876A34BA076964A7D3B34D29F26BA3E1", "BD5528B282B12FF1D1A58F05D38936D4638208AE35621C2074F0B66C410F88D6")]
    [InlineData(2, 15393, "D9BE0C63CD8BCE91D31C70E08C8435417CACB2E6BAD4E6400666635382A2CA8F", "FB8B0BC4FFFAB11A5FE4802500B385C1604D79C169F85368D355526CE54F9638")]
    public void KiloFieldRetainsEveryOriginalRouteWithoutAllocatingPerSlab(int scenario, int count, string routesHash, string fallbackHash)
    {
        var game = Scenario(scenario);
        var build = typeof(GameSession).GetMethod("BuildSizedNavigation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        long before = GC.GetAllocatedBytesForCurrentThread();

        var field = build.Invoke(game, [ZombieSize.Kilo])!;

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // The original implementation allocates about 1.1 GB for this 32-cube
        // field. This measures work, not wall time, and is stable on slower PCs.
        Assert.InRange(allocated, 0, 64L * 1024 * 1024);
        AssertField(field, "Routes", count, routesHash);
        AssertField(field, "Fallback", count, fallbackHash);
    }

    [Theory]
    [InlineData(0, 16380, 16380, "7984E06E681630C9FC545809EFF4DA3422DACFD946F85D0A879362E460B6F441", "99B2D08ADE7D2FF9AFCA4EC2767D0F363C4A8DF5A46287953CCF70A15C0955AE")]
    [InlineData(1, 16359, 16390, "74E2313B000A98BE8CD82EBA85C3353B0EB13DDFC6BB28C18BFC16219568133B", "090B39C3E3ABFA943B09EBAD3BD5D9AB2026C3FD8ED790867CF02B13DE23CF00")]
    [InlineData(2, 16295, 16327, "95B49B805242BA5A8A7367A0DC51D85C14FA8F6C7A918EF68EE7F9B8D17E4500", "47B6792B10CF95C9E1B5552BF6E9EA5B947C63A7F27BCF6177BC6DD326FEB630")]
    public void MilliFieldsRetainTheirOriginalRoutesWithoutAllocatingAnIteratorPerSolid(int scenario,
        int routeCount, int fallbackCount, string routesHash, string fallbackHash)
    {
        var game = Scenario(scenario);
        var build = typeof(GameSession).GetMethod("BuildSizedNavigation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        long before = GC.GetAllocatedBytesForCurrentThread();

        var field = build.Invoke(game, [ZombieSize.Milli])!;

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 16L * 1024 * 1024);
        AssertField(field, "Routes", routeCount, routesHash);
        AssertField(field, "Fallback", fallbackCount, fallbackHash);
    }

    // Captured from the pre-500-zombie-optimization Core assembly. The three
    // scenarios cover untouched terrain, trenches/offset and paired crusts,
    // and indexed towers with a larger excavation. Every Mega goal, breach,
    // fallback destination, and equal-cost next-step choice above Y=0 is included.
    [Theory]
    [InlineData(0, 14352, "57EDD852CC9ACD9A1C6FCE8CCB6637905A33DEAADAFE51743E337DB428827F49", "50ABF54CCBB6D55C8B390B67B60EE07F7B415077A6611E7D496208C28A1AECAD")]
    [InlineData(1, 14472, "983690BF6FF4CE0115ABA150C9C31B8C2A39808A47B2385C07B6710B93367A71", "30F50BFE35C8DEEB9FCE07769145D059FACB56B19E4388F30BB6DBC47BC54FFF")]
    [InlineData(2, 14475, "A4C9B7EF0E339E54D1729CEC860372C7A9A6F1EC9F443F763D811E15C1F6BAEB", "ADCCF1AB0F7F357BFF781367E7914614C4A1BD22D8BF45AC49FA12F205731ACC")]
    public void MegaFieldsRetainEveryPreOptimizationRoute(int scenario, int count, string routesHash, string fallbackHash)
    {
        var game = Scenario(scenario);
        var build = typeof(GameSession).GetMethod("BuildSizedNavigation", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var field = build.Invoke(game, [ZombieSize.Mega])!;

        AssertField(field, "Routes", count, routesHash);
        AssertField(field, "Fallback", count, fallbackHash);
    }

    [Fact]
    public void ReusingSizedScratchDoesNotMutatePreviouslyPublishedDirectFields()
    {
        var game = Scenario(0);
        var build = typeof(GameSession).GetMethod("BuildSizedNavigation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var kilo = build.Invoke(game, [ZombieSize.Kilo])!;

        build.Invoke(game, [ZombieSize.Milli]);
        Assert.True((bool)typeof(WorldGrid).GetMethod("Dig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(game.World, [new Cell(8, 15, 8)])!);
        build.Invoke(game, [ZombieSize.Mega]);

        AssertField(kilo, "Routes", 15358, "DDBAE052CE224AC8CD403E02EAAB58C00F6FF47918E6080BE474A5CAD606EE31");
        AssertField(kilo, "Fallback", 15358, "1A3B71F37D9A0E2B0305DE7C8AB9B89984E20849C6C111C2E319A067FC9D8DB5");
    }

    [Fact]
    public void CachedSizedRebuildRefillsItsMapsWithTheSameRoutesAsAnIndependentBuild()
    {
        var game = Scenario(0);
        var getRoute = typeof(GameSession).GetMethod("TryGetSizedRoute", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cache = (IDictionary)typeof(GameSession).GetField("_sizeRoutes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
        var zombie = new Zombie { Size = ZombieSize.Mega };
        object?[] arguments = [zombie, new Cell(0, 16, 0), default(Cell), true];
        getRoute.Invoke(game, arguments);
        var previous = cache[ZombieSize.Mega]!;
        Assert.True((bool)typeof(WorldGrid).GetMethod("Dig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(game.World, [new Cell(8, 15, 8)])!);

        getRoute.Invoke(game, arguments);
        var rebuilt = cache[ZombieSize.Mega]!;
        var independent = typeof(GameSession).GetMethod("BuildSizedNavigation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(game, [ZombieSize.Mega])!;

        foreach (string property in new[] { "Routes", "Fallback" })
        {
            var cachedMap = (IDictionary<Cell, Cell>)rebuilt.GetType().GetProperty(property)!.GetValue(rebuilt)!;
            var directMap = (IDictionary<Cell, Cell>)independent.GetType().GetProperty(property)!.GetValue(independent)!;
            Assert.Same(previous.GetType().GetProperty(property)!.GetValue(previous), cachedMap);
            Assert.NotSame(cachedMap, directMap);
            Assert.Equal(directMap.Count, cachedMap.Count);
            foreach (var pair in directMap) Assert.Equal(pair.Value, cachedMap[pair.Key]);
        }
    }

    private static void AssertField(object field, string property, int count, string expectedHash)
    {
        var routes = (IDictionary<Cell, Cell>)field.GetType().GetProperty(property)!.GetValue(field)!;
        // The original graph excluded Y=0. Preserve its exact above-bottom
        // mapping hashes while allowing the newly supported bottom layer.
        var originalRoutes = routes.Where(pair => pair.Key.Y > 0).ToArray();
        Assert.Equal(count, originalRoutes.Length);
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
            foreach (var pair in originalRoutes.OrderBy(pair => pair.Key.X).ThenBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.Z))
            {
                writer.Write(pair.Key.X); writer.Write(pair.Key.Y); writer.Write(pair.Key.Z);
                writer.Write(pair.Value.X); writer.Write(pair.Value.Y); writer.Write(pair.Value.Z);
            }
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(bytes.ToArray())));
    }

    private static GameSession Scenario(int scenario)
    {
        var game = new GameSession();
        if (scenario == 0) return game;
        var dig = typeof(WorldGrid).GetMethod("Dig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, bool>>(game.World);
        var add = typeof(WorldGrid).GetMethod("AddBarrier", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, Material, float?, bool>>(game.World);
        var crust = typeof(WorldGrid).GetMethod("AddObsidian", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, int, CrustFace, int, bool>>(game.World);
        for (int x = 4; x <= 8; x++)
            for (int z = 6; z <= 8; z++)
            {
                Assert.True(dig(new Cell(x, 15, z)));
                if (x < 6) Assert.True(dig(new Cell(x, 14, z)));
            }
        for (int z = 0; z < WorldGrid.Size; z++)
            if (z is not (15 or 16)) Assert.True(add(new Cell(10, 17, z), Material.Dirt, z % 3 == 0 ? 7 : null));
        for (int i = 0; i < 6; i++) Assert.True(crust(new Cell(12 + i, 18, 8), 1 + i % 3, (CrustFace)i, 0));
        Assert.True(crust(new Cell(8, 16, 8), 2, CrustFace.Bottom, 4));
        var pairedCell = new Cell(9, 16, 8);
        var restore = typeof(WorldGrid).GetMethod("RestoreObsidian", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<IEnumerable<ObsidianCell>>>(game.World);
        restore(game.World.ObsidianCells.Append(new ObsidianCell(pairedCell, 4, CrustFace.West, 20,
            OppositeUnits: 2, OppositeHp: 10)).ToArray());
        Assert.Equal(2, game.World.GetObsidian(pairedCell)!.Parts.Count());
        if (scenario == 2)
        {
            var index = (IDictionary<Cell, Turret>)typeof(GameSession).GetField("_turretsByCell", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
            foreach (var (cell, id) in new[] { (new Cell(7, 16, 11), 1), (new Cell(7, 17, 11), 2), (new Cell(22, 16, 20), 3) })
            {
                var turret = new Turret { Cell = cell, Id = id, Kind = PieceKind.Pawn };
                game.Turrets.Add(turret);
                index[cell] = turret;
            }
            for (int x = 11; x <= 20; x++)
                for (int z = 12; z <= 18; z++)
                    if (!WorldGrid.IsCore(new Cell(x, 15, z))) Assert.True(dig(new Cell(x, 15, z)));
        }
        return game;
    }
}
