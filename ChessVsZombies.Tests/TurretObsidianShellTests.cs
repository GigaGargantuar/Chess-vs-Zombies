using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class TurretObsidianShellTests
{
    [Fact]
    public void EncasedRookClearsItsShellAtItsNormalCadenceBeforeFiringAtEnemies()
    {
        var game = new GameSession();
        Assert.True(game.Deploy(PieceKind.Rook, new Cell(8, 16, 8)));
        var turret = Assert.Single(game.Turrets);
        var target = AddZombie(game, new Cell(11, 16, 8));
        AddShell(game, turret.Cell, 1, 2);

        Invoke(game, "UpdateTurrets", .01f);
        Assert.Equal(1, game.World.GetObsidian(turret.Cell)!.Hp);
        Assert.Empty(game.Projectiles);
        Invoke(game, "UpdateTurrets", .69f);
        Assert.Equal(1, game.World.GetObsidian(turret.Cell)!.Hp);
        Invoke(game, "UpdateTurrets", .02f);
        Assert.Null(game.World.GetObsidian(turret.Cell));
        Assert.Empty(game.Projectiles);
        Assert.Equal(Balance.ZombieHp, target.BodyHp);

        Invoke(game, "UpdateTurrets", .69f);
        Assert.Empty(game.Projectiles);
        Invoke(game, "UpdateTurrets", .02f);

        var shot = Assert.Single(game.Projectiles);
        Assert.Equal(target.Id, shot.TargetId);
        Assert.Equal(ShotKind.Pellet, shot.Kind);
        Assert.Equal(Balance.TurretHp, turret.Hp);
    }

    [Theory]
    [InlineData(PieceKind.Pawn, .7f)]
    [InlineData(PieceKind.Knight, 1f)]
    public void PiecesUseTheirUpgradedAttackToClearShellsEvenWithoutEnemies(PieceKind kind, float cadence)
    {
        var game = new GameSession();
        Assert.True(game.Deploy(kind, new Cell(8, 16, 8)));
        var turret = Assert.Single(game.Turrets);
        var progress = game.Economy.GetProgress(UpgradeKind.ATK);
        progress.PullCount = 2;
        progress.Available = 2;
        Assert.True(game.Economy.Apply(turret, UpgradeKind.ATK));
        Assert.Equal(2, game.Economy.GetAttack(turret));
        AddShell(game, turret.Cell, 1, 5);

        Invoke(game, "UpdateTurrets", .01f);

        Assert.Equal(3, game.World.GetObsidian(turret.Cell)!.Hp);
        Assert.Equal(cadence, turret.Cooldown);
        Assert.Empty(game.Projectiles);
        Invoke(game, "UpdateTurrets", cadence - .01f);
        Assert.Equal(3, game.World.GetObsidian(turret.Cell)!.Hp);
        Invoke(game, "UpdateTurrets", .02f);
        Assert.Equal(1, game.World.GetObsidian(turret.Cell)!.Hp);
    }

    [Fact]
    public void LiquidReactionCanEncaseALivingTurretWhichThenAttacksTheNewShell()
    {
        var game = new GameSession();
        Assert.True(game.Deploy(PieceKind.Rook, new Cell(8, 16, 8)));
        var turret = Assert.Single(game.Turrets);
        AddZombie(game, new Cell(11, 16, 8));
        Assert.True(game.Liquids.TryAdd(turret.Cell, LiquidKind.Mercury, 1));
        Assert.True(game.Liquids.TryAdd(turret.Cell, LiquidKind.Lava, 2));

        game.Liquids.Update(.1f);

        var shell = Assert.IsType<ObsidianCell>(game.World.GetObsidian(turret.Cell));
        Assert.Equal(2, shell.Units);
        Assert.Equal(10, shell.Hp);
        Assert.Equal(Balance.TurretHp, turret.Hp);
        Invoke(game, "UpdateTurrets", .01f);
        Assert.Equal(9, game.World.GetObsidian(turret.Cell)!.Hp);
        Assert.Empty(game.Projectiles);
    }

    [Theory]
    [InlineData(12, 100, 10, 485)]
    [InlineData(20, 100, 0, 475)]
    [InlineData(40, 75, 0, 375)]
    public void HurledBlockDamageSpendsShellHpBeforeReachingTheTower(
        float wholeDamage, float expectedTowerHp, float expectedShellHp, float expectedBlockHp)
    {
        var game = new GameSession();
        var turret = AddSupportedTower(game, 200, new Cell(10, 20, 8), 100);
        AddShell(game, turret.Cell, 5, 25);
        RestoreFlight(game, Debris(wholeDamage));

        Invoke(game, "UpdateMegaProjectiles", .2f);

        Assert.Equal(expectedTowerHp, turret.Hp, 4);
        if (expectedShellHp == 0) Assert.Null(game.World.GetObsidian(turret.Cell));
        else Assert.Equal(expectedShellHp, game.World.GetObsidian(turret.Cell)!.Hp, 4);
        Assert.Empty(game.MegaProjectiles);
        Assert.Equal(expectedBlockHp, Assert.Single(game.PendingBlockDrops).Hp);
    }

    [Fact]
    public void BlockThatDestroysAShellAndItsTowerContinuesWithReducedDurability()
    {
        var game = new GameSession();
        var first = AddSupportedTower(game, 200, new Cell(8, 20, 8), 10);
        var next = AddSupportedTower(game, 201, new Cell(11, 20, 8), 100);
        AddShell(game, first.Cell, 5, 25);
        RestoreFlight(game, Debris(40));

        Invoke(game, "UpdateMegaProjectiles", .3f);

        Assert.Equal(-15, first.Hp, 4);
        Assert.Null(game.World.GetObsidian(first.Cell));
        Assert.DoesNotContain(first, game.Turrets);
        Assert.Equal(53.5f, next.Hp, 4);
        Assert.Equal(365, Assert.Single(game.PendingBlockDrops).Hp);
        Assert.Empty(game.MegaProjectiles);
    }

    [Fact]
    public void ScatteredWeaponPartsDoNotDamageTheSameTowerShellAgainDuringOverlap()
    {
        var game = new GameSession();
        var turret = AddSupportedTower(game, 200, new Cell(10, 20, 8), 100);
        AddShell(game, turret.Cell, 5, 25);
        RestoreFlight(game, new SavedMegaWeaponFlight
        {
            OwnerId = 100, Formation = MegaWeaponFormation.Chunk,
            X = 5, Y = 21.1f, Z = 8.5f, VelocityX = 40, Damage = 12,
            Blocks =
            [
                Part(0, .5f, -.5f), Part(1, .5f, .5f),
                Part(2, -.5f, -.5f), Part(3, -.5f, .5f)
            ]
        });

        Invoke(game, "UpdateMegaProjectiles", .2f);

        Assert.Equal(100, turret.Hp);
        Assert.Equal(10, game.World.GetObsidian(turret.Cell)!.Hp);
        Assert.Equal(4, game.MegaProjectiles.Count);
        Assert.All(game.MegaProjectiles, part => Assert.True(part.IsDebris));
        Assert.Equal(485, game.MegaProjectiles.Single(part => part.BlockIndex == 0).Hp);
        Invoke(game, "UpdateMegaProjectiles", .01f);
        Assert.Equal(10, game.World.GetObsidian(turret.Cell)!.Hp);
        Assert.Equal(100, turret.Hp);
    }

    [Fact]
    public void FallingEncasedTurretCarriesItsShellAndDisplacesLiquidWithoutLosingUnits()
    {
        var game = new GameSession();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 16, 8)));
        Assert.True(game.Deploy(PieceKind.Rook, new Cell(8, 17, 8)));
        var upper = game.Turrets[1];
        var originalCell = upper.Cell;
        game.Liquids.RestoreState(new LiquidState
        {
            Cells = [new(originalCell, 2, 0, 0, 3, CrustFace.West)],
            Obsidian = [new(originalCell, 3, CrustFace.West, 11)]
        });

        Assert.True(game.Remove(new Cell(8, 16, 8)));

        Assert.Equal(new Cell(8, 16, 8), upper.Cell);
        Assert.Null(game.World.GetObsidian(originalCell));
        var shell = Assert.IsType<ObsidianCell>(game.World.GetObsidian(upper.Cell));
        Assert.Equal(3, shell.Units);
        Assert.Equal(11, shell.Hp);
        Assert.Equal(2, game.Liquids.GetTotalUnits(upper.Cell));
        Assert.Equal(2, game.Liquids.Cells.Sum(cell => cell.Water));
        Assert.InRange(game.Liquids.GetTotalUnits(upper.Cell) + shell.Units, 0, LiquidSystem.TurretCapacityUnits);
    }

    private static void AddShell(GameSession game, Cell cell, int units, float hp) =>
        game.Liquids.RestoreState(new LiquidState { Obsidian = [new(cell, units, CrustFace.West, hp)] });

    private static Zombie AddZombie(GameSession game, Cell cell)
    {
        var zombie = new Zombie { Id = 100, WaveIndex = -1 };
        zombie.Position = zombie.StandAt(cell);
        game.Zombies.Add(zombie);
        return zombie;
    }

    private static Turret AddSupportedTower(GameSession game, int id, Cell cell, float hp)
    {
        for (int y = 16; y < cell.Y; y++)
            Assert.True((bool)Invoke(game.World, "AddBarrier", new Cell(cell.X, y, cell.Z), Material.Osmium, null)!);
        var turret = new Turret { Id = id, Kind = PieceKind.Pawn, Cell = cell, Hp = hp };
        game.Turrets.Add(turret);
        return turret;
    }

    private static SavedMegaWeaponFlight Debris(float damage) => new()
    {
        OwnerId = 100, Formation = MegaWeaponFormation.Chunk, IsDebris = true,
        X = 5, Y = 21.1f, Z = 8.5f, VelocityX = 40, Damage = damage,
        Blocks = [Part(0, 0, 0)]
    };

    private static SavedMegaWeaponPart Part(int index, float x, float z) => new()
    {
        BlockIndex = index, Material = Material.Stone, Hp = 500, WasBarrier = true, OffsetX = x, OffsetZ = z
    };

    private static void RestoreFlight(GameSession game, SavedMegaWeaponFlight flight) =>
        Invoke(game, "RestoreMegaWeaponFlights", (object)new[] { flight });

    private static object? Invoke(object target, string method, params object?[] arguments) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
