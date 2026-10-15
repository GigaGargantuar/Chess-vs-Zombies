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
dotnet run --project ChessVsZombies/ChessVsZombies.csproj -- --smoke-liquids --capture artifacts/liquid-shop.png
dotnet run --project ChessVsZombies/ChessVsZombies.csproj -- --smoke-armor --capture artifacts/armor-facing.png
dotnet run --project ChessVsZombies/ChessVsZombies.csproj -- --smoke-bridges --capture artifacts/bridge-materials.png

dotnet run --project ChessVsZombies/ChessVsZombies.csproj -- --smoke-buckets --capture artifacts/bucket-cover-combat.png
dotnet run --project ChessVsZombies/ChessVsZombies.csproj -- --smoke-falls --capture artifacts/fall-first-second.png
dotnet run --project ChessVsZombies/ChessVsZombies.csproj -- --smoke-falls --capture-frame 150 --capture artifacts/fall-second-second.png
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
| 8 / 9 / 0 | Select water / mercury / lava to pour |
| L | Open the liquid shop and choose 1–10 units per pour |
| Left mouse | Remove the aimed defense; with tool 7, excavate aimed terrain |
| Right mouse | Place the selected piece, defensive block, or liquid at the crosshair |
| Middle mouse | Apply the chosen upgrade template to the aimed tower |
| Enter | Start the next level |
| G | Open the component gacha |
| U | Choose the point counts in the upgrade template |
| J | Edit the jump pattern included in the template |
| Delete | Recover the selected or aimed tower; blocks can be recovered during preparation |
| P | Pause |
| V | Toggle volume guides |
| F1 | Open the field manual |
| R | Retry the saved preparation after defeat |

Losing window focus releases and shows the mouse cursor. Returning focus leaves the cursor free until Tab or an explicit camera-return action; held clicks are ignored until released.

The crosshair and placement preview identify the aimed cell. A living turret stays selected only while the crosshair points at it and the camera is within five blocks of its cell center; looking away or moving farther clears the selection, including while menus are open. The camera flies freely with a small collision body against terrain and turrets; horizontal movement stays level even when looking up or down. Coordinates use Y for height. Begin by right-clicking to place pieces outside the central block enclosure. Use the Terrain panel to buy excavation tools, collect blocks with tool 7, then select dirt or stone and right-click to fortify the core or turret area. Press Enter to start the wave. Tower placement, removal, and upgrade application remain available during waves, including editing the template and its jump pattern. New towers cannot overlap a living zombie. Terrain changes, supply purchases, and gacha pulls happen between levels. Editing the template changes the next application; middle-click a tower to spend its points and apply it.

## Battlefield

The playable volume contains 32 × 32 × 32 cells, indexed 0 through 31 on every axis. The objective occupies the central 2 × 2 × 2 cells, coordinates 15–16. The centered 8 × 8 × 8 volume, coordinates 12–19 on all three axes, is reserved for defensive blocks and the objective: turrets cannot be placed there. Turrets belong inside the centered 30 × 30 × 30 volume, coordinates 1–30, excluding that inner cube. Defensive blocks can be built throughout this interior, including both the core enclosure and the turret area, but cannot occupy the core or an existing tower's cell. The remaining one-cell outer shell is the zombie spawn zone.

The bottom half starts solid: stone at Y=0–11 and dirt at Y=12–15. The core takes precedence over terrain, so its lower four cells replace dirt and its upper four cells sit above the surface. Turrets need an empty cell with a block or living tower directly below, so towers can be stacked. New blocks can attach to a block in any of the six orthogonally adjacent cells. Digging opens terrain cells for construction and changes zombie routes.

Removing or destroying a tower's support settles the remaining stack downward, bottom first, preserving each survivor's HP and upgrades. Natural terrain and fortifications keep holes, overhangs, and ceilings whenever at least one orthogonally adjacent block remains; diagonal neighbors do not count. Only isolated blocks fall, stopping at another block or tower, or the bottom of the grid. Adjacent blocks can hold each other in place without a connection to the ground. Blocks and towers settle in whole grid cells. Partial obsidian uses actual face contact: disjoint West/East or North/South slabs can fall alongside one another into the same cell, preserving the open gap, each orientation, and each slab's HP. A moving slab animates independently of its stationary neighbor. Shapes with overlapping footprints still obstruct a fall; a supported neighboring slab is never overwritten. Towers retain their grid support rule, while zombie, camera, and projectile collision use each crust's exact thickness. Falling solids displace liquid into their vacated cells without deleting liquid units.

