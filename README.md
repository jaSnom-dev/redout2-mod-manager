# Redout 2 Mod Manager

A standalone Windows GUI for managing `.pak` mods in Redout 2.
The header and window title show the build version so different executables are easy to identify.

Action buttons use equal-width columns, consistent heights and spacing, and an aligned game-folder field that resizes with the window.

## Features

- Import `.pak` files and `.pak.disabled` files (with matching `.sig` or `.sig.disabled` files), or ZIP mod packs containing any mix of these files. Each PAK's optional matching signature is taken from the same archive folder. Disabled files stay disabled on import and appear unchecked with a **Disabled** status. Existing PAKs in the game's folder can be registered through the same **Import Mods...** button.
- ZIP imports include PAKs in subfolders and install them directly into the game's Paks folder. Already-installed managed mods are skipped and counted without changing their files or enabled state, so the remaining mods can be imported. Duplicate PAK filenames within a ZIP or conflicts with unregistered files reject the whole ZIP without overwriting files; a failed import rolls back that ZIP's newly installed files and registrations.
- Reimporting a managed PAK, loose or inside a ZIP, restores it if its game-folder copy is missing, preserving the imported file's enabled/disabled state; already-installed managed PAKs are left unchanged.
- Drag and drop PAK files or ZIP archives onto the window to import them. External PAKs are copied into the game folder; source files are left untouched.
- Check mods individually or use **Toggle All** to check all mods, or uncheck them when all are checked, then use **Apply Changes** to toggle their enabled state. Disabling renames files in place to `.pak.disabled` and `.sig.disabled`; enabling restores their original names.
- Refresh the managed list and file statuses with **Refresh Mod List**. It removes entries whose enabled and disabled PAK files are both gone. Imports are added automatically; arbitrary game PAK files are not auto-registered.
- Recognize mods disabled by previous versions in `%LOCALAPPDATA%\Redout2ModManager` and restore them to the game folder when enabled.
- Uninstall registered mods, open the game Paks folder (including disabled files) with **Mod Folder**, open the Redout 2 Nexus page, and launch the game through Steam.
- Remember the selected game folder and each installation's managed mod list.
- Show the Nexus Mods, GameBanana, and Redout 2 icons on their matching buttons; all are embedded in the executable.

Button image credits: [Nexus Mods logo (Wikimedia Commons)](https://commons.wikimedia.org/wiki/File:Nexus_Mods_Logo.svg), the GameBanana favicon from [gamebanana.com](https://gamebanana.com), and the [Redout 2 Steam community icon](https://shared.akamai.steamstatic.com/community_assets/images/apps/1799930/f048130cc434edb7d97f1eedb877c0a079ea2dff.jpg).

The manager intentionally does not guess which PAK files are game files and which are mods. It never automatically registers or changes the game's existing PAKs. Use **Import Mods...** to import or register files you know are mods.
Newly disabled mods stay beside the active PAK files with a `.disabled` suffix, which keeps disabling to a same-folder rename and makes the disabled files easy to inspect. The **Mod Folder** button opens that folder. Existing disabled mods from older releases remain recognized in their previous Local AppData location and are moved into the Paks folder when enabled.
The rename approach assumes the game's PAK loader only loads files ending in `.pak`; confirm that behavior in-game with a harmless mod before relying on it.

### DML LogicMods

DML has its own in-game LogicMod list, separate from ordinary PAK file presence. The manager does not change that list. Open the game's DML console with **Home** and use `dml list`, `dml add <name>`, or `dml remove <name>`. See the [DML page on Nexus Mods](https://www.nexusmods.com/redout2/mods/6).

## Run

Run [`publish-compressed\Redout2ModManager.exe`](./publish-compressed/Redout2ModManager.exe). It is self-contained and does not require a separate .NET runtime.

The default Steam install guess is:

`C:\Program Files (x86)\Steam\steamapps\common\Redout2`

The manager searches Steam's registered library folders for Redout 2 on startup and selects it automatically when found. Use **Browse...** only if it cannot detect the installation. You can choose the top-level install folder, its `redout2` or `Content` subfolder, or the `Content\Paks` folder itself.

## Build / publish

Requires the .NET 10 SDK on Windows:

The `<Version>` in [Redout2ModManager.csproj](./Redout2ModManager.csproj) controls the displayed version and executable metadata. Update it for each release, or override it when publishing with `/p:Version=1.0.6`.

```powershell
dotnet publish Redout2ModManager.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true /p:DebugType=none -o publish-compressed
```

The compressed standalone executable is `publish-compressed\Redout2ModManager.exe`. Its first launch may take slightly longer while the runtime decompresses bundled files.
