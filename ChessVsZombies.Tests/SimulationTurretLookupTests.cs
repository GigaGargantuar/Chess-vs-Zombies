using System.Reflection;
using System.Text.Json;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class SimulationTurretLookupTests
{
    [Fact]
    public void IndexedSelectionRetainsFirstLivingMatchAndAdvancesAfterThatPieceDies()
    {
        var game = new GameSession();
        var cell = new Cell(8, 16, 8);
        var dead = new Turret { Id = 1, Cell = cell, Hp = 0 };
        var first = new Turret { Id = 2, Cell = cell };
        var second = new Turret { Id = 3, Cell = cell };
        game.Turrets.AddRange([dead, first, second]);
        Invoke(game, "RefreshSimulationTurretLookup");
        LookupFlag.SetValue(game, true);
        try
        {
            Assert.Same(first, game.TrySelectTurret(cell));
            first.Hp = 0;
            Assert.Same(second, game.TrySelectTurret(cell));
            second.Hp = 0;
            Assert.Null(game.TrySelectTurret(cell));
        }
        finally
        {
            LookupFlag.SetValue(game, false);
        }

        second.Hp = 12;
        second.Cell = new Cell(9, 16, 8);
        Assert.Null(game.TrySelectTurret(cell));
        Assert.Same(second, game.TrySelectTurret(second.Cell));
    }

    [Fact]
    public void UpdatesMatchLinearQueriesAfterPublicListHealthAndCellChanges()
    {
        var indexed = Battle();
        var linear = Battle();
        foreach (var game in new[] { indexed, linear })
        {
            game.Turrets.Add(new Turret { Id = 200, Cell = new Cell(8, 16, 8), Cooldown = 100 });
            game.Zombies.Add(new Zombie { Id = 100, WaveIndex = 0, Position = new Cell(8, 17, 8).Center });
        }

        CompareUpdate(indexed, linear);
        ChangeBoth(game => game.Turrets[0].Cell = new Cell(9, 16, 8));
        CompareUpdate(indexed, linear);
        ChangeBoth(game => game.Turrets.Insert(0,
            new Turret { Id = 201, Cell = new Cell(8, 16, 8), Cooldown = 100 }));
        CompareUpdate(indexed, linear);
        ChangeBoth(game => game.Turrets.Add(
            new Turret { Id = 202, Cell = new Cell(8, 16, 8), Cooldown = 100 }));
        CompareUpdate(indexed, linear);
        ChangeBoth(game => game.Turrets[0].Hp = 0);
        CompareUpdate(indexed, linear);
        ChangeBoth(game => game.Turrets.RemoveAt(0));
        CompareUpdate(indexed, linear);

        void ChangeBoth(Action<GameSession> change)
        {
            change(indexed);
            change(linear);
        }
    }

    [Fact]
    public void CollapseAndRemovalRetainLinearSupportAcrossMultipleSubsteps()
    {
        var indexed = CollapseBattle();
        var linear = CollapseBattle();
        CompareUpdate(indexed, linear, .1f);

        Assert.Equal(2, indexed.Turrets.Count);
        Assert.Equal(new Cell(11, 14, 15), indexed.Turrets[0].Cell);
        Assert.Equal(new Cell(11, 15, 15), indexed.Turrets[1].Cell);
        for (int i = 0; i < 20; i++) CompareUpdate(indexed, linear, .05f);
    }

    private static GameSession Battle()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Number = 1,
            Waves = [new() { Groups = [new() { Count = 1, Kind = ZombieKind.Basic, Tier = GearTier.Wood }] }]
        });
        var game = new GameSession(levels: levels);
        Assert.True(game.StartLevel());
        // The paired scenes use an explicit body so both update paths start from
        // the same support situation without including spawn placement in the test.
        game.Waves[0].Spawned = 1;
        return game;
    }

    private static GameSession CollapseBattle()
    {
        var game = new GameSession();
        Assert.True(game.BuyTool(ToolKind.Shovel));
        for (int x = 10; x <= 15; x++) Assert.True(game.Dig(new Cell(x, 14, 15)));
        Assert.True(game.Dig(new Cell(11, 15, 15)));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(11, 14, 15)));
        Assert.True(game.Deploy(PieceKind.Knight, new Cell(11, 15, 15)));
        Assert.True(game.Deploy(PieceKind.Bishop, new Cell(11, 16, 15)));
        foreach (var turret in game.Turrets) turret.Cooldown = 100;
        game.Turrets[0].Hp = 1;
        Assert.True(game.StartLevel());
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = new Cell(10, 14, 15).Center;
        zombie.NextCell = null;
        zombie.Cooldown = 0;
        zombie.Lane = 0;
        zombie.Attack = 34;
        return game;
    }

    private static void CompareUpdate(GameSession indexed, GameSession linear, float dt = .01f)
    {
        indexed.Update(dt);
        Invoke(linear, "UpdateCore", dt);
        Assert.False((bool)LookupFlag.GetValue(indexed)!);
        Assert.Equal(Snapshot(linear), Snapshot(indexed));
    }

    private static string Snapshot(GameSession game) => JsonSerializer.Serialize(new
    {
        game.CoreHp,
        game.Phase,
        game.Turrets,
        game.Zombies,
        game.Projectiles,
        game.Waves,
        Falls = game.Falls.Blocks,
        Liquids = game.Liquids.ExportState(),
        Buckets = game.Buckets.ExportState()
    }, new JsonSerializerOptions { IncludeFields = true });

    private static readonly FieldInfo LookupFlag = typeof(GameSession)
        .GetField("_simulationTurretLookupActive", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void Invoke(GameSession game, string method, params object[] args) => typeof(GameSession)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, args);
}
