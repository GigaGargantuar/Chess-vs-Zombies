using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChessVsZombies.Core;

public sealed record LiquidBucketFlightState(
    [property: JsonRequired, JsonConverter(typeof(BucketVector3Converter))] Vector3 Origin,
    [property: JsonRequired, JsonConverter(typeof(BucketVector3Converter))] Vector3 Direction,
    [property: JsonRequired] LiquidKind Kind,
    [property: JsonRequired] int Units,
    [property: JsonRequired] float Age,
    float UpwardVelocity = LiquidBucketFlight.UpwardSpeed);

public sealed record LiquidBucketSplashState(
    [property: JsonRequired, JsonConverter(typeof(BucketVector3Converter))] Vector3 Center,
    [property: JsonRequired] LiquidKind Kind,
    [property: JsonRequired] int Units);

public sealed class BucketSystemState
{
    [JsonRequired] public List<LiquidBucketFlightState> Flights { get; set; } = [];
    [JsonRequired] public List<LiquidBucketSplashState> PendingSplashes { get; set; } = [];
}

internal sealed class BucketVector3Converter : JsonConverter<Vector3>
{
    public BucketVector3Converter() { }

    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        return new Vector3(Component("X"), Component("Y"), Component("Z"));

        float Component(string name)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var component) ||
                component.ValueKind != JsonValueKind.Number || !component.TryGetSingle(out var value) || !float.IsFinite(value))
                throw new JsonException($"Missing or invalid bucket coordinate {name}.");
            return value;
        }
    }

    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("X", value.X);
        writer.WriteNumber("Y", value.Y);
        writer.WriteNumber("Z", value.Z);
        writer.WriteEndObject();
    }
}

public sealed class LiquidBucketFlight
{
    public const float HorizontalSpeed = 3;
    public const float UpwardSpeed = 5;
    public const float Gravity = 10;
    public Vector3 Origin { get; }
    public Vector3 Direction { get; }
    public LiquidKind Kind { get; }
    public int Units { get; }
    public float UpwardVelocity { get; }
    public float Age { get; internal set; }
    public Vector3 PreviousPosition { get; internal set; }
    public Vector3 Position => GetPositionAt(Age);

    internal LiquidBucketFlight(Vector3 origin, Vector3 direction, LiquidKind kind, int units, float age = 0,
        float upwardVelocity = UpwardSpeed)
    {
        Origin = origin;
        Direction = direction;
        Kind = kind;
        Units = units;
        UpwardVelocity = upwardVelocity;
        Age = age;
        PreviousPosition = Position;
    }

    public Vector3 GetPositionAt(float age) => Origin + Direction * (HorizontalSpeed * age) +
        Vector3.UnitY * (UpwardVelocity * age - .5f * Gravity * age * age);
}

/// <summary>Owns conserved bucket payloads from release until they enter ordinary liquid simulation.</summary>
public sealed class LiquidBucketSystem
{
    public const int MaximumBucketCapacity = 15;
    private const float MaximumStep = .02f;
    private const float Epsilon = .001f;
    private readonly WorldGrid _world;
    private readonly LiquidSystem _liquids;
    private readonly List<LiquidBucketFlight> _flights = [];
    private readonly List<LiquidBucketSplashState> _pending = [];
    public IReadOnlyList<LiquidBucketFlight> Flights => _flights;
    public IReadOnlyList<LiquidBucketSplashState> PendingSplashes => _pending;
    public int PendingUnits => _pending.Sum(splash => splash.Units);

