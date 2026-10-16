using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public sealed class CrowdHealthTests
{
    [Fact]
    public void RepeatedCrowdHealthChecksPreserveFractionalArmorSumsWithoutAllocating()
    {
        var zombies = Enumerable.Range(0, 500).Select(index => new Zombie
        {
            Id = index, Kind = ZombieKind.ArmedArmored, Tier = GearTier.Osmium
        }).ToArray();
        float[] hp = [.1f, 200.3f, 5.123456f, .000001f];
        var setter = typeof(ArmorPiece).GetProperty(nameof(ArmorPiece.Hp))!;
        foreach (var zombie in zombies)
            for (int slot = 0; slot < hp.Length; slot++) setter.SetValue(zombie.ArmorPieces[slot], hp[slot]);
        float expected = zombies[0].BodyHp + zombies[0].ArmorPieces.Sum(piece => piece.Hp);
        foreach (var zombie in zombies) Assert.Equal(expected, zombie.Hp);

        long before = GC.GetAllocatedBytesForCurrentThread();
        float actual = 0;
        foreach (var zombie in zombies) actual = zombie.Hp;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expected, actual);
        Assert.InRange(allocated, 0, 256);
        new GameSession().DamageZombie(zombies[0], Balance.ZombieHp, DamageKind.Chemical);
        Assert.Equal(0, zombies[0].Hp);
    }
}
