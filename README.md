# Chess vs Zombies

A 3D voxel defense prototype built with C# and MonoGame DesktopGL. Build around the central objective, deploy chess-inspired turrets, pull upgrade components, and survive successive zombie levels.

## Run

On this Windows machine, double-click `Play.cmd` to build and launch the game.

Install the .NET 8 SDK or a later compatible SDK, then run these commands from the repository root:

```powershell
dotnet restore ChessVsZombies.sln
dotnet run --project ChessVsZombies/ChessVsZombies.csproj
```

The game uses generated geometry and a bundled font; no MonoGame content pipeline installation or external art assets are needed.

Run the headless gameplay checks with:

```powershell
dotnet test ChessVsZombies.Tests/ChessVsZombies.Tests.csproj
```

The native renderer also supports short smoke runs that exit automatically after 90 frames. These use a temporary demonstration session and do not load or alter campaign checkpoints:

```powershell
dotnet run --project ChessVsZombies/ChessVsZombies.csproj -- --smoke --capture artifacts/build-mode.png
dotnet run --project ChessVsZombies/ChessVsZombies.csproj -- --smoke-combat --capture artifacts/combat-mode.png
```

## Controls

| Input | Action |
| --- | --- |
| W / A / S / D | Move horizontally relative to the camera |
| Space / Shift | Move up / down |
| Mouse | Pan the first-person view |
| Tab | Release or capture the mouse for menus |
| Escape | Exit the jump workshop, or toggle mouse capture |
| Left Ctrl | Move faster |
| 1 / 2 / 3 / 4 | Select Pawn / Knight / Bishop / Rook |
| 5 / 6 / 7 | Select dirt fortification / stone fortification / excavation |
| Left mouse | Remove the aimed defense; with tool 7, excavate aimed terrain |
| Right mouse | Place the selected piece or defensive block at the crosshair |
| Middle mouse | Apply the chosen upgrade template to the aimed tower |
| Enter | Start the next level |
| G | Open the component gacha |
| U | Choose the point counts in the upgrade template |
| J | Edit the jump pattern included in the template |
| Delete | Recover the selected or aimed defense during preparation |
| P | Pause |
| V | Toggle volume guides |
| F1 | Open the field manual |
| R | Retry the saved preparation after defeat |

The crosshair and placement preview identify the aimed cell. The camera flies freely with a small collision body against terrain and turrets; horizontal movement stays level even when looking up or down. Coordinates use Y for height. Begin by right-clicking to place pieces outside the central block enclosure. Use the Terrain panel to buy excavation tools, collect blocks with tool 7, then select dirt or stone and right-click near the core to fortify it. Press Enter to start the wave. Construction, tool purchases, pulling, and applying templates happen between levels. Editing the template changes the next application; middle-click a tower to spend its points and apply it.

## Battlefield

The playable volume contains 32 × 32 × 32 cells, indexed 0 through 31 on every axis. The objective occupies the central 2 × 2 × 2 cells, coordinates 15–16. Defensive blocks belong inside the centered 8 × 8 × 8 volume, coordinates 12–19 on all three axes. This entire inner cube is reserved for blocks and the objective: turrets cannot be placed there. Turrets belong inside the centered 30 × 30 × 30 volume, coordinates 1–30, excluding that inner cube. The remaining one-cell outer shell is the zombie spawn zone.

The bottom half starts solid: stone at Y=0–11 and dirt at Y=12–15. The core takes precedence over terrain, so its lower four cells replace dirt and its upper four cells sit above the surface. Turrets and new blocks need an empty cell with solid support immediately below. Digging opens terrain cells for construction and changes zombie routes.

Excavating dirt requires a shovel, bought once for 100 currency. Excavating stone requires a separate pickaxe, bought once for 1,000 currency. Each successfully excavated cell adds one block of that material to inventory. A dirt fortification uses one dirt block and has 100 HP; a stone fortification uses one stone block and has 500 HP. Placement consumes blocks rather than currency. Removing a fortification returns one block of its material to inventory.

## Pieces and components

You begin with 1,000 currency and no excavation tools or stored blocks. Each gacha pull costs 5 and grants one random component point. Buying pieces, buying tools, and pulling components draw from the same balance.

| Piece | Price | Initial attack pattern |
| --- | ---: | --- |
| Pawn | 1 | One cell along the six orthogonal directions |
| Knight | 2 | Eight horizontal knight jumps, editable in three dimensions |
| Bishop | 4 | Unlimited reach along the twelve plane diagonals |
| Rook | 4 | Unlimited reach along the six orthogonal directions |

Pawns strike physically at adjacent directional targets. Rooks and bishops fire pellets and begin with unlimited reach in their innate direction family. Solid terrain, defensive blocks, and other pieces block their line of fire. Longer directional attacks from other pieces also use pellets. Jump destinations produce lobbed projectiles whose arcs can clear low obstacles.

The six component types are HP, ATK, orthogonal, plane diagonal, space diagonal, and jump. Each roll grants exactly one available point of its type. Choose how many points of each type to allocate in the upgrade template, then aim at an existing tower and middle-click. The entire template must fit the inventory, the tower's remaining per-type allocation caps, and its jump-pattern rules; an invalid template spends nothing.

The first copy establishes a per-tower allocation cap of 1 for that type; every ten additional copies raise that cap by 1. The 11th, 21st, and 31st copies therefore allow up to 2, 3, and 4 allocated points of that type on each tower. Pulls and cap increases never change deployed towers automatically. Each point explicitly applied to a tower adds +10 maximum/current HP, +1 ATK, or one tile of its chosen finite directional reach. HP allocation preserves existing damage. A pawn starts with orthogonal range 1; new directional families start at range 1 after their first point. Rooks and bishops cannot spend points on their already unlimited innate family.

