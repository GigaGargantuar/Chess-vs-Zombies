using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class ZombieSizeTests
{
    public static IEnumerable<object[]> SizesAndKinds()
    {
        foreach (var size in Enum.GetValues<ZombieSize>())
            foreach (var kind in Enum.GetValues<ZombieKind>()) yield return [size, kind];
    }

    [Theory]
    [MemberData(nameof(SizesAndKinds))]
    public void SizesScaleActualBodyAndEveryGearPieceWithoutChangingWeaponStatus(ZombieSize size, ZombieKind kind)
    {
        var zombie = new Zombie { Size = size, Kind = kind, Tier = GearTier.Osmium };
        var healthMultiplier = size switch { ZombieSize.Milli => .5f, ZombieSize.Kilo => 4, ZombieSize.Mega => 9, _ => 1 };
        var bodyScale = size switch { ZombieSize.Milli => .5f, ZombieSize.Kilo => 2, ZombieSize.Mega => 3, _ => 1 };
        bool armored = kind is ZombieKind.Armored or ZombieKind.ArmedArmored;

        Assert.Equal(10 * healthMultiplier, zombie.BodyHp);
        Assert.Equal(zombie.IsGiant ? new Vector3(bodyScale * .5f) : new Vector3(.2f, .39f, .2f) * bodyScale, zombie.BodyHalfExtents);
        Assert.Equal(.5f * bodyScale, zombie.StandingHeight);
        Assert.Equal(armored ? 4 : 0, zombie.ArmorPieces.Count);
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(210 * healthMultiplier, piece.Hp));
        Assert.Equal((10 + (armored ? 840 : 0)) * healthMultiplier, zombie.Hp);
        Assert.Equal(zombie.Hp, zombie.MaxHp);
        Assert.Equal(zombie.MaxHp, LevelCatalog.ZombieMaxHp(kind, GearTier.Osmium, size));
        Assert.Equal(kind is ZombieKind.Armed or ZombieKind.ArmedArmored, zombie.HasWeapon);

        // Compatibility HP assignments must restore the scaled body and gear,
        // rather than silently recreating standard-sized health pools.
        zombie.Hp -= 1;
        zombie.Hp = zombie.MaxHp;
        Assert.Equal(10 * healthMultiplier, zombie.BodyHp);
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(210 * healthMultiplier, piece.Hp));
    }

    [Theory]
    [MemberData(nameof(SizesAndKinds))]
    public void PublicCombatMovementUsesBothWeaponAndSizeSpeedModifiers(ZombieSize size, ZombieKind kind)
    {
        var game = Combat(size, kind);
        var zombie = Add(game, size, kind, new Cell(5, 16, 5));
        zombie.NextCell = new Cell(6, 16, 5);
        var start = zombie.Position;
        float weaponSpeed = kind is ZombieKind.Armed or ZombieKind.ArmedArmored ? .85f : 1.275f;
        float sizeSpeed = size switch { ZombieSize.Milli => 2, ZombieSize.Kilo => .75f, ZombieSize.Mega => .5f, _ => 1 };

        // Keep this interval within one real walking step, avoiding the separate
        // grid decision slice at a completed tile boundary.
        game.Update(.25f);

        Assert.Equal(weaponSpeed * sizeSpeed, zombie.MovementSpeed, 5);
        Assert.InRange(Math.Abs(weaponSpeed * sizeSpeed * .25f - (zombie.Position.X - start.X)), 0, .000002f);
        Assert.Equal(start.Y, zombie.Position.Y, 5);
        Assert.Equal(start.Z, zombie.Position.Z, 5);
    }

    [Theory]
    [InlineData(ZombieSize.Milli, 16.25f)]
    [InlineData(ZombieSize.Standard, 16.5f)]
    [InlineData(ZombieSize.Kilo, 17f)]
    [InlineData(ZombieSize.Mega, 17.5f)]
    public void SpawningUsesTheSizedStandingHeightAndKeepsTheNavigationFootAnchor(ZombieSize size, float expectedY)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Number = 1,
            Waves = [new WaveDefinition { Groups = [new ZombieGroup { Kind = ZombieKind.Armed, Size = size, Count = 1 }] }]
        });
        var game = new GameSession(123, levels);
        Assert.True(game.StartLevel());
        var spawnCell = new Cell(0, 16, 8);
        typeof(GameSession).GetField("_spawnCells", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, new List<Cell> { spawnCell });

        game.Update(.001f);

        var zombie = Assert.Single(game.Zombies);
        Assert.Equal(size, zombie.Size);
        Assert.Equal(expectedY, zombie.Position.Y, 5);
        if (zombie.IsGiant)
        {
            var min = zombie.Position - zombie.BodyHalfExtents;
            var max = zombie.Position + zombie.BodyHalfExtents;
            Assert.Equal(new Vector3(zombie.Cell.X, zombie.Cell.Y, zombie.Cell.Z), min);
            Assert.True(min.X == 0 || min.Z == 0 || max.X == WorldGrid.Size || max.Z == WorldGrid.Size);
            Assert.InRange(max.X, zombie.GridFootprint, WorldGrid.Size);
            Assert.InRange(max.Z, zombie.GridFootprint, WorldGrid.Size);
        }
        else Assert.Equal(spawnCell, zombie.Cell);
        Assert.True(game.World.IsSolidAt(zombie.Position - Vector3.UnitY * (zombie.StandingHeight + .01f)));
        Assert.False(game.World.IsSolidAt(zombie.Position - zombie.BodyHalfExtents));
    }

    [Theory]
    [InlineData(ZombieSize.Milli, .3f, false)]
    [InlineData(ZombieSize.Milli, .2f, true)]
    [InlineData(ZombieSize.Standard, .3f, true)]
    [InlineData(ZombieSize.Standard, .75f, false)]
    [InlineData(ZombieSize.Kilo, .75f, true)]
    public void ActualProjectileContactUsesHalfAndDoubleHitboxSizes(ZombieSize size, float sidewaysOffset, bool hits)
    {
        var game = new GameSession();
        var zombie = Add(game, size, ZombieKind.Basic, new Cell(5, 16, 5));
        var originalHp = zombie.BodyHp;
        game.Projectiles.Add(new Projectile
        {
            Origin = new Vector3(2.5f, zombie.Position.Y, zombie.Position.Z + sidewaysOffset),
            Target = new Vector3(10.5f, zombie.Position.Y, zombie.Position.Z + sidewaysOffset),
            TargetId = zombie.Id, Damage = 1, Kind = ShotKind.Pellet, Duration = 1
        });

        game.Update(.5f);

        Assert.Equal(originalHp - (hits ? 1 : 0), zombie.BodyHp);
    }

    [Fact]
    public void AMilliWalksUnderACeilingThatPhysicallyBlocksAKilo()
    {
        foreach (var size in new[] { ZombieSize.Milli, ZombieSize.Kilo })
        {
            var game = Combat(size, ZombieKind.Armed);
            var start = new Cell(size == ZombieSize.Kilo ? 4 : 5, 16, 5);
            var zombie = Add(game, size, ZombieKind.Armed, start);
            // A roof attached to a full side column is stable under normal block
            // support rules; it does not fall away to make this test pass.
            Invoke(game.World, "AddBarrier", new Cell(6, 16, 6), Material.Stone, (float?)500f);
            Invoke(game.World, "AddBarrier", new Cell(6, 17, 6), Material.Stone, (float?)500f);
            Invoke(game.World, "AddBarrier", new Cell(6, 17, 5), Material.Stone, (float?)500f);
            zombie.NextCell = start + new Cell(1, 0, 0);
            var startX = zombie.Position.X;

            game.Update(.2f);

            if (size == ZombieSize.Milli) Assert.True(zombie.Position.X > startX + .3f);
            else Assert.Equal(startX, zombie.Position.X, 5);
            Assert.True((bool)Invoke(game, "IsBodyClear", zombie.Position, zombie)!);
        }
    }

    [Theory]
    [InlineData(ZombieSize.Milli, .4f, true)]
    [InlineData(ZombieSize.Standard, .6f, false)]
    [InlineData(ZombieSize.Standard, .7f, true)]
    [InlineData(ZombieSize.Kilo, 1f, false)]
    [InlineData(ZombieSize.Kilo, 1.4f, true)]
    public void DrowningDependsOnTheScaledHeadHeightRatherThanTheOccupiedCell(ZombieSize size, float depth, bool submerged)
    {
        var game = new GameSession();
        var zombie = Add(game, size, ZombieKind.Armored, new Cell(8, 16, 8), GearTier.Osmium);
        int units = (int)MathF.Round(depth * 10);
        var column = new Cell((int)zombie.Position.X, 16, (int)zombie.Position.Z);
        Assert.True(game.Liquids.TryAdd(column, LiquidKind.Water, Math.Min(10, units)));
        if (units > 10) Assert.True(game.Liquids.TryAdd(column + new Cell(0, 1, 0), LiquidKind.Water, units - 10));

        Invoke(game, "UpdateLiquidExposure", zombie, 5f);

        Assert.Equal(submerged ? 5 : Balance.AirSeconds, zombie.AirRemaining);
        Assert.Equal(zombie.MaxBodyHp, zombie.BodyHp);
        Invoke(game, "UpdateLiquidExposure", zombie, 5.01f);
        Assert.Equal(submerged ? 0 : zombie.MaxBodyHp, zombie.BodyHp);
        if (submerged) Assert.Equal(0, zombie.Hp); // Armor durability cannot prevent suffocation.
    }

    [Theory]
    [InlineData(ZombieSize.Milli)]
    [InlineData(ZombieSize.Standard)]
    [InlineData(ZombieSize.Kilo)]
    public void FloatingPlacesEachSizedBodyAtItsOwnSurfaceOffset(ZombieSize size)
    {
        var game = new GameSession();
        var zombie = Add(game, size, ZombieKind.Basic, new Cell(8, 16, 8));
        Assert.True(game.Liquids.TryAdd(new Cell((int)zombie.Position.X, 16, (int)zombie.Position.Z), LiquidKind.Mercury, 10));

        Assert.True((bool)Invoke(game, "UpdateBuoyancy", zombie, 1f)!);

        Assert.Equal(17 + .4f * zombie.BodyScale, zombie.Position.Y, 5);
        Assert.False(game.World.IsSolidAt(zombie.Position - zombie.BodyHalfExtents));
    }

    [Theory]
    [InlineData(ZombieSize.Milli)]
    [InlineData(ZombieSize.Standard)]
    [InlineData(ZombieSize.Kilo)]
    public void ArmorLossImmediatelyChangesBuoyancyForEverySize(ZombieSize size)
    {
        var game = new GameSession();
        var zombie = Add(game, size, ZombieKind.Armored, new Cell(8, 16, 8), GearTier.Osmium);
        Assert.True(game.Liquids.TryAdd(new Cell((int)zombie.Position.X, 16, (int)zombie.Position.Z), LiquidKind.Mercury, 10));
        Assert.True(zombie.SinksIn(LiquidKind.Mercury));
        zombie.Hp = zombie.MaxBodyHp;

        Assert.Empty(zombie.ArmorPieces);
        Assert.False(zombie.SinksIn(LiquidKind.Mercury));
        Assert.True((bool)Invoke(game, "UpdateBuoyancy", zombie, 1f)!);
        Assert.Equal(17 + .4f * zombie.BodyScale, zombie.Position.Y, 5);
    }

    private static GameSession Combat(ZombieSize size, ZombieKind kind)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Number = 1,
            Waves = [new WaveDefinition { SpawnInterval = 1000, Groups = [new ZombieGroup { Kind = kind, Size = size, Count = 1 }] }]
        });
        var game = new GameSession(12345, levels);
        Assert.True(game.StartLevel());
        foreach (var wave in game.Waves) wave.Spawned = wave.TotalCount;
        return game;
    }

    private static Zombie Add(GameSession game, ZombieSize size, ZombieKind kind, Cell cell, GearTier tier = GearTier.Wood)
    {
        var zombie = new Zombie { Id = 100 + game.Zombies.Count, WaveIndex = -1, Size = size, Kind = kind, Tier = tier, Attack = 1 };
        zombie.Position = zombie.StandAt(cell);
        game.Zombies.Add(zombie);
        return zombie;
    }

    private static object? Invoke(object owner, string method, params object?[] arguments) =>
        owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);
}
