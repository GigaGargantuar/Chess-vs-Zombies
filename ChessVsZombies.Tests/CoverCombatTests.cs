using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class CoverCombatTests
{
    private static readonly Cell TurretCell = new(8, 16, 8);
    private static readonly Cell CoverCell = new(10, 16, 8);
    private static readonly Cell HidingCell = new(11, 16, 8);

    [Fact]
    public void ARookKeepsShootingTheCoverThenHitsTheZombieAfterItBreaks()
    {
        var game = PreparedDefense();
        var cover = game.World.GetBarrier(CoverCell)!;
        cover.Hp = 2;
        var zombie = new Zombie { Id = 100, WaveIndex = -1, Position = HidingCell.Center };
        game.Zombies.Add(zombie);
        for (int shot = 0; shot < 2; shot++)
        {
            FireTurrets(game);
            Assert.Equal(zombie.Id, Assert.Single(game.Projectiles).TargetId);
            game.Update(.4f);
            Assert.Equal(10, zombie.BodyHp);
        }
        Assert.Null(game.World.GetBarrier(CoverCell));
        Assert.Equal(0, game.Inventory.DirtCount);
        Assert.Empty(game.PendingBlockDrops);

        FireTurrets(game);
        game.Update(.4f);

        Assert.Equal(9, zombie.BodyHp);
    }

    [Fact]
    public void CoveredTargetsStillHaveToMatchThePiecesAttackPattern()
    {
        var game = PreparedDefense();
        game.Zombies.Add(new Zombie { Id = 100, WaveIndex = -1, Position = new Cell(11, 16, 9).Center });

        FireTurrets(game);

        Assert.Empty(game.Projectiles);
        Assert.Equal(100, game.World.GetBarrier(CoverCell)!.Hp);
    }

    [Fact]
    public void AnUndamagedZombieWaitsAtCloseCoverWhileItsTurretWearsTheCoverDown()
    {
        var (game, zombie) = CombatAtCover();

        game.Update(.5f);

        Assert.Equal(HidingCell.Center, zombie.Position);
        Assert.Null(zombie.NextCell);
        Assert.True(game.IsZombieHiding(zombie));
        Assert.Equal(10, zombie.BodyHp);
        Assert.Equal(99, game.World.GetBarrier(CoverCell)!.Hp);
    }

    [Fact]
    public void FourZombiesAtTheHidingSpotDoNotReleaseTheWaitingZombie()
    {
        var (game, zombie) = CombatAtCover();
        AddAlly(game, new Cell(11, 16, 7));
        AddAlly(game, new Cell(11, 16, 9));
        AddAlly(game, new Cell(12, 16, 8));

        game.Update(.05f);

        Assert.True(game.IsZombieHiding(zombie));
        Assert.Equal(HidingCell.Center, zombie.Position);
    }

    [Fact]
    public void FiveLivingZombiesAdvanceTogetherAndDoNotRehideAsTheyLeaveTheSpot()
    {
        var (game, zombie) = CombatAtCover();
        AddAlly(game, new Cell(11, 16, 7));
        AddAlly(game, new Cell(11, 16, 9));
        AddAlly(game, new Cell(12, 16, 8));
        AddAlly(game, new Cell(12, 16, 9));
        var starting = game.Zombies.ToDictionary(ally => ally.Id, ally => ally.Position);

        game.Update(.6f);

        Assert.All(game.Zombies, ally =>
        {
            Assert.False(game.IsZombieHiding(ally));
            Assert.True(Vector3.DistanceSquared(starting[ally.Id], ally.Position) > .01f,
                $"Zombie {ally.Id} did not leave cover: {ally.Position}.");
        });
        Assert.True(zombie.Position.X > HidingCell.Center.X);
    }

    [Fact]
    public void DistantZombiesCannotCompleteACoverGroup()
    {
        var (game, zombie) = CombatAtCover();
        for (int i = 0; i < 4; i++) AddAlly(game, new Cell(11, 16, 12));

        game.Update(.05f);

        Assert.True(game.IsZombieHiding(zombie));
        Assert.Equal(HidingCell.Center, zombie.Position);
    }

    [Fact]
    public void DeadZombiesCannotCompleteACoverGroup()
    {
        var (game, zombie) = CombatAtCover();
        for (int i = 0; i < 3; i++) AddAlly(game, HidingCell);
        var dead = AddAlly(game, HidingCell);
        dead.Hp = 0;

        Assert.True(DecideCover(game, zombie));
        Assert.True(game.IsZombieHiding(zombie));
    }

    [Fact]
    public void ArmorDamageImmediatelyReleasesAHidingZombieWithoutBodyDamage()
    {
        var (game, zombie) = CombatAtCover(ZombieKind.Armored);
        game.Update(.05f);
        Assert.True(game.IsZombieHiding(zombie));

        game.DamageZombie(zombie, 1, DamageKind.Pierce, ShotKind.Pellet, Vector3.UnitX);

        Assert.Equal(10, zombie.BodyHp);
        Assert.Equal(49, zombie.Hp);
        Assert.False(game.IsZombieHiding(zombie));
        game.Update(.4f);
        Assert.True(zombie.Position.X > HidingCell.Center.X);
        Assert.False(game.IsZombieHiding(zombie));
    }

    [Fact]
    public void DamageBeforeTheFirstCoverDecisionAlsoPreventsWaitingAtThatSpot()
    {
        var (game, zombie) = CombatAtCover();
        game.DamageZombie(zombie, 1, DamageKind.Chemical);

        game.Update(.4f);

        Assert.Equal(9, zombie.BodyHp);
        Assert.False(game.IsZombieHiding(zombie));
        Assert.True(zombie.Position.X > HidingCell.Center.X);
    }

    [Fact]
    public void HeatProtectionWithoutActualDamageDoesNotInterruptTheWait()
    {
        var (game, zombie) = CombatAtCover(ZombieKind.Armored, GearTier.Osmium);
        game.Update(.05f);
        Assert.True(game.IsZombieHiding(zombie));

        game.DamageZombie(zombie, 1, DamageKind.Thermal);
        game.Update(.05f);

        Assert.Equal(850, zombie.Hp);
        Assert.True(game.IsZombieHiding(zombie));
        Assert.Equal(HidingCell.Center, zombie.Position);
    }

    [Fact]
    public void DestroyedCoverImmediatelyStopsTheWaitingBehavior()
    {
        var (game, zombie) = CombatAtCover();
        game.World.GetBarrier(CoverCell)!.Hp = 1;
        game.Update(.05f);
        Assert.True(game.IsZombieHiding(zombie));

        game.Update(.7f);

        Assert.Null(game.World.GetBarrier(CoverCell));
        Assert.False(game.IsZombieHiding(zombie));
        Assert.True(zombie.Position.X > HidingCell.Center.X);
    }

    [Fact]
    public void APartialCrustDoesNotProvideCoverThroughItsEmptyVolume()
    {
        var game = new GameSession();
        Assert.True(game.Deploy(PieceKind.Rook, TurretCell));
        game.Liquids.RestoreState(new LiquidState { Obsidian = [new(CoverCell, 2, CrustFace.Bottom, 10)] });
        var zombie = new Zombie { Id = 100, WaveIndex = -1, Position = HidingCell.Center };
        game.Zombies.Add(zombie);

        Assert.False(DecideCover(game, zombie));
        FireTurrets(game);
        game.Update(.4f);

        Assert.False(game.IsZombieHiding(zombie));
        Assert.Equal(9, zombie.BodyHp);
        Assert.Equal(10, game.World.GetObsidian(CoverCell)!.Hp);
    }

    [Fact]
    public void ResetForgetsCoverAndDamageSuppressionEvenWhenZombieIdsAreReused()
    {
        var (game, zombie) = CombatAtCover();
        game.DamageZombie(zombie, 1, DamageKind.Chemical);
        game.Reset();
        PrepareDefense(game);
        var replacement = new Zombie { Id = zombie.Id, WaveIndex = -1, Position = HidingCell.Center };
        game.Zombies.Add(replacement);

        Assert.True(DecideCover(game, replacement));
        Assert.True(game.IsZombieHiding(replacement));
    }

    private static GameSession PreparedDefense()
    {
        var game = new GameSession();
        PrepareDefense(game);
        return game;
    }

    private static void PrepareDefense(GameSession game)
    {
        Assert.True(game.Deploy(PieceKind.Rook, TurretCell));
        Assert.True(game.Inventory.Add(Material.Dirt, 3));
        for (int z = 7; z <= 9; z++) Assert.True(game.BuildBarrier(new Cell(10, 16, z)));
    }

    private static (GameSession Game, Zombie Zombie) CombatAtCover(ZombieKind kind = ZombieKind.Basic, GearTier tier = GearTier.Wood)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Number = 1,
            Waves = [new() { SpawnInterval = 1000, Groups = [new() { Count = 10, Kind = kind, Tier = tier }] }]
        });
        var game = new GameSession(12345, levels);
        PrepareDefense(game);
        Assert.True(game.StartLevel());
        game.Update(.001f);
        var zombie = Assert.Single(game.Zombies);
        game.Projectiles.Clear();
        foreach (var turret in game.Turrets) turret.Cooldown = 0;
        zombie.Position = HidingCell.Center;
        zombie.NextCell = null;
        zombie.MoveProgress = 0;
        zombie.Cooldown = 0;
        zombie.Lane = 0;
        return (game, zombie);
    }

    private static Zombie AddAlly(GameSession game, Cell cell)
    {
        var zombie = new Zombie { Id = 100 + game.Zombies.Count, WaveIndex = -1, Position = cell.Center, Attack = 1 };
        game.Zombies.Add(zombie);
        return zombie;
    }

    private static void FireTurrets(GameSession game) => typeof(GameSession)
        .GetMethod("UpdateTurrets", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, [.7f]);

    private static bool DecideCover(GameSession game, Zombie zombie) => (bool)typeof(GameSession)
        .GetMethod("ShouldWaitInCover", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, [zombie])!;
}
