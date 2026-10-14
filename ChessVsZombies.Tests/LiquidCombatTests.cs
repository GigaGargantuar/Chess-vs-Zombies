using System.Numerics;
using ChessVsZombies.Core;
using Xunit;

namespace ChessVsZombies.Tests;

public class LiquidCombatTests
{
    [Theory]
    [InlineData(ShotKind.Pellet, ArmorSlot.Chestplate, ArmorSlot.Helmet)]
    [InlineData(ShotKind.Lob, ArmorSlot.Helmet, ArmorSlot.Chestplate)]
    public void PhysicalDamageStripsShieldThenMatchingArmorAndSpillsToBody(ShotKind shot, ArmorSlot matching, ArmorSlot bypassed)
    {
        var game = new GameSession();
        var zombie = new Zombie { Kind = ZombieKind.Armored, Tier = GearTier.Wood, WaveIndex = -1 };
        Assert.Equal(40, zombie.Hp);
        var kind = shot == ShotKind.Pellet ? DamageKind.Pierce : DamageKind.Blunt;
        game.DamageZombie(zombie, 11, kind, shot, -zombie.Facing);
        Assert.Equal(10, zombie.BodyHp);
        Assert.Equal(29, zombie.Hp);
        Assert.Equal(new[] { ArmorSlot.Chestplate, ArmorSlot.Helmet }, zombie.ArmorPieces.Select(piece => piece.Slot));
        Assert.Equal(9, Assert.Single(zombie.ArmorPieces, piece => piece.Slot == matching).Hp);
        game.DamageZombie(zombie, 11, kind, shot, -zombie.Facing);
        Assert.Equal(bypassed, Assert.Single(zombie.ArmorPieces).Slot);
        Assert.Equal(10, Assert.Single(zombie.ArmorPieces).Hp);
        Assert.Equal(8, zombie.BodyHp);
        Assert.Equal(18, zombie.Hp);
    }

