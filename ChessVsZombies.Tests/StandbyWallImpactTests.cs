using System.Collections;
using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class StandbyWallImpactTests
{
    [Fact]
    public void BulletDoesNotHitStandbyBeforeItsBoundaryWall()
    {
        var game = Battle(1);
        var front = Assert.Single(Fronts(game));
        var shot = Shot(front);
        game.Projectiles.Add(shot);

        UpdateShots(game, .025f); // The bullet has passed the old interior zombie volume.
        Assert.Single(game.Projectiles);
        Assert.Equal(10, Hp(front));
        Assert.Empty(game.ProjectileDiagnostics!.Contacts);

        UpdateShots(game, .01f); // The bullet now crosses X/Z = 0 or 32.
        Assert.Empty(game.Projectiles);
        Assert.Equal(9, Hp(front));
        var contact = Assert.Single(game.ProjectileDiagnostics.Contacts);
        Assert.Equal("standby-body", contact.Kind);
        Assert.InRange(contact.Age, .03124f, .03126f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AllFourSideWallsUseTheirExactOuterFace(int face)
    {
        var game = Battle(5000);
        var front = Fronts(game).First(candidate => FaceOf(candidate) == face);
        var shot = Shot(front);
        game.Projectiles.Add(shot);
        UpdateShots(game, .025f);
        Assert.Single(game.Projectiles);
        Assert.Equal(10, Hp(front));
        UpdateShots(game, .01f);
        Assert.Empty(game.Projectiles);
        Assert.Equal(9, Hp(front));
        Assert.InRange(Assert.Single(game.ProjectileDiagnostics!.Contacts).Age, .03124f, .03126f);
    }

    [Fact]
    public void CornerCrossingExposesOnlyItsOwnStack()
    {
        var game = Battle(5000);
        var front = Fronts(game).Single(candidate => CellOf(candidate) is { X: 0, Z: 0 });
        var center = new Zombie().StandAt(CellOf(front));
        var cornerShot = new Projectile { Origin = center + new Vector3(2, 0, 2),
            Target = center - new Vector3(2, 0, 2), Damage = 1, Kind = ShotKind.Pellet, Duration = .05f };
        game.Projectiles.Add(cornerShot);
        UpdateShots(game, .05f);
        Assert.Equal(9, Hp(front));
        Assert.Equal(5000, game.LivingStandbyCount);
        Assert.Equal("standby-body", Assert.Single(game.ProjectileDiagnostics!.Contacts).Kind);
    }

    [Fact]
    public void ShotEndingExactlyOnTheWallCountsAsContact()
    {
        var game = Battle(5000);
        var front = Fronts(game).First(candidate => FaceOf(candidate) == 0);
        var center = new Zombie().StandAt(CellOf(front));
        game.Projectiles.Add(new Projectile { Origin = center + Vector3.UnitX * 2,
            Target = new Vector3(0, center.Y, center.Z), Damage = 1,
            Kind = ShotKind.Pellet, Duration = .05f });
        UpdateShots(game, .05f);
        Assert.Equal(9, Hp(front));
        Assert.Empty(game.Projectiles);
        Assert.Equal(.05f, Assert.Single(game.ProjectileDiagnostics!.Contacts).Age);
    }

    [Fact]
    public void SharedWallFootprintEdgeChoosesOneFrontInScheduledOrder()
    {
        var game = Battle(5000);
        var neighbors = Fronts(game).Where(candidate => CellOf(candidate) is { X: 0, Z: 0 or 1 })
            .OrderBy(Slot).ToArray();
        Assert.Equal(2, neighbors.Length);
        float y = new Zombie().StandAt(CellOf(neighbors[0])).Y;
        game.Projectiles.Add(new Projectile { Origin = new Vector3(2.5f, y, 1),
            Target = new Vector3(-1.5f, y, 1), Damage = 1,
            Kind = ShotKind.Pellet, Duration = .05f });
        UpdateShots(game, .05f);
        Assert.Equal(9, Hp(neighbors[0]));
        Assert.Equal(10, Hp(neighbors[1]));
        Assert.Single(game.ProjectileDiagnostics!.Contacts);
    }

    [Fact]
    public void MissingWallStackAndUnsupportedTopFaceStopWithoutQueuedDamage()
    {
        var game = Battle(5000);
        int living = game.LivingStandbyCount;
        var front = Fronts(game).First(candidate => FaceOf(candidate) == 0);
        var high = Shot(front);
        game.Projectiles.Add(new Projectile { Origin = high.Origin + Vector3.UnitY * 4,
            Target = high.Target + Vector3.UnitY * 4, Damage = 1, Kind = ShotKind.Pellet, Duration = .05f });
        UpdateShots(game, .05f);
        Assert.Equal("boundary", Assert.Single(game.ProjectileDiagnostics!.Contacts).Kind);
        Assert.Equal(living, game.LivingStandbyCount);

        game.ProjectileDiagnostics.Contacts.Clear();
        game.Projectiles.Add(new Projectile { Origin = new Vector3(16.5f, 30, 16.5f),
            Target = new Vector3(16.5f, 34, 16.5f), Damage = 1, Kind = ShotKind.Pellet, Duration = .05f });
        UpdateShots(game, .05f);
        Assert.Equal("boundary", Assert.Single(game.ProjectileDiagnostics.Contacts).Kind);
        Assert.Equal(living, game.LivingStandbyCount);

        game.ProjectileDiagnostics.Contacts.Clear();
        var center = new Zombie().StandAt(CellOf(front));
        game.Projectiles.Add(new Projectile { Origin = new Vector3(2.5f, 31, center.Z),
            Target = new Vector3(-1.5f, 35, center.Z), Damage = 1,
            Kind = ShotKind.Pellet, Duration = .05f });
        UpdateShots(game, .05f); // Y = 32 is reached before X = 0.
        Assert.Equal("boundary", Assert.Single(game.ProjectileDiagnostics.Contacts).Kind);
        Assert.Equal(10, Hp(front));
    }

    [Fact]
    public void InwardWallMotionAndOutsideOriginDoNotDamageStandby()
    {
        var game = Battle(5000);
        var front = Fronts(game).First(candidate => FaceOf(candidate) == 0);
        var center = new Zombie().StandAt(CellOf(front));
        game.Projectiles.Add(new Projectile { Origin = new Vector3(0, center.Y, center.Z),
            Target = new Vector3(1, center.Y, center.Z), Damage = 1, Kind = ShotKind.Pellet, Duration = .05f });
        UpdateShots(game, .05f);
        Assert.Equal(10, Hp(front));
        Assert.Single(game.Projectiles);

        game.Projectiles.Clear();
        game.Projectiles.Add(new Projectile { Origin = new Vector3(-.1f, center.Y, center.Z),
            Target = new Vector3(1, center.Y, center.Z), Damage = 1, Kind = ShotKind.Pellet, Duration = .05f });
        UpdateShots(game, .05f);
        Assert.Equal(10, Hp(front));
        Assert.Empty(game.Projectiles);
        Assert.Equal("boundary", Assert.Single(game.ProjectileDiagnostics!.Contacts).Kind);
    }

    [Fact]
    public void ShotStartingOnWallOnlyDamagesQueueWhenItTravelsOutward()
    {
        var game = Battle(5000);
        var front = Fronts(game).First(candidate => FaceOf(candidate) == 0);
        var center = new Zombie().StandAt(CellOf(front));
        game.Projectiles.Add(new Projectile { Origin = new Vector3(0, center.Y, center.Z),
            Target = new Vector3(-1, center.Y, center.Z), Damage = 1,
            Kind = ShotKind.Pellet, Duration = .05f });
        UpdateShots(game, .05f);
        Assert.Equal(9, Hp(front));
        Assert.Empty(game.Projectiles);
        Assert.Equal(0, Assert.Single(game.ProjectileDiagnostics!.Contacts).Age);
    }

    [Fact]
    public void GiantCornerUsesItsFullProjectedFootprintAtBothWalls()
    {
        var game = Battle(new ZombieGroup { Count = 4097, Size = ZombieSize.Kilo, IsBoss = true });
        var front = Fronts(game).Single(candidate => CellOf(candidate) is { X: 0, Z: 0 });
        var center = new Zombie { Size = ZombieSize.Kilo }.StandAt(CellOf(front));
        float shield = (float?)front.GetType().GetProperty("CoverHp")!.GetValue(front) ?? 0;
        Assert.True(shield > 0);
        foreach (var outward in new[] { -Vector3.UnitX, -Vector3.UnitZ })
        {
            // Aim inside the corner's wall footprint, away from adjacent
            // giant footprints that share its projected edge.
            var aim = new Vector3(outward.X != 0 ? center.X : .5f,
                CellOf(front).Y + .5f, outward.Z != 0 ? center.Z : .5f);
            game.Projectiles.Add(new Projectile { Origin = aim - outward * 3,
                Target = aim + outward * 3, Damage = 1,
                Kind = ShotKind.Pellet, Duration = .05f });
            UpdateShots(game, .05f);
            Assert.Empty(game.Projectiles);
        }
        Assert.Equal(shield - 2, (float?)front.GetType().GetProperty("CoverHp")!.GetValue(front));
        Assert.Equal("standby-cover", game.ProjectileDiagnostics!.Contacts[^1].Kind);
    }

    [Fact]
    public void TerrainAndActiveZombieAheadOfWallKeepFirstCollision()
    {
        var game = Battle(5000);
        var front = Fronts(game).First(candidate => FaceOf(candidate) == 0);
        var cell = CellOf(front);
        Assert.True((bool)typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", Flags)!
            .Invoke(game.World, [new Cell(1, cell.Y, cell.Z), Material.Dirt])!);
        game.Projectiles.Add(Shot(front));
        UpdateShots(game, .05f);
        Assert.Equal("terrain", Assert.Single(game.ProjectileDiagnostics!.Contacts).Kind);
        Assert.Equal(10, Hp(front));

        var activeGame = Battle(5000);
        var activeFront = Fronts(activeGame).First(candidate => FaceOf(candidate) == 0);
        var center = new Zombie().StandAt(CellOf(activeFront));
        var blocker = new Zombie { Id = 987, Position = center + Vector3.UnitX };
        activeGame.Zombies.Add(blocker);
        activeGame.Projectiles.Add(Shot(activeFront));
        UpdateShots(activeGame, .05f);
        Assert.Equal("body", Assert.Single(activeGame.ProjectileDiagnostics!.Contacts).Kind);
        Assert.Equal(9, blocker.Hp);
        Assert.Equal(10, Hp(activeFront));
    }

    [Fact]
    public void HighSpeedPelletAndDescendingLobReachTheWallWithoutEarlyQueueContact()
    {
        var game = Battle(5000);
        var front = Fronts(game).First(candidate => FaceOf(candidate) == 0);
        var center = new Zombie().StandAt(CellOf(front));
        game.Projectiles.Add(new Projectile { Origin = center + Vector3.UnitX * 10,
            Target = center - Vector3.UnitX * 10, Damage = 1, Kind = ShotKind.Pellet, Duration = .001f });
        UpdateShots(game, .05f);
        Assert.Equal(9, Hp(front));
        Assert.InRange(Assert.Single(game.ProjectileDiagnostics!.Contacts).Age, .00052f, .00053f);

        game.ProjectileDiagnostics.Contacts.Clear();
        game.Projectiles.Add(new Projectile { Origin = center + Vector3.UnitX * 2,
            Target = center - Vector3.UnitX * 2 - Vector3.UnitY * 2.5f,
            Damage = 1, Kind = ShotKind.Lob, Duration = .05f });
        UpdateShots(game, .025f);
        Assert.Single(game.Projectiles);
        Assert.Equal(9, Hp(front));
        UpdateShots(game, .01f);
        Assert.Empty(game.Projectiles);
        Assert.Equal(8, Hp(front));
        Assert.InRange(Assert.Single(game.ProjectileDiagnostics.Contacts).Age, .03124f, .03126f);
    }

    private static GameSession Battle(int count) => Battle(new ZombieGroup { Count = count });

    private static GameSession Battle(ZombieGroup group)
    {
        var catalog = new LevelCatalog();
        catalog.Set(new LevelDefinition { Waves = [new WaveDefinition { SpawnInterval = 10,
            Groups = [group] }] });
        var game = new GameSession(12345, catalog)
        { ProjectileDiagnostics = new ProjectileDiagnostics { CaptureContacts = true } };
        Assert.True(game.StartLevel());
        return game;
    }

    private static object[] Fronts(GameSession game)
    {
        var stacks = (IDictionary)typeof(GameSession).GetField("_standbyStacks", Flags)!.GetValue(game)!;
        return stacks.Values.Cast<object>()
            .Select(stack => stack.GetType().GetProperty("Front")!.GetValue(stack))
            .Where(front => front != null).Cast<object>().ToArray();
    }

    private static Cell CellOf(object front) => ((Cell?)front.GetType().GetProperty("Cell")!.GetValue(front))!.Value;
    private static int Slot(object front) => (int)front.GetType().GetProperty("Slot")!.GetValue(front)!;
    private static int FaceOf(object front)
    {
        var cell = CellOf(front);
        if (cell.X == 0 && cell.Z is > 0 and < 31) return 0;
        if (cell.X == 31 && cell.Z is > 0 and < 31) return 1;
        if (cell.Z == 0 && cell.X is > 0 and < 31) return 2;
        if (cell.Z == 31 && cell.X is > 0 and < 31) return 3;
        return -1;
    }

    private static Projectile Shot(object front)
    {
        var cell = (Cell?)front.GetType().GetProperty("Cell")!.GetValue(front);
        var group = (ZombieGroup)front.GetType().GetProperty("Group")!.GetValue(front)!;
        var shape = new Zombie { Size = group.Size };
        var position = shape.StandAt(cell!.Value);
        var inward = cell.Value.X == 0 ? Vector3.UnitX :
            cell.Value.X + shape.GridFootprint == WorldGrid.Size ? -Vector3.UnitX :
            cell.Value.Z == 0 ? Vector3.UnitZ : -Vector3.UnitZ;
        return new Projectile { Origin = position + inward * 2, Target = position - inward * 2,
            Damage = 1, Kind = ShotKind.Pellet, Duration = .05f };
    }

    private static float Hp(object front) => (float)front.GetType().GetProperty("Hp")!.GetValue(front)!;
    private static void UpdateShots(GameSession game, float dt) =>
        typeof(GameSession).GetMethod("UpdateProjectiles", Flags)!.Invoke(game, [dt]);
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
}
