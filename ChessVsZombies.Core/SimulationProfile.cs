using System.Diagnostics;

namespace ChessVsZombies.Core;

/// <summary>Opt-in inclusive CPU timings; never changes simulation decisions.</summary>
public sealed class SimulationProfile
{
    private readonly Dictionary<string, (long Ticks, int Calls)> samples = [];
    public Scope Measure(string name) => new(this, name);
    public void Reset() => samples.Clear();
    public object Report(int updates) => samples.OrderByDescending(pair => pair.Value.Ticks).Select(pair => new
    {
        Stage = pair.Key, pair.Value.Calls,
        TotalMs = pair.Value.Ticks * 1000d / Stopwatch.Frequency,
        MsPerUpdate = pair.Value.Ticks * 1000d / Stopwatch.Frequency / Math.Max(1, updates)
    }).ToArray();

    public readonly struct Scope : IDisposable
    {
        private readonly SimulationProfile owner;
        private readonly string name;
        private readonly long start;
        internal Scope(SimulationProfile owner, string name)
        { this.owner = owner; this.name = name; start = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            long elapsed = Stopwatch.GetTimestamp() - start;
            var sample = owner.samples.GetValueOrDefault(name);
            owner.samples[name] = (sample.Ticks + elapsed, sample.Calls + 1);
        }
    }
}
