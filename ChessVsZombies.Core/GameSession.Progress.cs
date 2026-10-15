using System.Numerics;

namespace ChessVsZombies.Core;

public sealed partial class GameSession
{
    private const float ZombieStagnationSeconds = 10;

    private sealed class ZombieProgress(Zombie zombie, float now)
    {
        public HashSet<Cell> Visited { get; } = [zombie.Cell];
        public float BestCoreDistance { get; set; } = CoreDistance(zombie.Position);
        public float LastProgressAt { get; set; } = now;
        public float CoverWaitingSeconds { get; set; }
    }

    private readonly Dictionary<int, ZombieProgress> _zombieProgress = [];

    private static float CoreDistance(Vector3 position) => Vector3.Distance(position, new Vector3(16));

    private void ResetZombieProgress() => _zombieProgress.Clear();

    private void ResetZombieProgress(Zombie zombie) =>
        _zombieProgress[zombie.Id] = new ZombieProgress(zombie, _retreatNavigationTime);

    // Call only for real work: reducing a physical obstacle/core's HP, placing
    // a finite carried block, scooping liquid, or completing a useful hurl.
    // Incoming damage and changing the chosen route never count as progress.
    private void RecordZombieProgress(Zombie zombie)
    {
        if (!_zombieProgress.TryGetValue(zombie.Id, out var progress))
            _zombieProgress[zombie.Id] = progress = new ZombieProgress(zombie, _retreatNavigationTime);
        progress.LastProgressAt = _retreatNavigationTime;
        progress.CoverWaitingSeconds = 0;
    }

    private void UpdateZombieProgress(float dt, HashSet<int> alive)
    {
        foreach (int id in _zombieProgress.Keys.ToArray())
            if (!alive.Contains(id)) _zombieProgress.Remove(id);

        foreach (var zombie in Zombies)
        {
            if (zombie.Hp <= 0 || IsZombieRetreating(zombie)) continue;
            if (!_zombieProgress.TryGetValue(zombie.Id, out var progress))
            {
                ResetZombieProgress(zombie);
                continue;
            }

            float distance = CoreDistance(zombie.Position);
            bool newGround = progress.Visited.Add(zombie.Cell);
            bool nearerCore = distance < progress.BestCoreDistance - .05f;
            if (nearerCore) progress.BestCoreDistance = distance;
            if (newGround || nearerCore || IsZombieHurled(zombie))
            {
                progress.LastProgressAt = _retreatNavigationTime;
                progress.CoverWaitingSeconds = 0;
            }

            // Existing group tactics are allowed their real assembly window.
            // A permanently stranded one/two-zombie shelter is not an infinite
            // exemption, and neither is a Kilo merely raising its held shields.
            if (_coverWaiting.ContainsKey(zombie.Id) && IsZombieHiding(zombie))
            {
                progress.CoverWaitingSeconds += dt;
                if (progress.CoverWaitingSeconds <= CoverPatienceSeconds)
                    progress.LastProgressAt = _retreatNavigationTime;
            }
            if (_retreatNavigationTime - progress.LastProgressAt >= ZombieStagnationSeconds)
                BeginZombieRetreat(zombie);
        }
    }
}
