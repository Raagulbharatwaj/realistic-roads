# Realistic Roads – retrospective and new direction

Written after PR #9 (October 2026). It covers what we built, what went wrong and why, what the
research into CS2 and existing CS1 mods taught us, and the plan from here.

## 1. Where we are

| Feature | State in game | Notes |
|---|---|---|
| RR curve tool (spline, snapping, continue a road) | Works | Segments now ~120 m (was 40 m, then 80 m: too crowded). |
| Bridges over water, level between banks, long spans | Works | Gorge bridges build since the deck was levelled between banks. |
| Suspension bridge (6-lane) | Built with towers on alternate nodes | Logged as tower/middle; still to be confirmed visually. |
| Level crossing / junction at a crossing | Works | |
| Bridge (flyover) over a road or rail | Works | |
| Overpass (other road lowered into a cutting) | Works sometimes | Fails when the other road can't ramp 8 m at 6 %; now retried and logged. |
| Underpass / tunnel | Was clipped and ugly, now "considerably better" | Rebuilt the vanilla way: open cutting, one slope piece with the portal, tunnel. |
| Smooth splits (SPL, auto on half-snap) | Works, with artefacts | Side-by-side corners. A render bug caused "Invalid list detected!" every frame (fixed). |
| LN lane tool (one-lane ramps squeezed to lane width) | Removed | Looked wrong and is the wrong model (see 3.3). |

## 2. Bugs we had and their root causes

1. **"Invalid list detected!" every frame.** The game's node render loop walks a list of
   render instances; our skip-drawing patch returned without stepping to the next one. Fixed by
   setting `instanceIndex = data.m_nextInstance`, as Adaptive Roads does. *Lesson: read the stack
   trace first; a patch that skips a game method must keep that method's side effects.*
2. **Rows of tunnel portals.** Every shallow segment used the slope piece, which draws walls and a
   portal each time. Vanilla rule: shallower than 8 m is the normal road in a cutting, deeper is
   tunnel, and only the one segment between them is the slope. The slope segment must also run from
   its underground end to the surface (`invert` keeps traffic direction), or the portal faces the
   wrong way. *Lesson: copy what the vanilla tool produces node by node instead of inventing rules.*
3. **Bridges that dipped to the water / refused gorges.** The height solver only let lifted parts
   follow the road, not the terrain. Fixed by levelling each lifted run between its banks.
4. **Lowered roads looked kinked.** After `MoveNode` the segment directions kept the old slope. Now
   directions are flattened and the game's `FindDirection` recomputes them, then `UpdateSegment`.
5. **Ramp lookup failed** (`Highway Ramp` vs prefab name `HighwayRamp`). *Lesson: log prefab
   names, not menu titles.*
6. **Squeezed / wedged smooth splits.** We only hid the close-up junction mesh; the far (LOD) junction
   mesh still draws. Node Controller also hides it in `CalculateGroupData`/`PopulateGroupData`.

General lessons: we have no game to test with, so each guess costs a round trip. What works best is
copying patterns from mods that are proven in game (Network Multitool, Node Controller, Move It,
Fine Road Tool), logging enough to diagnose from one test, and keeping changes small.

## 3. What the research says

### 3.1 How Cities: Skylines 2 builds roads

- A road prefab is a **list of sections across the road**, left to right: edge (sidewalk/curb),
  lanes, median, edge. Each section is made of **pieces** (a slice of cross-section with a width,
  surface heights, mesh, lanes and props).