Towers, blocks, and unsupported zombies visibly accelerate downward: the first second covers one block, then falling continues at five blocks per second until landing. The animation uses `Lerp` with an eased distance curve (`t^5` in the first second), so speed is continuous at the transition. Tower and block occupancy settles immediately for grid rules and saving while their meshes animate to the settled cells; zombie falling updates the body's actual position. Removing more support during a fall continues from the current position and speed.

Zombies take fall damage on landing after a drop of four or more full blocks: four blocks deals 5 damage, five deals 10, and each further full block adds 5. This is blunt impact damage to the body; helmets, chestplates, and shields do not absorb the landing. Changing the landing support during a fall preserves the total distance fallen. Separate landings reset the distance, and buoyancy ends a fall without a ground impact.

Excavating dirt requires a shovel, bought once for 100 currency. Excavating stone requires a separate pickaxe, bought once for 1,000 currency. Each successfully excavated cell adds one block of that material to inventory. A dirt fortification uses one dirt block and has 100 HP; a stone fortification uses one stone block and has 500 HP. Placement consumes blocks rather than currency. Removing a fortification returns one block of its material to inventory.

The pickaxe also recovers wood, copper, iron, gold, diamond, and osmium blocks left by zombies. In Terrain → Recovered materials, choose a material to reuse as fortification or sell one block at a time. Sales are available during preparation and consume the recovered block. Dirt has no resale value. These materials currently differ in durability and sale value; special material behavior has not been defined yet.

| Material | Block HP | Sale value |
| --- | ---: | ---: |
| Dirt | 100 | — |
| Wood | 250 | 100 |
| Stone | 500 | 200 |
| Copper | 1,000 | 300 |
| Iron | 2,000 | 500 |
| Gold | 3,000 | 800 |
| Diamond | 5,000 | 1,300 |
| Osmium | 8,000 | 2,100 |

Natural dirt also has 100 HP and natural stone has 500 HP. Zombies can attack and destroy terrain to open a route to the objective; destroyed terrain does not add blocks to the player's inventory. Blocks remain solid and stop bullets until their HP reaches zero. The crosshair displays the remaining HP of aimed terrain and fortifications.

Zombies navigate one-block climbs and drops, including shallow holes and low obstacles. They can jump over a single tower or a one-block-wide gap when the arc is clear and the landing is supported. They clear blocked headroom before stepping through it. Routes penalize digging according to estimated time spent breaking the remaining HP and the extra exposure to fire, so a longer open detour can be preferable; a blocked-in zombie can still dig out. The shared route estimate uses the strongest attack configured for the level. Destroyed towers immediately leave the live occupancy map; routes and interrupted steps are refreshed so their former cells are traversable. Terrain destruction opens new routes and causes unsupported defenses and zombies to fall.

Ordinary zombies carry exactly one block: basic zombies carry dirt, while armed or armored zombies carry their gear tier's material. Losing armor does not change that block. When no complete walking/digging route exists, zombies plan a shared bridge and place their blocks as adjacent, supported floor cells. Each successful placement consumes the builder's only block. Followers use the completed section and extend it with their own blocks; spent builders follow existing supported paths, including one-block descents from their own blocks, toward the closest reachable approach. They wait there when no complete route exists and reconsider when later carriers extend the bridge. A spent builder standing in a carrier's construction cell steps aside when a clear supported exit exists. Existing short-gap jumps take precedence over unnecessary construction. Waves can start and spawn without a complete route to the core. Spawning still requires a physically valid cell on the outer shell; if none is available, the wave waits without consuming its spawn quota.

