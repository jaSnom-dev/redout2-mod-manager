# Redout 2 Mod Manager

A standalone Windows GUI for managing `.pak` mods in Redout 2.
The header and window title show the build version so different executables are easy to identify.

Action buttons use equal-width columns, consistent heights and spacing, and an aligned game-folder field that resizes with the window.

## Features

- Import `.pak` files and `.pak.disabled` files (with matching `.sig` or `.sig.disabled` files), or ZIP mod packs containing any mix of these files. Each PAK's optional matching signature is taken from the same archive folder. Disabled files stay disabled on import and appear unchecked with a **Disabled** status. Existing PAKs in the game's folder can be registered through the same **Import Mods...** button.
- Detect potential asset overlaps between enabled, managed mods using either a direct PAK index scan or an optional `.pak.list` manifest. The conflict column shows the number of overlapping files with each archive; **Conflict List** shows all matching paths and archive filenames.
- ZIP imports include PAKs in subfolders and install them directly into the game's Paks folder. Already-installed managed mods are skipped and counted without changing their files or enabled state, so the remaining mods can be imported. Duplicate PAK filenames within a ZIP or conflicts with unregistered files reject the whole ZIP without overwriting files; a failed import rolls back that ZIP's newly installed files and registrations.
- Reimporting a managed PAK, loose or inside a ZIP, restores it if its game-folder copy is missing, preserving the imported file's enabled/disabled state; already-installed managed PAKs are left unchanged.
- Drag and drop PAK files or ZIP archives onto the window to import them. External PAKs are copied into the game folder; source files are left untouched.
- Check mods individually or use **Toggle All** to check all mods, or uncheck them when all are checked, then use **Apply Changes** to toggle their enabled state. Disabling renames files in place to `.pak.disabled` and `.sig.disabled`; enabling restores their original names.
- Select one mod and use **Move Up** or **Move Down** to stage its position. **Apply Load Order** then renames all registered PAKs and their matching signatures with `001_`, `002_`, and subsequent prefixes, preserving enabled and disabled states. Existing three-digit prefixes are replaced. Reordering supports up to 999 mods and refuses to overwrite other files.
- Refresh the managed list and file statuses with **Refresh Mod List**. It removes entries whose enabled and disabled PAK files are both gone. Imports are added automatically; arbitrary game PAK files are not auto-registered.
- Recognize mods disabled by previous versions in `%LOCALAPPDATA%\Redout2ModManager` and restore them to the game folder when enabled.
- Uninstall registered mods, open the game Paks folder (including disabled files) with **Mod Folder**, open the Redout 2 Nexus page, join the [official 34BigThings Discord](https://discord.com/invite/redout), and launch the game through Steam.
- Remember the selected game folder and each installation's managed mod list.
- Show the Nexus Mods, GameBanana, Discord, and Redout 2 icons on their matching buttons; all are embedded in the executable.

Button image credits: [Nexus Mods logo (Wikimedia Commons)](https://commons.wikimedia.org/wiki/File:Nexus_Mods_Logo.svg), the GameBanana favicon from [gamebanana.com](https://gamebanana.com), the [Redout 2 Steam community icon](https://shared.akamai.steamstatic.com/community_assets/images/apps/1799930/f048130cc434edb7d97f1eedb877c0a079ea2dff.jpg), and the [Discord logo from Discord's brand assets](https://discord.com/branding?lang=en).

The manager intentionally does not guess which PAK files are game files and which are mods. It never automatically registers or changes the game's existing PAKs. Use **Import Mods...** to import or register files you know are mods.
Newly disabled mods stay beside the active PAK files with a `.disabled` suffix, which keeps disabling to a same-folder rename and makes the disabled files easy to inspect. The **Mod Folder** button opens that folder. Existing disabled mods from older releases remain recognized in their previous Local AppData location and are moved into the Paks folder when enabled.
The rename approach assumes the game's PAK loader only loads files ending in `.pak`; confirm that behavior in-game with a harmless mod before relying on it.

**Move Up** and **Move Down** only change the on-screen order until **Apply Load Order** is confirmed. Pending order is discarded when closing the manager or selecting another game installation. Applying it changes the managed filenames, which may affect tools or mods that refer to the old names.

### Potential asset conflicts

Click **Scan PAKs** to read the indexes of registered mod archives directly; the manager does not extract their contents. Scans run in the background, and results are saved in the manager's mod manifest. The scanner accepts indexes up to 64 MB and archives with up to 100,000 paths; encrypted or unsupported PAKs are reported instead of being treated as conflict-free. Scan results are reused only while the PAK's size and modification time match the scanned file.

Click **Conflict List** to open a scrollable, copyable report of all known overlapping paths and the `.pak` filenames involved. Enabled archives without usable scan or list data are identified separately as unscanned. The report reflects applied file states, not pending checkbox changes.

Alternatively, place a UTF-8 text file named `ModName.pak.list` beside `ModName.pak`, or in the same folder as the PAK inside an imported ZIP. List one relative path per line, for example `Content/Vehicles/Car.uasset`; blank lines and lines beginning with `#` are ignored. The manager stores this metadata in its own mod manifest and does not copy the `.pak.list` file into the game folder or require the game loader to read it. Mods with neither a list nor a successful scan are shown as **Not scanned**.

The manager reports possible overlaps only among enabled, managed mods; it does not scan the game's base PAKs or arbitrary unregistered archives. These are warnings, not proof of a runtime conflict: matching paths may be intentional, and the manager does not determine which mod takes precedence. Numbered filenames are an ordering aid, but Redout 2's actual load precedence has not been verified; test with harmless mods before relying on it. Reordering does not rename external source files or `.pak.list` sidecars. Direct PAK parsing is provided by [CUE4Parse](https://github.com/FabianFG/CUE4Parse), licensed under Apache-2.0.
The build excludes CUE4Parse package assets not needed for PAK-index scanning. Required runtime dependencies, including Serilog and ZstdSharp, remain bundled; excluding them prevents CUE4Parse from scanning PAKs. The scanner reads only unencrypted PAK indexes.

### DML LogicMods

DML has its own in-game LogicMod list, separate from ordinary PAK file presence. The manager does not change that list. Open the game's DML console with **Home** and use `dml list`, `dml add <name>`, or `dml remove <name>`. See the [DML page on Nexus Mods](https://www.nexusmods.com/redout2/mods/6).

## Run

Download `Redout2ModManager.exe` from the [latest release](https://github.com/jaSnom-dev/redout2-mod-manager/releases/latest) and run it. It is self-contained and does not require a separate .NET runtime.

The default Steam install guess is:

`C:\Program Files (x86)\Steam\steamapps\common\Redout2`

The manager searches Steam's registered library folders for Redout 2 on startup and selects it automatically when found. Use **Browse...** only if it cannot detect the installation. You can choose the top-level install folder, its `redout2` or `Content` subfolder, or the `Content\Paks` folder itself.

## Build / publish

Requires the .NET 10 SDK on Windows:

The `<Version>` in [Redout2ModManager.csproj](./Redout2ModManager.csproj) controls the displayed version and executable metadata. Update it for each release, or override it when publishing with `/p:Version=1.1.3`.

```powershell
dotnet publish Redout2ModManager.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true /p:DebugType=none -o publish-compressed
```

The compressed standalone executable is `publish-compressed\Redout2ModManager.exe`. Its first launch may take slightly longer while the runtime decompresses bundled files.

For a smaller single-file build that requires the .NET 10 Windows Desktop Runtime on the target machine, publish framework-dependent instead:

```powershell
dotnet publish Redout2ModManager.csproj -c Release -r win-x64 /p:SelfContained=false /p:PublishSingleFile=true /p:DebugType=none -o publish-framework-dependent-compact
```

This keeps the application and parser dependencies in one executable with no loose DLLs, but does not bundle the .NET runtime. Single-file compression is not supported for framework-dependent publishing.
