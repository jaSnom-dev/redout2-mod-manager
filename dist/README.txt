REDOUT 2 MOD MANAGER
Standalone Windows x64 release

Extract this archive and run Redout2ModManager.exe. This compressed, self-contained
build includes the .NET runtime; no separate runtime or asset files are needed.

The manager searches Steam's registered libraries and selects Redout 2 automatically
when found. Use Browse... only if it cannot detect the installation. You can choose
the game root, its redout2 or Content subfolder, or the Content\Paks folder itself.
Use Import Mods to import .pak or .pak.disabled files, or ZIP archives containing
one .pak file and an optional matching .sig. A .pak.disabled file can include a
matching .sig.disabled file. Existing PAKs can be registered through the same
button. You can also drag and drop these files onto the window.
External PAKs are copied into the game folder, leaving the source files untouched.
Reimporting a managed PAK restores a missing game-folder copy; an installed or
disabled managed mod is not overwritten.
The manager does not automatically register arbitrary game PAK files.

Check mods to set their enabled state, then use Apply Changes. Disabling renames
registered files in place to .pak.disabled and .sig.disabled; enabling restores
their original names. Refresh Mod List reloads statuses and removes entries whose
enabled and disabled PAK files are both gone; imports are added automatically.
Use Mod Folder to inspect disabled files.
Mods disabled by earlier versions in %LOCALAPPDATA%\Redout2ModManager are still
recognized and restored when enabled.
The manager stores settings and its managed-mod list under:
%LOCALAPPDATA%\Redout2ModManager
The rename approach assumes the game only loads files ending in .pak; confirm
that behavior in-game with a harmless mod before relying on it.

DML LogicMods use a separate in-game list. The manager does not change it. Open the
game console with Home and use dml list, dml add <name>, or dml remove <name>.

This is an independent fan-made utility and is not affiliated with or endorsed by
34BigThings, Saber Interactive, or Nexus Mods. Redout 2 names and marks belong to
their respective rights holders.

Mod pages:
- Redout 2 on Nexus Mods: https://www.nexusmods.com/redout2
- DML: https://www.nexusmods.com/redout2/mods/6

Button image credits:
- Nexus Mods logo: https://commons.wikimedia.org/wiki/File:Nexus_Mods_Logo.svg
- GameBanana icon: https://gamebanana.com
- Redout 2 Steam icon: https://shared.akamai.steamstatic.com/community_assets/images/apps/1799930/f048130cc434edb7d97f1eedb877c0a079ea2dff.jpg
