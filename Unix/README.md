# Redout 2 Mod Manager for Linux

This experimental Avalonia desktop port manages Redout 2 PAK mods on Linux. It is separate from the Windows Forms application.

The earlier Linux release passed integration checks and a window-startup smoke test on Ubuntu 24.04 under WSL with a virtual display. The v1.1 changes have not yet been tested on Linux or in-game; macOS and other Unix platforms have not been tested.

## Requirements

- A Linux x64 system with a graphical desktop.
- The .NET 10 SDK to build from source. A self-contained published build does not require a separate .NET runtime.
- Steam to launch the game using **Start Game**, and permission to modify the game's Paks folder.
- Native desktop libraries. On a minimal Ubuntu 24.04 installation, install the following if missing:

  ```sh
  sudo apt install libicu74 libfontconfig1 libx11-6 libice6 libsm6
  ```

Package names may differ on other distributions. The self-contained build does not install system libraries.

## Build and run

Copy the entire source tree to Linux, not just the `Unix` folder: the project also uses files from the repository root. From that root:

```sh
dotnet build Unix/Redout2ModManager.Unix.csproj -c Release
dotnet run --project Unix/Redout2ModManager.Unix.csproj -c Release
```

### Publish a self-contained build

From the source tree root:

```sh
dotnet publish Unix/Redout2ModManager.Unix.csproj -c Release -r linux-x64 --self-contained true -o Unix/publish-linux-x64
chmod +x Unix/publish-linux-x64/Redout2ModManager.Unix
./Unix/publish-linux-x64/Redout2ModManager.Unix
```

Keep all files in the published directory together when copying it to another Linux machine. If copying from Windows, run `chmod +x` on the executable after copying.

The same Linux x64 build can be published from Windows with the .NET 10 SDK. From the source tree root in PowerShell:

```powershell
dotnet publish Unix\Redout2ModManager.Unix.csproj -c Release -r linux-x64 --self-contained true -o Unix\publish-linux-x64
```

Copy the entire `Unix\publish-linux-x64` directory to Linux before running it.

## Using the manager

1. Start the manager. It searches standard Steam and Flatpak Steam locations and their registered libraries for Redout 2. If detection fails, use **Browse...** to select the game installation, `Content`, or `Content/Paks` folder.
2. Use **Import Mods...** or drag PAKs and ZIP archives onto the window. Existing PAKs in the game folder can also be registered this way. Only import files you know are mods; game files are not registered automatically.
3. Check or uncheck mods and select **Apply Changes**. Disabling renames managed PAKs and matching signatures with a `.disabled` suffix; enabling restores their names. Verify with a harmless mod in-game that the game ignores disabled files.
4. Select **Scan PAKs** to inspect managed PAK indexes for potential conflicts. The conflict column names each overlapping archive and shows its count. **Details** shows the paths for a mod; **Conflict List** opens a copyable report of all overlaps and unscanned archives. Scans report errors for unsupported or encrypted archives; they do not extract PAK contents.
5. Select a mod using its round selection control and use **Move Up** or **Move Down** to stage its position. Select **Apply Load Order** and confirm to rename registered PAKs and their matching signatures with `001_`, `002_`, and subsequent prefixes. The checkboxes and **Apply Changes** control enabled state separately. The staged order is discarded when closing the manager or selecting another installation.
6. Use **Refresh Mod List** to update statuses, **Mod Folder** to open the Paks folder, or **Uninstall** to delete a registered mod's managed PAK and signature.

The manager also supports optional `.pak.list` files. Conflict results compare enabled, managed mods only; they do not prove an in-game conflict or determine load order. Numbered filenames are an ordering aid: Redout 2's actual load precedence is unverified, so test with harmless mods before relying on it. Reordering supports up to 999 mods, refuses collisions, and leaves external sources and `.pak.list` sidecars unchanged. It changes managed filenames, which may affect tools or mods that refer to the old names. See [potential asset conflicts](../README.md#potential-asset-conflicts) for the list format and scanner limits.

## Settings and existing Windows mods

Settings and per-installation mod lists are stored in the .NET local application data directory, normally `~/.local/share/Redout2ModManager` or under `XDG_DATA_HOME`. The Linux port does not automatically migrate a Windows mod list or legacy disabled files. You can register existing PAKs in the game folder using **Import Mods...**.

## Integration checks

From the source tree root:

```sh
dotnet run --project Unix/Tests/Redout2ModManager.Unix.Tests.csproj -c Release
```

The checks use temporary files and do not modify the game installation.