An unspent block becomes solid terrain where its carrier dies and follows the normal support/falling rules. A zombie that already built with its block drops nothing. If the death cell is occupied, the block is released above it in the same column; falling displaces liquid without deleting it. A fully obstructed column retains a pending drop until space becomes available, including across checkpoints. Zombie-built and dropped blocks can subsequently be excavated, reused, sold, or destroyed.

## Liquids and reactions

The liquid shop is a separate section under Deploy, also opened with L. Purchases give ten units (one block); choose a pour amount from one to ten units and right-click a surface or existing liquid. Water costs 100 currency per block, mercury 800, and lava 1,000. Liquids flow downward and spread to neighboring cells during preparation and combat. Excavated basins contain them; water floats above mercury without reacting. Solid terrain, defenses, and obsidian contain or redirect the flow.

When liquid blocks an approach to the center, one ordinary spawn in every five can carry a bucket on its back instead of a block. Bucket carriers retain that roster entry's kind, tier, HP, and attack, so the wave's fixed spawn and HP budgets do not grow. Every Kilo carries two independent ten-unit buckets alongside its two-block cover loadout, switching to buckets when liquid work is needed. Both buckets scoop in the same action cycle and throw each payload along its own arc. Each scoop or throw takes one second; during that action the cover blocks are stowed and cannot intercept shots. Taking damage cannot interrupt the action to raise cover. Outside liquid work, the Kilo raises its blocks again; it cannot scoop while using them to block fire. Ordinary carriers retain one bucket in place of their backpack block. They approach banks with enough clearance for their actual body, scoop reachable surface liquid, and throw it away from the center. The throw travels three blocks horizontally before returning to its launch height; its arc continues farther when the landing ground is lower, and actual terrain or liquid contact stops it. The landing distributes the bucket's existing units across a balanced 3×3 footprint centered on impact. Walls constrain the splash, and obstructed overflow remains pending rather than disappearing. A killed carrier spills all bucket contents, and surviving Kilo cover blocks drop normally. Empty buckets can be reused.

Aiming at liquid highlights the cell directly above it, regardless of the selected tool or viewing angle. A pour into that cell flows down into the pool. An occupied cell above the surface is shown as invalid; a liquid at the top of the grid has no placement cell. Picking follows the actual partial liquid volume and obsidian geometry.

Water makes all zombies sink. Iron, Gold, Diamond, and Osmium armor make zombies sink in lava; only Osmium armor makes them sink in mercury. A surviving qualifying piece is sufficient. Losing the last heavy piece immediately cancels any old descent and restores buoyancy in that liquid; the zombie rises toward the surface at two tiles per second, subject to solid collision. Breath starts running out only when liquid reaches the head's breathing point, 0.7 blocks above the feet (seven units in a normal full-width cell). Actual head height and liquid geometry determine submersion, including stacked or mixed liquids and crusts. Zombies have ten seconds of air, then die immediately regardless of their remaining health or armor; reaching air restores their breath.

Mercury contact deals 5 chemical damage per second and lava deals 10 thermal damage per second. Water vapor deals 3 thermal damage per second per cloud; additional steam in the same cloud extends its lifetime without increasing its damage rate. These rates are tunable balance constants. Each surviving Osmium armor piece contributes ten seconds of heat protection: a full set starts with thirty seconds. Protection is consumed during exposure and does not refill on leaving the liquid; breaking a piece removes its unused protection. Sinking Osmium zombies can still suffocate while protected from heat.

| Contact | Reaction |
| --- | --- |
| Mercury + water | No reaction; denser mercury settles beneath water. |
| Mercury + lava | Two lava units form two obsidian units, a 0.2-block crust at the interface, while one mercury unit becomes chemical vapor in a 5×5×5 volume. Continued contact thickens the crust; ten lava units can vaporize five mercury units and form one obsidian block. More vapor increases fume strength and lifetime. |
| Water + lava | Equal units react: ten lava units become one obsidian block and ten water units become thermal vapor in a 3×3×3 volume. Smaller amounts react proportionally. |

