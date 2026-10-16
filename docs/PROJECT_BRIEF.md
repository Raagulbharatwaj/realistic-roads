# CS1 Road Builder Mod – Project Brief

Oct 16, 2026 · @Raagul

## Overview

We are building a C# mod for Cities: Skylines 1 (CS1) that brings CS2-style road building to CS1. It has two goals:

1. **Complex curves.** Draw long, smooth roads (arcs, S-curves, spirals, freehand splines) in one action, the way CS2 allows, instead of CS1's one-curve-per-segment limit.
2. **Realistic flyovers.** Build elevated roads and monorail/metro tracks directly above an existing road, with the support pillars standing on the road below (on its median or centre line), like flyovers and metro lines in Indian and other real-world cities.

The mod is played on Windows through Steam and must coexist with popular mods (Harmony, Move It, Network Anarchy, TM:PE, Node Controller Renewal).

## Background: how CS1 roads work

Every CS1 road is a graph of nodes (points) joined by segments. Each segment is one cubic Bézier curve defined by its two end nodes plus a start and end direction. That is why the vanilla tool can only draw a single gentle curve at a time.

Constraints the mod must respect:

- **Curvature limit.** A single segment cannot bend sharply; tight curves must be split into many short segments.
- **Segment length.** The game splits long segments automatically (roughly 96 m max per segment for most roads); the mod should place its own nodes at shorter, even spacing.
- **Network variants.** Each road type (`NetInfo`) has ground, elevated, bridge, slope and tunnel versions, reached through its AI (for example `RoadAI.m_elevatedInfo`, `m_bridgeInfo`).
- **Pillars.** Elevated and bridge networks spawn pillar buildings at nodes (`m_bridgePillarInfo`, `m_middlePillarInfo` on the bridge AI). Pillars are placed automatically and collide with roads below, which is why vanilla CS1 refuses to put pillars on a road.
- **Collision and clearance.** The game blocks overlapping networks; building over a road needs either enough height or collision rules bypassed (Network Anarchy does this today).
- **Threading.** Network changes must run on the simulation thread (`SimulationManager.instance.AddAction`), not the UI/render thread, or the game can crash or corrupt saves.

## Current state

A starter project exists (`RealisticRoads/`) and has not yet been compiled against the game.

| File | What it does |
|---|---|
| `RealisticRoads.csproj` | SDK-style project targeting `net35`. References `Assembly-CSharp`, `ColossalManaged`, `ICities`, `UnityEngine` from `Cities_Data\Managed` (not copied). Uses NuGet `CitiesHarmony.API` 2.2.0. A post-build step copies the DLLs to `%LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Mods\RealisticRoads`. |
| `Mod.cs` | `IUserMod` entry point (name, description, settings page with a test button). Applies Harmony patches on enable via `HarmonyHelper.DoOnHarmonyReady`. `Loading : LoadingExtensionBase` logs when a city loads. `Mod.Log()` writes to `output_log.txt` and the F7 debug panel. |
| `Patcher.cs` | Applies and removes all Harmony patches. Example Postfix on `NetManager.CreateSegment` logs every new segment. |
| `README.md` | Build and in-game test steps. |

Runtime dependency: the Harmony 2 Steam Workshop item must be subscribed.

## Features and requirements

### A. Smooth curve tool (CS2-style)

- A new tool, opened from a button in the roads panel or a hotkey (default Ctrl+Shift+C), that uses whichever road/track the player last selected in the vanilla Roads menu.
- Modes:
  - **Spline:** click any number of control points; a Catmull-Rom (or chained Bézier) spline passes through them. Right-click removes the last point, Enter or double-click builds, Esc cancels.
  - **Arc:** start point, end point, then drag to set radius. Show the radius in metres.
  - **Parallel offset:** draw a guide curve, build the road offset left/right by N metres.
- Live preview while placing: the curve drawn as an overlay (`RenderManager.OverlayEffect`), green when buildable, red where it is blocked, with total length and estimated cost.
- Height: Page Up / Page Down change elevation in 3 m steps, like vanilla. Optional smooth slope from start height to end height.
- Building: split the curve into segments of even length (setting, default 40 m, min 16 m), check each segment's bend is within the game's limit, then create nodes and segments in one simulation-thread action. Connect to existing nodes/segments at the start and end when snapped.
- Snapping: to existing nodes, to existing segments (splits them), to grid and to 15° angles (toggle).
- Undo: one undo removes the whole curve.

### B. Flyovers with pillars on the road below

- Build an elevated road or elevated monorail/metro/train track directly over an existing ground road, following it.
- Follow mode: the player clicks a start and end point on an existing road; the elevated network copies that road's path (same curve) at a chosen height (default 12 m road, 15 m rail).
- Pillar placement: vanilla pillars are suppressed for that network. The mod then places pillars itself on the centre line of the road below, every N metres (setting, default 32 m), lined up with the elevated nodes.
- Pillar styles: choose from vanilla pillars, a slim single-column pillar, and any pillar asset the player has (pillar buildings/props from the Workshop).
- Traffic below keeps working: pillars must not block lanes. On roads without a median, pillars go on the centre line and the mod warns the player if lanes would be covered. No collision between the pillars and the road below.
- Ramps: optional on/off ramps from the flyover down to the ground road using the curve tool (Feature A).

### C. Settings page

- Segment spacing, pillar spacing, default heights, default pillar style, hotkeys, show/hide preview info, debug logging on/off.

## Technical design

### Proposed classes

