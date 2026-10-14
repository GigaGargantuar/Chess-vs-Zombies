using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class ZombieFallDamageTests
{
    [Theory]
    [InlineData(3f)]
    [InlineData(3.9f)]
    [InlineData(3.999f)]
    public void FallsBelowFourBlocksDoNotDamageTheZombie(float distance)
    {
        var game = NewSession();
        var zombie = BeginAt(game, new Vector3(8.5f, 16.5f + distance, 8.5f));

        Land(game, zombie, 16.5f);

        Assert.Equal(Balance.ZombieHp, zombie.BodyHp);
        Assert.Equal(0, Assert.Single(game.Waves).DamageTaken);
        Assert.Empty(game.Feedback.Hits);
    }

    [Fact]
    public void FourBlockFallDealsFiveBluntDamageOnLandingAndNeverAgainWhileGrounded()
    {
        var game = NewSession();
        var zombie = BeginAt(game, new Vector3(8.5f, 20.5f, 8.5f));

        game.Update(1.5f);

        Assert.True(zombie.Position.Y > 16.5f);
        Assert.Equal(Balance.ZombieHp, zombie.BodyHp);
        Assert.Equal(0, Assert.Single(game.Waves).DamageTaken);
        Assert.Empty(game.Feedback.Hits);

        Land(game, zombie, 16.5f);

        Assert.Equal(5, zombie.BodyHp);
        Assert.Equal(5, Assert.Single(game.Waves).DamageTaken);
        var hit = Assert.Single(game.Feedback.Hits);
        Assert.Equal(5, hit.Amount);
        Assert.Equal(DamageKind.Blunt, hit.Kind);
        Assert.Equal(16.5f, hit.Position.Y, 4);

        game.Update(2);

        Assert.True(zombie.Hp > 0);
        Assert.Equal(5, zombie.BodyHp);
        Assert.Equal(5, Assert.Single(game.Waves).DamageTaken);
        Assert.Empty(game.Feedback.Hits);
    }

    [Fact]
    public void FiveBlockFallKillsTheBasicZombieAndAccountsForItsBodyAndDeathDropOnce()
    {
        var game = NewSession();
        var landingCell = new Cell(8, 16, 8);
        var zombie = BeginAt(game, new Vector3(8.5f, 21.5f, 8.5f));

        Land(game, zombie, landingCell.Center.Y);

        Assert.Equal(0, zombie.BodyHp);
        Assert.Equal(0, zombie.Hp);
        Assert.Empty(game.Zombies);
        Assert.Equal(Balance.ZombieHp, Assert.Single(game.Waves).DamageTaken);
        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.Equal(2, game.LevelNumber);
        Assert.Equal(Material.Dirt, game.World.GetNaturalMaterial(landingCell));
        Assert.Equal(0, zombie.CarriedBlocks);
        var hit = Assert.Single(game.Feedback.Hits);
        Assert.Equal(10, hit.Amount);
        Assert.Equal(DamageKind.Blunt, hit.Kind);

        game.Update(.2f);

        Assert.Equal(Balance.ZombieHp, Assert.Single(game.Waves).DamageTaken);
        Assert.Single(game.Feedback.Hits);
        Assert.Equal(Material.Air, game.World.GetNaturalMaterial(landingCell + new Cell(0, 1, 0)));
    }

    [Fact]
    public void DestroyingTheOriginalLandingDuringDescentPreservesTheFullFallHeight()
    {
        var game = NewSession();
        var support = new Cell(8, 16, 8);
        game.Liquids.RestoreState(new LiquidState
        {
            Obsidian = [new(support, 10, CrustFace.Bottom, LiquidSystem.ObsidianHpPerUnit * 10)]
        });
        var zombie = BeginAt(game, new Vector3(8.5f, 20.5f, 8.5f));
        game.Update(1);
        Assert.Equal(19.5f, zombie.Position.Y, 4);
        Assert.Equal(Balance.ZombieHp, zombie.BodyHp);

        Assert.True(game.World.DamageObsidian(support, LiquidSystem.ObsidianHpPerUnit * 10));
        Land(game, zombie, 16.5f);

        // The original target was only three blocks below the start. Retargeting
        // after one block of descent makes the complete fall four blocks long.
        Assert.Equal(5, zombie.BodyHp);
        Assert.Equal(5, Assert.Single(game.Waves).DamageTaken);
        Assert.Equal(5, Assert.Single(game.Feedback.Hits).Amount);
    }

    [Fact]
    public void CancellingAStaleMovementPlanInMidairDoesNotForgetHeightAlreadyLost()
    {
        var game = NewSession();
        var zombie = BeginAt(game, new Vector3(8.5f, 20.5f, 8.5f));
        game.Update(1);
        Assert.Equal(19.5f, zombie.Position.Y, 4);
        var clearMovement = typeof(GameSession).GetMethod("ClearZombieMovement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<Zombie>>(game);

        clearMovement(zombie);
        Land(game, zombie, 16.5f);

        Assert.Equal(5, zombie.BodyHp);
        Assert.Equal(5, Assert.Single(game.Waves).DamageTaken);
        Assert.Equal(5, Assert.Single(game.Feedback.Hits).Amount);
    }

    [Fact]
    public void SeparateSafeFallsDoNotAccumulateIntoOneDamagingFall()
    {
        var game = NewSession();
        var start = new Vector3(8.5f, 19.5f, 8.5f);
        var zombie = BeginAt(game, start);
        Land(game, zombie, 16.5f);
        Assert.Equal(Balance.ZombieHp, zombie.BodyHp);

        // Reuse the same unit for another independent three-block descent after
        // it has made real ground contact and completed the first fall.
        Place(zombie, start);
        Land(game, zombie, 16.5f);

        Assert.Equal(Balance.ZombieHp, zombie.BodyHp);
        Assert.Equal(0, Assert.Single(game.Waves).DamageTaken);
        Assert.Empty(game.Feedback.Hits);
    }

    [Fact]
    public void AThinCrustUsesItsActualLandingSurfaceRatherThanTheDifferenceBetweenGridCells()
    {
        var game = NewSession();
        game.Liquids.RestoreState(new LiquidState
        {
            Obsidian = [new(new Cell(8, 16, 8), 2, CrustFace.Bottom, LiquidSystem.ObsidianHpPerUnit * 2)]
        });
        var zombie = BeginAt(game, new Vector3(8.5f, 20.5f, 8.5f));

        Land(game, zombie, 16.7f);

        Assert.Equal(Balance.ZombieHp, zombie.BodyHp);
        Assert.Equal(0, Assert.Single(game.Waves).DamageTaken);
        Assert.Empty(game.Feedback.Hits);
    }

    [Fact]
    public void ArmorDoesNotAbsorbAnUndirectedBluntFallImpact()
    {
        var game = NewSession(ZombieKind.Armored);
        var zombie = BeginAt(game, new Vector3(8.5f, 20.5f, 8.5f));
        var armorBefore = zombie.ArmorPieces.Select(piece => (piece.Slot, piece.Hp)).ToArray();
        zombie.FaceToward(-Vector3.UnitZ);

        Land(game, zombie, 16.5f);

        Assert.Equal(5, zombie.BodyHp);
        Assert.Equal(armorBefore, zombie.ArmorPieces.Select(piece => (piece.Slot, piece.Hp)).ToArray());
        Assert.Equal(5, Assert.Single(game.Waves).DamageTaken);
        Assert.Equal(DamageKind.Blunt, Assert.Single(game.Feedback.Hits).Kind);
    }

    [Theory]
    [InlineData(.05f)]
    [InlineData(.02f)]
    public void ShallowMercuryCancelsTheFallBeforeGroundImpactAtEitherUpdateSize(float stepSeconds)
    {
        var game = NewSession();
        var puddle = new Cell(8, 16, 8);
        Assert.True(game.Liquids.TryAdd(puddle, LiquidKind.Mercury, 1));
        var zombie = BeginAt(game, new Vector3(8.5f, 21.5f, 8.5f));
        Assert.False(zombie.SinksIn(LiquidKind.Mercury));

        // At terminal speed a .05s update can cross both this shallow surface
        // and the floor. Buoyancy must interrupt the swept descent first.
        for (int step = 0; step < 250 && zombie.Position.Y > 16.5001f; step++)
        {
            game.Update(stepSeconds);
            Assert.True(zombie.Hp > 0, "The buoyant unit received a lethal ground impact while entering mercury.");
            Assert.DoesNotContain(game.Feedback.Hits, hit => hit.Kind == DamageKind.Blunt);
        }

        Assert.InRange(Math.Abs(zombie.Position.Y - 16.5f), 0, .0001f);
        Assert.InRange(zombie.BodyHp, 9, Balance.ZombieHp); // Brief chemical contact remains legitimate.
        Assert.All(game.Feedback.Hits, hit => Assert.Equal(DamageKind.Chemical, hit.Kind));
        Assert.Equal(1, game.Liquids.GetUnits(puddle, LiquidKind.Mercury));
        Assert.Equal(GamePhase.Combat, game.Phase);
        Assert.Equal(1, zombie.CarriedBlocks);
    }

    private static GameSession NewSession(ZombieKind kind = ZombieKind.Basic)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Number = 1,
            Name = "Fall damage",
            Waves = [new() { SpawnInterval = .01f, Groups = [new() { Count = 1, Kind = kind, Tier = GearTier.Wood }] }]
        });
        return new GameSession(12345, levels);
    }

    private static Zombie BeginAt(GameSession game, Vector3 start)
    {
        Assert.True(game.StartLevel(), game.Message);
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        Place(zombie, start);
        return zombie;
    }

    private static void Place(Zombie zombie, Vector3 position)
    {
        zombie.Position = position;
        zombie.NextCell = null;
        zombie.MoveProgress = 0;
        zombie.Cooldown = 10000;
        zombie.Lane = 0;
    }

    private static void Land(GameSession game, Zombie zombie, float landingY)
    {
        for (int step = 0; step < 250 && zombie.Position.Y > landingY + .0001f; step++) game.Update(.02f);
        Assert.InRange(Math.Abs(zombie.Position.Y - landingY), 0, .0001f);
    }
}