Obsidian crusts have their actual thickness for collision and rendering. They obstruct fluid transfer at the interface while continued heat contact can grow the crust. Each tenth-block layer has 5 HP, giving a full block 50 HP. The existing pickaxe can mine a crust during preparation, recovering its exact number of layers without shattering it; remaining HP does not change the layer count. Obsidian inventory, placement, and sales appear under Terrain → Recovered materials: ten layers form one whole block and sell together for 250 currency, leaving any extra layers in inventory. Placement uses one layer per right-click. The first layer sits against the supporting face; aiming at a partial crust adds layers while retaining that first orientation, up to ten layers. Paired crusts retain both geometries and durability values in checkpoints; mining recovers their combined layers, and adding a layer or attacking a crust affects the face actually selected or struck. Older checkpoints start with zero stored obsidian layers and load existing single crusts unchanged. When a zombie destroys a crust, it shatters for 20 piercing damage to every zombie whose body overlaps the 3×3×3 volume centered on that cell, including the attacker. Shards use the normal directional shield and chestplate rules. An unsupported partial crust also shatters when a solid block or column above it pushes it down into the ground, using the same shard damage. Full obsidian blocks, crusts already resting on the ground, and crusts or loads with external block support do not trigger this compression rule. Mining and projectile destruction do not produce shards.

Zombies attack crusts that form through their bodies or block their ascent, and can move off partially filled obsidian cells. Floating zombies reuse route results while liquid geometry, terrain, defenses, and their armor-dependent buoyancy remain unchanged. Flow advances every 0.1 seconds in whole tenth-block units. Reactions account for consumed liquid as obsidian or vapor; vapor eventually dissipates into the atmosphere. Buying another block replenishes spent supply.

## Pieces and components

You begin with 1,000 currency and no excavation tools or stored blocks. Each gacha pull costs 5 and grants one random component point. Buying pieces, buying tools, and pulling components draw from the same balance.

| Piece | Price | Initial attack pattern |
| --- | ---: | --- |
| Pawn | 1 | One cell along the six orthogonal directions |
| Knight | 2 | Eight horizontal knight jumps, editable in three dimensions |
| Bishop | 4 | Unlimited reach along the twelve plane diagonals |
| Rook | 4 | Unlimited reach along the six orthogonal directions |

Pawns strike physically at adjacent directional targets. Rooks and bishops fire pellets and begin with unlimited reach in their innate direction family. Solid terrain, defensive blocks, and obsidian stop shots. Friendly pieces do not block targeting or projectiles: pellets and lobbed shots pass through them. Longer directional attacks from other pieces also use pellets. Jump destinations produce lobbed projectiles whose arcs can clear low obstacles.

Ranged pieces keep targeting zombies in their attack pattern even when blocks hide them. Each shot hits the first physical obstruction along its trajectory, wearing down that cover until a clear shot opens. Melee strikes still require unobstructed reach.

Fired projectiles follow their launch trajectories even if their original target dies. A missed pellet continues along its ray; a missed lob continues its ballistic arc. Either can hit another zombie or damage terrain behind the target. Terrain impacts consume the shot and apply its attack damage to the struck block, including fortifications and obsidian. Blocks broken by projectiles do not produce inventory or replacement drops, and projectile destruction of obsidian does not trigger the zombie-only shatter burst. Shots already in flight finish after a wave ends.

The six component types are HP, ATK, orthogonal, plane diagonal, space diagonal, and jump. Each roll grants exactly one available point of its type. Choose how many points of each type to allocate in the upgrade template, then aim at an existing tower and middle-click. Each type applies as many requested points as the inventory and tower's remaining cap permit. Unavailable or excess points do not block other upgrades; unused inventory stays banked and the reusable template stays unchanged. Already-unlimited range types are skipped. If the requested jump pattern does not fit the tower's achieved range or construction budget, its previous pattern remains while valid point upgrades still apply. The result message reports applied points and skipped requests.

The first copy establishes a per-tower allocation cap of 1 for that type; every ten additional copies raise that cap by 1. The 11th, 21st, and 31st copies therefore allow up to 2, 3, and 4 allocated points of that type on each tower. Pulls and cap increases never change deployed towers automatically. Each point explicitly applied to a tower adds +10 maximum/current HP, +1 ATK, or one tile of its chosen finite directional reach. HP allocation preserves existing damage. A pawn starts with orthogonal range 1; new directional families start at range 1 after their first point. Rooks and bishops cannot spend points on their already unlimited innate family.

