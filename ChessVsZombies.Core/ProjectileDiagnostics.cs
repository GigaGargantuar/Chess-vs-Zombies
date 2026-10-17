namespace ChessVsZombies.Core;

/// <summary>Optional work counts and ordered contacts for separate correctness/profiling runs.</summary>
public sealed class ProjectileDiagnostics
{
    private readonly Dictionary<Projectile, int> shotIds = [];
    public bool CaptureContacts { get; init; }
    public long ForecastRequests, ForecastEvaluations, PendingSorts, BodyQueries, CoverQueries, FlightQueries, Slices;
    public int Frame { get; private set; }
    public List<ProjectileContact> Contacts { get; } = [];

    internal void BeginFrame(IEnumerable<Projectile> shots)
    {
        Frame++;
        if (!CaptureContacts) return;
        foreach (var shot in shots)
            if (!shotIds.ContainsKey(shot)) shotIds.Add(shot, shotIds.Count);
    }

    internal void Contact(Projectile shot, float age, string kind, int targetId, int blockIndex, Cell? cell,
        CrustFace? face, float hpBefore, float hpAfter)
    {
        if (!CaptureContacts) return;
        Contacts.Add(new(Frame, shotIds.GetValueOrDefault(shot, -1), age, kind, targetId, blockIndex,
            cell, face, shot.Damage, hpBefore, hpAfter));
    }
}

public sealed record ProjectileContact(int Frame, int Shot, float Age, string Kind, int TargetId, int BlockIndex,
    Cell? Cell, CrustFace? Face, float Damage, float HpBefore, float HpAfter);
