using System.Reflection;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class CoverCrowdTests
{
    [Fact]
    public void FiveHundredWaveMembersDoNotReleaseTwoCoveredAlliesUntilTheFinalSurvivorsRemain()
    {
        var game = new GameSession();
        Assert.True(game.Deploy(PieceKind.Rook, new(8, 16, 8)));
        Assert.True(game.Inventory.Add(Material.Dirt, 9));
        for (int z = 5; z <= 13; z++) Assert.True(game.BuildBarrier(new(10, 16, z)));
        game.Waves.Add(new() { Index = 0, TotalCount = 500, Spawned = 500, Started = true });
        var leader = new Zombie { Id = 100, WaveIndex = 0, Position = new Cell(11, 16, 8).Center, Attack = 1 };
        var companion = new Zombie { Id = 101, WaveIndex = 0, Position = new Cell(11, 16, 9).Center, Attack = 1 };
        game.Zombies.Add(leader);
        game.Zombies.Add(companion);
        for (int i = 0; i < 498; i++)
            game.Zombies.Add(new Zombie
            {
                Id = 102 + i, WaveIndex = 0, Position = new Cell(24 + i % 7, 16, 24 + i / 7 % 7).Center, Attack = 1
            });

        Assert.True(Wait(game, leader));
        Assert.True(Wait(game, companion));
        AdvanceGroups(game, 100);

        Assert.True(game.IsZombieHiding(leader));
        Assert.True(game.IsZombieHiding(companion));
        // Distant casualties leave only four actual wave members. The local
        // pair must then advance even though the dead entries remain in the list.
        foreach (var zombie in game.Zombies.Skip(4)) zombie.Hp = 0;
        AdvanceGroups(game, .05f);

        Assert.False(game.IsZombieHiding(leader));
        Assert.False(game.IsZombieHiding(companion));
        Assert.False(Wait(game, leader));
        Assert.False(Wait(game, companion));
    }

    private static bool Wait(GameSession game, Zombie zombie) => (bool)typeof(GameSession)
        .GetMethod("ShouldWaitInCover", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(game, [zombie])!;

    private static void AdvanceGroups(GameSession game, float seconds) => typeof(GameSession)
        .GetMethod("UpdateCoverGroups", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(game, [seconds]);
}
