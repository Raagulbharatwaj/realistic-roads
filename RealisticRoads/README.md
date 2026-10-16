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

## 5. Test the curve tool (milestone 2)
1. Load a city. A small **RR** button appears near the top-left (right-drag it to move it).
2. Optionally pick a road in the vanilla Roads menu first; the log shows which network the tool will use.
3. Click **RR** or press **Ctrl+Shift+C**. A white circle follows the mouse over the ground.
4. Left-click a few times: each click leaves a blue circle and logs `Point N at (x, y, z)`.
5. Right-click removes the last point (`Removed last point, N left`).
6. Press **Esc**, or click **RR** / press Ctrl+Shift+C again: the circles disappear and the normal tool is back.

Expected log lines:

    [RealisticRoads] Curve tool ready: click the RR button or press Ctrl+Shift+C
    [RealisticRoads] Curve tool on, network = Basic Road
    [RealisticRoads] Point 1 at (123.4, 60.2, -456.7)
    [RealisticRoads] Removed last point, 0 left
    [RealisticRoads] Esc pressed, back to the vanilla tool

Nothing is built yet; that comes in milestone 4.

## 6. Test smooth splits (milestone S1)
1. Load a city with a road that splits in two (e.g. a one-way road splitting into two one-way roads).
2. Click **SPL** (below RR) or press **Ctrl+Shift+J**.
3. Hover over junctions. The circle shows: **green** = smoothed, **yellow** = a split that can be smoothed, **red** = not a split.
   On a yellow/green junction, small blue dots show where the road edges will start.
4. Click a yellow junction: it turns green and the junction should become a taper with a pointed gore.
   The log shows `Smooth split ON at node #...` with the branch angle and how far from the node the branches start.
5. Click it again to go back to the vanilla junction. Clicking a red one logs why it is not a split.
6. Watch a few cars drive through, then save, quit, reload the city and check the shape is still there.

Screenshots of before/after help a lot: the shape is computed, and the numbers may need tuning.

## 7. What's next
See the milestones in [docs/PROJECT_BRIEF.md](../docs/PROJECT_BRIEF.md). Next up is milestone 3:
a live spline/arc preview between the points, showing length and turning red where a bend is too sharp.
