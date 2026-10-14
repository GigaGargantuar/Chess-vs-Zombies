using System.Numerics;

namespace ChessVsZombies.Core;

public sealed record FallingBlock(Cell Destination, Vector3 Position, Material Material, Material? Content, ObsidianCell? Crust);

/// <summary>
/// Transient presentation tracks for settled grid objects, plus the same falling
/// curve for physical zombie movement. None of this state belongs in checkpoints.
/// </summary>
public sealed class FallAnimations
{
    public const float AccelerationSeconds = 1;
    public const float TerminalSpeed = 5;
    private readonly Dictionary<(Cell Cell, CrustFace? Face), BlockTrack> _blocks = [];
    private readonly Dictionary<int, Motion> _turrets = [];
    private readonly Dictionary<int, Motion> _zombies = [];

    public int Revision { get; private set; }
    public IReadOnlyList<FallingBlock> Blocks => _blocks.OrderBy(pair => pair.Key.Cell.X).ThenBy(pair => pair.Key.Cell.Y).ThenBy(pair => pair.Key.Cell.Z)
        .ThenBy(pair => pair.Key.Face)
        .Select(pair => new FallingBlock(pair.Key.Cell, pair.Value.Motion.Position, pair.Value.Material, pair.Value.Content, pair.Value.Crust)).ToArray();

    /// <summary>One block in the first second, then five blocks per second.</summary>
    public static float DistanceAfter(float elapsedSeconds)
    {
        if (!float.IsFinite(elapsedSeconds) || elapsedSeconds < 0) throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        return elapsedSeconds <= AccelerationSeconds ? MathF.Pow(elapsedSeconds, 5) :
            1 + TerminalSpeed * (elapsedSeconds - AccelerationSeconds);
    }

    public Vector3 GetTurretPosition(Turret turret) => _turrets.TryGetValue(turret.Id, out var fall) && fall.End == turret.Cell.Center
        ? fall.Position : turret.Cell.Center;

    internal void Clear()
    {
        _blocks.Clear();
        _turrets.Clear();
        _zombies.Clear();
        Revision++;
    }

    internal void Update(float dt, WorldGrid world, IEnumerable<Turret> turrets)
    {
        Prune(world, turrets);
        foreach (var entry in _blocks.ToArray())
        {
            entry.Value.Motion.Advance(dt);
            if (!entry.Value.Motion.Complete) continue;
            _blocks.Remove(entry.Key);
            Revision++;
        }
        foreach (var entry in _turrets.ToArray())
        {
            entry.Value.Advance(dt);
            if (!entry.Value.Complete) continue;
            _turrets.Remove(entry.Key);
            Revision++;
        }
    }

    internal void Prune(WorldGrid world, IEnumerable<Turret> turrets)
    {
        foreach (var entry in _blocks.ToArray())
        {
            var crust = entry.Key.Face is { } face
                ? world.GetObsidian(entry.Key.Cell)?.Parts.FirstOrDefault(part => part.Face == face) : null;
            if (world.GetMaterial(entry.Key.Cell) != entry.Value.Material || entry.Key.Face.HasValue && crust is null)
            {
                _blocks.Remove(entry.Key);
                Revision++;
                continue;
            }
            entry.Value.Content = world.GetBarrier(entry.Key.Cell)?.Content;
            entry.Value.Crust = crust;
        }
        if (_turrets.Count > 0)
        {
            var living = turrets.Where(turret => turret.Hp > 0).ToDictionary(turret => turret.Id);
            foreach (var entry in _turrets.ToArray())
                if (!living.TryGetValue(entry.Key, out var turret) || turret.Cell.Center != entry.Value.End)
                {
                    _turrets.Remove(entry.Key);
                    Revision++;
                }
        }
    }

    internal void RecordBlockDrop(Cell from, Cell to, WorldGrid world)
    {
        if (from == to) return;
        if (world.GetObsidian(to) is { } crust)
        {
            foreach (var part in crust.Parts) RecordObsidianDrop(from, to, part);
            return;
        }
        Motion motion;
        if (_blocks.Remove((from, null), out var previous))
        {
            motion = previous.Motion;
            motion.Retarget(motion.Position, to.Center);
        }
        else motion = new Motion(from.Center, to.Center);
        _blocks[(to, null)] = new BlockTrack(motion, world.GetMaterial(to), world.GetBarrier(to)?.Content, null);
        Revision++;
    }

    /// <summary>A slab moves independently of any stationary opposite slab sharing its destination.</summary>
    internal void RecordObsidianDrop(Cell from, Cell to, ObsidianCell part)
    {
        if (from == to) return;
        Motion motion;
        if (_blocks.Remove((from, part.Face), out var previous))
        {
            motion = previous.Motion;
            motion.Retarget(motion.Position, to.Center);
        }
        else motion = new Motion(from.Center, to.Center);
        _blocks[(to, part.Face)] = new BlockTrack(motion, Material.Obsidian, null, part with { Cell = to });
        Revision++;
    }

    internal void RecordTurretDrop(Turret turret, Cell from)
    {
        if (from == turret.Cell) return;
        if (_turrets.TryGetValue(turret.Id, out var previous) && previous.End == from.Center)
            previous.Retarget(previous.Position, turret.Cell.Center);
        else _turrets[turret.Id] = new Motion(from.Center, turret.Cell.Center);
        Revision++;
    }

    internal Vector3 AdvanceZombieFall(Zombie zombie, Vector3 destination, float dt)
    {
        if (!_zombies.TryGetValue(zombie.Id, out var motion) || Vector3.DistanceSquared(motion.Position, zombie.Position) > .000001f)
            _zombies[zombie.Id] = motion = new Motion(zombie.Position, destination);
        else if (motion.End != destination)
            motion.Retarget(zombie.Position, destination);
        motion.Advance(dt);
        var position = motion.Position;
        if (motion.Complete) _zombies.Remove(zombie.Id);
        return position;
    }

    internal void CancelZombie(Zombie zombie) => _zombies.Remove(zombie.Id);

    private sealed class BlockTrack(Motion motion, Material material, Material? content, ObsidianCell? crust)
    {
        public Motion Motion { get; } = motion;
        public Material Material { get; } = material;
        public Material? Content { get; set; } = content;
        public ObsidianCell? Crust { get; set; } = crust;
    }

    private sealed class Motion(Vector3 start, Vector3 end)
    {
        private Vector3 _start = start;
        private float _elapsed;
        private float _distanceAtStart;
        public Vector3 End { get; private set; } = end;
        private float Length => Vector3.Distance(_start, End);
        private float Travel => Math.Max(0, DistanceAfter(_elapsed) - _distanceAtStart);
        public bool Complete => Travel >= Length - .00001f;
        public Vector3 Position => Complete ? End : Vector3.Lerp(_start, End, Math.Clamp(Travel / Length, 0, 1));
        public void Advance(float dt) => _elapsed += dt;
        public void Retarget(Vector3 current, Vector3 destination)
        {
            _start = current;
            End = destination;
            _distanceAtStart = DistanceAfter(_elapsed);
        }
    }
}