    public LiquidBucketSystem(WorldGrid world, LiquidSystem liquids)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _liquids = liquids ?? throw new ArgumentNullException(nameof(liquids));
    }

    /// <summary>Success transfers ownership of one held bucket to this system.</summary>
    public bool Launch(Vector3 origin, Vector3 horizontalDirection, LiquidKind kind, int units, float? standingHeight = null)
    {
        if (!ValidOrigin(origin) || !ValidPayload(kind, units) || !Finite(horizontalDirection)) return false;
        float upwardVelocity = LiquidBucketFlight.UpwardSpeed;
        if (standingHeight.HasValue)
        {
            if (!float.IsFinite(standingHeight.Value) || standingHeight.Value < 0 || standingHeight.Value >= WorldGrid.Size) return false;
            upwardVelocity += standingHeight.Value - origin.Y;
        }
        if (!ValidUpwardVelocity(upwardVelocity)) return false;
        horizontalDirection.Y = 0;
        if (horizontalDirection.LengthSquared() < .000001f) return false;
        _flights.Add(new LiquidBucketFlight(origin, Vector3.Normalize(horizontalDirection), kind, units, upwardVelocity: upwardVelocity));
        return true;
    }

    /// <summary>Dead carriers spill their remaining payload; even a full destination retains overflow.</summary>
    public bool Spill(Vector3 origin, LiquidKind kind, int units)
    {
        if (!Finite(origin) || !ValidPayload(kind, units)) return false;
        if (!InsideVolume(origin)) _liquids.ReleaseOutside(kind, units);
        else _pending.Add(new LiquidBucketSplashState(origin, kind, units));
        return true;
    }

    public void Update(float dt, Func<Cell, bool>? occupied = null)
    {
        if (!float.IsFinite(dt) || dt < 0) throw new ArgumentOutOfRangeException(nameof(dt));
        for (int index = _flights.Count - 1; index >= 0; index--)
        {
            var flight = _flights[index];
            flight.PreviousPosition = flight.Position;
            float remaining = dt;
            while (remaining > 0)
            {
                float step = Math.Min(MaximumStep, remaining);
                var from = flight.Position;
                var to = flight.GetPositionAt(flight.Age + step);
                if (TryFirstContact(from, to, occupied, true, out var impact))
                {
                    var approach = to - from;
                    if (approach.LengthSquared() > .000001f) impact -= Vector3.Normalize(approach) * Epsilon;
                    if (InsideVolume(impact)) _pending.Add(new LiquidBucketSplashState(impact, flight.Kind, flight.Units));
                    else _liquids.ReleaseOutside(flight.Kind, flight.Units);
                    _flights.RemoveAt(index);
                    break;
                }
                flight.Age += step;
                remaining -= step;
                if (!InsideVolume(to))
                {
                    _liquids.ReleaseOutside(flight.Kind, flight.Units);
                    _flights.RemoveAt(index);
                    break;
                }
            }
        }
        for (int index = _pending.Count - 1; index >= 0; index--)
        {
            var splash = _pending[index];
            int remaining = Distribute(splash, occupied);
            if (remaining == 0) _pending.RemoveAt(index);
            else _pending[index] = splash with { Units = remaining };
        }
    }

    public void Reset()
    {
        _flights.Clear();
        _pending.Clear();
    }

    public BucketSystemState ExportState() => new()
    {
        Flights = _flights.Select(flight => new LiquidBucketFlightState(flight.Origin, flight.Direction,
            flight.Kind, flight.Units, flight.Age, flight.UpwardVelocity)).ToList(),
        PendingSplashes = _pending.ToList()
    };

    /// <summary>Validate every parcel before replacing the current state or touching any world liquid.</summary>
    public void RestoreState(BucketSystemState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Flights is null || state.PendingSplashes is null) throw new InvalidDataException("Missing bucket state.");
        var flights = new List<LiquidBucketFlight>();
        var oldCeilingEscapes = new List<LiquidBucketFlightState>();
        foreach (var flight in state.Flights)
        {
            if (flight is null || !ValidOrigin(flight.Origin) || !ValidPayload(flight.Kind, flight.Units) ||
                !Finite(flight.Direction) || Math.Abs(flight.Direction.Y) > .00001f ||
                Math.Abs(flight.Direction.LengthSquared() - 1) > .0001f || !float.IsFinite(flight.Age) || flight.Age < 0 ||
                !ValidUpwardVelocity(flight.UpwardVelocity))
                throw new InvalidDataException("Invalid bucket flight.");
            var restored = new LiquidBucketFlight(flight.Origin, flight.Direction, flight.Kind, flight.Units, flight.Age, flight.UpwardVelocity);
            if (!Finite(restored.Position) || restored.Position.X < 0 || restored.Position.X >= WorldGrid.Size ||
                restored.Position.Z < 0 || restored.Position.Z >= WorldGrid.Size || restored.Position.Y < 0)
                throw new InvalidDataException("Bucket flight lies outside the battlefield.");
            // Older checkpoints allowed an arc above the ceiling. Its actual
            // parcel has already crossed today's open boundary, so migrate it
            // into escape accounting instead of rejecting the whole campaign.
            if (restored.Position.Y >= WorldGrid.Size) oldCeilingEscapes.Add(flight);
            else flights.Add(restored);
        }
        foreach (var splash in state.PendingSplashes)
            if (splash is null || !ValidOrigin(splash.Center) || !ValidPayload(splash.Kind, splash.Units))
                throw new InvalidDataException("Invalid pending bucket splash.");
        foreach (var kind in Enum.GetValues<LiquidKind>())
            if (oldCeilingEscapes.Where(flight => flight.Kind == kind).Sum(flight => (long)flight.Units) >
                long.MaxValue - _liquids.GetEscapedUnits(kind))
                throw new InvalidDataException("Escaped bucket volume exceeds its saved accounting limit.");
        _flights.Clear();
        _flights.AddRange(flights);
        _pending.Clear();
        _pending.AddRange(state.PendingSplashes);
        foreach (var flight in oldCeilingEscapes) _liquids.ReleaseOutside(flight.Kind, flight.Units);
    }

    private int Distribute(LiquidBucketSplashState splash, Func<Cell, bool>? occupied)
    {
        var candidates = new List<Cell>();
        var center = ClampToWorld(splash.Center + Vector3.UnitY * Epsilon);
        int centerX = (int)MathF.Floor(center.X), centerZ = (int)MathF.Floor(center.Z);
        foreach (int dz in new[] { 0, -1, 1 })
            foreach (int dx in new[] { 0, -1, 1 })
            {
                int x = centerX + dx, z = centerZ + dz;
                if (x < 0 || x >= WorldGrid.Size || z < 0 || z >= WorldGrid.Size)
                {
                    candidates.Add(new Cell(x, (int)MathF.Floor(center.Y), z));
                    continue;
                }
                var start = new Vector3(x + .5f, center.Y, z + .5f);
                // Splash cannot pour through a wall to an enclosed void on the other side.
                if (TryFirstContact(center, start, occupied, false, out _)) continue;
                var bottom = new Vector3(start.X, 0, start.Z);
                bool contact = TryFirstContact(start, bottom, occupied, true, out var impact);
                var landing = contact ? impact + Vector3.UnitY * Epsilon : bottom;
                if (!contact) { candidates.Add(new Cell(x, -1, z)); continue; }
                var cell = CellAt(landing);
                if (WorldGrid.IsInside(cell) && occupied?.Invoke(cell) != true && _liquids.CanReceiveFromAbove(cell, splash.Kind))
                    candidates.Add(cell);
            }
        int remaining = splash.Units;
        while (remaining > 0)
        {
            var cell = candidates.Where(candidate => !WorldGrid.IsInside(candidate) || occupied?.Invoke(candidate) != true &&
                    _liquids.CanReceiveFromAbove(candidate, splash.Kind))
                .OrderBy(candidate => _liquids.GetTotalUnits(candidate))
                .ThenBy(candidate => Math.Abs(candidate.X - centerX) + Math.Abs(candidate.Z - centerZ))
                .ThenBy(candidate => candidate.Z).ThenBy(candidate => candidate.X)
                .Select(candidate => (Cell?)candidate).FirstOrDefault();
            if (!cell.HasValue) break;
            if (!WorldGrid.IsInside(cell.Value)) _liquids.ReleaseOutside(splash.Kind, 1);
            else if (!_liquids.TryAdd(cell.Value, splash.Kind, 1)) break;
            remaining--;
        }
        return remaining;
    }

    private bool TryFirstContact(Vector3 from, Vector3 to, Func<Cell, bool>? occupied, bool includeLiquid, out Vector3 contact)
    {
        var min = Vector3.Min(from, to);
        var max = Vector3.Max(from, to);
        float nearest = float.PositiveInfinity;
        for (int y = Math.Max(0, (int)MathF.Floor(min.Y)); y <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.Y)); y++)
            for (int z = Math.Max(0, (int)MathF.Floor(min.Z)); z <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.Z)); z++)
                for (int x = Math.Max(0, (int)MathF.Floor(min.X)); x <= Math.Min(WorldGrid.Size - 1, (int)MathF.Floor(max.X)); x++)
                {
                    var cell = new Cell(x, y, z);
                    if (_world.GetObsidian(cell) is { } crust)
                        foreach (var part in crust.Parts) Check(part.Min, part.Max);
                    else if (_world.GetMaterial(cell) != Material.Air || occupied?.Invoke(cell) == true)
                        Check(new Vector3(x, y, z), new Vector3(x + 1, y + 1, z + 1));
                    if (includeLiquid)
                        foreach (var volume in _liquids.GetVolumes(cell)) Check(volume.Min, volume.Max);
                }
        contact = float.IsFinite(nearest) ? Vector3.Lerp(from, to, nearest) : default;
        return float.IsFinite(nearest);

        void Check(Vector3 boundsMin, Vector3 boundsMax)
        {
            float enter = 0, leave = 1;
            var movement = to - from;
            if (Axis(from.X, movement.X, boundsMin.X, boundsMax.X, ref enter, ref leave) &&
                Axis(from.Y, movement.Y, boundsMin.Y, boundsMax.Y, ref enter, ref leave) &&
                Axis(from.Z, movement.Z, boundsMin.Z, boundsMax.Z, ref enter, ref leave) && enter < nearest)
                nearest = enter;
        }
    }

    private static bool Axis(float origin, float movement, float min, float max, ref float enter, ref float leave)
    {
        if (Math.Abs(movement) < .000001f) return origin > min + .000001f && origin < max - .000001f;
        float a = (min - origin) / movement, b = (max - origin) / movement;
        if (a > b) (a, b) = (b, a);
        enter = Math.Max(enter, a);
        leave = Math.Min(leave, b);
        return enter <= leave && leave >= 0 && enter <= 1;
    }

    private static bool ValidPayload(LiquidKind kind, int units) => Enum.IsDefined(kind) && units is > 0 and <= MaximumBucketCapacity;
    private static bool ValidUpwardVelocity(float velocity) => float.IsFinite(velocity) && velocity > 0 && velocity <= LiquidBucketFlight.UpwardSpeed * 2;
    private static bool ValidOrigin(Vector3 point) => Finite(point) && point.X >= 0 && point.X < WorldGrid.Size &&
        point.Z >= 0 && point.Z < WorldGrid.Size && point.Y >= 0 && point.Y < WorldGrid.Size;
    private static bool InsideVolume(Vector3 point) => point.X >= 0 && point.X < WorldGrid.Size &&
        point.Z >= 0 && point.Z < WorldGrid.Size && point.Y >= 0 && point.Y < WorldGrid.Size;
    private static bool Finite(Vector3 vector) => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
    private static Vector3 ClampToWorld(Vector3 point) => Vector3.Clamp(point, new Vector3(Epsilon), new Vector3(WorldGrid.Size - Epsilon));
    private static Cell CellAt(Vector3 point) => new((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y), (int)MathF.Floor(point.Z));
}
