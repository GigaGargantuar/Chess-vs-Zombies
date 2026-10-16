using System.Numerics;
using System.Reflection;

namespace ChessVsZombies.Core;

/// <summary>Isolated checkpoint approximation shared by rendered and headless performance checks.</summary>
public static class SaveHordeScenario
{
    public static GameSession Create(string savePath, int turretCount = 409, int zombieCount = 300)
    {
        if (turretCount <= 0) throw new ArgumentOutOfRangeException(nameof(turretCount));
        if (zombieCount <= 0) throw new ArgumentOutOfRangeException(nameof(zombieCount));
        if (!SaveStore.TryLoad(savePath, out var loaded, out var error))
            throw new InvalidOperationException($"Cannot load checkpoint for isolated benchmark: {error}");
        var session = loaded!;
        if (session.Turrets.Count > turretCount)
            throw new InvalidOperationException($"Checkpoint has more than {turretCount} towers.");
        var originals = session.Turrets.ToArray();
        if (originals.Length == 0) throw new InvalidOperationException("Checkpoint has no towers to reproduce.");
        int additional = 0;
        // Restore the saved tower mix and allocations in legal supported cells,
        // nearest the existing defense. Original destroyed tower positions are unknown.
        var candidates = from y in Enumerable.Range(1, 31)
                         from x in Enumerable.Range(1, 30)
                         from z in Enumerable.Range(1, 30)
                         let cell = new Cell(x, y, z)
                         where WorldGrid.IsTurretZone(cell)
                         orderby originals.Min(tower => Vector3.DistanceSquared(tower.Cell.Center, cell.Center)), y, x, z
                         select cell;
        foreach (var cell in candidates)
        {
            if (session.Turrets.Count >= turretCount) break;
            var source = originals[additional % originals.Length];
            if (!session.Deploy(source.Kind, cell)) continue;
            var added = session.Turrets[^1];
            foreach (var point in source.UpgradePoints) added.UpgradePoints[point.Key] = point.Value;
            added.JumpPattern.Clear();
            added.JumpPattern.AddRange(source.JumpPattern);
            added.Hp = source.Hp;
            additional++;
        }
        if (session.Turrets.Count != turretCount)
            throw new InvalidOperationException($"Could not stage {turretCount} supported towers.");
        // Campaign equipment and the next five saved levels supply the roster.
        // Calling production spawning preserves equipment, random choices and IDs.
        if (!session.SetAllInOne(true) || !session.StartLevel()) throw new InvalidOperationException(session.Message);
        var spawn = typeof(GameSession).GetMethod("Spawn", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<WaveState, bool>>(session);
        var wave = session.Waves.Single();
        if (wave.TotalCount < zombieCount)
            throw new InvalidOperationException($"The saved encounter cannot supply {zombieCount} enemies.");
        while (session.Zombies.Count < zombieCount)
            if (!spawn(wave)) throw new InvalidOperationException($"Could not stage {zombieCount} simultaneous enemies.");
        // The same finite initial horde is used by both benchmark frontends.
        wave.SpawnTimer = float.MaxValue;
        return session;
    }
}
