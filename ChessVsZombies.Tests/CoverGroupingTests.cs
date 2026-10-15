using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class CoverGroupingTests
{
    private static readonly Cell HidingCell = new(11, 16, 8);

    [Fact]
    public void TheLastFourLivingZombiesLeaveCoverDespiteOtherWavesStillHavingEnemies()
    {
        var (game, leader) = Prepare();
        var allies = new[] { Add(game, new(11, 16, 7)), Add(game, new(11, 16, 9)), Add(game, new(12, 16, 8)) };
        Assert.True(Wait(game, leader));
        foreach (var ally in allies) Assert.True(Wait(game, ally));
        game.Waves[0].Spawned = game.Waves[0].TotalCount;
        game.Waves.Add(new() { Index = 1, TotalCount = 100, Started = true });

        AdvanceGroups(game, .05f);

        foreach (var zombie in allies.Prepend(leader))
        {
            Assert.False(game.IsZombieHiding(zombie));
            Assert.False(Wait(game, zombie));
        }
    }

    [Fact]
    public void UnspawnedAlliesCountTowardTheRemainingWavePopulation()
    {
        var (game, leader) = Prepare();
        Add(game, HidingCell);
        game.Waves[0].Spawned = game.Waves[0].TotalCount - 3;

        Assert.True(Wait(game, leader));
        AdvanceGroups(game, 100);
        Assert.True(game.IsZombieHiding(leader));

        // Two alive plus only two still to spawn is now a final group of four.
        game.Waves[0].Spawned++;
        AdvanceGroups(game, .05f);

        Assert.False(game.IsZombieHiding(leader));
        Assert.False(Wait(game, leader));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void ThreeOrFourZombiesAttackAfterMoreThanThirtyContinuousSeconds(int count)
    {
        var (game, leader) = Prepare();
        for (int i = 1; i < count; i++) Add(game, HidingCell);
        Assert.True(Wait(game, leader));

        AdvanceGroups(game, 30);
        Assert.True(game.IsZombieHiding(leader));
        AdvanceGroups(game, .01f);

        Assert.All(game.Zombies, zombie => Assert.False(Wait(game, zombie)));
        Assert.All(game.Zombies, zombie => Assert.False(game.IsZombieHiding(zombie)));
    }

    [Fact]
    public void FewerThanThreeCannotUseTheTimeoutWhileReinforcementsRemain()
    {
        var (game, leader) = Prepare();
        Add(game, HidingCell);
        Assert.True(Wait(game, leader));

        AdvanceGroups(game, 300);

        Assert.True(Wait(game, leader));
        Assert.True(game.IsZombieHiding(leader));
    }

    [Fact]
    public void LosingTheThirdMemberResetsTheThirtySecondAssemblyTimer()
    {
        var (game, leader) = Prepare();
        Add(game, HidingCell);
        var third = Add(game, HidingCell);
        Assert.True(Wait(game, leader));
        AdvanceGroups(game, 20);
        third.Position = new Cell(24, 16, 24).Center;
        AdvanceGroups(game, 20);
        third.Position = HidingCell.Center;

        AdvanceGroups(game, 15);
        Assert.True(Wait(game, leader));
        AdvanceGroups(game, 15.01f);

        Assert.False(Wait(game, leader));
    }

    [Fact]
    public void AGroupHasNoUpperLimitAndAllMembersAdvanceTogether()
    {
        var (game, leader) = Prepare();
        Assert.True(Wait(game, leader));
        for (int i = 0; i < 8; i++) Add(game, HidingCell);

        AdvanceGroups(game, .05f);

        Assert.All(game.Zombies, zombie => Assert.False(Wait(game, zombie)));
        Assert.All(game.Zombies, zombie => Assert.False(game.IsZombieHiding(zombie)));
    }

    [Fact]
    public void NearbyZombiesPreferARealRouteToAnExistingHidingGroup()
    {
        var (game, leader) = Prepare();
        Assert.True(Wait(game, leader));
        var recruit = Add(game, new(11, 16, 13));
        float before = Vector3.DistanceSquared(recruit.Position, leader.Position);

        Assert.True(JoinStep(game, recruit, out var next));
        Assert.True(Vector3.DistanceSquared(next.Center, leader.Position) < before);
        Assert.False(Wait(game, recruit));
        game.Update(.8f);

        Assert.True(Vector3.DistanceSquared(recruit.Position, leader.Position) < before - .5f,
            $"Recruit did not approach its waiting ally: {recruit.Position}.");
        game.Update(3);
        Assert.True(game.IsZombieHiding(recruit));
        Assert.True(Vector3.DistanceSquared(recruit.Position, leader.Position) <= 4);
    }

    [Fact]
    public void RecruitmentCannotCrossASealedWall()
    {
        var (game, leader) = Prepare();
        Assert.True(Wait(game, leader));
        var recruit = Add(game, new(11, 16, 13));
        Assert.True(JoinStep(game, recruit, out _));
        var place = typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", BindingFlags.NonPublic | BindingFlags.Instance)!;
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int y = 16; y < WorldGrid.Size; y++)
                if (game.World.GetMaterial(new Cell(x, y, 11)) == Material.Air)
                    Assert.True((bool)place.Invoke(game.World, [new Cell(x, y, 11), Material.Dirt])!);

        Assert.False(JoinStep(game, recruit, out _));
        Assert.Equal(new Cell(11, 16, 13).Center, recruit.Position);
    }

    [Fact]
    public void DistantRecruitsUseAPhysicalRouteAcrossTheMapToTheSameCluster()
    {
        var (game, leader) = Prepare();
        Assert.True(Wait(game, leader));
        var recruit = Add(game, new(11, 16, 30));
        float before = Vector3.DistanceSquared(recruit.Position, leader.Position);

        Assert.True(JoinStep(game, recruit, out var next));
        Assert.True(Vector3.DistanceSquared(next.Center, leader.Position) < before);
        Assert.False(Wait(game, recruit));
        game.Update(.8f);

        Assert.True(Vector3.DistanceSquared(recruit.Position, leader.Position) < before - .5f);
        Assert.False(game.IsZombieHiding(recruit));
    }

    [Fact]
    public void WeakerSeparatedHidingGroupsJoinTheStrongestCluster()
    {
        var (game, weaker) = Prepare(distantCover: true);
        Assert.True(Wait(game, weaker));
        var stronger = Add(game, new(11, 16, 24));
        Add(game, new(11, 16, 23));
        Add(game, new(11, 16, 25));

        AdvanceGroups(game, .05f);

        Assert.True(Wait(game, stronger));
        Assert.False(Wait(game, weaker));
        Assert.False(game.IsZombieHiding(weaker));
        Assert.True(JoinStep(game, weaker, out var next));
        Assert.True(next.Z > weaker.Cell.Z);
        Assert.True(game.IsZombieHiding(stronger));
    }

    [Fact]
    public void RemainingArmorCountsTowardClusterStrengthInsteadOfOnlyHeadcount()
    {
        var (game, weaker) = Prepare(distantCover: true);
        Add(game, HidingCell);
        var stronger = new Zombie
        {
            Id = 1000, WaveIndex = 0, Kind = ZombieKind.Armored, Tier = GearTier.Wood,
            Position = new Cell(11, 16, 24).Center, Attack = 1
        };
        game.Zombies.Add(stronger);

        AdvanceGroups(game, .05f);

        Assert.Equal(40, stronger.Hp);
        Assert.True(Wait(game, stronger));
        Assert.False(Wait(game, weaker));
        Assert.True(JoinStep(game, weaker, out var next));
        Assert.True(next.Z > weaker.Cell.Z);
    }

    [Fact]
    public void EqualHealthClustersPreferMoreAttackThenTheLowerLeaderId()
    {
        var (game, first) = Prepare(distantCover: true);
        var second = new Zombie { Id = 1000, WaveIndex = 0, Position = new Cell(11, 16, 24).Center, Attack = 2 };
        game.Zombies.Add(second);
        AdvanceGroups(game, .05f);
        Assert.True(Wait(game, second));
        Assert.False(Wait(game, first));

        // Replace the stronger weapon with equal attack. The lower ID wins
        // even when the other cluster is first in the zombie iteration order.
        var replacement = new Zombie { Id = second.Id, WaveIndex = 0, Position = second.Position, Attack = first.Attack };
        second.Hp = 0;
        game.Zombies.Remove(second);
        game.Zombies.Insert(0, replacement);
        AdvanceGroups(game, .05f);

        Assert.True(Wait(game, first));
        Assert.False(Wait(game, replacement));
        Assert.True(JoinStep(game, replacement, out var next));
        Assert.True(next.Z < replacement.Cell.Z);
    }

    [Fact]
    public void AnIsolatedWeakerGroupCannotKeepCampingWhenTheRallyIsUnreachable()
    {
        var (game, weaker) = Prepare(distantCover: true);
        Assert.True(Wait(game, weaker));
        var stronger = Add(game, new(11, 16, 24));
        Add(game, new(11, 16, 23));
        var place = typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", BindingFlags.NonPublic | BindingFlags.Instance)!;
        for (int x = 0; x < WorldGrid.Size; x++)
            for (int y = 16; y < WorldGrid.Size; y++)
                if (game.World.GetMaterial(new Cell(x, y, 20)) == Material.Air)
                    Assert.True((bool)place.Invoke(game.World, [new Cell(x, y, 20), Material.Dirt])!);

        AdvanceGroups(game, .05f);

        Assert.True(Wait(game, stronger));
        Assert.False(Wait(game, weaker));
        Assert.False(game.IsZombieHiding(weaker));
        Assert.False(JoinStep(game, weaker, out _));
        Assert.Equal(HidingCell.Center, weaker.Position);
    }

    [Fact]
    public void ChoosingAStrongerClusterDoesNotTransferTheWeakerClustersWaitTimer()
    {
        var (game, weaker) = Prepare(distantCover: true);
        Add(game, HidingCell);
        Add(game, HidingCell);
        Assert.True(Wait(game, weaker));
        AdvanceGroups(game, 29);
        var stronger = Add(game, new(11, 16, 24));
        for (int i = 0; i < 3; i++) Add(game, new(11, 16, 24));

        AdvanceGroups(game, 1);
        Assert.True(Wait(game, stronger));
        Assert.False(Wait(game, weaker));
        AdvanceGroups(game, 29);
        Assert.True(Wait(game, stronger));
        AdvanceGroups(game, .01f);

        Assert.False(Wait(game, stronger));
        Assert.False(game.IsZombieHiding(stronger));
    }

    [Fact]
    public void AKiloWithPersonalMovingCoverDoesNotHideRecruitOrCompleteATerrainCoverGroup()
    {
        var (game, leader) = Prepare();
        var kilo = new Zombie { Id = 1000, WaveIndex = 0, Size = ZombieSize.Kilo, Attack = 1 };
        kilo.Position = kilo.StandAt(new(24, 16, 24));
        kilo.FaceToward(Vector3.UnitX);
        game.Zombies.Add(kilo);
        typeof(GameSession).GetMethod("TryKiloAction", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(game, [kilo, .05f]);
        Assert.Equal(2, game.KiloCovers.Count);
        kilo.Position = kilo.StandAt(HidingCell);
        Add(game, HidingCell);
        Assert.True(Wait(game, leader));

        Assert.False(Wait(game, kilo));
        Assert.False(JoinStep(game, kilo, out _));
        Assert.False(game.IsZombieHiding(kilo));
        AdvanceGroups(game, 31);

        Assert.True(Wait(game, leader));
        Assert.True(game.IsZombieHiding(leader));
    }

    [Fact]
    public void DamagedMembersAdvanceAndDoNotKeepTheTimeoutAboveItsMinimum()
    {
        var (game, leader) = Prepare();
        Add(game, HidingCell);
        var third = Add(game, HidingCell);
        Assert.True(Wait(game, leader));
        AdvanceGroups(game, 29);

        game.DamageZombie(third, 1, DamageKind.Chemical);
        AdvanceGroups(game, 2);

        Assert.False(Wait(game, third));
        Assert.True(Wait(game, leader));
        Assert.True(game.IsZombieHiding(leader));
    }

    [Fact]
    public void AGroupOnFractionalObsidianKeepsItsTimerWhenItsGridStandingHeightIsObstructed()
    {
        var (game, leader) = Prepare();
        game.Liquids.RestoreState(new LiquidState
        {
            Obsidian = [new(HidingCell, 2, CrustFace.Bottom, 10)]
        });
        var place = typeof(WorldGrid).GetMethod("RestoreTerrainPlacement", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var side in new[] { new Cell(10, 16, 8), new Cell(12, 16, 8), new Cell(11, 16, 7), new Cell(11, 16, 9) })
            for (int dy = 0; dy <= 1; dy++)
                if (game.World.GetMaterial(side + new Cell(0, dy, 0)) == Material.Air)
                    Assert.True((bool)place.Invoke(game.World, [side + new Cell(0, dy, 0), Material.Dirt])!);
        leader.Position = HidingCell.Center + Vector3.UnitY * .2f;
        var second = Add(game, HidingCell);
        var third = Add(game, HidingCell);
        second.Position = third.Position = leader.Position;
        Assert.True(Wait(game, leader));

        // Ordinary decisions between slices must not replace the leader's
        // group and discard the previously accumulated wait on the thin slab.
        for (int secondElapsed = 0; secondElapsed < 30; secondElapsed++)
        {
            AdvanceGroups(game, 1);
            Assert.True(Wait(game, leader));
        }
        AdvanceGroups(game, .01f);

        Assert.False(Wait(game, leader));
        Assert.False(game.IsZombieHiding(leader));
    }

    private static (GameSession Game, Zombie Leader) Prepare(bool distantCover = false)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Number = 1,
            Waves = [new() { SpawnInterval = 1000, Groups = [new() { Count = 20 }] }]
        });
        var game = new GameSession(12345, levels);
        Assert.True(game.Deploy(PieceKind.Rook, new(8, 16, 8)));
        Assert.True(game.Inventory.Add(Material.Dirt, 9));
        for (int z = 5; z <= 13; z++) Assert.True(game.BuildBarrier(new(10, 16, z)));
        if (distantCover) PrepareDistantCover(game);
        Assert.True(game.StartLevel());
        game.Update(.001f);
        var leader = Assert.Single(game.Zombies);
        game.Projectiles.Clear();
        game.Turrets[0].Cooldown = 1_000_000;
        leader.Position = HidingCell.Center;
        leader.NextCell = null;
        leader.MoveProgress = 0;
        leader.Lane = 0;
        return (game, leader);
    }

    private static Zombie Add(GameSession game, Cell cell)
    {
        var zombie = new Zombie { Id = 100 + game.Zombies.Count, WaveIndex = 0, Position = cell.Center, Attack = 1 };
        game.Zombies.Add(zombie);
        return zombie;
    }

    private static void PrepareDistantCover(GameSession game)
    {
        Assert.True(game.Deploy(PieceKind.Rook, new(8, 16, 24)));
        game.Turrets[^1].Cooldown = 1_000_000;
        Assert.True(game.Inventory.Add(Material.Dirt, 7));
        for (int z = 21; z <= 27; z++) Assert.True(game.BuildBarrier(new(10, 16, z)));
    }

    private static bool Wait(GameSession game, Zombie zombie) => (bool)typeof(GameSession)
        .GetMethod("ShouldWaitInCover", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(game, [zombie])!;

    private static void AdvanceGroups(GameSession game, float seconds) => typeof(GameSession)
        .GetMethod("UpdateCoverGroups", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(game, [seconds]);

    private static bool JoinStep(GameSession game, Zombie zombie, out Cell next)
    {
        object?[] arguments = [zombie, zombie.Cell, default(Cell)];
        bool result = (bool)typeof(GameSession).GetMethod("TryGetCoverJoinStep", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(game, arguments)!;
        next = (Cell)arguments[2]!;
        return result;
    }
}
