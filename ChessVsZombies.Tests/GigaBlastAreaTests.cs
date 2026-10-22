using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class GigaBlastAreaTests
{
    [Fact]
    public void ABlastDamagesAllTwentySevenCellsIncludingThreeAxisCornersAndExcludesSecondNeighbors()
    {
        var (game, owner) = Battle();
        var center = new Cell(10, 18, 10);
        var affected = new List<Cell>();
        for (int x = 9; x <= 11; x++)
            for (int y = 17; y <= 19; y++)
                for (int z = 9; z <= 11; z++)
                {
                    var cell = new Cell(x, y, z);
                    Place(game, cell);
                    affected.Add(cell);
                }
        Cell[] untouched =
        [
            new(8, 18, 10), new(12, 18, 10), new(10, 16, 10),
            new(10, 20, 10), new(10, 18, 8), new(10, 18, 12),
            new(12, 19, 11), new(9, 20, 11), new(9, 19, 12)
        ];
        // The lower second neighbor supports the connected lattice. The other
        // guards share its exterior faces, edges, or corners without belonging
        // to one of the 27 affected grid cells.
        foreach (var cell in untouched) Place(game, cell);
        var shot = new GigaProjectile { OwnerId = owner.Id, Damage = 1 };

        Explode(game, shot, center.Center);

        Assert.Equal(27, affected.Count);
        Assert.All(affected, cell => Assert.Equal(Balance.StoneBlockHp - 1, game.World.GetTerrainHp(cell)));
        Assert.All(untouched, cell => Assert.Equal(Balance.StoneBlockHp, game.World.GetTerrainHp(cell)));
        Assert.Equal(27, game.World.DamagedTerrain.Count());
        Assert.Equal(2, owner.Giga!.AttackCounter); // One successful attack, regardless of targets hit.
        Assert.Equal(3, shot.BlastSize);
        Assert.Equal(1.5f, shot.Radius);
    }

    [Fact]
    public void ACornerImpactClipsTheCubeToTheTwelveInBoardCellsWithoutReachingSecondNeighbors()
    {
        var (game, owner) = Battle();
        var center = new Cell(0, 17, 0);
        var affected = new List<Cell>();
        for (int x = 0; x <= 1; x++)
            for (int y = 16; y <= 18; y++)
                for (int z = 0; z <= 1; z++)
                {
                    var cell = new Cell(x, y, z);
                    Place(game, cell);
                    affected.Add(cell);
                }
        Cell[] untouched = [new(2, 17, 0), new(0, 17, 2), new(0, 19, 0)];
        foreach (var cell in untouched) Place(game, cell);

        Explode(game, new GigaProjectile { OwnerId = owner.Id, Damage = 1 }, center.Center);

        Assert.Equal(12, affected.Count);
        Assert.All(affected, cell => Assert.Equal(Balance.StoneBlockHp - 1, game.World.GetTerrainHp(cell)));
        Assert.All(untouched, cell => Assert.Equal(Balance.StoneBlockHp, game.World.GetTerrainHp(cell)));
        Assert.Equal(12, game.World.DamagedTerrain.Count());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(185)]
    public void AttackStrengthCannotShrinkOrExpandTheFixedCube(int attackCounter)
    {
        Cell center = new(10, 16, 5), corner = new(11, 16, 6), secondNeighbor = new(12, 16, 5);
        var (game, owner) = Battle(center, corner, secondNeighbor);
        owner.RestoreGigaState(10, 10, 10, attackCounter, 0);
        var centerTurret = game.TrySelectTurret(center)!;
        var cornerTurret = game.TrySelectTurret(corner)!;
        var outsideTurret = game.TrySelectTurret(secondNeighbor)!;
        float damage = owner.AttackDamage;
        var shot = new GigaProjectile { OwnerId = owner.Id, TargetId = centerTurret.Id, Damage = damage };

        Explode(game, shot, center.Center);

        float expected = float.IsPositiveInfinity(damage) ? 0 : Balance.TurretHp - damage;
        Assert.Equal(expected, centerTurret.Hp, 4);
        Assert.Equal(expected, cornerTurret.Hp, 4);
        Assert.Equal(Balance.TurretHp, outsideTurret.Hp);
        Assert.Contains(outsideTurret, game.Turrets);
        Assert.Equal(Balance.CoreHp, game.CoreHp);
        Assert.Equal(3, shot.BlastSize);
        Assert.Equal(1.5f, shot.Radius);
        Assert.Equal(1.5f, owner.Giga!.BlastRadius);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AProjectileTravelingThroughANegativeFaceAnchorsItsBlastToTheStruckCell(bool alongX)
    {
        var struck = alongX ? new Cell(10, 17, 5) : new Cell(5, 17, 10);
        var inside = alongX ? new Cell(9, 16, 5) : new Cell(5, 16, 9);
        var outside = alongX ? new Cell(12, 16, 5) : new Cell(5, 16, 12);
        var (game, owner) = Battle(inside, outside);
        var insideTurret = game.TrySelectTurret(inside)!;
        var outsideTurret = game.TrySelectTurret(outside)!;
        Place(game, struck + new Cell(0, -1, 0));
        Place(game, struck);
        var origin = alongX ? new Vector3(14, 17.5f, 5.5f) : new Vector3(5.5f, 17.5f, 14);
        var shot = new GigaProjectile
        {
            OwnerId = owner.Id, Origin = origin, Target = struck.Center, Damage = 2, Duration = 1
        };
        game.GigaProjectiles.Add(shot);

        Invoke(game, "UpdateProjectiles", 1f);

        Assert.Empty(game.GigaProjectiles);
        Assert.Equal(Balance.StoneBlockHp - 2, game.World.GetTerrainHp(struck));
        Assert.Equal(Balance.TurretHp - 2, insideTurret.Hp);
        Assert.Equal(Balance.TurretHp, outsideTurret.Hp);
        Assert.Equal(2, owner.Giga!.AttackCounter);
    }

    private static (GameSession Game, Zombie Owner) Battle(params Cell[] towers)
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
        var owner = new Zombie { Id = 1000, WaveIndex = -1, Size = ZombieSize.Giga };
        owner.RestoreGigaState(10, 10, 10, 1, 0);
        owner.Position = owner.StandAt(new Cell(2, 16, 2));
        game.Zombies.Add(owner);
        return (game, owner);
    }

    private static void Place(GameSession game, Cell cell) => Assert.True((bool)Invoke(game.World, "RestoreTerrainPlacement", cell, Material.Stone)!);

    private static void Explode(GameSession game, GigaProjectile shot, Vector3 center) =>
        Invoke(game, "ExplodeGigaProjectile", shot, center);

    private static object? Invoke(object owner, string name, params object?[] arguments) => owner.GetType()
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, arguments);
}
