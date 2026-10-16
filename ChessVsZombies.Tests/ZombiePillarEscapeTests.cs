using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class ZombiePillarEscapeTests
{
    [Theory]
    [InlineData(ZombieSize.Standard)]
    [InlineData(ZombieSize.Milli)]
    [InlineData(ZombieSize.Kilo)]
    [InlineData(ZombieSize.Mega)]
    public void AThreeBlockPillarCanBeLeftWithoutDamagingItsFloorOrTheZombie(ZombieSize size)
    {
        var (game, zombie, pillar) = BeginOnPillar(3, size: size);
        var bodyHp = zombie.BodyHp;

        Land(game, zombie);

        Assert.Equal(bodyHp, zombie.BodyHp);
        AssertPillarIntact(game, pillar);
        Assert.Empty(game.Feedback.Hits);
        Assert.Equal(0, zombie.CarriedBlocks);
    }

    [Fact]
    public void AFourBlockPillarCanBeLeftForAnExactlyFiveDamageLanding()
    {
        var (game, zombie, pillar) = BeginOnPillar(4);

        Land(game, zombie);

        Assert.Equal(5, zombie.BodyHp);
        AssertPillarIntact(game, pillar);
        Assert.Equal(5, Assert.Single(game.Waves).DamageTaken);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public void ALethalDropIsShortenedByMiningItsActualSupportAtTheOrdinaryAttackCadence(int height)
    {
        var (game, zombie, pillar) = BeginOnPillar(height, ZombieKind.Armed);
        var top = pillar[^1];
        var start = zombie.Position;
        int attack = LevelCatalog.ZombieAttack(ZombieKind.Armed, GearTier.Osmium);

        game.Update(.01f);

        Assert.Equal(start, zombie.Position);
        Assert.Equal(Balance.DirtBlockHp - attack, game.World.GetTerrainHp(top));
        Assert.InRange(zombie.Cooldown, .9f, 1f);
        Assert.Equal(Balance.ZombieHp, zombie.BodyHp);
        game.Update(.45f);
        Assert.Equal(start, zombie.Position);
        Assert.Equal(Balance.DirtBlockHp - attack, game.World.GetTerrainHp(top));

        Land(game, zombie, 35);

        Assert.Equal(Material.Air, game.World.GetMaterial(top));
        Assert.Equal(5, zombie.BodyHp);
        Assert.Equal(Material.Dirt, game.World.GetMaterial(pillar[0]));
        Assert.Equal(Balance.DirtBlockHp, game.World.GetTerrainHp(pillar[0]));
        Assert.Equal(0, zombie.CarriedBlocks);
    }

    [Fact]
    public void ArmorCannotMakeALethalFourBlockDropSafeForAFiveBodyHpZombie()
    {
        var (game, zombie, pillar) = BeginOnPillar(4, ZombieKind.ArmedArmored);
        game.DamageZombie(zombie, 5, DamageKind.Chemical);
        Assert.Equal(5, zombie.BodyHp);
        Assert.True(zombie.Hp > 10);
        var armor = zombie.ArmorPieces.Select(piece => (piece.Slot, piece.Hp)).ToArray();
        var start = zombie.Position;
        var top = pillar[^1];

        game.Update(.01f);

        Assert.Equal(start, zombie.Position);
        Assert.Equal(Balance.DirtBlockHp - zombie.Attack, game.World.GetTerrainHp(top));
        Land(game, zombie);

        Assert.Equal(5, zombie.BodyHp);
        Assert.Equal(armor, zombie.ArmorPieces.Select(piece => (piece.Slot, piece.Hp)).ToArray());
        Assert.Equal(Material.Air, game.World.GetMaterial(top));
        Assert.Equal(5, Assert.Single(game.Waves).DamageTaken);
    }

    [Fact]
    public void DamageBeforeLeavingTheEdgeCancelsANowLethalDropAndMinesASaferDescent()
    {
        var (game, zombie, pillar) = BeginOnPillar(4, ZombieKind.Armed);
        var start = zombie.Position;

        game.Update(.2f);

        Assert.Equal(start.Y, zombie.Position.Y);
        Assert.InRange(Vector3.Distance(start, zombie.Position), .001f, .2f);
        AssertPillarIntact(game, pillar);
        game.DamageZombie(zombie, 5, DamageKind.Chemical);
        Assert.Equal(5, zombie.BodyHp);

        game.Update(.02f);

        Assert.Equal(start.Y, zombie.Position.Y);
        Assert.Equal(5, zombie.BodyHp);
        Land(game, zombie);

        Assert.Equal(5, zombie.BodyHp);
        Assert.Equal(Material.Air, game.World.GetMaterial(pillar[^1]));
        Assert.Equal(5, Assert.Single(game.Waves).DamageTaken);
    }

    [Fact]
    public void AnIsolatedRetreatSelfRouteCanLeaveATallPillarForTheRealGround()
    {
        var (game, zombie, pillar) = BeginOnPillar(3);
        typeof(GameSession).GetMethod("BeginZombieRetreat", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(game, [zombie]);
        var start = zombie.Position;
        Assert.True(game.IsZombieRetreating(zombie));

        game.Update(.02f);

        Assert.InRange(Vector3.Distance(start, zombie.Position), 0, zombie.MovementSpeed * .02f + .0001f);
        Land(game, zombie);

        Assert.True(game.IsZombieRetreating(zombie));
        Assert.Equal(Balance.ZombieHp, zombie.BodyHp);
        Assert.Equal(0, zombie.CarriedBlocks);
        AssertPillarIntact(game, pillar);
    }

    private static (GameSession Game, Zombie Zombie, Cell[] Pillar) BeginOnPillar(int height,
        ZombieKind kind = ZombieKind.Basic, ZombieSize size = ZombieSize.Standard)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Number = 1,
            Name = "Pillar escape",
            Waves = [new() { Groups = [new() { Count = 1, Kind = kind, Tier = GearTier.Osmium, Size = size }] }]
        });
        var game = new GameSession(12345, levels);
        var zombie = new Zombie
        {
            Id = -1, WaveIndex = 0, Kind = kind, Tier = GearTier.Osmium, Size = size,
            Attack = LevelCatalog.ZombieAttack(kind, GearTier.Osmium, size), Lane = 0
        };
        var place = typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Cell, Material, bool>>(game.World);
        var pillar = new List<Cell>();
        for (int y = 16; y < 16 + height; y++)
            for (int x = 8; x < 8 + zombie.GridFootprint; x++)
                for (int z = 8; z < 8 + zombie.GridFootprint; z++)
                {
                    var cell = new Cell(x, y, z);
                    Assert.True(place(cell, Material.Dirt));
                    pillar.Add(cell);
                }
        zombie.Position = zombie.StandAt(new Cell(8, 16 + height, 8));
        typeof(Zombie).GetProperty(nameof(Zombie.CarriedBlocks))!.SetValue(zombie, 0);
        if (zombie.IsGiant)
        {
            // Isolate movement from the giant's independent terrain-pickup action.
            var cooldowns = (Dictionary<int, float>)typeof(GameSession)
                .GetField(zombie.Size == ZombieSize.Kilo ? "_kiloActionCooldowns" : "_megaActionCooldowns",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
            cooldowns[zombie.Id] = 1000;
        }
        Assert.True(game.StartLevel(), game.Message);
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        game.Zombies.Add(zombie);
        return (game, zombie, pillar.ToArray());
    }

    private static void Land(GameSession game, Zombie zombie, float seconds = 20)
    {
        var ground = 16 + zombie.StandingHeight;
        var clearBody = typeof(GameSession).GetMethod("IsBodyClear", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Vector3, Zombie, bool>>(game);
        for (float elapsed = 0; elapsed < seconds && zombie.Position.Y > ground + .0001f; elapsed += .02f)
        {
            var previous = zombie.Position;
            game.Update(.02f);
            Assert.True(zombie.BodyHp > 0, $"The zombie chose a lethal descent from {previous}.");
            Assert.Contains(zombie, game.Zombies);
            Assert.True(clearBody(zombie.Position, zombie), $"The departing body intersected terrain at {zombie.Position}.");
            var horizontal = new Vector2(zombie.Position.X - previous.X, zombie.Position.Z - previous.Z);
            Assert.InRange(horizontal.Length(), 0, zombie.MovementSpeed * .02f + .0001f);
        }
        Assert.True(Math.Abs(zombie.Position.Y - ground) <= .0001f,
            $"{zombie.Size} remained at {zombie.Position}; next={zombie.NextCell}, retreat={game.IsZombieRetreating(zombie)}, damaged={string.Join(';', game.World.DamagedTerrain)}.");
    }

    private static void AssertPillarIntact(GameSession game, IEnumerable<Cell> pillar)
    {
        Assert.All(pillar, cell =>
        {
            Assert.Equal(Material.Dirt, game.World.GetMaterial(cell));
            Assert.Equal(Balance.DirtBlockHp, game.World.GetTerrainHp(cell));
        });
        Assert.Empty(game.World.DamagedTerrain);
    }
}
