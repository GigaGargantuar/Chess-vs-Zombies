using System.Numerics;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class CombatTowerInteractionTests
{
    [Theory]
    [InlineData(PieceKind.Pawn, 1)]
    [InlineData(PieceKind.Knight, 2)]
    [InlineData(PieceKind.Bishop, 4)]
    [InlineData(PieceKind.Rook, 4)]
    public void TowersCanBePurchasedAndDeployedDuringAWaveAtTheirNormalCost(PieceKind kind, int cost)
    {
        var game = Battle();
        Assert.True(game.StartLevel());

        Assert.True(game.Deploy(kind, new Cell(8, 16, 8)), game.Message);

        var turret = Assert.Single(game.Turrets);
        Assert.Equal(kind, turret.Kind);
        Assert.Equal(1000 - cost, game.Economy.Currency);
        Assert.Equal(GamePhase.Combat, game.Phase);
        Assert.False(game.Deploy(kind, turret.Cell));
        Assert.False(game.Deploy(kind, new Cell(12, 16, 12)));
        Assert.False(game.Deploy(kind, new Cell(9, 18, 8)));
        Assert.Equal(1000 - cost, game.Economy.Currency);
    }

    [Fact]
    public void ANewlyDeployedTowerImmediatelyParticipatesInCombat()
    {
        var game = Battle();
        var zombie = Begin(game, new Cell(11, 16, 11).Center);

        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(10, 16, 11)));
        game.Update(.01f);

        Assert.Equal(9, zombie.Hp);
        Assert.Equal(999, game.Economy.Currency);
        Assert.Equal(GamePhase.Combat, game.Phase);
    }

    [Theory]
    [InlineData(7.79f, 16.5f, false)]
    [InlineData(7.81f, 16.5f, true)]
    [InlineData(8.5f, 16.5f, true)]
    [InlineData(9.19f, 16.5f, true)]
    [InlineData(9.21f, 16.5f, false)]
    [InlineData(8.5f, 17.38f, true)]
    [InlineData(8.5f, 17.4f, false)]
    public void DeploymentUsesTheCurrentLivingBodyBoundsWithoutSpendingOnOverlap(float x, float y, bool occupied)
    {
        var game = Battle();
        var zombie = Begin(game, new Vector3(x, y, 8.5f));
        var target = new Cell(8, 16, 8);

        Assert.Equal(occupied, game.IsOccupiedByZombie(target));
        Assert.Equal(!occupied, game.Deploy(PieceKind.Pawn, target));

        Assert.Equal(occupied ? 1000 : 999, game.Economy.Currency);
        Assert.Equal(occupied ? 0 : 1, game.Turrets.Count);
        Assert.Equal(10, zombie.Hp);
        Assert.Equal(new Vector3(x, y, 8.5f), zombie.Position);
    }

    [Fact]
    public void ADeadZombieDoesNotReserveItsFormerCellForPlacement()
    {
        var game = Battle();
        var target = new Cell(8, 16, 8);
        var zombie = Begin(game, target.Center);
        game.DamageZombie(zombie, 1000);

        Assert.False(game.IsOccupiedByZombie(target));
        Assert.True(game.Deploy(PieceKind.Pawn, target));
        Assert.Equal(999, game.Economy.Currency);
    }

    [Fact]
    public void CombatTemplatesApplyOnlyAvailablePointsAndPreserveExistingTowerDamage()
    {
        var game = Battle();
        var cell = new Cell(8, 16, 8);
        Assert.True(game.Deploy(PieceKind.Pawn, cell));
        var turret = Assert.Single(game.Turrets);
        turret.Hp = 17;
        Seed(game, UpgradeKind.HP, 11, 2);
        Seed(game, UpgradeKind.ATK, 1, 1);
        Seed(game, UpgradeKind.Orthogonal, 1, 0);
        var template = new UpgradeTemplate { Points = new() { [UpgradeKind.HP] = 3, [UpgradeKind.ATK] = 5, [UpgradeKind.Orthogonal] = 2 } };
        Assert.True(game.StartLevel());

        Assert.True(game.ApplyUpgradeTemplate(cell, template), game.Message);

        Assert.Equal(50, game.Economy.GetMaxHp(turret));
        Assert.Equal(37, turret.Hp);
        Assert.Equal(2, game.Economy.GetAttack(turret));
        Assert.Equal(1, game.Economy.GetRange(turret, UpgradeKind.Orthogonal));
        Assert.Equal(0, game.Economy.GetProgress(UpgradeKind.HP).Available);
        Assert.Equal(0, game.Economy.GetProgress(UpgradeKind.ATK).Available);
        Assert.Equal(999, game.Economy.Currency);
        Assert.Contains("skipped", game.Message);
        Assert.Equal(3, template.Points[UpgradeKind.HP]);
        Assert.Equal(5, template.Points[UpgradeKind.ATK]);
    }

    [Fact]
    public void AJumpPatternCanBeAppliedToALivingTowerDuringCombat()
    {
        var game = Battle();
        var cell = new Cell(8, 16, 8);
        Assert.True(game.Deploy(PieceKind.Knight, cell));
        var template = new UpgradeTemplate { UseJumpPattern = true };
        template.JumpPattern.Clear();
        template.JumpPattern.Add(new Cell(0, 2, 0));
        var jump = game.Economy.GetProgress(UpgradeKind.Jump);
        jump.PullCount = 11;
        jump.Available = 2;
        Assert.True(game.StartLevel());

        Assert.True(game.ApplyUpgradeTemplate(cell, template), game.Message);

        Assert.Equal(new Cell(0, 2, 0), Assert.Single(Assert.Single(game.Turrets).JumpPattern));
        Assert.Equal(998, game.Economy.Currency);
    }

    [Fact]
    public void CombatRemovalRefundsOnlyThePieceAndSettlesItsRemainingStack()
    {
        var game = Battle();
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(8, 16, 8)));
        Assert.True(game.Deploy(PieceKind.Knight, new Cell(8, 17, 8)));
        Assert.True(game.Deploy(PieceKind.Bishop, new Cell(8, 18, 8)));
        var middle = game.Turrets[1];
        var top = game.Turrets[2];
        foreach (var tower in game.Turrets) tower.Cooldown = 10000;
        Seed(game, UpgradeKind.ATK, 1, 1);
        Assert.True(game.ApplyUpgradeTemplate(new Cell(8, 16, 8), new UpgradeTemplate { Points = new() { [UpgradeKind.ATK] = 1 } }));
        Assert.True(game.StartLevel());

        Assert.True(game.Remove(new Cell(8, 16, 8)), game.Message);

        Assert.Equal(994, game.Economy.Currency);
        Assert.Equal(0, game.Economy.GetProgress(UpgradeKind.ATK).Available);
        Assert.Equal(new Cell(8, 16, 8), middle.Cell);
        Assert.Equal(new Cell(8, 17, 8), top.Cell);
        Assert.Equal(new Cell(8, 17, 8).Center, game.Falls.GetTurretPosition(middle));
        Assert.Equal(new Cell(8, 18, 8).Center, game.Falls.GetTurretPosition(top));
        game.Update(1);
        Assert.Equal(middle.Cell.Center, game.Falls.GetTurretPosition(middle));
        Assert.Equal(top.Cell.Center, game.Falls.GetTurretPosition(top));
        Assert.Equal(GamePhase.Combat, game.Phase);
    }

    [Fact]
    public void RemovingATowerDuringCombatClearsItsNavigationObstacleForAnInterruptedClimber()
    {
        var game = Battle();
        var cell = new Cell(11, 16, 15);
        Assert.True(game.Deploy(PieceKind.Pawn, cell));
        Assert.Single(game.Turrets).Cooldown = 10000;
        var zombie = Begin(game, new Vector3(10.5f, 16.8f, 15.5f));
        zombie.NextCell = cell + new Cell(0, 1, 0);
        zombie.MoveProgress = .5f;

        Assert.True(game.Remove(cell));
        game.Update(.01f);

        Assert.Null(zombie.NextCell);
        Assert.Null(game.TrySelectTurret(cell));
        Assert.Equal(1000, game.Economy.Currency);
        bool entered = false;
        for (int step = 0; step < 175; step++)
        {
            game.Update(.02f);
            entered |= zombie.Cell == cell;
        }
        Assert.True(entered, $"The removed tower's cell remained inaccessible: {zombie.Position}.");
        Assert.True(zombie.Position.X > cell.X + 1);
        Assert.True(zombie.Hp > 0);
    }

    [Fact]
    public void TerrainAndShopActionsRemainUnavailableDuringCombat()
    {
        var game = Battle();
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.Inventory.Add(Material.Dirt, 2));
        var barrierCell = new Cell(12, 16, 12);
        Assert.True(game.BuildBarrier(barrierCell));
        Assert.True(game.BuyLiquid(LiquidKind.Water));
        Assert.True(game.StartLevel());
        int currency = game.Economy.Currency;

        Assert.False(game.BuildBarrier(new Cell(13, 16, 12)));
        Assert.False(game.Dig(new Cell(8, 15, 8)));
        Assert.False(game.Remove(barrierCell));
        Assert.False(game.BuyTool(ToolKind.Pickaxe));
        Assert.False(game.BuyLiquid(LiquidKind.Water));
        Assert.False(game.PourLiquid(new Cell(8, 16, 8), LiquidKind.Water, 1));
        Assert.False(game.StartLevel());
        Assert.Equal(currency, game.Economy.Currency);
        Assert.Equal(1, game.Inventory.DirtCount);
        Assert.Equal(10, game.LiquidInventory[LiquidKind.Water]);
        Assert.NotNull(game.World.GetBarrier(barrierCell));
        Assert.Equal(Material.Dirt, game.World.GetMaterial(new Cell(8, 15, 8)));
    }

    [Fact]
    public void CombatMutationsCannotOverwriteThePreparationCheckpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ChessVsZombiesCombatSave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "campaign.json");
        try
        {
            var game = Battle();
            var originalCell = new Cell(2, 16, 2);
            Assert.True(game.Deploy(PieceKind.Pawn, originalCell));
            Seed(game, UpgradeKind.ATK, 1, 1);
            SaveStore.Save(game, path);
            var originalBytes = File.ReadAllBytes(path);
            Assert.True(game.StartLevel());
            Assert.True(game.ApplyUpgradeTemplate(originalCell, new UpgradeTemplate { Points = new() { [UpgradeKind.ATK] = 1 } }));
            Assert.True(game.Deploy(PieceKind.Rook, new Cell(3, 16, 2)));
            Assert.True(game.Remove(originalCell));

            Assert.False(SaveStore.TrySave(game, path, out var error));
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(originalBytes, File.ReadAllBytes(path));
            Assert.True(SaveStore.TryLoad(path, out var restored, out error), error);
            var tower = Assert.Single(restored!.Turrets);
            Assert.Equal(PieceKind.Pawn, tower.Kind);
            Assert.Equal(originalCell, tower.Cell);
            Assert.Empty(tower.UpgradePoints);
            Assert.Equal(999, restored.Economy.Currency);
            Assert.Equal(1, restored.Economy.GetProgress(UpgradeKind.ATK).Available);
            Assert.Equal(GamePhase.Building, restored.Phase);
            Assert.Equal(996, game.Economy.Currency);
            Assert.Equal(PieceKind.Rook, Assert.Single(game.Turrets).Kind);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DefeatStillRejectsAllTowerInteractionsWithoutSpendingOrRefunding()
    {
        var game = Battle(ZombieKind.Armed, GearTier.Osmium);
        var cell = new Cell(2, 16, 2);
        Assert.True(game.Deploy(PieceKind.Pawn, cell));
        Assert.Single(game.Turrets).Cooldown = 10000;
        Seed(game, UpgradeKind.ATK, 1, 1);
        var zombie = Begin(game, new Cell(14, 16, 15).Center);
        zombie.Cooldown = 0;
        game.Update(6);
        Assert.Equal(GamePhase.Defeat, game.Phase);

        Assert.False(game.Deploy(PieceKind.Pawn, new Cell(3, 16, 2)));
        Assert.False(game.Remove(cell));
        Assert.False(game.ApplyUpgradeTemplate(cell, new UpgradeTemplate { Points = new() { [UpgradeKind.ATK] = 1 } }));
        Assert.Equal(999, game.Economy.Currency);
        Assert.Equal(1, game.Economy.GetProgress(UpgradeKind.ATK).Available);
        Assert.Empty(Assert.Single(game.Turrets).UpgradePoints);
    }

    private static void Seed(GameSession game, UpgradeKind kind, int pulls, int available)
    {
        game.Economy.GetProgress(kind).PullCount = pulls;
        game.Economy.GetProgress(kind).Available = available;
    }

    private static GameSession Battle(ZombieKind kind = ZombieKind.Basic, GearTier tier = GearTier.Wood)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Waves = [new() { Groups = [new() { Kind = kind, Tier = tier }] }] });
        return new GameSession(levels: levels);
    }

    private static Zombie Begin(GameSession game, Vector3 position)
    {
        Assert.True(game.StartLevel());
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = position;
        zombie.NextCell = null;
        zombie.MoveProgress = 0;
        zombie.Cooldown = 10000;
        zombie.Lane = 0;
        return zombie;
    }
}
