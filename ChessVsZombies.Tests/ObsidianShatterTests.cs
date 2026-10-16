using System.Numerics;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class ObsidianShatterTests
{
    private static readonly Cell Source = new(8, 16, 8);

    [Fact]
    public void ShatterHitsBodiesAcrossTheWholeThreeCellCubeIncludingItsAttacker()
    {
        var game = Battle(new ZombieGroup { Kind = ZombieKind.Basic, Count = 6 });
        var zombies = game.Zombies.ToArray();
        // Isolate the burst boundary from secondary impacts by blocks dropped on death.
        foreach (var zombie in zombies)
            typeof(Zombie).GetProperty(nameof(Zombie.CarriedBlocks))!.SetValue(zombie, 0);
        Position(zombies[0], Source.Center);
        Position(zombies[1], new Vector3(9.9f, 17.8f, 9.9f)); // Inside the cube, outside a radius-1.5 sphere.
        Position(zombies[2], new Vector3(10.19f, 16.5f, 8.5f)); // Body overlaps even though center is outside.
        Position(zombies[3], new Vector3(8.5f, 18.38f, 8.5f));
        Position(zombies[4], new Vector3(10.21f, 16.5f, 8.5f));
        Position(zombies[5], new Vector3(8.5f, 18.4f, 8.5f));
        InstallDamagedCrust(game);

        game.Update(.01f);

        Assert.Null(game.World.GetObsidian(Source));
        Assert.All(zombies.Take(4), zombie => Assert.Equal(0, zombie.Hp));
        Assert.All(zombies.Skip(4), zombie => Assert.Equal(10, zombie.Hp));
        Assert.Equal(40, game.Waves[0].DamageTaken);
        Assert.Equal(4, game.Feedback.Hits.Count);
        Assert.All(game.Feedback.Hits, hit => { Assert.Equal(DamageKind.Pierce, hit.Kind); Assert.Equal(10, hit.Amount); });
    }

    [Fact]
    public void ShardsUseSourceDirectionForShieldsThenChestplatesAndHitOnlyOnce()
    {
        var game = Battle(new ZombieGroup { Kind = ZombieKind.Armored, Tier = GearTier.Iron, Count = 3 });
        var zombies = game.Zombies.ToArray();
        Position(zombies[0], Source.Center);
        Position(zombies[1], Source.Center + Vector3.UnitX);
        Position(zombies[2], Source.Center - Vector3.UnitX);
        zombies[1].FaceToward(-Vector3.UnitX);
        zombies[2].FaceToward(-Vector3.UnitX);
        InstallDamagedCrust(game);

        game.Update(.01f);

        Assert.Equal(50, ArmorHp(zombies[0], ArmorSlot.Shield)); // At the source there is no incident direction.
        Assert.Equal(30, ArmorHp(zombies[0], ArmorSlot.Chestplate));
        Assert.Equal(30, ArmorHp(zombies[1], ArmorSlot.Shield));
        Assert.Equal(50, ArmorHp(zombies[1], ArmorSlot.Chestplate));
        Assert.Equal(50, ArmorHp(zombies[2], ArmorSlot.Shield));
        Assert.Equal(30, ArmorHp(zombies[2], ArmorSlot.Chestplate));
        Assert.All(zombies, zombie => { Assert.Equal(10, zombie.BodyHp); Assert.Equal(50, ArmorHp(zombie, ArmorSlot.Helmet)); });
        Assert.Equal(60, game.Waves[0].DamageTaken);
        Assert.Equal(3, game.Feedback.Hits.Count);
        Assert.All(game.Feedback.Hits, hit => Assert.Equal(20, hit.Amount));

        game.Update(.2f);

        Assert.All(zombies, zombie => Assert.Equal(190, zombie.Hp));
        Assert.Equal(60, game.Waves[0].DamageTaken);
        Assert.Equal(3, game.Feedback.Hits.Count);
        Assert.False(game.World.DamageObsidian(Source, 100));
    }

    [Fact]
    public void NonlethalObsidianHitsDoNotBurstAndExternalDestructionDoesNotBurst()
    {
        var game = Battle(new ZombieGroup { Kind = ZombieKind.Armored, Tier = GearTier.Iron, Count = 2 });
        var zombies = game.Zombies.ToArray();
        Position(zombies[0], Source.Center);
        Position(zombies[1], Source.Center + Vector3.UnitX);
        game.Liquids.RestoreState(new LiquidState { Obsidian = [new(Source, 2, CrustFace.Bottom, 10, 4)] });

        game.Update(.01f);

        Assert.Equal(9, game.World.GetObsidian(Source)!.Hp);
        Assert.All(zombies, zombie => Assert.Equal(210, zombie.Hp));
        Assert.Empty(game.Feedback.Hits);
        Assert.True(game.World.DamageObsidian(Source, 9));
        game.Update(.01f);
        Assert.All(zombies, zombie => Assert.Equal(210, zombie.Hp));
        Assert.Empty(game.Feedback.Hits);
        Assert.Equal(0, game.Waves[0].DamageTaken);
    }

    [Fact]
    public void ShatterDeathsAdvanceWaveAccountingAndStartTheNextWaveWithoutDoubleCounting()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Waves =
            [
                new() { SpawnInterval = .001f, Groups = [new() { Kind = ZombieKind.Basic, Count = 3 }] },
                new() { Groups = [new() { Kind = ZombieKind.Basic, Count = 1 }] }
            ]
        });
        var game = new GameSession(levels: levels);
        Assert.True(game.StartLevel());
        game.Update(.01f);
        var zombies = game.Zombies.ToArray();
        Position(zombies[0], Source.Center);
        Position(zombies[1], Source.Center + Vector3.UnitX);
        Position(zombies[2], Source.Center + Vector3.UnitX * 3);
        InstallDamagedCrust(game);

        game.Update(.01f);
        Assert.Equal(20, game.Waves[0].DamageTaken);
        Assert.False(game.Waves[1].Started);
        game.Update(.01f);

        Assert.True(game.Waves[1].Started);
        Assert.Single(game.Zombies, zombie => zombie.WaveIndex == 1);
        Assert.Equal(20, game.Waves[0].DamageTaken);
        Assert.Equal(2, game.Feedback.Hits.Count);
    }

    [Fact]
    public void ShatteringTheLastSinkingArmorUsesTheExistingMovementInvalidationHook()
    {
        var game = Battle(new ZombieGroup { Kind = ZombieKind.Armored, Tier = GearTier.Iron, Count = 2 });
        var victim = game.Zombies[0];
        var attacker = game.Zombies[1];
        Position(victim, Source.Center + Vector3.UnitX);
        Position(attacker, Source.Center);
        game.DamageZombie(victim, 50, DamageKind.Pierce, ShotKind.Pellet, -victim.Facing);
        game.DamageZombie(victim, 50, DamageKind.Blunt, ShotKind.Lob, -Vector3.UnitY);
        game.DamageZombie(victim, 50, DamageKind.Pierce, ShotKind.Pellet, Vector3.UnitY);
        game.DamageZombie(victim, 30, DamageKind.Pierce, ShotKind.Pellet, victim.Facing);
        Assert.Equal(20, Assert.Single(victim.ArmorPieces).Hp);
        Assert.True(victim.SinksIn(LiquidKind.Lava));
        victim.NextCell = new Cell(10, 16, 8);
        victim.MoveProgress = .5f;
        InstallDamagedCrust(game);

        game.Update(.01f);

        Assert.Empty(victim.ArmorPieces);
        Assert.Equal(10, victim.BodyHp);
        Assert.False(victim.SinksIn(LiquidKind.Lava));
        Assert.Null(victim.NextCell);
        Assert.Equal(0, victim.MoveProgress);
    }

    private static float ArmorHp(Zombie zombie, ArmorSlot slot) => zombie.ArmorPieces.Single(piece => piece.Slot == slot).Hp;

    private static void InstallDamagedCrust(GameSession game) =>
        game.Liquids.RestoreState(new LiquidState { Obsidian = [new(Source, 1, CrustFace.Bottom, 1, 4)] });

    private static void Position(Zombie zombie, Vector3 position)
    {
        zombie.Position = position;
        zombie.NextCell = null;
        zombie.MoveProgress = 0;
        zombie.Cooldown = 0;
        zombie.Lane = 0;
    }

    private static GameSession Battle(params ZombieGroup[] groups)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Waves = [new() { SpawnInterval = .001f, Groups = groups.ToList() }] });
        var game = new GameSession(levels: levels);
        Assert.True(game.StartLevel());
        game.Update(.01f);
        Assert.Equal(groups.Sum(group => group.Count), game.Zombies.Count);
        return game;
    }
}
