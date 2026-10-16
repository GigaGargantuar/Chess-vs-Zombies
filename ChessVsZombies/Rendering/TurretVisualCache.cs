using System.Numerics;
using ChessVsZombies.Core;

namespace ChessVsZombies.Rendering;

/// <summary>Tracks only the inputs that affect a turret's rendered geometry.</summary>
public sealed class TurretVisualCache
{
    private readonly List<State> states = [];
    private WorldGrid? previousWorld;
    private readonly record struct State(Turret Turret, PieceKind Kind, Vector3 Position, bool Selected, bool Upgraded);

    /// <returns>True when the stored geometry must be rebuilt.</returns>
    public bool Refresh(IReadOnlyList<Turret> turrets, FallAnimations falls, Turret? selected, WorldGrid world)
    {
        bool changed = previousWorld != world || states.Count != turrets.Count;
        previousWorld = world;
        for (int i = 0; i < turrets.Count; i++)
        {
            var turret = turrets[i];
            bool upgraded = false;
            foreach (int points in turret.UpgradePoints.Values)
                if (points > 0) { upgraded = true; break; }
            var state = new State(turret, turret.Kind, falls.GetTurretPosition(turret), turret == selected, upgraded);
            if (i == states.Count) states.Add(state);
            else
            {
                if (states[i] != state) changed = true;
                states[i] = state;
            }
        }
        if (states.Count > turrets.Count) states.RemoveRange(turrets.Count, states.Count - turrets.Count);
        return changed;
    }
}
