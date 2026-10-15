using System.Numerics;
using System.Text.Json.Serialization;

namespace ChessVsZombies.Core;

public enum MegaWeaponFormation { Stick, Chunk }

/// <summary>One real material block in a Mega's weapon; inactive blocks are stowed during bucket work.</summary>
public sealed record MegaWeaponBlock(int CarrierId, int BlockIndex, Material Material, float Hp, bool WasBarrier,
    Vector3 Min, Vector3 Max, bool IsActive)
{
    // The lower/upper pair in each hand shares one grip; four independent hands
    // are never implied by the four individually durable material pieces.
    public int HandIndex => BlockIndex % 2;
}

public sealed record MegaThrownBlock(int CarrierId, int BlockIndex, Material Material, float Hp, bool WasBarrier,
    Vector3 Min, Vector3 Max, MegaWeaponFormation Formation, bool IsDebris);

/// <summary>Ballistic material still in flight when a between-level checkpoint is taken.</summary>
public sealed class SavedMegaWeaponFlight
{
    [JsonRequired] public int OwnerId { get; set; }
    [JsonRequired] public MegaWeaponFormation Formation { get; set; }
    [JsonRequired] public bool IsDebris { get; set; }
    [JsonRequired] public float X { get; set; }
    [JsonRequired] public float Y { get; set; }
    [JsonRequired] public float Z { get; set; }
    [JsonRequired] public float VelocityX { get; set; }
    [JsonRequired] public float VelocityY { get; set; }
    [JsonRequired] public float VelocityZ { get; set; }
    [JsonRequired] public float Age { get; set; }
    [JsonRequired] public float Damage { get; set; }
    [JsonRequired] public List<SavedMegaWeaponPart> Blocks { get; set; } = [];
    [JsonRequired] public List<int> DamagedTowerIds { get; set; } = [];
}

public sealed class SavedMegaWeaponPart
{
    [JsonRequired] public int BlockIndex { get; set; }
    [JsonRequired] public Material Material { get; set; }
    [JsonRequired] public float Hp { get; set; }
    [JsonRequired] public bool WasBarrier { get; set; }
    [JsonRequired] public float OffsetX { get; set; }
    [JsonRequired] public float OffsetY { get; set; }
    [JsonRequired] public float OffsetZ { get; set; }
}