The first jump point gives a non-jumping piece range 2, and further allocated points each add one tile. A knight already starts at range 2, so its first allocated jump point raises it to range 3. Jump patterns contain explicit three-dimensional destination offsets. Each destination costs one construction point; range 2 has a budget of 8, with 4 more construction points per additional tile of reach. Destinations must be distinct and have a longest-axis distance from 2 through the resulting range. The template can carry a custom pattern, applied together with its points. Jump attacks skip intermediate cells.

Removing a turret refunds its purchase price during both preparation and combat; removing a fortification during preparation returns its material to inventory. Upgrade points already allocated to a removed turret remain consumed; their collection progress still contributes toward permanent allocation caps. Gravity can carry a surviving tower into the reserved block cube or onto the bottom grid layer, although direct deployment there remains forbidden.

## Zombies and levels

Basic zombies have 10 body HP and 1 attack. Armed zombies add a weapon; armored zombies carry a helmet, chestplate, and shield; the combined type carries both. Zombies turn to face their travel or attack direction, and their visible equipment turns with them. Purely vertical movement preserves their horizontal heading.

Zombies without a weapon move 50% faster than weapon carriers of the same size. Milli bodies are half the standard size with half the body HP and armor durability. Kilos occupy a full 2×2×2 volume with four times the standard body HP and armor durability. An unarmed Milli moves twice as fast as an unarmed standard zombie, while an unarmed Kilo moves at 75% of an unarmed standard zombie's speed. Collision, breathing height, visible equipment, and health bars follow the actual size. Kilos can hurl standard zombies and Millis over cover and towers. A Kilo recovers reachable nearby solid blocks, including blocks dropped by dead teammates, before excavating a ground block. Nearby allies do not prevent recovering an exposed block. It also brings its backpack block into its hands, holding up to two real blocks with their original materials, remaining durability, and independent positions. Either empty hand can recover a replacement without creating material. Both blocks and the Kilo's visible equipment stay within the same 2×2×2 volume at every facing. Held cover uses the body's clearance for walking, turning, jumping, and falling. Held blocks can be spent on construction, and surviving held blocks fall into the world on death. Kilos holding cover keep advancing instead of joining stationary hiding groups.

Armor pieces have separate durability and coverage. The shield absorbs frontal physical hits within a total 90-degree horizontal arc (45 degrees left/right) and 60-degree vertical arc (30 degrees up/down). Coverage uses the incoming projectile's direction at impact, including a lob's descending arc. After any shield absorption, helmets absorb only lobbed attacks and chestplates absorb only pellet attacks. Melee bypasses helmets and chestplates. Excess damage reaches the next eligible layer, then body HP; ineligible armor stays intact. Broken pieces fall away in the zombie's current orientation. Chemical and thermal damage bypass armor and damage the body, except where a piece supplies resistance, such as Osmium's heat protection. Weapons add their damage to the base attack.

Damage flashes the zombie and its equipped armor white and displays a floating number for the actual body/armor durability lost. Continuous chemical and thermal damage is grouped into short readable bursts. A separate labeled bar shows body HP, helmet, chestplate, and shield durability; a broken piece loses its bar, and submerged zombies also show remaining air. Fully resisted damage produces no damage flash or number.

Undamaged grounded zombies can wait behind nearby blocks that screen an in-range pellet turret. When separate clusters are hiding, they choose one rally cluster by combined remaining body and armor HP, then attack strength, size of the group, and leader ID to break ties. Weaker groups stop camping and follow physically reachable routes toward that rally from across the map. Five or more living zombies assembled within two blocks advance immediately, with no maximum group size. A group of three or four attacks after remaining together for more than 30 seconds; dropping below three resets that timer. When fewer than five zombies remain in their wave, counting both living zombies and those still to spawn, the remaining zombies advance without waiting. Actual body or armor damage immediately ends a zombie's wait and makes it advance out of that hiding spot. A released group keeps moving as its members leave, and destroyed cover ends the wait. Exact obsidian thickness matters: an empty gap in a crust does not provide cover.

