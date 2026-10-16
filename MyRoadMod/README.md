# My Road Mod – CS1 starter

## 1. Install the tools (one time)
1. Install **Visual Studio 2022 Community** (free) with the ".NET desktop development" workload.
   (Or just the **.NET SDK 8** if you prefer VS Code.)
2. In Steam, subscribe to **Harmony** (Workshop item by boformer, "Harmony 2.x"). Your mod needs it at runtime.

## 2. Point the project at your game
Open `MyRoadMod.csproj` and check this line matches where CS1 is installed:

    <GameDir>C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines</GameDir>

(Steam > right-click Cities: Skylines > Manage > Browse local files shows the real path.)

## 3. Build
- Visual Studio: open `MyRoadMod.csproj`, press **Ctrl+Shift+B**.
- Command line: `dotnet build` inside this folder.

The build copies `MyRoadMod.dll` + `CitiesHarmony.API.dll` to:

    %LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Mods\MyRoadMod

## 4. Test in game
1. Close the game before every build (Windows locks loaded DLLs).
2. Launch CS1 > **Content Manager > Mods** > tick **My Road Mod**.
3. Load a city, press **F7** to open the debug panel.
4. Draw a road. You should see `[MyRoadMod] Road segment created: #1234`.
5. Options > Mod Settings > My Road Mod > "Say hello" also logs a message.

What the log should contain (milestone 1 sign-off):

    [MyRoadMod] Enabled, version 0.1.x.x
    [MyRoadMod] Patching NetManager.CreateSegment(UInt16& segment, ...)
    [MyRoadMod] Harmony patches applied
    [MyRoadMod] City loaded, mode = LoadGame
    [MyRoadMod] Road segment created: #1234

If any line is missing, or you see `Applying Harmony patches failed`, paste every
`[MyRoadMod]` line (plus any exception under it) from `output_log.txt` back into the issue/chat.

Full log: `C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\output_log.txt`
(search for `[MyRoadMod]`).

Tip: launch with `-noWorkshop` in Steam launch options to test with only local mods
(remove it afterwards, or Harmony from the Workshop won't load).

## 5. Next steps toward a curve tool
- Make a class that inherits `ToolBase`, and activate it from a button in `OnLevelLoaded`.
- In `OnToolUpdate`, raycast the mouse to the terrain to pick points.
- Turn your curve into short pieces and create each with
  `NetManager.instance.CreateNode(...)` then `CreateSegment(...)`.
- Study **Network Multitool** source on GitHub, it does exactly this.