    [Theory]
    [InlineData(DamageKind.Chemical)]
    [InlineData(DamageKind.Thermal)]
    public void UnresistedChemicalAndThermalDamageBypassAllArmor(DamageKind kind)
    {
        var game = new GameSession();
        var zombie = new Zombie { Kind = ZombieKind.ArmedArmored, Tier = GearTier.Diamond, WaveIndex = -1 };
        game.DamageZombie(zombie, 3, kind);
        Assert.Equal(7, zombie.BodyHp);
        Assert.Equal(397, zombie.Hp);
        Assert.Equal(3, zombie.ArmorPieces.Count);
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(130, piece.Hp));
    }

    [Fact]
    public void OsmiumProtectionAddsPerPieceAndLosingArmorDiscardsItsUnusedProtection()
    {
        var game = new GameSession();
        var zombie = new Zombie { Kind = ZombieKind.Armored, Tier = GearTier.Osmium, WaveIndex = -1 };
        Assert.Equal(30, zombie.ArmorPieces.Sum(piece => piece.HeatProtectionRemaining));
        game.DamageZombie(zombie, 25, DamageKind.Thermal); // Five seconds at the baseline lava damage rate.
        Assert.Equal(10, zombie.BodyHp);
        Assert.Equal(25, zombie.ArmorPieces.Sum(piece => piece.HeatProtectionRemaining));
        game.Update(1); // Time away from heat cannot replenish protection.
        Assert.Equal(25, zombie.ArmorPieces.Sum(piece => piece.HeatProtectionRemaining));
        game.DamageZombie(zombie, 210, DamageKind.Pierce, ShotKind.Pellet, -zombie.Facing);
        Assert.Equal(20, zombie.ArmorPieces.Sum(piece => piece.HeatProtectionRemaining));
        game.DamageZombie(zombie, 100, DamageKind.Thermal);
        Assert.Equal(10, zombie.BodyHp);
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(0, piece.HeatProtectionRemaining));
        game.DamageZombie(zombie, 1, DamageKind.Thermal);
        Assert.Equal(9, zombie.BodyHp);
        Assert.Equal(2, zombie.ArmorPieces.Count);
    }

    [Theory]
    [InlineData(GearTier.Wood, LiquidKind.Lava, false)]
    [InlineData(GearTier.Copper, LiquidKind.Lava, false)]
    [InlineData(GearTier.Iron, LiquidKind.Lava, true)]
    [InlineData(GearTier.Diamond, LiquidKind.Lava, true)]
    [InlineData(GearTier.Diamond, LiquidKind.Mercury, false)]
    [InlineData(GearTier.Osmium, LiquidKind.Mercury, true)]
    [InlineData(GearTier.Wood, LiquidKind.Water, true)]
    public void SurvivingArmorDeterminesBuoyancyAndItsLossCanRestoreFloating(GearTier tier, LiquidKind liquid, bool sinks)
    {
        var game = new GameSession();
        var zombie = new Zombie { Kind = ZombieKind.Armored, Tier = tier, WaveIndex = -1 };
        Assert.Equal(sinks, zombie.SinksIn(liquid));
        game.DamageZombie(zombie, 2 * Balance.GearHp(tier), DamageKind.Pierce, ShotKind.Pellet, -zombie.Facing);
        game.DamageZombie(zombie, Balance.GearHp(tier), DamageKind.Blunt, ShotKind.Lob, Vector3.UnitY);
        Assert.Equal(10, zombie.BodyHp);
        Assert.Empty(zombie.ArmorPieces);
        Assert.Equal(liquid == LiquidKind.Water, zombie.SinksIn(liquid));
    }

    [Theory]
    [InlineData(LiquidKind.Lava, GearTier.Iron, 13.5f)]
    [InlineData(LiquidKind.Lava, GearTier.Gold, 14.13f)]
    [InlineData(LiquidKind.Lava, GearTier.Diamond, 15.75f)]
    [InlineData(LiquidKind.Mercury, GearTier.Osmium, 13.5f)]
    [InlineData(LiquidKind.Mercury, GearTier.Osmium, 14.13f)]
    [InlineData(LiquidKind.Mercury, GearTier.Osmium, 15.75f)]
    public void LosingTheLastSinkingArmorCancelsDescentImmediatelyAndRisesFromEveryDepth(LiquidKind liquid, GearTier tier, float y)
    {
        var game = Battle(ZombieKind.Armored, tier);
        FillWell(game, liquid, 13, 15);
        var zombie = BeginAt(game, new Cell(8, 13, 8));
        zombie.Position = new Vector3(8.5f, y, 8.5f);
        game.DamageZombie(zombie, 2 * Balance.GearHp(tier), DamageKind.Pierce, ShotKind.Pellet, -zombie.Facing);
        Assert.Single(zombie.ArmorPieces);
        Assert.True(zombie.SinksIn(liquid));
        zombie.NextCell = new Cell(8, 13, 8);
        zombie.MoveProgress = .5f;

        game.DamageZombie(zombie, Balance.GearHp(tier), DamageKind.Blunt, ShotKind.Lob, Vector3.UnitY);

        Assert.Equal(10, zombie.BodyHp);
        Assert.Empty(zombie.ArmorPieces);
        Assert.False(zombie.SinksIn(liquid));
        Assert.Null(zombie.NextCell);
        Assert.Equal(0, zombie.MoveProgress);
        Assert.Equal(y, zombie.Position.Y); // Damage changes movement authority without teleporting the body.
        game.Update(.05f);
        Assert.InRange(zombie.Position.Y, y + .099f, y + .101f);
        Assert.Equal(8.5f, zombie.Position.X);
        Assert.Equal(8.5f, zombie.Position.Z);
        Assert.True(zombie.Hp > 0);

        for (int step = 0; step < 40 && zombie.Position.Y < 16.39f && zombie.Hp > 0; step++) game.Update(.05f);
        Assert.True(zombie.Hp > 0, "A living zombie should reach the surface before this shallow well's exposure becomes lethal.");
        Assert.InRange(zombie.Position.Y, 16.39f, 16.41f);
    }

    [Theory]
    [InlineData(LiquidKind.Lava, GearTier.Copper)]
    [InlineData(LiquidKind.Mercury, GearTier.Diamond)]
    public void RemainingNonqualifyingArmorDoesNotKeepASubmergedZombieOnItsOldDescendingRoute(LiquidKind liquid, GearTier tier)
    {
        var game = Battle(ZombieKind.Armored, tier);
        FillWell(game, liquid, 13, 15);
        var zombie = BeginAt(game, new Cell(8, 14, 8));
        zombie.Position = new Vector3(8.5f, 14.13f, 8.5f);
        game.DamageZombie(zombie, Balance.GearHp(tier), DamageKind.Pierce, ShotKind.Pellet, -zombie.Facing);
        Assert.Equal(2, zombie.ArmorPieces.Count);
        Assert.False(zombie.SinksIn(liquid));
        zombie.NextCell = new Cell(8, 13, 8);
        zombie.MoveProgress = .5f;

        game.Update(.05f);

        Assert.Equal(2, zombie.ArmorPieces.Count);
        Assert.Null(zombie.NextCell);
        Assert.Equal(0, zombie.MoveProgress);
        Assert.InRange(zombie.Position.Y, 14.229f, 14.231f);
        Assert.True(zombie.BodyHp > 0);
    }

    [Theory]
    [InlineData(LiquidKind.Lava, GearTier.Iron)]
    [InlineData(LiquidKind.Mercury, GearTier.Osmium)]
    public void BreakingOnlySomeQualifyingArmorDoesNotMakeTheZombieFloat(LiquidKind liquid, GearTier tier)
    {
        var game = Battle(ZombieKind.Armored, tier);
        FillWell(game, liquid, 13, 15);
        var zombie = BeginAt(game, new Cell(8, 14, 8));
        var before = zombie.Position.Y;
        game.DamageZombie(zombie, Balance.GearHp(tier), DamageKind.Pierce, ShotKind.Pellet, -zombie.Facing);
        Assert.Equal(2, zombie.ArmorPieces.Count);
        Assert.True(zombie.SinksIn(liquid));

        game.Update(.05f);

        Assert.True(zombie.Position.Y < before);
        Assert.True(zombie.SinksIn(liquid));
    }

    [Fact]
    public void BodyDeathCountsDiscardedArmorOnceTowardTheWaveOverlapThreshold()
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition
        {
            Waves = [new() { Groups = [new() { Kind = ZombieKind.Armored, Tier = GearTier.Osmium }] },
                new() { Groups = [new() { Kind = ZombieKind.Basic }] }]
        });
        var game = new GameSession(levels: levels);
        Assert.True(game.StartLevel());
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        game.DamageZombie(zombie, 5, DamageKind.Chemical);
        Assert.Equal(5, game.Waves[0].DamageTaken);
        Assert.False(game.Waves[1].Started);
        game.DamageZombie(zombie, 5, DamageKind.Chemical);
        Assert.Equal(0, zombie.BodyHp);
        Assert.Equal(0, zombie.Hp);
        Assert.Equal(640, game.Waves[0].DamageTaken);
        game.DamageZombie(zombie, 1000);
        Assert.Equal(640, game.Waves[0].DamageTaken);
        game.Update(.01f);
        Assert.True(game.Waves[1].Started);
        Assert.Single(game.Zombies, living => living.WaveIndex == 1);
    }

    [Theory]
    [InlineData(LiquidKind.Water, 100)]
    [InlineData(LiquidKind.Mercury, 800)]
    [InlineData(LiquidKind.Lava, 1000)]
    public void LiquidPurchasesBuyExactlyTenUnitsForTheirPrice(LiquidKind kind, int price)
    {
        var game = new GameSession();
        Assert.True(game.BuyLiquid(kind));
        Assert.Equal(1000 - price, game.Economy.Currency);
        Assert.Equal(10, game.LiquidInventory[kind]);
        Assert.False(game.BuyLiquid((LiquidKind)100));
        Assert.Equal(1000 - price, game.Economy.Currency);
        game.Reset();
        Assert.All(game.LiquidInventory, pair => Assert.Equal(0, pair.Value));
        Assert.Equal(1000, game.Economy.Currency);
    }

    [Fact]
    public void PouringIsAtomicAndRejectsSolidsPiecesInvalidAmountsAndOverfilledCells()
    {
        var game = new GameSession();
        Assert.True(game.BuyLiquid(LiquidKind.Water));
        Assert.True(game.BuyLiquid(LiquidKind.Water));
        var destination = new Cell(8, 16, 8);
        Assert.True(game.PourLiquid(destination, LiquidKind.Water, 7));
        Assert.False(game.PourLiquid(destination, LiquidKind.Water, 4));
        Assert.False(game.PourLiquid(new Cell(8, 15, 8), LiquidKind.Water, 1));
        Assert.False(game.PourLiquid(new Cell(15, 16, 15), LiquidKind.Water, 1));
        Assert.False(game.PourLiquid(new Cell(-1, 16, 8), LiquidKind.Water, 1));
        Assert.False(game.PourLiquid(destination, LiquidKind.Water, 0));
        Assert.False(game.PourLiquid(destination, LiquidKind.Water, -1));
        Assert.True(game.Deploy(PieceKind.Pawn, new Cell(9, 16, 8)));
        Assert.False(game.PourLiquid(new Cell(9, 16, 8), LiquidKind.Water, 1));
        Assert.False(game.Deploy(PieceKind.Pawn, destination));
        Assert.Equal(13, game.LiquidInventory[LiquidKind.Water]);
        Assert.Equal(7, game.Liquids.GetTotalUnits(destination));
        Assert.Equal(799, game.Economy.Currency);
        Assert.True(game.StartLevel());
        Assert.False(game.BuyLiquid(LiquidKind.Water));
        Assert.False(game.PourLiquid(destination, LiquidKind.Water, 1));
        Assert.Equal(13, game.LiquidInventory[LiquidKind.Water]);
    }

    [Fact]
    public void LiquidFlowsDuringPreparationWithoutSpendingMoreInventory()
    {
        var game = new GameSession();
        Assert.True(game.BuyTool(ToolKind.Shovel));
        Assert.True(game.Dig(new Cell(8, 15, 8)));
        Assert.True(game.BuyLiquid(LiquidKind.Water));
        Assert.True(game.PourLiquid(new Cell(8, 16, 8), LiquidKind.Water));
        game.Update(.1f);
        Assert.Equal(GamePhase.Building, game.Phase);
        Assert.True(game.Liquids.GetUnits(new Cell(8, 15, 8), LiquidKind.Water) > 0);
        Assert.Equal(10, game.Liquids.Cells.Sum(cell => cell.Water));
        Assert.Equal(0, game.LiquidInventory[LiquidKind.Water]);
        Assert.Equal(800, game.Economy.Currency);
    }

    [Fact]
    public void TenSecondsWithoutAirKillsAnArmoredZombieRegardlessOfItsRemainingHp()
    {
        var game = Battle(ZombieKind.Armored, GearTier.Osmium);
        FillWell(game, LiquidKind.Water, 13, 15);
        var zombie = BeginAt(game, new Cell(8, 13, 8));
        game.Update(9.9f);
        Assert.Equal(640, zombie.Hp);
        Assert.InRange(zombie.AirRemaining, .09f, .11f);
        game.Update(.2f);
        Assert.Equal(0, zombie.BodyHp);
        Assert.Equal(640, game.Waves[0].DamageTaken);
        Assert.Empty(game.Zombies);
    }

    [Fact]
    public void BreathingRestoresAirBeforeAnotherSubmersion()
    {
        var game = Battle(ZombieKind.Basic);
        FillWell(game, LiquidKind.Water, 13, 15);
        var zombie = BeginAt(game, new Cell(8, 13, 8));
        game.Update(5);
        Assert.InRange(zombie.AirRemaining, 4.99f, 5.01f);
        zombie.Position = new Cell(7, 16, 8).Center;
        zombie.NextCell = null;
        game.Update(.01f);
        Assert.Equal(10, zombie.AirRemaining);
        zombie.Position = new Cell(8, 13, 8).Center;
        zombie.NextCell = null;
        game.Update(9.9f);
        Assert.Equal(10, zombie.Hp);
        game.Update(.2f);
        Assert.Equal(0, zombie.Hp);
    }

    [Fact]
    public void ThreeOsmiumPiecesProvideThirtySecondsOfCumulativeLavaProtection()
    {
        var game = Battle(ZombieKind.Armored, GearTier.Osmium);
        // Keep a solid roof and walls so this fixture measures uninterrupted heat
        // contact instead of the zombie's ability to climb out of a shallow pool.
        FillWell(game, LiquidKind.Lava, 13, 13, unitsPerCell: 5);
        var zombie = BeginAt(game, new Cell(8, 13, 8));
        game.Update(15);
        Assert.Equal(10, zombie.BodyHp);
        Assert.InRange(zombie.ArmorPieces.Sum(piece => piece.HeatProtectionRemaining), 14.99f, 15.01f);
        zombie.Position = new Cell(3, 16, 3).Center;
        zombie.NextCell = null;
        game.Update(1);
        Assert.InRange(zombie.ArmorPieces.Sum(piece => piece.HeatProtectionRemaining), 14.99f, 15.01f);
        zombie.Position = new Cell(8, 13, 8).Center;
        zombie.NextCell = null;
        game.Update(14.9f);
        Assert.Equal(10, zombie.BodyHp);
        Assert.Equal(10, zombie.AirRemaining);
        game.Update(.2f);
        Assert.InRange(zombie.BodyHp, 9.49f, 9.51f);
        Assert.All(zombie.ArmorPieces, piece => Assert.Equal(0, piece.HeatProtectionRemaining));
    }

    [Fact]
    public void ALightZombieCanEnterACellDeepMercuryPoolFromLandAndLeaveItsOtherBank()
    {
        var game = Battle(ZombieKind.Basic);
        FillWell(game, LiquidKind.Mercury, 13, 15);
        var zombie = BeginAt(game, new Cell(7, 16, 8));
        var entered = false;
        for (int step = 0; step < 70; step++)
        {
            game.Update(.05f);
            if (zombie.Position.X is >= 8 and < 9 && zombie.Position.Z is >= 8 and < 9)
            {
                entered = true;
                Assert.True(zombie.Position.Y >= 16.39f, $"Floater sank to {zombie.Position}.");
            }
        }
        Assert.True(entered);
        Assert.True(zombie.Position.X >= 9, $"Zombie did not leave the pool: {zombie.Position}.");
        Assert.True(zombie.Hp > 0);
        Assert.Equal(10, zombie.AirRemaining);
    }

    [Theory]
    [InlineData(DamageKind.Chemical, 2, 10.99f, true)]
    [InlineData(DamageKind.Chemical, 2, 11.01f, false)]
    [InlineData(DamageKind.Thermal, 1, 9.99f, true)]
    [InlineData(DamageKind.Thermal, 1, 10.01f, false)]
    public void VaporHazardsUseTheirFullOddSizedCube(DamageKind kind, int radius, float x, bool exposed)
    {
        var game = Battle(ZombieKind.Basic);
        game.Liquids.RestoreState(new LiquidState
        {
            Hazards = [new VaporCloud(new Cell(8, 16, 8), radius, kind, 20, 2, 1)]
        });
        var zombie = BeginAt(game, new Cell(10, 16, 8));
        zombie.Position = new Vector3(x, 16.5f, 8.5f);
        game.Update(.01f);
        Assert.InRange(zombie.BodyHp, exposed ? 9.979f : 10, exposed ? 9.981f : 10);
    }

    private static GameSession Battle(ZombieKind kind, GearTier tier = GearTier.Wood)
    {
        var levels = new LevelCatalog();
        levels.Set(new LevelDefinition { Waves = [new() { Groups = [new() { Kind = kind, Tier = tier }] }] });
        return new GameSession(levels: levels);
    }

    private static void FillWell(GameSession game, LiquidKind kind, int bottom, int top, int unitsPerCell = 10)
    {
        Assert.True(game.BuyTool(ToolKind.Shovel));
        for (int y = bottom; y <= top; y++)
        {
            var cell = new Cell(8, y, 8);
            Assert.True(game.Dig(cell));
            Assert.True(game.Liquids.TryAdd(cell, kind, unitsPerCell));
        }
    }

    private static Zombie BeginAt(GameSession game, Cell cell)
    {
        Assert.True(game.StartLevel());
        game.Update(.01f);
        var zombie = Assert.Single(game.Zombies);
        zombie.Position = cell.Center;
        zombie.NextCell = null;
        zombie.Cooldown = 10000;
        return zombie;
    }
}