Shoreline movement checks the entire body between its endpoints. Zombies in shallow liquid keep their feet on the real supporting surface. Floating zombies can break cover that obstructs the upper or side of their body.

If no active zombie has a useful walking or digging route, a reachable construction job, useful bucket work, or a feasible Kilo throw, the army retreats along physical routes to the outer spawn edge. There ordinary zombies replenish missing blocks or exchange their backpack block for an empty bucket when liquid clearance is needed. Kilos keep both buckets, their liquid payloads, and surviving cover blocks, replenishing only missing slots in their two-block loadout before returning in the same wave. Each zombie can resupply and return three times per wave. Returns preserve body HP, identity, spawn counts, and the original spawn-budget charge. Armor is repaired and advances through available tiers; the third return grants the strongest available armor. Added armor durability is included in the wave's actual combat HP pool. Held liquids are conserved when exchanging equipment. Repeated backtracking or guarding without meaningful progress triggers an individual retreat after ten seconds, even while other zombies move. Actual excavation, core damage, construction, liquid removal, useful throws, and new ground count as progress. On a fourth retreat, the zombie walks to the outer edge and leaves the current wave without a kill or material drop. It joins the next scheduled wave with its remaining body HP, armor, tools, bucket contents, and held cover; the return allowance resets for that wave. A survivor from the final wave joins the next level's first wave, and that queue is saved with the campaign. Its next-wave budget contribution stays at its original cost; remaining combat HP is counted separately. A Kilo hit by a projectile faces the shot's source while continuing its advance or retreat. While its cover blocks are raised, the hand nearest an incoming projectile moves independently to intercept it; the other hand retains its own position. Scooping and throwing keep both blocks stowed for the full action, leaving the Kilo exposed to shots. Both cubes remain separate inside the body's reserved volume. Each new impact updates the body's facing, including impacts on its cover blocks. Movement follows the route independently of this guard heading. Resupplying clears that guard facing before it advances again.

| Tier | HP per armor item | Weapon bonus | Fully armored HP | Armed attack |
| --- | ---: | ---: | ---: | ---: |
| Wood | 10 | 2 | 40 | 3 |
| Stone | 20 | 3 | 70 | 4 |
| Copper | 30 | 5 | 100 | 6 |
| Iron | 50 | 8 | 160 | 9 |
| Gold | 80 | 13 | 250 | 14 |
| Diamond | 130 | 21 | 400 | 22 |
| Osmium | 210 | 34 | 640 | 35 |

Every wave has an overall HP budget, including zombies that have not spawned yet. A standard armored zombie consumes the same normal-spawn budget as several basic zombies because the cost includes its body HP and all armor durability. Generated waves reserve 97% of their budget for normal enemies and 3% for bosses; authoring can choose a boss reserve from 1% through 5%. Natural Kilo bosses wear the strongest unlocked armor once armor is introduced; explicitly authored boss equipment remains authoritative. Bosses are currently Kilos and each costs the reserved budget only as much as one regular unarmed zombie, while retaining the Kilo's actual health in combat. Actual spawned HP and budget cost are tracked separately, so the next wave still starts when damage removes at least 50% of the previous wave's original combat HP pool. Completion requires all waves to finish spawning and all zombies to be defeated. Each surviving turret pays 2 currency once between levels.

Generated wave 5 introduces wooden equipment, wave 7 increases its presence, and wave 9 is predominantly wooden-geared enemies while retaining unarmed cannon fodder. Higher-tier waves keep every earlier equipment tier in smaller proportions, with the strongest tier most common. Sizes and equipment types are interleaved across the roster instead of leaving armored enemies until the end. Authored waves without an explicit HP budget retain their exact counts, tiers, and sizes. A body death or drowning removes that zombie's remaining armor from the wave's live HP pool as well.

