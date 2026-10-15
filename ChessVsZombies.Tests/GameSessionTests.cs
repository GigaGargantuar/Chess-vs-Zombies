using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class GameSessionTests
{
    [Fact]
    public void InvalidPlacementDoesNotSpendCurrencyAndRemovingSupportSettlesDefenses()
    {
        var game = new GameSession();
        Assert.False(game.Deploy(PieceKind.Pawn, new Cell(0, 16, 5)));
        Assert.False(game.Deploy(PieceKind.Pawn, new Cell(5, 18, 5)));
        Assert.False(game.Deploy(PieceKind.Pawn, new Cell(15, 16, 15)));
        Assert.False(game.Deploy(PieceKind.Pawn, new Cell(12, 16, 12)));
        Assert.False(game.Deploy(PieceKind.Pawn, new Cell(19, 16, 19)));
        Assert.False(game.Deploy(PieceKind.Pawn, new Cell(5, 15, 5)));
        Assert.False(game.BuildBarrier(new Cell(11, 16, 12)));
        Assert.False(game.BuildBarrier(new Cell(12, 17, 12)));
        Assert.Equal(1000, game.Economy.Currency);

        Assert.True(game.Deploy(PieceKind.Rook, new Cell(5, 16, 5)));
        Assert.Equal(996, game.Economy.Currency);
        Assert.False(game.Deploy(PieceKind.Pawn, new Cell(5, 16, 5)));
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.Dig(new Cell(5, 15, 5)));
        Assert.Equal(new Cell(5, 15, 5), Assert.Single(game.Turrets).Cell);
        Assert.True(game.Remove(new Cell(5, 15, 5)));
        Assert.True(game.Dig(new Cell(5, 15, 6)));
        Assert.Equal(900, game.Economy.Currency);

        Assert.True(game.BuildBarrier(new Cell(12, 16, 12)));
        Assert.False(game.Deploy(PieceKind.Knight, new Cell(12, 17, 12)));
        Assert.True(game.BuildBarrier(new Cell(12, 17, 12)));
        Assert.True(game.Remove(new Cell(12, 16, 12)));
        Assert.Equal(new Cell(12, 16, 12), Assert.Single(game.World.Barriers).Cell);
        Assert.Equal(900, game.Economy.Currency);
    }

    [Fact]
    public void WaveOverlapUsesTheFullRosterHpBudgetAndStartsTheNextWaveOnce()
    {
        var game = NewSession(
            new WaveDefinition { SpawnInterval = 1, Groups = [new() { Count = 4 }] },
            new WaveDefinition { SpawnInterval = 1, Groups = [new() { Count = 1, Kind = ZombieKind.Armored }] });
        Assert.True(game.StartLevel());
        Assert.Equal(40, game.Waves[0].TotalHp);
        Assert.Equal(40, game.Waves[1].TotalHp);
        game.Update(.01f);
        var first = Assert.Single(game.Zombies);
        Assert.True(WorldGrid.IsSpawnZone(first.Cell));

        game.DamageZombie(first, 1000);
        game.Update(.01f);
        Assert.Equal(10, game.Waves[0].DamageTaken);
        Assert.False(game.Waves[1].Started); // All currently spawned HP is gone, but three zombies remain unspawned.
        Assert.Equal(GamePhase.Combat, game.Phase);

        game.Update(1);
        var second = Assert.Single(game.Zombies);
        game.DamageZombie(second, 9);
        game.Update(.01f);
        Assert.Equal(19, game.Waves[0].DamageTaken);
        Assert.False(game.Waves[1].Started);
        game.DamageZombie(second, 1);
        game.Update(.01f);
        Assert.True(game.Waves[1].Started);
        Assert.Equal(1, game.Waves[1].Spawned);
        Assert.Equal(20, game.Waves[0].DamageTaken);
        game.DamageZombie(second, 1000); // Repeated damage to a corpse must not inflate the budget.
        game.Update(.1f);
        Assert.Equal(20, game.Waves[0].DamageTaken);
        Assert.Single(game.Zombies, zombie => zombie.WaveIndex == 1);
    }

    [Fact]
    public void LevelCompletionPaysOnlySurvivorsExactlyOnce()
    {
        var game = NewSession(new WaveDefinition { Groups = [new() { Count = 1 }] });
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(2, 16, 2)));
        Assert.True(game.Deploy(PieceKind.Knight, new Cell(3, 16, 3)));
        Assert.True(game.Deploy(PieceKind.Rook, new Cell(4, 16, 4)));
        game.Turrets[2].Hp = 0;
        int balance = game.Economy.Currency;
        Assert.True(game.StartLevel());
        game.Update(.01f);
        game.DamageZombie(Assert.Single(game.Zombies), 1000);
        Assert.Equal(balance, game.Economy.Currency); // A kill is not a separate income source.
        game.Update(.01f);

        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(2, game.LevelNumber);
        Assert.Equal(balance + 4, game.Economy.Currency);
        game.Update(100);
        Assert.Equal(balance + 4, game.Economy.Currency);
    }

    [Fact]
    public void CombatAllowsTowerInteractionsButRejectsTerrainChangesAndWaveRestart()
    {
        var game = NewSession(new WaveDefinition { Groups = [new() { Count = 1 }] });
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(2, 16, 2)));
        Assert.True(game.StartLevel());
        int balance = game.Economy.Currency;
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(3, 16, 2)));
        Assert.False(game.BuildBarrier(new Cell(12, 16, 12)));
        Assert.False(game.Dig(new Cell(3, 15, 3)));
        Assert.True(game.Remove(new Cell(2, 16, 2)));
        Assert.False(game.StartLevel());
        Assert.Equal(balance, game.Economy.Currency);
        Assert.Single(game.Waves);
    }

    [Fact]
    public void AnUpgradedMeleePieceCanFinishARealWaveAndEarnItsSurvivorPayment()
    {
        var game = NewSession(new WaveDefinition { Groups = [new() { Count = 1 }] });
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(11, 16, 12)));
        var pawn = Assert.Single(game.Turrets);
        var attack = game.Economy.GetProgress(UpgradeKind.ATK);
        attack.PullCount = 91; // Rank 10: base 1 + 10 attack.
        attack.Available = 10;
        var template = new UpgradeTemplate();
        template.Points[UpgradeKind.ATK] = 10;
        Assert.True(game.ApplyUpgradeTemplate(pawn.Cell, template));
        Assert.True(game.StartLevel());
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = new Cell(12, 16, 12).Center;
        zombie.NextCell = null;
        float coreBefore = game.CoreHp;

        game.Update(.01f);

        Assert.Empty(game.Zombies);
        Assert.Equal(10, game.Waves[0].DamageTaken);
        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(2, game.LevelNumber);
        Assert.Equal(coreBefore, game.CoreHp);
        Assert.Equal(1001, game.Economy.Currency);
    }

    [Fact]
    public void AnUndefendedZombieNavigatesFromTheSpawnShellAndCanDestroyTheCore()
    {
        var game = NewSession(new WaveDefinition
        {
            Groups = [new() { Count = 1, Kind = ZombieKind.Armed, Tier = GearTier.Osmium }]
        });
        Assert.True(game.StartLevel());
        game.Update(90);

        Assert.Equal(GamePhase.Defeat, game.Phase);
        Assert.Equal(0, game.CoreHp);
        Assert.Equal(1, game.LevelNumber);
        Assert.Equal(1000, game.Economy.Currency);
    }

    [Fact]
    public void LaneChangesRequireAccumulatedDamageAndDoNotTeleportTheZombie()
    {
        var game = NewSession(new WaveDefinition { Groups = [new() { Count = 1 }] });
        Assert.True(game.StartLevel());
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        var position = zombie.Position;
        var nextCell = zombie.NextCell;
        int initialLane = zombie.Lane;

        game.DamageZombie(zombie, 4);
        Assert.Equal(initialLane, zombie.Lane);
        game.DamageZombie(zombie, 1);
        Assert.NotEqual(initialLane, zombie.Lane);
        Assert.Equal(position, zombie.Position);
        Assert.Equal(nextCell, zombie.NextCell);
        Assert.Equal(0, zombie.DamageSinceLaneSwitch);
    }

    [Theory]
    [InlineData(false, 9)]
    [InlineData(true, 10)]
    public void UnupgradedRookShootsDistantTargetsAndDamagesInterveningCover(bool blocked, int expectedHp)
    {
        var game = NewSession(new WaveDefinition { Groups = [new() { Count = 1 }] });
        Assert.True(game.Deploy(PieceKind.Rook, new Cell(1, 16, 15)));
        if (blocked) Assert.True(game.BuildBarrier(new Cell(13, 16, 15)));
        Assert.True(game.StartLevel());
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = new Cell(14, 16, 15).Center;
        zombie.NextCell = null;

        game.Update(.01f);
        Assert.Equal(ShotKind.Pellet, Assert.Single(game.Projectiles).Kind);
        game.Update(.85f);
        Assert.Equal(expectedHp, zombie.Hp);
        if (blocked) Assert.Equal(Balance.DirtBlockHp - 1, game.World.GetBarrier(new Cell(13, 16, 15))!.Hp);
    }

    [Fact]
    public void AJumpAttackLobsOverAnIntermediateDefensiveBlock()
    {
        var game = NewSession(new WaveDefinition { Groups = [new() { Count = 1 }] });
        Assert.True(game.Deploy(PieceKind.Knight, new Cell(11, 16, 14)));
        Assert.True(game.BuildBarrier(new Cell(12, 16, 14)));
        Assert.True(game.StartLevel());
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = new Cell(13, 16, 15).Center;
        zombie.NextCell = null;

        game.Update(.01f);
        Assert.Equal(ShotKind.Lob, Assert.Single(game.Projectiles).Kind);
        game.Update(.6f);
        Assert.Equal(9, zombie.Hp);
        Assert.Equal(100, Assert.Single(game.World.Barriers).Hp);
    }

    [Theory]
    [InlineData(PieceKind.Rook, 10, 16, 11)]
    [InlineData(PieceKind.Bishop, 10, 16, 10)]
    public void SlidingPiecesFirePelletsAgainstAdjacentTargets(PieceKind kind, int x, int y, int z)
    {
        var game = NewSession(new WaveDefinition { Groups = [new() { Count = 1 }] });
        Assert.True(game.Deploy(kind, new Cell(x, y, z)));
        Assert.True(game.StartLevel());
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = new Cell(11, 16, 11).Center;
        zombie.NextCell = null;

        game.Update(.01f);

        Assert.Equal(ShotKind.Pellet, Assert.Single(game.Projectiles).Kind);
        Assert.Equal(10, zombie.Hp);
        game.Update(.2f);
        Assert.Equal(9, zombie.Hp);
    }

    [Fact]
    public void AttachedBlocksKeepAnOverheadTowerSupportedAfterTheBottomBlockIsDestroyed()
    {
        var game = NewSession(new WaveDefinition
        {
            Groups = [new() { Count = 1, Kind = ZombieKind.Armed, Tier = GearTier.Osmium }]
        });
        for (int y = 16; y <= 19; y++) Assert.True(game.BuildBarrier(new Cell(14, y, 15)));
        Assert.True(game.BuildBarrier(new Cell(14, 16, 16)));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(14, 20, 15)));
        game.World.GetBarrier(new Cell(14, 16, 15))!.Hp = 1;
        int currency = game.Economy.Currency;
        Assert.True(game.StartLevel());
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = new Cell(13, 16, 15).Center;
        zombie.NextCell = null;
        zombie.Lane = 0;

        game.Update(.1f);

        Assert.Equal(Material.Air, game.World.GetMaterial(new Cell(14, 16, 15)));
        Assert.Equal(new Cell(14, 20, 15), Assert.Single(game.Turrets).Cell);
        Assert.Equal(currency, game.Economy.Currency);
        game.DamageZombie(zombie, 1000);
        game.Update(.01f);
        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(currency + 2, game.Economy.Currency);
    }

    private static GameSession NewSession(params WaveDefinition[] waves)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Number = 1, Name = "Test encounter", Waves = [.. waves] });
        var game = new GameSession(12345, levels);
        game.Inventory.Add(Material.Dirt, 100); // Isolate combat scenarios from excavation setup.
        return game;
    }
}