| Class | Responsibility |
|---|---|
| `Mod` / `Loading` | Entry point, settings UI, create the tool and UI button on `OnLevelLoaded`, clean up on `OnLevelUnloading`. |
| `CurveTool : ToolBase` | Mouse input, raycasting to terrain/networks, control points, preview overlay, triggers the build. |
| `CurveMath` | Pure math, no game types beyond `Vector3`: spline evaluation, arc-length sampling, splitting into even segments, tangent at each split, bend-angle check. Unit-testable outside the game. |
| `NetworkBuilder` | Takes a list of points + tangents + `NetInfo`; creates nodes and segments on the simulation thread; returns the created IDs for undo. |
| `FlyoverTool : ToolBase` | Picks a path along an existing segment chain, builds the elevated copy via `NetworkBuilder`, then calls `PillarPlacer`. |
| `PillarPlacer` | Removes/suppresses auto pillars for the mod's segments and places chosen pillar buildings on the road below at even spacing. |
| `UndoStack` | Records created/removed node and segment IDs per action. |
| `Settings` | Values saved to XML in the mod folder (or via `SavedInt`/`SavedFloat`). |

### Key game APIs

- `NetManager.instance.CreateNode(out ushort node, ref Randomizer r, NetInfo info, Vector3 position, uint buildIndex)`
- `NetManager.instance.CreateSegment(out ushort segment, ref Randomizer r, NetInfo info, ushort startNode, ushort endNode, Vector3 startDir, Vector3 endDir, uint buildIndex, uint modifiedIndex, bool invert)`
- `NetManager.instance.ReleaseSegment` / `ReleaseNode` for undo.
- `ToolBase.RayCast` with `RaycastInput` for mouse-to-world; `ToolsModifierControl.SetTool<T>()` to activate.
- `ColossalFramework.Math.Bezier3` for segment curves; `NetSegment.CalculateMiddlePoints` to get Bézier control points from directions.
- `SimulationManager.instance.AddAction(...)` for all network changes; `Singleton<SimulationManager>.instance.m_currentBuildIndex` for build indices.
- Pillar control: Harmony patch on the bridge/elevated AI's pillar placement (e.g. `RoadBridgeAI` / `TrainTrackBridgeAI` / `MonorailTrackAI`) so segments the mod owns skip vanilla pillars; place new ones with `BuildingManager.instance.CreateBuilding`.

### Rules

- Target .NET Framework 3.5 and C# features that compile to it (no async, no `ValueTuple` without a polyfill).
- Never touch game state from the UI thread; queue it on the simulation thread.
- Patch narrowly and guard every patch so an exception logs and falls back to vanilla instead of crashing.
- Do not break saves: if the mod is removed, everything it built must remain valid vanilla networks and buildings.

## Milestones and acceptance criteria

Build in this order; each milestone must work in game before the next starts.

| # | Milestone | Done when |
|---|---|---|
| 1 | Starter mod builds and loads | Mod appears in Content Manager, F7 shows the segment log when a road is drawn, no errors in `output_log.txt`. |
| 2 | Tool skeleton | Button/hotkey activates `CurveTool`; clicks place control points shown as circles; Esc returns to the vanilla tool. |
| 3 | Curve preview | Spline and arc previews follow the mouse, show length, turn red where the bend is too sharp. |
| 4 | Build the curve | Enter builds the curve with the selected road; vehicles drive it end to end; it connects to existing roads at snapped ends. |
| 5 | Undo, slopes, settings | One undo removes a whole curve; start/end heights work; settings save and reload. |
| 6 | Flyover follow mode | Elevated road/monorail built over 500 m of an existing curved road, matching its path. |
| 7 | Pillars on the road | Pillars stand on the centre line/median of the road below at the set spacing, no vanilla pillars, ground traffic unaffected, save reloads with pillars intact. |
| 8 | Polish and release | Works alongside Move It, Network Anarchy, TM:PE, Node Controller Renewal; Workshop page, preview image, README. |

## Testing, constraints and notes for the coding agent

### How to test

1. Close the game, run `dotnet build` (or Ctrl+Shift+B in Visual Studio). The DLL is copied to the local Mods folder.
2. Start CS1, enable the mod in Content Manager, load a test city (a flat map with a few straight and curved roads).
3. Watch F7 and `Cities_Data\output_log.txt` (search `[RealisticRoads]`).
4. Put `CurveMath` in a separate test project (xUnit/NUnit, can target a newer .NET) so the curve maths is tested without the game.
5. Before each milestone sign-off: save, quit, reload the city and confirm the networks and pillars survive.

### Constraints

- The agent cannot run the game. Every change must compile cleanly and log enough detail that the player can paste the log back to diagnose problems.
- Game DLLs are not in the repo and must not be committed; paths come from the `GameDir` property in the `.csproj`.
- Keep each patch and feature small; one milestone per pull request.

### Non-goals (for now)

- Custom road meshes or new road assets (use existing roads and Workshop assets).
- Traffic AI changes; leave that to TM:PE.
- Mac/Linux-specific builds.

### Reference mods to study

- **Network Multitool:** arcs, slopes and segment creation code.
- **Move It:** undo handling and safe network edits.
- **Network Anarchy / Fine Road Anarchy:** collision and height bypass.
- **Network Skins 2:** how pillars are swapped or removed per segment.
- **Parallel Road Tool:** offset curves.

Most are open source on GitHub; read them for patterns, and do not copy code without checking each project's licence.
