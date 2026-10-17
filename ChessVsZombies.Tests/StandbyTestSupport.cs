using System.Collections;
using System.Numerics;
using System.Reflection;
using ChessVsZombies.Core;

namespace ChessVsZombies.Tests;

internal static class StandbyTestSupport
{
    // Campaign tests focus on rewards and checkpoints. If a fixed boundary
    // position was sealed by an earlier death drop, finish its queued slots
    // explicitly instead of changing the production spawn location.
    public static void FinishQueuedSlots(GameSession game)
    {
        var waves = (IEnumerable)typeof(GameSession).GetField("_standbyWaves",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(game)!;
        var damage = typeof(GameSession).GetMethod("DamageStandby", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (IEnumerable slots in waves)
            foreach (var slot in slots)
                while (!(bool)slot!.GetType().GetProperty("Consumed")!.GetValue(slot)! &&
                    (float)slot.GetType().GetProperty("BodyHp")!.GetValue(slot)! > 0)
                    damage.Invoke(game, [slot, 1_000_000f, DamageKind.Chemical, ShotKind.Pellet, Vector3.UnitX]);
    }
}
