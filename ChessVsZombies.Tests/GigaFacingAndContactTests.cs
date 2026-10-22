using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class GigaFacingAndContactTests
{
    [Theory]
    [InlineData(3, 3)]
    [InlineData(3, 20)]
    [InlineData(20, 3)]
    [InlineData(20, 20)]
    public void FiringAndCooldownBothFaceTheRealProjectileYawInEveryHorizontalQuadrant(int targetX, int targetZ)
    {
        var game = Battle(new Cell(targetX, 16, targetZ));
        var giga = AddGiga(game, new Cell(10, 16, 10));
        giga.FaceToward(-Vector3.UnitZ);

        game.Update(.01f);

        var shot = Assert.Single(game.GigaProjectiles);
        var yaw = shot.Target - shot.Origin;
        yaw.Y = 0;
        yaw = Vector3.Normalize(yaw);
        Assert.True(Vector3.Dot(yaw, giga.Facing) > .99999f);
        Assert.Equal(0, giga.Facing.Y);
        Assert.True(Vector3.Dot(shot.Origin - giga.Position, yaw) > 0);
        Assert.True(giga.Cooldown > 0);
        giga.FaceToward(-yaw);

        game.Update(.01f);

        Assert.Same(shot, Assert.Single(game.GigaProjectiles));
        Assert.True(Vector3.Dot(yaw, giga.Facing) > .99999f);
        Assert.Equal(0, giga.Facing.Y);
    }

    [Theory]
    [InlineData("turret", true)]
    [InlineData("turret", false)]
    [InlineData("barrier", true)]
    [InlineData("barrier", false)]
    [InlineData("terrain", true)]
    [InlineData("terrain", false)]
    public void ContactAttacksResetAfterDestroyingAnythingAndOtherwiseAdvanceEvenWithADifferentLockedTarget(string targetKind, bool lethal)
    {
        var contact = new Cell(14, 16, 10);
        var lockedCell = new Cell(20, 16, 20);
        var game = targetKind == "turret" ? Battle(lockedCell, contact) : Battle(lockedCell);
        var giga = AddGiga(game, new Cell(10, 16, 10), attackCounter: 5);
        var locked = game.TrySelectTurret(lockedCell)!;
        TargetLocks(game)[giga.Id] = locked.Id;
        float hp = lethal ? 1 : targetKind == "turret" ? Balance.TurretHp : Balance.StoneBlockHp;
        Turret? targetTurret = null;
        ChessVsZombies.Core.Barrier? targetBarrier = null;
        switch (targetKind)
        {
            case "turret":
                targetTurret = game.TrySelectTurret(contact)!;
                targetTurret.Hp = hp;
                break;
            case "barrier":
                Assert.True((bool)Invoke(game.World, "AddBarrier", contact, Material.Stone, (float?)hp)!);
                targetBarrier = game.World.GetBarrier(contact)!;
                break;
            case "terrain":
                Place(game, contact);
                if (lethal) Assert.True((bool)Invoke(game.World, "RestoreTerrainHp", contact, hp)!);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(targetKind));
        }
        float attack = giga.AttackDamage;

        Assert.True((bool)Invoke(game, "AttackObstacle", giga, contact, null)!);

        Assert.Equal(lethal ? 1 : 6, giga.Giga!.AttackCounter);
        Assert.Equal(locked.Id, TargetLocks(game)[giga.Id]);
        Assert.Contains(locked, game.Turrets);
        Assert.Equal(Balance.TurretHp, locked.Hp);
        float remaining = targetKind switch
        {
            "turret" => targetTurret!.Hp,
            "barrier" => targetBarrier!.Hp,
            _ => game.World.GetTerrainHp(contact)
        };
        Assert.Equal(lethal ? 0 : hp - attack, remaining, 4);
        if (lethal)
        {
            Assert.Null(game.TrySelectTurret(contact));
            Assert.Null(game.World.GetBarrier(contact));
            Assert.Equal(Material.Air, game.World.GetMaterial(contact));
        }
    }

    [Fact]
    public void AnOverflowingMathematicalAttackStillDestroysFiniteTerrainAndResetsItsCounter()
    {
        var game = Battle();
        var giga = AddGiga(game, new Cell(10, 16, 10), initialCounter: 185);
        var target = new Cell(14, 16, 10);
        Place(game, target);
        Assert.Equal(Balance.StoneBlockHp, game.World.GetTerrainHp(target));
        Assert.True(float.IsPositiveInfinity(giga.AttackDamage));

        Assert.True((bool)Invoke(game, "AttackObstacle", giga, target, null)!);

        Assert.Equal(Material.Air, game.World.GetMaterial(target));
        Assert.Equal(0, game.World.GetTerrainHp(target));
        Assert.Equal(1, giga.Giga!.AttackCounter);
        Assert.All(game.World.DamagedTerrain, entry => Assert.True(float.IsFinite(entry.Value)));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(185, true)]
    public void RealCoreContactAdvancesOnSurvivalAndResetsOnAClampedLethalAttack(int attackCounter, bool lethal)
    {
        var game = Battle();
        var giga = AddGiga(game, new Cell(11, 16, 14), initialCounter: Math.Max(10, attackCounter), attackCounter: attackCounter);
        float damage = giga.AttackDamage;
        float originalHp = game.CoreHp;

        game.Update(.01f);

        Assert.True(float.IsFinite(game.CoreHp));
        Assert.Equal(lethal ? 0 : originalHp - damage, game.CoreHp, 4);
        Assert.Equal(lethal ? 1 : attackCounter + 1, giga.Giga!.AttackCounter);
        Assert.Equal(lethal ? GamePhase.Defeat : GamePhase.Combat, game.Phase);
        Assert.True(giga.Cooldown > 0);
    }

    private static GameSession Battle(params Cell[] towers)
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition
        {
            Waves = [new WaveDefinition { SpawnInterval = 1000, Groups = [new ZombieGroup { Kind = ZombieKind.Basic }] }]
        });
        var game = new GameSession(123, catalog);
        foreach (var cell in towers) Assert.True(game.Deploy(PieceKind.Rook, cell));
        Assert.True(game.StartLevel());
        foreach (var wave in game.Waves) wave.Spawned = wave.TotalCount;
        foreach (var turret in game.Turrets) turret.Cooldown = 1000;
        return game;
    }

    private static Zombie AddGiga(GameSession game, Cell cell, int initialCounter = 10, int? attackCounter = null)
    {
        var giga = new Zombie { Id = 1000, WaveIndex = -1, Size = ZombieSize.Giga, IsBoss = true };
        giga.InitializeGigaCounter(initialCounter);
        if (attackCounter is { } counter) giga.RestoreGigaState(initialCounter, initialCounter, initialCounter, counter, 0);
        giga.Position = giga.StandAt(cell);
        game.Zombies.Add(giga);
        return giga;
    }

    private static Dictionary<int, int> TargetLocks(GameSession game) => (Dictionary<int, int>)typeof(GameSession)
        .GetField("_gigaTargets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(game)!;

    private static void Place(GameSession game, Cell cell) => Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", cell, Material.Stone)!);

    private static object? Invoke(object owner, string name, params object?[] arguments) => owner.GetType()
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, arguments);
}
