# R3 – Road Builder for CS1

Design for milestone R3 from [RETROSPECTIVE.md](RETROSPECTIVE.md). R3 lets the player put a road together from a list of
pieces (sidewalks, lanes, medians), as CS2's Road Builder does, and the mod turns that list into a real CS1 road type.
R4 (merges and splits with transition roads) is built on top of it.

## 1. Goal

The player opens a panel and builds a cross-section from left to right, for example:

```
| sidewalk 3 m | bike 2 m | car ← | car ← | grass median 3 m | car → | car → | bus → | sidewalk 3 m |
```

They name it and press **Create**. The road then:

- can be drawn with RR, and with the vanilla road tool;
- drives, parks, stops and zones like a vanilla road with the same lanes, and works with TM:PE;
- has elevated, bridge, slope and tunnel versions, so RR's bridges, flyovers, cuttings and tunnels work with it;
- gets CS2-style junctions from R2 and smooth splits from SPL;
- loads again with the city, and survives a game restart.

"Done" for R3 means a six-lane road with a median and a bus lane can be built across a river and through a tunnel, and
it looks as clean as a vanilla road. It must keep working after a save and a reload.

## 2. Prior art

| Mod | What it does | What we take |
|---|---|---|
| **Road Builder (CS2)** | The player picks lanes from a list. It clones vanilla pieces, stretches them to the chosen widths, and generates the prefab at runtime from a saved config. | The model: config → generated prefab, configs saved in a folder *and* in the save. |
| **Road Builder (CS1, by T.D.W., Workshop 2891132324)** | Already exists for CS1: lane-based roads with per-lane width, height and speed, and "filler" lanes (grass, gravel, pavement). It appears to build on Adaptive Networks. | Study it before coding (UI, lane types, limits; its changelog mentions a ~250-lane limit while generating). I could not open its page from here, so its details are unconfirmed. |
| **Network Extensions 2** | Generates new road types at load by cloning vanilla `NetInfo`s and changing lanes, meshes and textures. | Cloning, the load hook, and variants (elevated, bridge, slope, tunnel). |
| **CSUR** | Real assets with fixed lane positions, plus transition modules (e.g. 3 to 3+1). | Fixed lane grid so transitions line up (R4). |
| **Network Skins 2** | Per-segment props, trees and textures stored in the save; the prefab stays vanilla. | Saving our data next to the network data without changing prefab names. |
| **Adaptive Networks** | Per-lane meshes ("tracks") and custom flags. | Drawing lane markings along lanes and through junctions (later, optional). |

**Build or use T.D.W.'s Road Builder?** I recommend building our own, while staying compatible with theirs (no name
clashes, no patches on the same methods). Three reasons:

- R2's junction renderer and R4's transitions need to read the lane list directly.
- R4 needs to generate road pairs from code, not by hand in a panel.
- We want the CS2 look (flat junctions, sidewalk fillets) to match the road meshes.

If their mod turns out to cover most of this and can be driven from code, R3 shrinks to an integration layer. **Step 0
of R3 is to read their mod and decide.**

## 3. What a CS1 road type is made of

A road type is a `NetInfo` (a Unity component on a prefab GameObject). The fields R3 has to generate:

- **Cross-section**
  - `m_halfWidth`: the road's half width. It drives collision, zone blocks, and the mesh scale.
  - `m_pavementWidth`: the sidewalk width.
  - `m_surfaceLevel`: the road surface height.
- **Lanes:** `m_lanes`, an array of `NetInfo.Lane`. Each lane has these fields:
  - `m_position` and `m_width`: across the road, measured from the centre.
  - `m_verticalOffset`: about 0.25 m for sidewalks.
  - `m_laneType`: Vehicle, Pedestrian, Parking, TransportVehicle (bus/tram) or None (decoration: median trees).
  - `m_vehicleType`: Car, Bicycle, Tram, ...
  - `m_direction`: Forward, Backward, Both.
  - `m_stopType`, `m_speedLimit`, `m_allowConnect`.
  - `m_laneProps`: the props drawn along the lane (arrows, lights, signs, trees). Each prop has rules for where it
    goes: offsets, repeat distance, and flags such as traffic lights at the lane's start or end.

  The game fills in the derived fields (`m_finalDirection`, `m_similarLaneIndex`, ...) in `InitializePrefab`.
