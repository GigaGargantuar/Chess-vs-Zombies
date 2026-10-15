using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class KiloPickupTests
{
    [Fact]
    public void AKiloRecoversADeadTeammatesActualBlockBeforeExcavatingTheFloor()
    {
        var game = new GameSession();
        var kilo = AddKilo(game);
        var source = DropTeammateBlock(game, new Cell(7, 16, 5), GearTier.Copper);
        Invoke(game.World, "DamageTerrain", source, 77f);
        var floor = new Cell(7, 15, 6);

        Invoke(game, "TryKiloAction", kilo, .05f);

        var recovered = Assert.Single(game.KiloCovers, cover => cover.Material == Material.Copper);
        Assert.Equal(923, recovered.Hp);
        Assert.False(recovered.WasBarrier);
        Assert.Equal(Material.Air, game.World.GetMaterial(source));
        Assert.Equal(Material.Dirt, game.World.GetMaterial(floor));
        Assert.Equal(100, game.World.GetTerrainHp(floor));
        Assert.Equal(2, game.KiloCovers.Count);
        Assert.Equal(0, kilo.CarriedBlocks);
        Assert.Empty(game.PendingBlockDrops);
        Assert.Equal(0, game.Inventory.GetCount(Material.Copper));
        Assert.False((bool)Invoke(game, "TryGrabKiloCover", kilo)!);

        Invoke(game, "HandleKiloDeath", kilo);
        Invoke(game, "HandleKiloDeath", kilo);

        Assert.Equal(2, game.PendingBlockDrops.Count);
        Assert.Contains(game.PendingBlockDrops, drop => drop.Content == Material.Copper && drop.Hp == 923 && !drop.WasBarrier);
        Assert.Contains(game.PendingBlockDrops, drop => drop.Content == Material.Dirt && drop.Hp == 100);
    }

    [Fact]
    public void NearbyLivingAlliesDoNotPreventRecoveryOfAnExposedTeammateBlock()
    {
        var game = new GameSession();
        var kilo = AddKilo(game);
        var ally = new Zombie { Id = 20, Size = ZombieSize.Kilo };
        ally.Position = ally.StandAt(new Cell(5, 16, 8));
        game.Zombies.Add(ally); // Cannot be hurled, and does not intersect the lift.
        var source = DropTeammateBlock(game, new Cell(7, 16, 5), GearTier.Wood);

        Invoke(game, "TryKiloAction", kilo, .05f);

        Assert.Contains(game.KiloCovers, cover => cover.Material == Material.Wood && cover.Hp == 250);
        Assert.Equal(Material.Air, game.World.GetMaterial(source));
        Assert.Equal(Material.Dirt, game.World.GetMaterial(new Cell(7, 15, 6)));
    }

    [Fact]
    public void AnExposedBarrierKeepsItsMaterialDurabilityAndProvenanceWhenLifted()
    {
        var game = new GameSession();
        var kilo = AddKilo(game);
        var source = new Cell(7, 16, 5);
        Assert.True((bool)Invoke(game.World, "AddBarrier", source, Material.Osmium, (float?)321f)!);

        Assert.True((bool)Invoke(game, "TryGrabKiloCover", kilo)!);

        var recovered = Assert.Single(game.KiloCovers, cover => cover.Material == Material.Osmium);
        Assert.True(recovered.WasBarrier);
        Assert.Equal(321, recovered.Hp);
        Assert.Null(game.World.GetBarrier(source));
        Invoke(game, "HandleKiloDeath", kilo);
        Assert.Contains(game.PendingBlockDrops, drop => drop.Content == Material.Osmium && drop.Hp == 321 && drop.WasBarrier);
    }

    [Fact]
    public void ASpentBackpackKiloCanRecoverASecondRealBlockIntoItsOtherHand()
    {
        var game = new GameSession();
        var kilo = AddKilo(game);
        typeof(Zombie).GetProperty(nameof(Zombie.CarriedBlocks))!.SetValue(kilo, 0);
        var first = DropTeammateBlock(game, new Cell(7, 16, 5), GearTier.Copper);
        Assert.True((bool)Invoke(game, "TryGrabKiloCover", kilo)!);
        var second = DropTeammateBlock(game, new Cell(7, 16, 6), GearTier.Iron);
        Invoke(game.World, "DamageTerrain", second, 19f);

        Assert.True((bool)Invoke(game, "TryGrabKiloCover", kilo)!);

        Assert.Equal(2, game.KiloCovers.Count);
        Assert.Contains(game.KiloCovers, cover => cover.BlockIndex == 0 && cover.Material == Material.Copper && cover.Hp == 1000);
        Assert.Contains(game.KiloCovers, cover => cover.BlockIndex == 1 && cover.Material == Material.Iron && cover.Hp == 1981);
        Assert.Equal(Material.Air, game.World.GetMaterial(first));
        Assert.Equal(Material.Air, game.World.GetMaterial(second));
        Assert.False((bool)Invoke(game, "TryGrabKiloCover", kilo)!);
        Assert.Equal(0, kilo.CarriedBlocks);
        Assert.Equal(Material.Dirt, game.World.GetMaterial(new Cell(7, 15, 6)));
    }

    [Fact]
    public void PickupCannotPullARealBlockThroughAnotherSolidBlock()
    {
        var game = new GameSession();
        var kilo = AddKilo(game);
        var source = DropTeammateBlock(game, new Cell(7, 16, 5), GearTier.Copper);
        var obstacle = new Cell(7, 17, 5);
        Invoke(game.World, "AddBarrier", obstacle, Material.Stone, (float?)500f);

        Assert.False((bool)Invoke(game, "TryTakeKiloCover", kilo, source, new Vector3(6.5f, 17.5f, 5.5f), 0)!);

        Assert.Equal(Material.Copper, game.World.GetMaterial(source));
        Assert.Equal(1000, game.World.GetTerrainHp(source));
        Assert.Equal(500, game.World.GetBarrier(obstacle)!.Hp);
        Assert.Empty(game.KiloCovers);
        Assert.Equal(1, kilo.CarriedBlocks);
    }

    private static Zombie AddKilo(GameSession game)
    {
        var kilo = new Zombie { Id = 10, Size = ZombieSize.Kilo, WaveIndex = -1 };
        kilo.Position = kilo.StandAt(new Cell(5, 16, 5));
        kilo.FaceToward(Vector3.UnitX);
        game.Zombies.Add(kilo);
        return kilo;
    }

    private static Cell DropTeammateBlock(GameSession game, Cell cell, GearTier tier)
    {
        var teammate = new Zombie { Id = 100 + game.Zombies.Count, Kind = ZombieKind.Armored, Tier = tier, WaveIndex = -1 };
        teammate.Position = teammate.StandAt(cell);
        teammate.Hp = 0;
        game.Zombies.Add(teammate);
        Invoke(game, "QueueDeathBlock", teammate);
        Invoke(game, "ResolveBlockDrops");
        Assert.Equal(0, teammate.CarriedBlocks);
        Assert.Empty(game.PendingBlockDrops);
        return cell;
    }

    private static object? Invoke(object owner, string method, params object?[] arguments) =>
        owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);
}