- Every piece has **requirements** (flags) such as `Elevated`, `Tunnel`, `LowTransition`,
  `Intersection`, `Median`, `MedianBreak`, `Sidewalk`, `LevelCrossing`, tree/grass upgrades. Each
  edge and node gets composition flags and the game keeps the pieces whose requirements match.
  One road definition therefore renders correctly as ground, cut, elevated, tunnel, junction or
  upgraded, and **junctions are built from the same pieces with junction flags** (no curbs,
  medians or crosswalks where they don't belong, median noses at the junction).
- Upgrades (retaining wall, quay, grass, trees, elevated, tunnel) are just flags set on an edge.
- Elevation: shallow negative = cut road, deeper = tunnel, positive = elevated or bridge. Bridges use
  fixed segment patterns (suspension bridge: 88/76/76/88 m) so towers land in the right places.
- Lane connections through junctions are generated from the lanes automatically.

### 3.2 Road Builder (CS2 mod)

It builds a road type from a **list of lanes** chosen by the player. It clones existing vanilla
pieces, sets widths, swaps lane definitions, adds sidewalks, a median filler and edges, and generates
a new prefab at runtime from a saved config. It can't make new meshes, so it re-uses and stretches
vanilla pieces.

### 3.3 CS1 mods and what they teach

- **Network Multitool, Move It:** all edits on the simulation thread; per-segment variant from
  `NetAI.GetInfo(minElevation, maxElevation, ...)`; `Underground` below -8 m, `OnGround` on ground
  nodes; slope segments created from the underground end; `FindDirection` after creating or moving.
- **Fine Road Tool / Network Anarchy:** forced ground/elevated/bridge/tunnel modes by patching
  `GetInfo`; a cleanup pass that fixes tunnel entrances (slope vs tunnel, invert, Underground flag)
  after every change. Good template for us.
- **Node Controller:** corner offsets and "nodeless" segment ends through `CalculateCorner`, which
  also moves lanes and vehicle paths (they are built from the same corners). Hides junction meshes
  close up and in the LOD meshes.
- **Adaptive Roads:** custom flags on lanes, segments, nodes; per-lane "track" meshes drawn along
  lanes and through junctions following lane connections. This is how markings and medians can follow
  lanes in CS1.
- **Intersection Marking Tool:** draws lines and fills on the junction surface between lane ends; it
  works on the same layer we need for clean CS2-style junction surfaces and lane guide lines.
- **CSUR, Network Extensions 3:** custom road types and transition modules (e.g. 3 lanes to 3+1)
  are the only proven way to get clean merges and splits in CS1.
- **Why the LN lane tool was wrong:** separate one-lane roads can't change lanes between each other,
  make junctions wherever they meet, and squeezing a ramp mesh looks wrong. CS2 and CSUR solve the
  same problem with proper road types whose cross-section has the right lanes.

## 4. New direction

The user's decisions:

- Custom road types, Road Builder style.
- CS2-style junctions: flat asphalt inside the junction, sidewalks wrapping round the corners,
  medians ending with a nose before the junction, crosswalks only where wanted.
- Intersection Marking Tool as inspiration only (same drawing layer), not a marking tool.
- The new junction look applies to junctions the mod builds automatically, plus a tool for existing
  junctions.

### Plan

**Update:** the smooth split tool (SPL), the junction tool (JN, with the CS2 junction look and grade
separation) and the road builder (RB) have been removed from the mod. The plan is now to perfect the curve
tool (RR) and finish the features around it: building realistic roads and improving the existing network as
the curve crosses it. R2-R4 below are kept only as a record; their code is in the git history.

**R1 – Builder like the vanilla tool (small, now).**
- ~~Slope segments from underground to surface, OnGround flags, FindDirection after building or
  moving.~~ Done.
- Choose each segment's variant with the game's own `NetAI.GetInfo` (keep our overrides for the
  overpass deck and underpass cutting).
- A tunnel-entrance cleanup pass like Fine Road Tool's after each build.
- `TerrainModify.UpdateArea` after lowering roads.
- Hide smooth-split junction meshes in the LOD meshes too; clear per-node state when a node is released.

**R2 – CS2-style junctions (JN tool + automatic for RR junctions).**
- Hide the vanilla junction meshes at chosen nodes (close-up and LOD), as smooth splits already do.
- Draw our own junction surface: an asphalt polygon spanning the road ends' corners, curbs and
  sidewalks as fillets round each corner (radius from the roads' widths), and median noses where a
  road has a median. Meshes are generated at runtime from the segment corners, like Intersection
  Marking Tool's fills.
- Lane guide lines and stop lines from the lanes and lane connections (game or TM:PE), drawn on the same
  layer. Crosswalks optional per road end.
- Vehicle paths are unchanged (vanilla lane connections), so traffic keeps working.

**R3 – Road Builder for CS1.**
- A panel to compose a road from a list: sidewalk/curb, lanes (car, bus, bike, parking, tram), median
  (none, painted, raised, grass, trees), sidewalk, with direction per lane.
- At load, generate a `NetInfo` per saved config by cloning the closest vanilla road. Rewrite
  `m_lanes` (positions, widths, directions, types), the half-width, the elevated, bridge, slope and
  tunnel variants and the props (arrows, lights). Build the segment mesh procedurally from
  cross-section pieces (asphalt, curb, sidewalk, median), CS2 style. Saved in the city and recreated
  on load. Without the mod those roads are missing, so a "Convert to vanilla" button swaps them back to the road
  they were cloned from.
- The R2 junction renderer draws these roads' junctions too, so custom roads get CS2 junctions.
- Full design and milestones: `docs/R3_ROAD_BUILDER.md` in the git history.

**R4 – Merges and splits with proper road types.**
- Use R3 to generate the transition road types a split or merge needs (e.g. 3 lanes to 2+1, a 3+1
  weaving section), and join them at nodeless split nodes from SPL.
  Lanes then line up across the split because both sides come from the same lane list.

### Open questions for later

- Procedural meshes: CS1's road shader bends segment meshes along the Bezier. The generated mesh
  has to use the same vertex layout and the vanilla road textures (or generated ones). Prototype one
  piece (plain asphalt with curbs) first.
- Asset compatibility: custom roads should probably also export to a normal asset for sharing.

## 5. Code review follow-ups

From an outside code review (it assumed a standalone game; items that only apply there are left out:
Harmony patching, `Unity.Mathematics`/Burst and Addressables don't exist for a CS1 mod on Unity 5.6 / .NET 3.5).

Done:
- One README at the root (build, test and tool instructions); design docs live in `docs/`.
- The icon generator moved to `tools/`; the mod only embeds the generated PNGs, which are packed into one
  UI atlas at runtime.

To do, in priority order:
1. **Split drift:** when a crossing splits an existing curved road, the new pieces are rebuilt from end
   directions and can drift slightly from the original curve. Pick directions and node positions so the
   game's own middle points reproduce the original halves as closely as possible, and test it in CurveMath.
2. **Vertical curves:** heights are solved per node at a constant 6 % grade, so ramps have kinks where they
   start and end. Smooth the grade changes over a parabolic crest or sag curve.
3. **Preview telemetry:** show the tightest radius, the steepest grade and a cost estimate while drawing, and
   highlight pillars that would land on a road below.
4. **Planner tests:** RoadPlanner and CrossingFinder read the game's managers directly. Put terrain and
   network reads behind a small interface so crossing and lowering cases (back-to-back overpass and
   underpass, crossings at both ends, water plus a crossing) can be unit-tested like CurveMath.
5. **Plans as data:** the tools already hand a `Plan` to `NetworkBuilder`, which applies it on the
   simulation thread and keeps an undo record. Make the record a full diff (created and removed nodes and
   segments, moved nodes, flags) so redo becomes possible as well as undo.
6. **Transition curves:** clothoid (Euler spiral) easing for ramps and S-bends, so curvature changes
   gradually instead of jumping between pieces.

## 6. Player-side review (October 2026): what is left

Fixed from that review: undo only touches roads it built (build indices), cutting a road can't lose it, curves can
start/end mid-road (T-junctions), construction is charged and refunded on undo, buildings in the way are warned
about, short end segments, preview recomputed only on change, per-network ramp grade (rail 3.5 %), water without a
bridge version, very tall decks, CS2 junction look limited to new street junctions (setting), quieter logging, an
on-screen warning for road builder roads a save can't load.

Still open, in rough priority order:
1. **Road builder roads in saves.** They need a load-time hook that works with Loading Screen Mod; until then they
   are missing when a city loads (the player is warned). Then "Convert to vanilla" (R3.7) before any public release.
2. **Registering a road type mid-game** (`RoadTypes.CreateNow`) runs while the simulation reads the prefab lists;
   needs testing in game, or registration only between loads.
3. **Collisions:** remove (and pay for) growables in the way like the vanilla tool, refuse on service buildings;
   detect a new road lying along an existing one; trees.
4. **TM:PE settings** are lost on roads we cut; keep them through TM:PE's API.
5. **Picking the road inside RR**, and RR as a mode in the vanilla road tool's options panel.
6. **Height per control point** (Page Up / Down) for elevated interchanges; arc and parallel modes.
7. **Elevation profile strip** (height against ground, grades, clearances), cost and tightest radius at the cursor,
   clearance on each crossing icon; CS2-style guides (lengths, angles, snapping).
8. **Realism:** design speed sets the minimum radius, construction time (road closed for N days), smooth vertical
   curves, warnings for junctions too close together or at sharp angles.
9. **CS2 junctions:** the road's own asphalt texture (drop the shade setting) and drawing them far away too.
10. Memory: stretched meshes and copied lane props are never freed; redo; settings for hotkeys, grade and spacing.
11. Check in game: Esc (CS1 may switch tools on the first press).
