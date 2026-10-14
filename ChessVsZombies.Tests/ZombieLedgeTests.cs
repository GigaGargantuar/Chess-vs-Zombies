using System.Numerics;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class ZombieLedgeTests
{
    [Theory]
    [InlineData(.4f)]
    [InlineData(.45f)]
    [InlineData(.49f)]
    public void ADeathDropJustBelowADescendingBodyBecomesFootingWithoutBeingAttacked(float heightAboveBlockTop)
    {
        var game = NewSession(ZombieKind.Basic);
        var drop = new Cell(13, 16, 15);
        var follower = BeginAt(game, drop + new Cell(0, 1, 0));
        follower.Position = new Vector3(drop.Center.X, drop.Y + 1 + heightAboveBlockTop, drop.Center.Z);
        QueueDonorDrop(game, drop, ZombieKind.Basic, GearTier.Wood, -1);

        // The drop's upper face is below the .39-half-height collision body,
        // but above the nominal Position.Y-.5 standing point. This is legal
        // placement during a descent, not an embedded body or a crush.
        game.Update(.01f);
        Assert.Equal(Material.Dirt, game.World.GetNaturalMaterial(drop));
        Assert.Equal(Balance.BlockHp(Material.Dirt), game.World.GetTerrainHp(drop));
        AssertBodyClear(game, follower);
        bool leftDropColumn = false;

        Advance(game, follower, 15, () => game.CoreHp < Balance.CoreHp, () =>
        {
            leftDropColumn |= follower.Cell.X != drop.X || follower.Cell.Z != drop.Z;
            Assert.Equal(Balance.BlockHp(Material.Dirt), game.World.GetTerrainHp(drop));
            Assert.Empty(game.World.DamagedTerrain);
        });

        Assert.True(leftDropColumn, $"Follower remained above the new floor at {follower.Position}.");
        Assert.True(game.CoreHp < Balance.CoreHp);
        Assert.Equal(Balance.ZombieHp, follower.Hp);
        Assert.Equal(1, follower.CarriedBlocks);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Theory]
    [InlineData(ZombieKind.Basic, GearTier.Wood, Material.Dirt)]
    [InlineData(ZombieKind.Armed, GearTier.Stone, Material.Stone)]
    [InlineData(ZombieKind.Armed, GearTier.Osmium, Material.Osmium)]
    public void AFollowerClimbsAndStepsDownFromBlocksDroppedByOtherZombies(
        ZombieKind donorKind, GearTier donorTier, Material material)
    {
        var game = NewSession();
        var follower = BeginAt(game, new Cell(12, 16, 15));
        DropRow(game, 13, 16, donorKind, donorTier);
        game.Update(.01f);
        var drop = new Cell(13, 16, 15);
        Assert.Equal(material, game.World.GetNaturalMaterial(drop));
        bool crossedOnTop = false;
        bool descended = false;

        Advance(game, follower, 15, () => game.CoreHp < Balance.CoreHp, () =>
        {
            crossedOnTop |= follower.Position.X >= 13.499f && follower.Position.Y >= 17.499f;
            descended |= crossedOnTop && follower.Position.X >= 14.499f && follower.Position.Y <= 16.501f;
            if (follower.Position.Y < 17.499f)
                Assert.True(follower.Position.X <= 12.501f || follower.Position.X >= 14.499f,
                    $"Follower cut through the donor block's corner at {follower.Position}.");
        });

        Assert.True(crossedOnTop, $"Follower never crossed the dropped block: {follower.Position}.");
        Assert.True(descended, $"Follower did not descend from the dropped block: {follower.Position}.");
        Assert.True(game.CoreHp < Balance.CoreHp);
        Assert.Equal(1, follower.CarriedBlocks);
        Assert.Equal(material, game.World.GetNaturalMaterial(drop));
        Assert.Equal(Balance.BlockHp(material), game.World.GetTerrainHp(drop));
        Assert.Empty(game.World.DamagedTerrain);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void NewDeathDropsInterruptADescentAndTheFollowerStepsDownFromTheExtendedLedge()
    {
        var game = NewSession();
        var follower = BeginAt(game, new Cell(9, 16, 15));
        DropRow(game, 10, 16, ZombieKind.Basic, GearTier.Wood);
        var oldLanding = new Cell(11, 16, 15);
        Advance(game, follower, 10, () => follower.NextCell == oldLanding);
        Assert.Equal(oldLanding, follower.NextCell);
        game.Update(.1f);
        Assert.InRange(follower.Position.X, 10.51f, 10.7f);
        Assert.InRange(follower.Position.Y, 17.499f, 17.501f);
        var interruptedPosition = follower.Position;

        // Donors die below the follower's body. Their blocks occupy its former
        // low landing and extend the ledge, requiring a fresh physical route.
        DropRow(game, 11, 16, ZombieKind.Armed, GearTier.Stone);
        game.Update(.01f);
        Assert.Equal(Material.Stone, game.World.GetNaturalMaterial(oldLanding));
        Assert.InRange(Vector3.Distance(interruptedPosition, follower.Position), 0, .009f);
        Assert.NotEqual(oldLanding, follower.NextCell);
        bool reachedExtension = false;
        bool descended = false;

        Advance(game, follower, 15, () => game.CoreHp < Balance.CoreHp, () =>
        {
            reachedExtension |= follower.Position.X >= 11.499f && follower.Position.Y >= 17.499f;
            descended |= reachedExtension && follower.Position.X >= 12.499f && follower.Position.Y <= 16.501f;
        });

        Assert.True(reachedExtension, $"Follower did not traverse the new ledge: {follower.Position}.");
        Assert.True(descended, $"Follower stalled after its descent was interrupted: {follower.Position}.");
        Assert.True(game.CoreHp < Balance.CoreHp);
        Assert.Equal(1, follower.CarriedBlocks);
        Assert.Equal(Balance.BlockHp(Material.Stone), game.World.GetTerrainHp(oldLanding));
        Assert.Empty(game.World.DamagedTerrain);
        Assert.Empty(game.PendingBlockDrops);
    }

    [Fact]
    public void ADonorBlockDescentBreachesOppositeHeadroomBeforeCrossingBelowTheOverhang()
    {
        var game = NewSession();
        Assert.True(game.BuyTool(ToolKind.Shovel));
        var drop = new Cell(12, 13, 15);
        var start = new Cell(12, 14, 15);
        var headroom = new Cell(13, 14, 15);
        foreach (var cell in new[]
        {
            drop, start, new Cell(13, 13, 15), new Cell(14, 13, 15),
            new Cell(14, 14, 15), new Cell(15, 14, 15)
        }) Assert.True(game.Dig(cell));
        var follower = BeginAt(game, start);
        QueueDonorDrop(game, drop, ZombieKind.Armed, GearTier.Stone, -1);

        game.Update(.01f);

        Assert.Equal(Material.Stone, game.World.GetNaturalMaterial(drop));
        Assert.Equal(65, game.World.GetTerrainHp(headroom));
        Assert.Equal(start.Center, follower.Position);
        Assert.Null(follower.NextCell);
        game.Update(.5f);
        Assert.Equal(65, game.World.GetTerrainHp(headroom));
        Assert.Equal(start.Center, follower.Position);
        bool descended = false;

        Advance(game, follower, 20, () => game.CoreHp < Balance.CoreHp, () =>
        {
            descended |= follower.Position.Y <= 13.501f;
            if (follower.Position.Y < 14.499f)
                Assert.True(follower.Position.X >= 13.499f,
                    $"Follower descended before clearing the donor block: {follower.Position}.");
        });

        Assert.True(descended, $"Follower never descended beneath the overhang: {follower.Position}.");
        Assert.True(game.CoreHp < Balance.CoreHp);
        Assert.Equal(Material.Air, game.World.GetMaterial(headroom));
        Assert.Equal(Material.Stone, game.World.GetNaturalMaterial(drop));
        Assert.Equal(Balance.BlockHp(Material.Stone), game.World.GetTerrainHp(drop));
        Assert.Equal(1, follower.CarriedBlocks);
    }

    private static GameSession NewSession(ZombieKind followerKind = ZombieKind.Armed)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Number = 1,
            Name = "Zombie-made ledges",
            Waves = [new() { Groups = [new() { Count = 1, Kind = followerKind, Tier = GearTier.Osmium }] }]
        });
        return new GameSession(12345, levels);
    }

    private static Zombie BeginAt(GameSession game, Cell start)
    {
        Assert.True(game.StartLevel(), game.Message);
        game.Update(.001f);
        var follower = Assert.Single(game.Zombies);
        follower.Position = start.Center;
        follower.NextCell = null;
        follower.MoveProgress = 0;
        follower.Cooldown = 0;
        follower.Lane = 0;
        return follower;
    }

    private static void DropRow(GameSession game, int x, int y, ZombieKind kind, GearTier tier)
    {
        // A full row removes an equally short walk around the donor block, so
        // this test exercises a real climb and descent rather than a detour.
        for (int z = 0; z < WorldGrid.Size; z++)
            QueueDonorDrop(game, new Cell(x, y, z), kind, tier, -1 - x * WorldGrid.Size - z);
    }

    private static void QueueDonorDrop(GameSession game, Cell cell, ZombieKind kind, GearTier tier, int id)
    {
        var donor = new Zombie { Id = id, WaveIndex = -1, Kind = kind, Tier = tier, Position = cell.Center, Attack = 1 };
        game.Zombies.Add(donor);
        game.DamageZombie(donor, 100_000, DamageKind.Chemical);
        Assert.Equal(0, donor.CarriedBlocks);
    }

    private static void Advance(GameSession game, Zombie follower, float seconds, Func<bool> complete, Action? inspect = null)
    {
        for (float elapsed = 0; elapsed < seconds && !complete(); elapsed += .02f)
        {
            var previous = follower.Position;
            game.Update(.02f);
            Assert.True(follower.Hp > 0);
            Assert.InRange(Vector3.Distance(previous, follower.Position), 0, .018f);
            AssertBodyClear(game, follower);
            inspect?.Invoke();
        }
    }

    private static void AssertBodyClear(GameSession game, Zombie zombie)
    {
        var min = zombie.Position - new Vector3(.2f, .39f, .2f);
        var max = zombie.Position + new Vector3(.2f, .39f, .2f);
        for (int x = (int)MathF.Floor(min.X); x <= (int)MathF.Floor(max.X); x++)
            for (int y = (int)MathF.Floor(min.Y); y <= (int)MathF.Floor(max.Y); y++)
                for (int z = (int)MathF.Floor(min.Z); z <= (int)MathF.Floor(max.Z); z++)
                    Assert.Equal(Material.Air, game.World.GetMaterial(new Cell(x, y, z)));
    }
}