Zombies can change lanes after losing 50% of their maximum HP. The threshold decreases by five percentage points after every five completed levels: 45% on level 6, 40% on level 11, and so on, with a minimum of zero. Generated levels have one wave by default, introduce the four zombie kinds over the opening levels, and progress through gear tiers.

## Campaign checkpoints

Preparation changes, exiting during preparation, and successful level completion save a checkpoint under `%LOCALAPPDATA%/ChessVsZombies/campaign.json`. Currency, collected point inventory and caps, each tower's allocated points, custom jump patterns, the reusable upgrade template, surviving defenses and their materials, stored material blocks, purchased tools, removed terrain and remaining terrain HP, bridges and death drops, pending blocked drops, liquid inventory and flow state, obsidian crusts, remaining vapor clouds, objective health, and the next level persist between launches. Gacha random state is saved too. In-flight projectiles are transient.

Changing a template's requested point counts or pattern toggle saves immediately. The jump workshop saves its pattern only when you choose to use it in the template; uncommitted edits are discarded on cancel. Template edits during combat update only the template in the preparation checkpoint, preserving the saved battlefield. Requested counts remain intact even when the current inventory or a tower's cap cannot satisfy them.

Active combat resumes from the latest preparation checkpoint when you relaunch or retry after defeat. Mid-wave progress, including tower purchases, removals, and applied upgrades, is not saved until the level is completed; template preferences are saved separately as described above. Checkpoints are validated before loading. A failed load preserves the original file and disables automatic overwriting so the damaged checkpoint remains available for recovery.

Version 1 checkpoints migrate to allocated points: each previously equipped component becomes one allocated point; towers inside the now-reserved block cube are removed with their purchase price and equipped components refunded. Components on a rook's innate orthogonal family or a bishop's innate plane-diagonal family are refunded because those families are unlimited. Existing HP damage is preserved against the corrected maximum, with at least 1 HP retained, and jump patterns are reduced to their corrected reach and budget.

Version 1 and 2 checkpoints also migrate to material inventories: previously excavated cells are credited as dirt or stone, and old fortifications become dirt with existing damage preserved against the new 100 HP maximum. Tools remain unowned until purchased. Version 3 inventories migrate without crediting excavated cells again. Version 4 adds remaining terrain HP. Version 5 adds liquids and reaction products, with empty supplies for older campaigns. Version 6 preserves fallen natural terrain's material, location, and damage, as well as settled tower stacks and suspended blocks. Version 7 saves upgrade templates and allows fortifications throughout the turret area. Older saves receive the default template; existing obsidian retains its percentage of durability when converted from 50 to 5 HP per layer. Version 8 adds the new material inventories and pending death drops; older campaigns start with zero new material inventory and no pending drops. Loading leaves the original file intact; the first subsequent save keeps an original `.v1.bak` through `.v7.bak` copy before writing version 8.

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

Kinds are `Basic`, `Armed`, `Armored`, and `ArmedArmored`. Tiers are `Wood`, `Stone`, `Copper`, `Iron`, `Gold`, `Diamond`, and `Osmium`. Each group can additionally specify `size` (`Milli`, `Standard`, or `Kilo`), defaulting to `Standard`, and `isBoss`, defaulting to false; boss groups must use `Kilo`. A wave can specify an overall `hpBudget` and a `bossBudgetFraction` from `0.01` to `0.05` (default `0.03`). In a budgeted wave, group counts provide the normal roster proportions rather than a fixed spawn quota; boss costs draw from the reserved portion. Spawn intervals must be positive and each group must have a positive count. Authoring definitions are copied into the catalog and returned as independent snapshots.

## Prototype balance and structure

Values not supplied in the original design are explicit prototype defaults: turrets have 30 HP, the objective has 200 HP, and all six gacha types have equal odds. Core and terrain are stationary grid cells. This is a local single-player prototype with generated placeholder geometry.

`ChessVsZombies.Core` contains the world, economy, level definitions, pathfinding, and combat without a graphics dependency. `ChessVsZombies` contains the MonoGame renderer, embodied camera, crosshair, and menus. `ChessVsZombies.Tests` exercises rule boundaries, economy milestones, authored levels, wave transitions, and simulation behavior without creating a graphics device.
