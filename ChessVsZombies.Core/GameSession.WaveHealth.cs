namespace ChessVsZombies.Core;

public readonly record struct WaveHealthSnapshot(
    double CombatHp, double CombatMaxHp, long SpawnHp, long SpawnHpBudget, double BarMaximum);

public sealed partial class GameSession
{
    /// <summary>Actual living combat bodies and the unspent purchase wallet, independent of future roster candidates.</summary>
    public WaveHealthSnapshot GetWaveHealth(int waveIndex)
    {
        if ((uint)waveIndex >= (uint)Waves.Count) throw new ArgumentOutOfRangeException(nameof(waveIndex));
        var wave = Waves[waveIndex];
        double combatHp = 0, maximumHp = 0;
        foreach (var zombie in Zombies)
        {
            if (zombie.WaveIndex != waveIndex || zombie.BodyHp <= 0) continue;
            combatHp += zombie.Hp;
            maximumHp += zombie.MaxHp;
        }
        long spawnHp = Math.Max(0, wave.HpBudget - wave.BudgetSpent);
        // Paid spawn HP becomes combat HP on the same scale. Discounted bosses
        // and later max-HP growth expand that scale by their actual premium;
        // removing a dead body does not make the gold remainder refill the bar.
        double maximum = Math.Max(wave.HpBudget + wave.CombatHpPremium, maximumHp + spawnHp);
        return new(combatHp, maximumHp, spawnHp, wave.HpBudget, maximum);
    }

    private static double CombatHealthPremium(Zombie zombie, double maximumHp) =>
        Math.Max(0, maximumHp - zombie.OriginalBudgetCost);

    private static void RecordCombatHealthPurchase(Zombie zombie, WaveState wave) =>
        wave.CombatHpPremium += CombatHealthPremium(zombie, zombie.MaxHp);

    private static void RecordCombatHealthMaximumChange(Zombie zombie, WaveState wave, double previousMaximum, double newMaximum) =>
        wave.CombatHpPremium += CombatHealthPremium(zombie, newMaximum) - CombatHealthPremium(zombie, previousMaximum);
}