The first jump point gives a non-jumping piece range 2, and further allocated points each add one tile. A knight already starts at range 2, so its first allocated jump point raises it to range 3. Jump patterns contain explicit three-dimensional destination offsets. Each destination costs one construction point; range 2 has a budget of 8, with 4 more construction points per additional tile of reach. Destinations must be distinct and have a longest-axis distance from 2 through the resulting range. The template can carry a custom pattern, applied together with its points. Jump attacks skip intermediate cells.

Removing a turret during preparation refunds its purchase price; removing a fortification returns its material to inventory. Upgrade points already allocated to a removed turret remain consumed; their collection progress still contributes toward permanent allocation caps. A turret whose support collapses into the reserved block cube is destroyed without a refund.

## Zombies and levels

Basic zombies have 10 HP and 1 attack. Armed zombies add a weapon; armored zombies carry a helmet, chestplate, and shield; the combined type carries both. The three armor items add their durability to the zombie's HP pool. Weapons add their damage to the base attack.

| Tier | HP per armor item | Weapon bonus | Fully armored HP | Armed attack |
| --- | ---: | ---: | ---: | ---: |
| Wood | 10 | 2 | 40 | 3 |
| Stone | 20 | 3 | 70 | 4 |
| Copper | 30 | 5 | 100 | 6 |
| Iron | 50 | 8 | 160 | 9 |
| Gold | 80 | 13 | 250 | 14 |
| Diamond | 130 | 21 | 400 | 22 |
| Osmium | 210 | 34 | 640 | 35 |

Every wave has a fixed spawn roster and HP budget, including zombies that have not spawned yet. When damage removes at least 50% of a wave's original HP budget, the next available wave starts spawning. Completion still requires all waves to finish spawning and all zombies to be defeated. Each surviving turret pays 2 currency once between levels.

Zombies can change lanes after losing 50% of their maximum HP. The threshold decreases by five percentage points after every five completed levels: 45% on level 6, 40% on level 11, and so on, with a minimum of zero. Generated levels have one wave by default, introduce the four zombie kinds over the opening levels, and progress through gear tiers.

## Campaign checkpoints

Preparation changes and successful level completion save a checkpoint under `%LOCALAPPDATA%/ChessVsZombies/campaign.json`. Currency, collected point inventory and caps, each tower's allocated points, custom jump patterns, surviving defenses and their materials, stored dirt/stone, purchased tools, excavated terrain, objective health, and the next level persist between launches. Gacha random state is saved too.

Active combat resumes from the latest preparation checkpoint when you relaunch or retry after defeat. Mid-wave progress is not saved. Checkpoints are validated before loading. A failed load preserves the original file and disables automatic overwriting so the damaged checkpoint remains available for recovery.

Version 1 checkpoints migrate to allocated points: each previously equipped component becomes one allocated point; towers inside the now-reserved block cube are removed with their purchase price and equipped components refunded. Components on a rook's innate orthogonal family or a bishop's innate plane-diagonal family are refunded because those families are unlimited. Existing HP damage is preserved against the corrected maximum, with at least 1 HP retained, and jump patterns are reduced to their corrected reach and budget.

Version 1 and 2 checkpoints also migrate to material inventories: previously excavated cells are credited as dirt or stone, and old fortifications become dirt with existing damage preserved against the new 100 HP maximum. Tools remain unowned until purchased. Loading leaves the original file intact; the first subsequent save keeps an original `.v1.bak` or `.v2.bak` copy before writing version 3.

To keep a separate campaign for experiments, launch with `--save-path`, for example:

```powershell
dotnet run --project ChessVsZombies/ChessVsZombies.csproj -- --save-path artifacts/test-campaign.json
```

## Level authoring

`ChessVsZombies.Core/LevelCatalog.cs` provides the seam for a future level editor. Add JSON files under `Levels/`; a file overrides the generated level matching its `Number`. Files are copied beside the application on build. The catalog validates definitions and reports invalid files without discarding other valid levels.

```json
{
  "number": 1,
  "name": "Two fronts",
  "waves": [
    {
      "spawnInterval": 1.4,
      "groups": [
        { "kind": "Basic", "tier": "Wood", "count": 8 }
      ]
    },
    {
      "spawnInterval": 1.0,
      "groups": [
        { "kind": "Armed", "tier": "Stone", "count": 3 }
      ]
    }
  ]
}
```

Kinds are `Basic`, `Armed`, `Armored`, and `ArmedArmored`. Tiers are `Wood`, `Stone`, `Copper`, `Iron`, `Gold`, `Diamond`, and `Osmium`. Spawn intervals must be positive and each group must have a positive count. Authoring definitions are copied into the catalog and returned as independent snapshots.

## Prototype balance and structure

Values not supplied in the original design are explicit prototype defaults: turrets have 30 HP, the objective has 200 HP, and all six gacha types have equal odds. Core and terrain are stationary grid cells. This is a local single-player prototype with generated placeholder geometry.

`ChessVsZombies.Core` contains the world, economy, level definitions, pathfinding, and combat without a graphics dependency. `ChessVsZombies` contains the MonoGame renderer, embodied camera, crosshair, and menus. `ChessVsZombies.Tests` exercises rule boundaries, economy milestones, authored levels, wave transitions, and simulation behavior without creating a graphics device.