- **Meshes**
  - `m_segments`: segment meshes with flags required or forbidden (e.g. a bus-stop version).
  - `m_nodes`: junction and end meshes with node flags required or forbidden.
  - Each mesh has a material, a LOD mesh and a LOD material.
- **AI:** a `RoadAI` (ground), `RoadBridgeAI` (elevated and bridge) or `RoadTunnelAI` (slope and tunnel).
  - It links the variants: `m_elevatedInfo`, `m_bridgeInfo`, `m_slopeInfo`, `m_tunnelInfo`.
  - The bridge AI also sets pillars: `m_bridgePillarInfo`, `m_middlePillarInfo`.
  - It also holds traffic lights, highway rules, zoning, costs and noise.
- **Class and limits:** `m_class` (`ItemClass`: service, sub-service, level), `m_connectGroup`, `m_maxSlope`,
  `m_minHeight`, `m_maxHeight`, and terrain flags (`m_flattenTerrain`, `m_createPavement`, `m_clipTerrain`, ...).
- **Menu:** `m_UICategory`, `m_UIPriority`, the thumbnail and its atlas.

Rendering facts we already rely on for R2's junction renderer:

- Segment meshes are bent along the Bezier by the `Custom/Net/Road` shader (left/right control matrices).
- The shader scales the mesh across the road by `m_halfWidth`. A mesh built for a 16 m road therefore stretches to fit
  a 24 m road. CS2's Road Builder relies on the same trick, and so will we for tunnels and slopes.
- Surfaces use the road material's `_MainTex`, `_XYSMap` and `_APRMap` (alpha, pavement, road). `_APRMap` decides which
  parts are tinted as asphalt and which as pavement.

## 4. Design

### 4.1 The road config (data)

One config per road type. It is plain data, with no Unity types, so it can be tested and saved anywhere:

```
RoadConfig
  Id          stable id (Guid); the prefab name is built from it, never from the display name
  Name        "6-lane avenue with bus lanes"
  Pieces      left to right; each piece has:
    Kind        Sidewalk | Curb | CarLane | BusLane | BikeLane | TramLane | Parking | Median | Shoulder
    Width       metres (defaults per kind: car 3.0, bus 3.25, bike 2.0, parking 2.5, sidewalk 3.0)
    Direction   Forward | Backward | Both                 (lanes only)
    Median      Painted | Raised | Grass | Trees           (medians only)
  SpeedLimit  km/h
  Options     zoning on/off, highway rules, traffic lights, noise barriers (elevated), lights left/right/median
  Version     for loading older configs
```

Configs are saved as XML (`XmlSerializer` is available on .NET 3.5) in two places:

- the library, `%LOCALAPPDATA%\Colossal Order\Cities_Skylines\RealisticRoads\Roads\<Id>.xml`, so every city can use them;
- a copy in each city that uses them (`SerializableDataExtensionBase`, like the JN junction data), so a shared save
  can bring its roads along.

### 4.2 Cross-section composer (pure, unit-tested)

`CrossSection.Compose(config)` turns the piece list into numbers. It contains no game code.

- **Width and positions**
  - The half width is half the sum of the widths.
  - Each piece's centre is placed from the left edge, then shifted so the road's centre is 0.
  - The road is not required to be symmetric; vanilla asymmetric roads exist.
- **Pavement:** the pavement width comes from the outer sidewalks. When the two sides differ, use the wider one and
  log it.
- **Lanes:** one lane per lane piece. Sidewalk pieces get a pedestrian lane. A median with trees gets a decoration
  lane.
