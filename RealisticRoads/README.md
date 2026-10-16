# Realistic Roads – CS1 mod

## 1. Install the tools (one time)
1. Install **Visual Studio 2022 Community** (free) with the ".NET desktop development" workload.
   (Or just the **.NET SDK 8** if you prefer VS Code.)
2. In Steam, subscribe to **Harmony** (Workshop item by boformer, "Harmony 2.x"). Your mod needs it at runtime.

## 2. Point the project at your game
Open `RealisticRoads.csproj` and check this line matches where CS1 is installed:

    <GameDir>C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines</GameDir>

(Steam > right-click Cities: Skylines > Manage > Browse local files shows the real path.)

## 3. Build
- Visual Studio: open `RealisticRoads.csproj`, press **Ctrl+Shift+B**.
- Command line: `dotnet build` inside this folder.

The build copies `RealisticRoads.dll` + `CitiesHarmony.API.dll` to:

    %LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Mods\RealisticRoads

## 4. Test in game
1. Close the game before every build (Windows locks loaded DLLs).
2. Launch CS1 > **Content Manager > Mods** > tick **Realistic Roads**.
3. Load a city, press **F7** to open the debug panel.
4. Draw a road. You should see `[RealisticRoads] Road segment created: #1234`.
5. Options > Mod Settings > Realistic Roads > "Say hello" also logs a message.

What the log should contain (milestone 1 sign-off):

    [RealisticRoads] Enabled, version 0.1.x.x
    [RealisticRoads] Patched NetManager.CreateSegment(UInt16& segment, ...)   (one line per overload)
    [RealisticRoads] Harmony patches applied
    [RealisticRoads] City loaded, mode = LoadGame
    [RealisticRoads] Road segment created: #1234

If any line is missing, or you see `Applying Harmony patches failed`, paste every
`[RealisticRoads]` line (plus any exception under it) from `output_log.txt` back into the issue/chat.

Full log: `C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\output_log.txt`
(search for `[RealisticRoads]`).

Tip: launch with `-noWorkshop` in Steam launch options to test with only local mods
(remove it afterwards, or Harmony from the Workshop won't load).

## 5. Curve tool: build CS2-style curves (milestones 3 + 4)
1. Pick a road in the vanilla **Roads** menu (e.g. Basic Road or Oneway Road), then click **RR** or press **Ctrl+Shift+C**.
   The panel under the buttons shows the road, length and segment count.
2. Click points on the ground. The curve follows the mouse as a road-width band through all of them:
   **green** = can be built, **red** = a bend is too tight (spread the points out).
3. Clicking on (or near the end of) an existing road snaps to it (yellow circle) so the new road connects there,
   at the start or the end of the curve. Where it can, the curve continues that road smoothly ("continues the road"
   in the panel); otherwise it meets it at an angle like a normal junction.
   **Splits:** on the end of a road at least ~1.3x wider than the road you are drawing, the snap circle jumps to the
   **left or right half** under your cursor. The new road starts from that half, runs parallel for a short way, then
   curves off. Attach a second branch to the other half and the smooth split is applied automatically.
   **Crossings:** where the curve crosses an existing road or track, a ring marks the crossing and a row of
   **icons** above it picks what happens (selected one is green; our road is yellow in the icons):
   - **junction** (default): junction, or level crossing for road x rail; passes over/under an elevated or
     underground road;
   - **bridge**: our road goes over on a flyover (9 m clearance), the existing one is untouched. **+/-** sets the height;
   - **overpass**: the existing road is lowered 8 m into a cutting (ramping down along it at 6 %), ours crosses at
     ground level on a bridge deck. Needs ~140 m of plain road (no junction) on each side of the crossing;
   - **underpass**: the existing road gets a short bridge, ours is dug 8 m down into a cutting under it;
   - **tunnel**: ours goes 12 m under through a tunnel with proper portals.
   Over water the preview shows **+/-** at the middle of the river: the bridge deck is 10 m above the water by
   default, 5 m per click (up to +60 m) so ships fit. Bridges over water use ~90 m spans.
   The panel lists each crossing and water bridge with its height/depth; problems are listed with "!" and the
   curve won't build until they are fixed. Preview colours: green ground, blue raised, orange lowered, red too sharp.
4. Points can go over trees and water. Over water the preview turns **blue**: that part is built as the road's
   bridge, 10 m above the water, with elevated ramps (pillars) on land at a gentle 6 % slope.
5. **Enter** or **double-click** builds it. Right-click removes the last point, **Esc** clears, Esc again leaves the tool.
6. **Ctrl+Z** (with the tool open) removes the last curve you built, up to 20 back.

Notes for this version: it does not charge money yet and does not bulldoze trees/buildings in the way.
Bridges work; flyovers, tunnels, cuttings and level crossings are new: check them and send screenshots.
Ctrl+Z undoes a curve including any junctions/bridges it put into existing roads (those segments are rebuilt, so
TM:PE settings or custom names on them are lost). Long curves are cut into ~40 m segments (shorter in tight bends).

## 6. Test smooth splits (milestone S1)
1. Load a city with a road that splits in two (e.g. a one-way road splitting into two one-way roads).
2. Click **SPL** (below RR) or press **Ctrl+Shift+J**.
3. Hover over junctions. The circle shows: **green** = smoothed, **yellow** = a split that can be smoothed, **red** = not a split.
   On a yellow/green junction, small blue dots show where the road edges will meet the trunk.
4. Click a yellow junction: it turns green, the junction block and its crosswalks should disappear, and the two
   branches should run back into the trunk side by side, with a gore nose where they part.
   The log shows `Smooth split ON at node #...` with the branch angle and each branch's share of the trunk.
5. Click it again to go back to the vanilla junction. Clicking a red one logs why it is not a split.
6. Watch a few cars drive through, then save, quit, reload the city and check the shape is still there.

Screenshots of before/after help a lot: the shape is computed, and the numbers may need tuning.

## 7. What's next
See the milestones in [docs/PROJECT_BRIEF.md](../docs/PROJECT_BRIEF.md). Next up is milestone 3:
a live spline/arc preview between the points, showing length and turning red where a bend is too sharp.