- **Checks**
  - At least one lane.
  - Widths in range.
  - Lanes that face each other need a median or a painted line between them.
  - A bus stop needs a sidewalk beside the lane.
  - Lane count is under the game limit (to find out; see T.D.W.'s ~250 note).
- **Level and class**
  - The level is picked from the vehicle lane count: like vanilla small, medium and large roads, or highway when it
    has no sidewalks.
  - The class (and therefore the menu tab and connect rules) is copied from the base road.
- **Base road:** the vanilla road closest in width and features, used as the source for cloning (4.3).
- **Mesh layout:** the list of mesh pieces across the road (asphalt, curb, sidewalk, median) with their x ranges and
  heights. The mesh builder (4.4) reads this list.

This is where most of the logic lives, so it is tested like CurveMath:

- widths add up;
- lanes are mirrored for a symmetric road;
- directions are right for left- and right-hand traffic;
- parking and bus lanes sit on the outside;
- bad configs give clear errors.

### 4.3 Prefab generator

`RoadFactory.Create(config, crossSection)` builds the `NetInfo`s on the main thread during loading.

1. **Clone the base road's ground prefab:** `Object.Instantiate(base.gameObject)`, renamed to `RR.<Id>`, kept inactive
   and parented to a hidden object (as NE2 does).
2. **Replace `m_lanes`.** Copy the closest base lane of the same type for each new lane, then set its position, width,
   direction, speed and props. Copying keeps the lane flags and prop rules the game expects.
3. **Set the cross-section fields:** `m_halfWidth` and `m_pavementWidth`. Scale costs by width, and scale noise by
   lane count.
4. **Replace the meshes** with generated ones (4.4).
5. **Variants:** repeat steps 1–4 for the elevated, bridge, slope and tunnel versions of the base road.
   - Link them through the new AI's fields.
   - Keep the base road's pillars.
   - The bridge version reuses the elevated mesh, because RR only builds elevated pieces anyway.
6. **Register** all of them with `PrefabCollection<NetInfo>.InitializePrefabs("RealisticRoads", infos, null)`.
   Registration runs once per config, and before the city's networks are bound to prefabs by name (4.6).

### 4.4 Meshes and textures

Build the meshes in stages, so each stage can be checked in game before the next one starts.

1. **Stretched base meshes (first version).** Use the base road's meshes as they are; the shader stretches them to the
   new width. Lane markings and sidewalks won't line up with the new lanes, but this proves the lanes, the variants,
   and saving and loading before any mesh work starts.
2. **Generated ground segment mesh.**
   - The mesh layout from 4.2 becomes a mesh: a strip per piece, with vertical faces for curbs and raised medians.
   - Use the same vertex layout as the base road's segment mesh: read its bounds, UV ranges and colour channels at
     runtime. This means length along z, width along x, and the vertex count per metre.
   - It uses the base road's material and textures, with UVs pointing at the asphalt, curb and pavement parts. The
     `_APRMap` is generated to match.
   - Lane lines (dashed, solid, double yellow at a two-way divide, bus-lane colour) are painted into a generated
     texture strip, or drawn as thin decal quads. The renderer from R2 already uses this shader and material setup.
3. **Node meshes.**
   - **End nodes and bends:** reuse the segment mesh, as vanilla does.
   - **Junctions:** a plain flat-asphalt node mesh with sidewalk corners. It is used for junctions without the CS2
     look, and for the far (LOD) view of junctions that have it.
   - **CS2-style junctions:** drawn by R2's renderer, which reads the lanes and pavement widths of each road end.
4. **LOD meshes.** The same layout, built with fewer vertices.
5. **Elevated deck.** The generated cross-section plus a deck underside and barriers on both edges. Pillars come from
   the base road.
6. **Slope and tunnel.** Keep the stretched base meshes. Tunnel walls stretch well, and the roadway inside is mostly
   hidden.

### 4.5 Props

- Copy the prop rules from the base road's lane of the same kind. These are street lights, traffic lights and signs
  on the outer sidewalk lanes, and arrows on vehicle lanes. Then move them to the new lane positions.
- Lights go on the side or sides chosen in the options, or on the median.
- Trees on a tree median use a decoration lane, with the base road's tree props.
- Bus-stop props come from the base road's bus-stop lane rules.

### 4.6 Loading, saving, and life without the mod

- **Load order.** Generated road types must exist before the city's networks are bound to prefabs by name. Otherwise
  the game reports missing assets.
  - NE2 creates its prefabs in a hook during prefab initialisation when a level loads. We do the same, using every
    config in the library.
  - The exact hook still has to be checked against NE2's source, and logged in game.
- **A city whose road isn't in the library** (shared saves).
  - Our data in the save is only readable after the networks are loaded, which is too late for that load.
  - So on load we copy the city's configs into the library, and tell the player to reload the city once.
- **Without the mod,** a save that uses generated roads has missing networks; CS1 can't draw roads it doesn't know.
  - We give the player a **Convert to vanilla** button. It swaps every generated segment back to its base road, the way
    Move It swaps networks, so the city can be shared or the mod removed.
  - The retrospective's earlier claim that roads "fall back to the vanilla road" without the mod is wrong. This button
    is the honest version of it.
- **Changing a config later.**
  - Lane changes rebuild the road type, and existing segments are recreated on the simulation thread to get their new
    lanes.
  - Pure looks (median type, lights) only update the prefab.

### 4.7 Panel

- An **RB** button below JN, with a hotkey (Ctrl+Shift+B).
- **A preview across the top:** the cross-section to scale. Pieces can be dragged to reorder, and widths changed with
  +/- or by dragging an edge. The preview shows the total width and the lane count.
- **A piece list below it.** Click a piece to change its kind, width, direction or median type. Add and remove buttons
  sit on each side. A **Mirror** button copies one side's layout onto the other.
- **Presets** to start from: 2-lane street, 4-lane with median, 6-lane avenue with bus lanes, one-way with bike lane,
  highway 3+3.
- **Create** / **Update** builds the road type and selects it for RR and the vanilla road tool.
- **Library:** list, rename, duplicate, delete. Delete is blocked while the city uses that road.
- **Errors** from the composer show live, as RR's problems do.

### 4.8 How it fits the other tools

- **RR** draws any network, so it can draw generated roads at once. The RR info panel gets a road picker, so a road can
  be chosen without the vanilla menu.
- **JN (R2)** reads lanes and pavement widths from the `NetInfo`, so generated roads get CS2-style junctions. Their
  generated node mesh also makes the far view match.
- **SPL and R4** use the composer to generate transition roads. A 3-lane road splitting into 2+1 needs the trunk, the
  two branches, and optionally a weaving section, each with lanes at matching x positions. R4 calls
  `CrossSection.Compose` with those lists and joins the results at a smooth-split node.

## 5. Milestones

Each milestone is one PR, and each ends with something to check in game.

| # | Milestone | Check in game |
|---|---|---|
| R3.0 | Read T.D.W.'s Road Builder and NE2; confirm the load hook and how lanes are cloned. Decide build vs. integrate. | Nothing to check; a short note in this doc. |
| R3.1 | `RoadConfig`, `CrossSection.Compose`, XML save/load, unit tests. | Nothing to check (tests only). |
| R3.2 | `RoadFactory`: clone + new lanes + stretched base meshes + variants; created at load from the library; RR road picker. | Build a 3+1 road; cars and buses use the right lanes; bridge and tunnel build; save, quit, reload. |
| R3.3 | Generated ground segment and LOD meshes with lane lines; generated junction node mesh. | Markings line up with traffic; sidewalks and curbs at the right width; far view. |
| R3.4 | Generated elevated deck; slope/tunnel tuning. | RR bridge over water and a flyover on the new road. |
| R3.5 | Props: lights, arrows, traffic lights, signs, median trees, bus stops. | Traffic lights at junctions; arrows match lanes; bus stop works. |
| R3.6 | RB panel, presets, library, live errors. | Make a road from scratch in the panel. |
| R3.7 | Thumbnails and vanilla menu entry, Convert to vanilla, config changes on built roads, city configs copied to the library. | Share a save to a fresh game folder; uninstall path. |

## 6. Risks and open questions

- **Load hook timing.** This is the riskiest part. If prefabs are created too late, every load reports missing
  assets. Prove it in R3.2 with the stretched meshes before investing in meshes.
- **Mesh vertex layout.** We don't know yet which vertex colours, tangents and UV2 the road shader expects. Read them
  from vanilla meshes at runtime and log them in R3.2.
- **Lane limits and performance.** Generated prefabs add draw calls and memory, so stay well under the game's prefab
  and lane limits.
- **Mod compatibility.**
  - TM:PE stores settings per lane index, so changing a built road's lanes resets them. The panel should warn.
  - Node Controller and Adaptive Networks may also touch the same junctions.
- **Asset sharing.** Exporting a generated road as a normal Workshop asset would remove the "needs this mod" problem,
  but CS1 has no public API for writing `.crp` files. This is out of scope for now.

## 7. Out of scope for R3

- Lane markings that follow lane connections through junctions (Adaptive Networks style). That belongs with R2.
- Tram wires, monorail and train tracks. These are road pieces only for now. Tram lanes come later, if the copied
  tram lane props work.
- Custom textures beyond generated lane lines.
