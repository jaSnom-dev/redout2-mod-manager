using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Redout2ModManager;

internal sealed class MainForm : Form
{
    private static readonly Color BackgroundColor = Color.FromArgb(8, 10, 17);
    private static readonly Color PanelColor = Color.FromArgb(18, 21, 34);
    private static readonly Color ButtonColor = Color.FromArgb(28, 34, 55);
    private static readonly Color TextColor = Color.FromArgb(244, 246, 255);
    private static readonly Color MutedColor = Color.FromArgb(158, 174, 211);
    private static readonly Color AccentColor = Color.FromArgb(64, 84, 178);
    private static readonly Color BorderColor = Color.FromArgb(58, 74, 135);
    private static readonly Color ButtonHoverColor = Color.FromArgb(43, 53, 91);
    private static readonly Color AccentHoverColor = Color.FromArgb(83, 105, 203);
    private const string SteamLaunchUri = "steam://rungameid/1799930";
    private const string NexusPage = "https://www.nexusmods.com/redout2";
    private const string GameBananaPage = "https://gamebanana.com/games/24088";

    private readonly string _settingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Redout2ModManager");
    private readonly TextBox _gamePathBox = new();
    private readonly ListView _modList = new();
    private readonly Label _statusLabel = new();
    private readonly Button _applyChangesButton;
    private readonly Button _removeButton;
    private readonly List<ModEntry> _mods = [];
    private string _gameDirectory = "";
    private string _paksDirectory = "";
    private string _manifestPath = "";
    private bool _initializing;

    public MainForm()
    {
        Text = "Redout 2 Mod Manager";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(960, 680);
        MinimumSize = new Size(820, 560);
        BackColor = BackgroundColor;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 9);

        _modList.View = View.Details;
        _modList.FullRowSelect = true;
        _modList.CheckBoxes = true;
        _modList.GridLines = true;
        _modList.HideSelection = false;
        _modList.BackColor = PanelColor;
        _modList.ForeColor = TextColor;
        _modList.BorderStyle = BorderStyle.FixedSingle;
        _modList.Dock = DockStyle.Fill;
        _modList.OwnerDraw = true;
        _modList.Columns.Add("Mod List", 260);
        _modList.Columns.Add("STATUS", 150);
        _modList.DrawColumnHeader += (_, e) =>
        {
            using var background = new SolidBrush(ButtonColor);
            e.Graphics.FillRectangle(background, e.Bounds);
            var textBounds = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
            TextRenderer.DrawText(
                e.Graphics,
                e.Header?.Text ?? "",
                _modList.Font,
                textBounds,
                TextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using var border = new Pen(BorderColor);
            e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        };
        _modList.DrawItem += (_, e) => e.DrawDefault = true;
        _modList.DrawSubItem += (_, e) => e.DrawDefault = true;
        _modList.Resize += (_, _) => ResizeLastColumn();
        _modList.ItemCheck += (_, _) =>
        {
            if (!_initializing && IsHandleCreated)
            {
                BeginInvoke(new Action(UpdateSelectionButtons));
            }
        };
        _modList.SelectedIndexChanged += (_, _) => UpdateSelectionButtons();
        _modList.DoubleClick += (_, _) => OpenModFolder();

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 96,
            Padding = new Padding(18, 6, 12, 4),
            BackColor = BackgroundColor,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };
        actions.Controls.Add(CreateButton("Import Mods...", new Size(135, 30), ImportMods));
        actions.Controls.Add(CreateButton("Refresh Mod List", new Size(150, 30), RefreshMods));
        actions.Controls.Add(CreateButton("Toggle All", new Size(100, 30), ToggleAllMods));
        _applyChangesButton = CreateButton("Apply Changes", new Size(120, 30), ApplyModChanges);
        _applyChangesButton.Enabled = false;
        actions.Controls.Add(_applyChangesButton);
        _removeButton = CreateButton("Uninstall", new Size(100, 30), UninstallSelectedMods);
        actions.Controls.Add(_removeButton);
        actions.Controls.Add(CreateButton("Mod Folder", new Size(120, 30), OpenModFolder));

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 212,
            BackColor = BackgroundColor
        };
        var title = new Label
        {
            Text = "REDOUT 2 MOD MANAGER",
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            ForeColor = TextColor,
            Location = new Point(24, 16),
            Size = new Size(480, 36)
        };
        header.Controls.Add(title);
        var subtitle = new Label
        {
            Text = "Install and toggle managed PAK mods. Game files are left untouched.",
            ForeColor = MutedColor,
            Location = new Point(26, 54),
            Size = new Size(680, 22)
        };
        header.Controls.Add(subtitle);

        var nexusButton = CreateButton("Nexus Mods", new Size(150, 32), OpenNexusPage);
        nexusButton.Image = LoadButtonIcon(
            "Redout2ModManager.Assets.nexus-mods.png",
            new Size(22, 22),
            cropToLeftSquare: true);
        nexusButton.ImageAlign = ContentAlignment.MiddleLeft;
        nexusButton.TextImageRelation = TextImageRelation.ImageBeforeText;
        nexusButton.Padding = new Padding(12, 0, 0, 0);
        nexusButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        nexusButton.Location = new Point(Width - 190, 18);
        header.Controls.Add(nexusButton);

        var gameBananaButton = CreateButton("GameBanana", new Size(150, 32), () => OpenUrl(GameBananaPage));
        gameBananaButton.Image = LoadButtonIcon(
            "Redout2ModManager.Assets.gamebanana.png",
            new Size(22, 22));
        gameBananaButton.ImageAlign = ContentAlignment.MiddleLeft;
        gameBananaButton.TextImageRelation = TextImageRelation.ImageBeforeText;
        gameBananaButton.Padding = new Padding(12, 0, 0, 0);
        gameBananaButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        gameBananaButton.Location = new Point(Width - 190, 56);
        header.Controls.Add(gameBananaButton);

        var startButton = CreateButton("Start Game", new Size(150, 32), StartGame);
        startButton.Image = LoadButtonIcon(
            "Redout2ModManager.Assets.redout2-steam.jpg",
            new Size(22, 22));
        startButton.ImageAlign = ContentAlignment.MiddleLeft;
        startButton.TextImageRelation = TextImageRelation.ImageBeforeText;
        startButton.Padding = new Padding(12, 0, 0, 0);
        startButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        startButton.Location = new Point(Width - 190, 94);
        startButton.BackColor = AccentColor;
        startButton.ForeColor = TextColor;
        startButton.FlatAppearance.BorderSize = 0;
        startButton.FlatAppearance.MouseOverBackColor = AccentHoverColor;
        header.Controls.Add(startButton);
        header.Resize += (_, _) =>
        {
            nexusButton.Left = header.ClientSize.Width - nexusButton.Width - 24;
            gameBananaButton.Left = header.ClientSize.Width - gameBananaButton.Width - 24;
            startButton.Left = header.ClientSize.Width - startButton.Width - 24;
        };

        var pathLabel = new Label
        {
            Text = "GAME FOLDER",
            ForeColor = MutedColor,
            Location = new Point(26, 136),
            Size = new Size(130, 20)
        };
        header.Controls.Add(pathLabel);
        _gamePathBox.ReadOnly = true;
        _gamePathBox.BackColor = PanelColor;
        _gamePathBox.ForeColor = TextColor;
        _gamePathBox.BorderStyle = BorderStyle.FixedSingle;
        _gamePathBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _gamePathBox.Location = new Point(26, 160);
        _gamePathBox.Size = new Size(Width - 250, 28);
        header.Controls.Add(_gamePathBox);

        var browseButton = CreateButton("Browse...", new Size(150, 32), BrowseForGameFolder);
        browseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        browseButton.Location = new Point(Width - 190, 158);
        header.Controls.Add(browseButton);
        header.Resize += (_, _) =>
        {
            _gamePathBox.Width = header.ClientSize.Width - _gamePathBox.Left - browseButton.Width - 40;
            browseButton.Left = header.ClientSize.Width - browseButton.Width - 24;
        };

        _statusLabel.ForeColor = MutedColor;
        _statusLabel.Location = new Point(26, 190);
        _statusLabel.Size = new Size(850, 20);
        _statusLabel.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        header.Controls.Add(_statusLabel);

        Controls.Add(_modList);
        Controls.Add(actions);
        Controls.Add(header);
        EnableFileDrop(this);

        Load += (_, _) => InitializeManager();
        FormClosed += (_, _) =>
        {
            nexusButton.Image?.Dispose();
            gameBananaButton.Image?.Dispose();
            startButton.Image?.Dispose();
        };
    }

    private void EnableFileDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += HandleFileDragEnter;
        control.DragDrop += HandleFileDrop;
        foreach (Control child in control.Controls)
        {
            EnableFileDrop(child);
        }
    }

    private void HandleFileDragEnter(object? sender, DragEventArgs e)
    {
        var data = e.Data;
        if (data is not null &&
            data.GetData(DataFormats.FileDrop) is string[] paths &&
            paths.Any(IsSupportedImportPath))
        {
            e.Effect = DragDropEffects.Copy;
            return;
        }
        e.Effect = DragDropEffects.None;
    }

    private void HandleFileDrop(object? sender, DragEventArgs e)
    {
        var data = e.Data;
        if (data is not null && data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            if (EnsureGameSelected())
            {
                ImportModFiles(paths);
            }
        }
    }

    private static bool IsSupportedImportPath(string path)
    {
        return IsPakImportPath(path) ||
            Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPakImportPath(string path) =>
        Path.GetExtension(path).Equals(".pak", StringComparison.OrdinalIgnoreCase) ||
        IsDisabledPakPath(path);

    private static bool IsDisabledPakPath(string path) =>
        Path.GetFileName(path).EndsWith(".pak.disabled", StringComparison.OrdinalIgnoreCase);

    private static string GetPakFileName(string path)
    {
        var fileName = Path.GetFileName(path);
        return IsDisabledPakPath(path) ? fileName[..^".disabled".Length] : fileName;
    }

    private static string GetSourceSignaturePath(string sourcePakPath, bool isDisabledPak)
    {
        if (!isDisabledPak)
        {
            return Path.ChangeExtension(sourcePakPath, ".sig");
        }

        var directory = Path.GetDirectoryName(sourcePakPath)!;
        var pakBaseName = Path.GetFileName(sourcePakPath)[..^".pak.disabled".Length];
        return Path.Combine(directory, pakBaseName + ".sig.disabled");
    }

    private static Image LoadButtonIcon(string resourceName, Size size, bool cropToLeftSquare = false)
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded button image '{resourceName}' was not found.");
        using var source = new Bitmap(stream);
        var sourceBounds = cropToLeftSquare
            ? new Rectangle(0, 0, Math.Min(source.Width, source.Height), Math.Min(source.Width, source.Height))
            : new Rectangle(0, 0, source.Width, source.Height);
        var image = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image: source, destRect: new Rectangle(Point.Empty, size), srcRect: sourceBounds, srcUnit: GraphicsUnit.Pixel);
        }
        return image;
    }

    private static Button CreateButton(string text, Size size, Action action)
    {
        var button = new Button
        {
            Text = text,
            Size = size,
            BackColor = ButtonColor,
            ForeColor = TextColor,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(4)
        };
        button.FlatAppearance.BorderColor = BorderColor;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = ButtonHoverColor;
        button.Click += (_, _) => action();
        return button;
    }

    private void InitializeManager()
    {
        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            var settingsPath = Path.Combine(_settingsDirectory, "settings.json");
            var savedDirectory = "";
            if (File.Exists(settingsPath))
            {
                var settings = JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(settingsPath));
                savedDirectory = settings?.GameDirectory ?? "";
            }

            var initialDirectory = FindPaksDirectory(savedDirectory) is not null
                ? savedDirectory
                : FindDefaultGameDirectory();
            if (!string.IsNullOrWhiteSpace(initialDirectory))
            {
                SetGameDirectory(initialDirectory, saveSettings: false);
            }
            else
            {
                _gamePathBox.Text = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Steam", "steamapps", "common", "Redout2");
                SetStatus("Redout 2 was not found in your Steam libraries. Use Browse... to select it.");
            }
        }
        catch (Exception ex)
        {
            ShowError("Could not initialize the manager.", ex);
        }
    }

    private string FindDefaultGameDirectory()
    {
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registeredSteamPath = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Valve\Steam",
            "SteamPath",
            null) as string;
        if (!string.IsNullOrWhiteSpace(registeredSteamPath))
        {
            AddDirectory(steamRoots, registeredSteamPath);
        }
        AddDirectory(
            steamRoots,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        AddDirectory(
            steamRoots,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));

        var libraries = new HashSet<string>(steamRoots, StringComparer.OrdinalIgnoreCase);
        foreach (var steamRoot in steamRoots)
        {
            var libraryFoldersFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFoldersFile))
            {
                continue;
            }

            var contents = File.ReadAllText(libraryFoldersFile);
            AddVdfPaths(libraries, contents, "\"path\"\\s+\"(?<path>(?:\\\\.|[^\"])*)\"");
            AddVdfPaths(libraries, contents, "\"\\d+\"\\s+\"(?<path>(?:\\\\.|[^\"])*)\"");
        }

        foreach (var library in libraries)
        {
            var steamApps = Path.Combine(library, "steamapps");
            var manifestPath = Path.Combine(steamApps, "appmanifest_1799930.acf");
            if (File.Exists(manifestPath))
            {
                var manifest = File.ReadAllText(manifestPath);
                var installDirectory = Regex.Match(
                    manifest,
                    "\"installdir\"\\s+\"(?<name>[^\"]+)\"",
                    RegexOptions.IgnoreCase).Groups["name"].Value;
                if (!string.IsNullOrWhiteSpace(installDirectory) &&
                    Path.GetFileName(installDirectory).Equals(installDirectory, StringComparison.Ordinal))
                {
                    var installedGame = Path.Combine(steamApps, "common", installDirectory);
                    if (FindPaksDirectory(installedGame) is not null)
                    {
                        return installedGame;
                    }
                }
            }

            foreach (var name in new[] { "Redout2", "Redout 2" })
            {
                var candidate = Path.Combine(steamApps, "common", name);
                if (FindPaksDirectory(candidate) is not null)
                {
                    return candidate;
                }
            }
        }
        return "";
    }

    private static void AddDirectory(HashSet<string> directories, string path)
    {
        if (Directory.Exists(path))
        {
            directories.Add(Path.GetFullPath(path));
        }
    }

    private static void AddVdfPaths(HashSet<string> directories, string contents, string pattern)
    {
        foreach (Match match in Regex.Matches(contents, pattern, RegexOptions.IgnoreCase))
        {
            var path = match.Groups["path"].Value.Replace(@"\\", @"\");
            AddDirectory(directories, path);
        }
    }

    private void BrowseForGameFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the Redout 2 installation folder",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_gameDirectory) ? _gameDirectory : ""
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            SetGameDirectory(dialog.SelectedPath, saveSettings: true);
        }
        catch (Exception ex)
        {
            ShowError("Could not use that game folder.", ex);
        }
    }

    private void SetGameDirectory(string gameDirectory, bool saveSettings)
    {
        var paksDirectory = FindPaksDirectory(gameDirectory)
            ?? throw new DirectoryNotFoundException(
                "Could not find Redout 2's Content\\Paks folder. Select the game folder, its redout2 subfolder, the Content folder, or the Paks folder itself.");

        _gameDirectory = Path.GetFullPath(gameDirectory);
        _paksDirectory = paksDirectory;
        _manifestPath = Path.Combine(_settingsDirectory, $"{GetInstallKey(_paksDirectory)}-mods.json");
        _gamePathBox.Text = _gameDirectory;
        LoadManifest();
        RefreshModList();
        if (saveSettings)
        {
            SaveSettings();
        }
        SetStatus($"Managing {_mods.Count} registered PAK mod(s). Unregistered game PAK files are not changed.");
    }

    private static string? FindPaksDirectory(string gameDirectory)
    {
        if (!Directory.Exists(gameDirectory))
        {
            return null;
        }

        var selectedDirectory = new DirectoryInfo(Path.GetFullPath(gameDirectory));
        if (selectedDirectory.Name.Equals("Paks", StringComparison.OrdinalIgnoreCase) &&
            selectedDirectory.Parent?.Name.Equals("Content", StringComparison.OrdinalIgnoreCase) == true)
        {
            return selectedDirectory.FullName;
        }
        if (selectedDirectory.Name.Equals("Content", StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(Path.Combine(selectedDirectory.FullName, "Paks")))
        {
            return Path.Combine(selectedDirectory.FullName, "Paks");
        }

        var candidates = new[]
        {
            Path.Combine(gameDirectory, "redout2", "Content", "Paks"),
            Path.Combine(gameDirectory, "Redout2", "Content", "Paks"),
            Path.Combine(gameDirectory, "Content", "Paks")
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private void LoadManifest()
    {
        _mods.Clear();
        if (!File.Exists(_manifestPath))
        {
            return;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<ModEntry>>(File.ReadAllText(_manifestPath))
                ?? throw new InvalidDataException("The saved mod list is empty or invalid.");
            foreach (var entry in entries)
            {
                if (!IsSafePakFileName(entry.FileName))
                {
                    throw new InvalidDataException($"The saved mod list contains an invalid PAK name: {entry.FileName}");
                }
                if (_mods.Any(mod => mod.FileName.Equals(entry.FileName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException($"The saved mod list contains a duplicate PAK: {entry.FileName}");
                }
                _mods.Add(entry);
            }
        }
        catch (Exception ex)
        {
            _mods.Clear();
            throw new InvalidDataException($"Could not load the saved mod list at '{_manifestPath}'.", ex);
        }
    }

    private void SaveManifest()
    {
        var temporaryPath = _manifestPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_mods, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _manifestPath, overwrite: true);
    }

    private void SaveSettings()
    {
        var settings = new ManagerSettings { GameDirectory = _gameDirectory };
        var path = Path.Combine(_settingsDirectory, "settings.json");
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private void ImportMods()
    {
        if (!EnsureGameSelected())
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Import Redout 2 mods",
            Filter = "Mod files (*.pak;*.pak.disabled;*.zip)|*.pak;*.pak.disabled;*.zip|PAK files (*.pak;*.pak.disabled)|*.pak;*.pak.disabled|ZIP archives (*.zip)|*.zip",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            ImportModFiles(dialog.FileNames);
        }
    }

    private void ImportModFiles(IEnumerable<string> paths)
    {
        var imported = 0;
        var alreadyManaged = 0;
        var restoredMods = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var extension = Path.GetExtension(path);
                if (IsPakImportPath(path))
                {
                    var fileName = GetPakFileName(path);
                    var wasManaged = _mods.Any(mod =>
                        mod.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
                    if (ImportPak(path))
                    {
                        imported++;
                        if (wasManaged)
                        {
                            restoredMods[fileName] = !IsDisabledPakPath(path);
                        }
                    }
                    else
                    {
                        alreadyManaged++;
                    }
                }
                else if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    imported += InstallArchive(path);
                }
                else
                {
                    throw new InvalidDataException("Choose a .pak, .pak.disabled, or a ZIP containing .pak or .pak.disabled mods.");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        RefreshModList(preserveDesiredStates: true, stateOverrides: restoredMods);
        if (failures.Count == 0)
        {
            SetStatus(imported == 0
                ? $"No new mods imported; already managed: {alreadyManaged}."
                : $"Imported or restored {imported} mod(s).");
            return;
        }

        SetStatus($"Imported or restored {imported} mod(s); {failures.Count} failed.");
        MessageBox.Show(
            this,
            $"Imported or restored {imported} mod(s).\r\n\r\nCould not import:\r\n{string.Join("\r\n", failures)}",
            "Mod import results",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private int InstallArchive(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var pakEntries = archive.Entries
            .Where(entry => IsPakImportPath(entry.FullName.Replace('\\', '/')))
            .ToList();
        if (pakEntries.Count == 0)
        {
            throw new InvalidDataException("The ZIP does not contain a .pak or .pak.disabled mod file.");
        }

        var filesToCopy = new List<(ZipArchiveEntry Entry, string Target)>();
        var newMods = new List<ModEntry>();
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pakEntry in pakEntries)
        {
            var entryPath = pakEntry.FullName.Replace('\\', '/');
            var isDisabledPak = IsDisabledPakPath(entryPath);
            var fileName = GetPakFileName(entryPath);
            if (!IsSafePakFileName(fileName))
            {
                throw new InvalidDataException("The archive contains an invalid PAK file name.");
            }
            if (!fileNames.Add(fileName))
            {
                throw new InvalidDataException($"The ZIP contains multiple PAKs with the same filename: {fileName}");
            }
            EnsureNotRegisteredOrInstalled(fileName);

            filesToCopy.Add((pakEntry, isDisabledPak ? GetDisabledPath(fileName) : Path.Combine(_paksDirectory, fileName)));
            var signatureEntry = FindSignatureEntry(archive, pakEntry, fileName);
            if (signatureEntry is not null)
            {
                var signatureName = Path.ChangeExtension(fileName, ".sig");
                var signatureTarget = isDisabledPak ? GetDisabledPath(signatureName) : Path.Combine(_paksDirectory, signatureName);
                if (File.Exists(Path.Combine(_paksDirectory, signatureName)) || File.Exists(GetDisabledPath(signatureName)))
                {
                    throw new IOException($"A signature file already exists: {Path.GetFileName(signatureTarget)}");
                }
                filesToCopy.Add((signatureEntry, signatureTarget));
            }
            newMods.Add(new ModEntry
            {
                FileName = fileName,
                DisplayName = Path.GetFileNameWithoutExtension(fileName),
                HasSignature = signatureEntry is not null
            });
        }

        var createdFiles = new List<string>();
        try
        {
            foreach (var (entry, target) in filesToCopy)
            {
                CopyArchiveEntry(entry, target);
                createdFiles.Add(target);
            }

            _mods.AddRange(newMods);
            SaveManifest();
        }
        catch
        {
            foreach (var mod in newMods)
            {
                _mods.Remove(mod);
            }
            foreach (var path in createdFiles)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            throw;
        }
        return newMods.Count;
    }

    private bool ImportPak(string sourcePath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var isDisabledPak = IsDisabledPakPath(fullSourcePath);
        var fileName = GetPakFileName(fullSourcePath);
        if (!IsSafePakFileName(fileName))
        {
            throw new InvalidDataException($"Invalid PAK filename: {fileName}");
        }

        var managedMod = _mods.FirstOrDefault(mod =>
            mod.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (managedMod is not null)
        {
            return RestoreManagedPak(managedMod, fullSourcePath);
        }

        if (Path.GetDirectoryName(fullSourcePath)!.Equals(_paksDirectory, StringComparison.OrdinalIgnoreCase))
        {
            if (isDisabledPak)
            {
                RegisterExistingDisabledPak(fullSourcePath, fileName);
            }
            else
            {
                RegisterExistingPak(fullSourcePath);
            }
            return true;
        }

        EnsureNotRegisteredOrInstalled(fileName);
        var sourceSignature = GetSourceSignaturePath(fullSourcePath, isDisabledPak);
        var hasSignature = File.Exists(sourceSignature);
        var targetPak = isDisabledPak ? GetDisabledPath(fileName) : Path.Combine(_paksDirectory, fileName);
        var signatureName = Path.ChangeExtension(fileName, ".sig");
        var targetSignature = isDisabledPak ? GetDisabledPath(signatureName) : Path.Combine(_paksDirectory, signatureName);
        if (hasSignature &&
            (File.Exists(Path.Combine(_paksDirectory, signatureName)) || File.Exists(GetDisabledPath(signatureName))))
        {
            throw new IOException($"A signature file already exists: {Path.GetFileName(targetSignature)}");
        }

        var copiedFiles = new List<string>();
        try
        {
            File.Copy(fullSourcePath, targetPak, overwrite: false);
            copiedFiles.Add(targetPak);
            if (hasSignature)
            {
                File.Copy(sourceSignature, targetSignature, overwrite: false);
                copiedFiles.Add(targetSignature);
            }

            AddManagedMod(fileName, hasSignature);
            return true;
        }
        catch
        {
            foreach (var path in copiedFiles)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            throw;
        }
    }

    private bool RestoreManagedPak(ModEntry mod, string sourcePath)
    {
        var activePak = Path.Combine(_paksDirectory, mod.FileName);
        var renamedDisabledPak = GetDisabledPath(mod.FileName);
        var legacyDisabledPak = GetLegacyDisabledPath(mod.FileName);
        var isActive = File.Exists(activePak);
        var isRenamedDisabled = File.Exists(renamedDisabledPak);
        var isLegacyDisabled = File.Exists(legacyDisabledPak);
        if ((isActive && (isRenamedDisabled || isLegacyDisabled)) ||
            (isRenamedDisabled && isLegacyDisabled))
        {
            throw new IOException($"'{mod.FileName}' has conflicting enabled and disabled copies. Resolve the conflict before importing it.");
        }
        if (isActive || isRenamedDisabled || isLegacyDisabled)
        {
            return false;
        }

        var isDisabledPak = IsDisabledPakPath(sourcePath);
        var sourceSignature = GetSourceSignaturePath(sourcePath, isDisabledPak);
        var hasSourceSignature = File.Exists(sourceSignature);
        var targetPak = isDisabledPak ? renamedDisabledPak : activePak;
        var signatureName = Path.ChangeExtension(mod.FileName, ".sig");
        var targetSignature = isDisabledPak ? GetDisabledPath(signatureName) : Path.Combine(_paksDirectory, signatureName);
        var otherSignature = isDisabledPak ? Path.Combine(_paksDirectory, signatureName) : GetDisabledPath(signatureName);
        if (File.Exists(otherSignature))
        {
            throw new IOException($"A signature file in the opposite enabled state already exists: {Path.GetFileName(otherSignature)}");
        }
        var hasTargetSignature = File.Exists(targetSignature);
        if (hasSourceSignature && hasTargetSignature)
        {
            throw new IOException($"A signature file already exists: {Path.GetFileName(targetSignature)}");
        }
        if (mod.HasSignature && !hasSourceSignature && !hasTargetSignature)
        {
            throw new FileNotFoundException(
                $"'{mod.FileName}' requires its matching .sig file. Import the PAK and signature together.",
                sourceSignature);
        }
        if (!mod.HasSignature && hasTargetSignature)
        {
            throw new IOException($"A signature file already exists: {Path.GetFileName(targetSignature)}");
        }

        var copiedFiles = new List<string>();
        var previousHasSignature = mod.HasSignature;
        try
        {
            File.Copy(sourcePath, targetPak, overwrite: false);
            copiedFiles.Add(targetPak);
            if (hasSourceSignature)
            {
                File.Copy(sourceSignature, targetSignature, overwrite: false);
                copiedFiles.Add(targetSignature);
            }

            mod.HasSignature = hasSourceSignature || hasTargetSignature;
            SaveManifest();
            return true;
        }
        catch
        {
            mod.HasSignature = previousHasSignature;
            foreach (var path in copiedFiles)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            throw;
        }
    }

    private static ZipArchiveEntry? FindSignatureEntry(ZipArchive archive, ZipArchiveEntry pakEntry, string pakFileName)
    {
        var normalizedPath = pakEntry.FullName.Replace('\\', '/');
        var directory = Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/') ?? "";
        var signatureName = Path.ChangeExtension(pakFileName, ".sig");
        if (IsDisabledPakPath(normalizedPath))
        {
            signatureName += ".disabled";
        }
        var fullSignaturePath = string.IsNullOrEmpty(directory) ? signatureName : $"{directory}/{signatureName}";
        return archive.Entries.FirstOrDefault(entry =>
            entry.FullName.Replace('\\', '/').Equals(fullSignaturePath, StringComparison.OrdinalIgnoreCase));
    }

    private static void CopyArchiveEntry(ZipArchiveEntry entry, string destination)
    {
        using var input = entry.Open();
        var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            using (output)
            {
                input.CopyTo(output);
            }
        }
        catch
        {
            File.Delete(destination);
            throw;
        }
    }

    private void RegisterExistingPak(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        if (!IsSafePakFileName(fileName))
        {
            throw new InvalidDataException($"Invalid PAK filename: {fileName}");
        }
        if (!Path.GetDirectoryName(fullPath)!.Equals(_paksDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only PAK files directly inside the game's Content\\Paks folder can be registered.");
        }
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected PAK file no longer exists.", fullPath);
        }
        if (_mods.Any(mod => mod.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"'{fileName}' is already in this manager's list.");
        }
        if (File.Exists(GetDisabledPath(fileName)) || File.Exists(GetLegacyDisabledPath(fileName)))
        {
            throw new IOException($"A disabled mod with this name is already stored: {fileName}");
        }

        var signaturePath = Path.Combine(_paksDirectory, Path.ChangeExtension(fileName, ".sig"));
        AddManagedMod(fileName, File.Exists(signaturePath));
    }

    private void RegisterExistingDisabledPak(string fullPath, string fileName)
    {
        var disabledPak = GetDisabledPath(fileName);
        if (!Path.GetFullPath(fullPath).Equals(disabledPak, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only disabled PAKs directly inside the game's Content\\Paks folder can be registered.");
        }
        if (!File.Exists(disabledPak))
        {
            throw new FileNotFoundException("The selected disabled PAK file no longer exists.", disabledPak);
        }
        if (File.Exists(Path.Combine(_paksDirectory, fileName)) ||
            File.Exists(GetLegacyDisabledPath(fileName)))
        {
            throw new IOException($"'{fileName}' already has another enabled or disabled copy.");
        }

        var signatureName = Path.ChangeExtension(fileName, ".sig");
        AddManagedMod(fileName, File.Exists(GetDisabledPath(signatureName)));
    }

    private void AddManagedMod(string fileName, bool hasSignature)
    {
        var mod = new ModEntry
        {
            FileName = fileName,
            DisplayName = Path.GetFileNameWithoutExtension(fileName),
            HasSignature = hasSignature
        };
        _mods.Add(mod);
        try
        {
            SaveManifest();
        }
        catch
        {
            _mods.Remove(mod);
            throw;
        }
    }

    private void EnsureNotRegisteredOrInstalled(string fileName)
    {
        if (_mods.Any(mod => mod.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"'{fileName}' is already in this manager's list.");
        }
        if (File.Exists(Path.Combine(_paksDirectory, fileName)))
        {
            throw new IOException(
                $"'{fileName}' already exists in the game Paks folder. Import that file to register it without replacing it.");
        }
        if (File.Exists(GetDisabledPath(fileName)) || File.Exists(GetLegacyDisabledPath(fileName)))
        {
            throw new IOException($"A disabled mod with this name is already stored: {fileName}");
        }
    }

    private void ApplyModChanges()
    {
        if (!EnsureGameSelected())
        {
            return;
        }

        var desiredStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (ListViewItem item in _modList.Items)
        {
            if (item.Tag is string fileName)
            {
                desiredStates[fileName] = item.Checked;
            }
        }

        var changed = 0;
        try
        {
            foreach (var mod in _mods)
            {
                var activePak = Path.Combine(_paksDirectory, mod.FileName);
                var disabledPak = GetDisabledPath(mod.FileName);
                var legacyDisabledPak = GetLegacyDisabledPath(mod.FileName);
                var isActive = File.Exists(activePak);
                var isDisabled = File.Exists(disabledPak);
                var isLegacyDisabled = File.Exists(legacyDisabledPak);
                if (!isActive && !isDisabled && !isLegacyDisabled)
                {
                    continue;
                }
                if ((isActive && (isDisabled || isLegacyDisabled)) ||
                    (isDisabled && isLegacyDisabled))
                {
                    throw new IOException(
                        $"Cannot determine the state of '{mod.FileName}'. Its enabled and disabled copies are conflicting or missing.");
                }
                if (!desiredStates.TryGetValue(mod.FileName, out var shouldBeEnabled) ||
                    shouldBeEnabled == isActive)
                {
                    continue;
                }

                TransferModFiles(mod, enable: shouldBeEnabled, fromLegacyDisabled: isLegacyDisabled);
                changed++;
            }

            RefreshModList();
            SetStatus(changed == 0 ? "No changes to apply." : $"Applied changes to {changed} mod(s).");
        }
        catch (Exception ex)
        {
            RefreshModList(preserveDesiredStates: true);
            ShowError("Could not apply all mod changes.", ex);
        }
    }

    private void TransferModFiles(ModEntry mod, bool enable, bool fromLegacyDisabled)
    {
        var sourcePak = enable
            ? fromLegacyDisabled ? GetLegacyDisabledPath(mod.FileName) : GetDisabledPath(mod.FileName)
            : Path.Combine(_paksDirectory, mod.FileName);
        var destinationPak = enable ? Path.Combine(_paksDirectory, mod.FileName) : GetDisabledPath(mod.FileName);
        var sourceFiles = new List<(string Source, string Destination)> { (sourcePak, destinationPak) };
        if (mod.HasSignature)
        {
            var signatureName = Path.ChangeExtension(mod.FileName, ".sig");
            var sourceSignature = enable
                ? fromLegacyDisabled ? GetLegacyDisabledPath(signatureName) : GetDisabledPath(signatureName)
                : Path.Combine(_paksDirectory, signatureName);
            var destinationSignature = enable
                ? Path.Combine(_paksDirectory, signatureName)
                : GetDisabledPath(signatureName);
            sourceFiles.Add((sourceSignature, destinationSignature));
        }

        foreach (var pair in sourceFiles)
        {
            if (!File.Exists(pair.Source))
            {
                throw new FileNotFoundException("A managed mod file is missing.", pair.Source);
            }
            if (File.Exists(pair.Destination))
            {
                throw new IOException($"Refusing to overwrite an existing file: {pair.Destination}");
            }
        }

        var movedFiles = new List<(string Source, string Destination)>();
        try
        {
            foreach (var pair in sourceFiles)
            {
                MoveManagedFile(pair.Source, pair.Destination);
                movedFiles.Add(pair);
            }
        }
        catch (Exception operationError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var pair in movedFiles.AsEnumerable().Reverse())
            {
                try
                {
                    MoveManagedFile(pair.Destination, pair.Source);
                }
                catch (Exception ex)
                {
                    rollbackErrors.Add(ex);
                }
            }
            if (rollbackErrors.Count > 0)
            {
                rollbackErrors.Insert(0, operationError);
                throw new AggregateException("The rename failed and rollback was incomplete.", rollbackErrors);
            }
            throw;
        }
    }

    private static void MoveManagedFile(string source, string destination)
    {
        if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            File.Move(source, destination);
            return;
        }

        File.Copy(source, destination, overwrite: false);
        try
        {
            File.Delete(source);
        }
        catch (Exception moveError)
        {
            try
            {
                DeleteIfExists(destination);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Could not complete the cross-volume move or clean up its copy.", moveError, cleanupError);
            }
            throw;
        }
    }

    private void UninstallSelectedMods()
    {
        if (_modList.SelectedItems.Count == 0)
        {
            return;
        }

        var selectedNames = _modList.SelectedItems.Cast<ListViewItem>()
            .Select(item => item.Tag as string)
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedMods = _mods.Where(mod => selectedNames.Contains(mod.FileName)).ToList();
        var prompt = $"Uninstall {selectedMods.Count} selected mod(s)? Their managed PAK and optional signature files will be deleted.";
        if (MessageBox.Show(this, prompt, "Confirm uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            foreach (var mod in selectedMods)
            {
                DeleteIfExists(Path.Combine(_paksDirectory, mod.FileName));
                DeleteIfExists(GetDisabledPath(mod.FileName));
                DeleteIfExists(GetLegacyDisabledPath(mod.FileName));
                if (mod.HasSignature)
                {
                    var signatureName = Path.ChangeExtension(mod.FileName, ".sig");
                    DeleteIfExists(Path.Combine(_paksDirectory, signatureName));
                    DeleteIfExists(GetDisabledPath(signatureName));
                    DeleteIfExists(GetLegacyDisabledPath(signatureName));
                }
                _mods.Remove(mod);
                SaveManifest();
            }

            RefreshModList();
            SetStatus($"Uninstalled {selectedMods.Count} mod(s).");
        }
        catch (Exception ex)
        {
            RefreshModList();
            ShowError("Could not uninstall all selected mods.", ex);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void OpenModFolder()
    {
        if (!EnsureGameSelected())
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _paksDirectory,
                UseShellExecute = true
            });
            SetStatus("Disabled mods in this folder end in .pak.disabled.");
        }
        catch (Exception ex)
        {
            ShowError("Could not open the game's Paks folder.", ex);
        }
    }

    private void StartGame()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = SteamLaunchUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError("Could not launch Redout 2 through Steam.", ex);
        }
    }

    private void OpenNexusPage() => OpenUrl(NexusPage);

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError("Could not open the web page.", ex);
        }
    }

    private bool EnsureGameSelected()
    {
        if (!string.IsNullOrEmpty(_paksDirectory) && Directory.Exists(_paksDirectory))
        {
            return true;
        }
        MessageBox.Show(
            this,
            "Select your Redout 2 installation folder first.",
            "Game folder required",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return false;
    }

    private void RefreshMods()
    {
        if (!EnsureGameSelected())
        {
            return;
        }

        var previousMods = _mods.ToList();
        try
        {
            LoadManifest();
            var missingMods = _mods.Where(mod =>
                !File.Exists(Path.Combine(_paksDirectory, mod.FileName)) &&
                !File.Exists(GetDisabledPath(mod.FileName)) &&
                !File.Exists(GetLegacyDisabledPath(mod.FileName))).ToList();
            foreach (var missingMod in missingMods)
            {
                _mods.Remove(missingMod);
            }
            if (missingMods.Count > 0)
            {
                SaveManifest();
            }

            RefreshModList(preserveDesiredStates: true);
            SetStatus(missingMods.Count == 0
                ? $"Refreshed {_mods.Count} managed mod(s)."
                : $"Removed {missingMods.Count} missing mod(s); refreshed {_mods.Count} managed mod(s).");
        }
        catch (Exception ex)
        {
            _mods.Clear();
            _mods.AddRange(previousMods);
            RefreshModList(preserveDesiredStates: true);
            ShowError("Could not refresh the mod list.", ex);
        }
    }

    private void RefreshModList(bool preserveDesiredStates = false, IReadOnlyDictionary<string, bool>? stateOverrides = null)
    {
        var desiredStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (preserveDesiredStates)
        {
            foreach (ListViewItem item in _modList.Items)
            {
                if (item.Tag is string fileName)
                {
                    desiredStates[fileName] = item.Checked;
                }
            }
        }

        if (stateOverrides is not null)
        {
            foreach (var (fileName, enabled) in stateOverrides)
            {
                desiredStates[fileName] = enabled;
            }
        }

        _initializing = true;
        _modList.BeginUpdate();
        try
        {
            _modList.Items.Clear();
            foreach (var mod in _mods.OrderBy(mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var item = new ListViewItem(mod.DisplayName) { Tag = mod.FileName };
                item.Checked = desiredStates.TryGetValue(mod.FileName, out var shouldBeEnabled)
                    ? shouldBeEnabled
                    : File.Exists(Path.Combine(_paksDirectory, mod.FileName));
                item.SubItems.Add(GetModStatus(mod));
                _modList.Items.Add(item);
            }
        }
        finally
        {
            _modList.EndUpdate();
            _initializing = false;
        }
        ResizeLastColumn();
        UpdateSelectionButtons();
    }

    private string GetModStatus(ModEntry mod)
    {
        var isActive = File.Exists(Path.Combine(_paksDirectory, mod.FileName));
        var isRenamedDisabled = File.Exists(GetDisabledPath(mod.FileName));
        var isLegacyDisabled = File.Exists(GetLegacyDisabledPath(mod.FileName));
        if (isActive && !isRenamedDisabled && !isLegacyDisabled)
        {
            return "Enabled";
        }
        if (!isActive && isRenamedDisabled && !isLegacyDisabled)
        {
            return "Disabled";
        }
        if (!isActive && !isRenamedDisabled && isLegacyDisabled)
        {
            return "Disabled (legacy)";
        }
        return "Missing / conflict";
    }

    private void ResizeLastColumn()
    {
        if (_modList.Columns.Count == 0)
        {
            return;
        }
        _modList.Columns[^1].Width = Math.Max(
            140,
            _modList.ClientSize.Width - _modList.Columns[0].Width - 6);
    }

    private void UpdateSelectionButtons()
    {
        if (_initializing)
        {
            return;
        }
        _applyChangesButton.Enabled = HasPendingChanges();
        _removeButton.Enabled = _modList.SelectedItems.Count > 0;
    }

    private void ToggleAllMods()
    {
        var shouldCheck = _modList.Items.Cast<ListViewItem>().Any(item => !item.Checked);
        _initializing = true;
        _modList.BeginUpdate();
        try
        {
            foreach (ListViewItem item in _modList.Items)
            {
                item.Checked = shouldCheck;
            }
        }
        finally
        {
            _modList.EndUpdate();
            _initializing = false;
        }
        UpdateSelectionButtons();
    }

    private bool HasPendingChanges()
    {
        foreach (ListViewItem item in _modList.Items)
        {
            if (item.Tag is not string fileName)
            {
                continue;
            }

            var isActive = File.Exists(Path.Combine(_paksDirectory, fileName));
            var isRenamedDisabled = File.Exists(GetDisabledPath(fileName));
            var isLegacyDisabled = File.Exists(GetLegacyDisabledPath(fileName));
            if ((isActive && (isRenamedDisabled || isLegacyDisabled)) ||
                (!isActive && !isRenamedDisabled && !isLegacyDisabled) ||
                (isRenamedDisabled && isLegacyDisabled))
            {
                continue;
            }

            if (item.Checked != isActive)
            {
                return true;
            }
        }
        return false;
    }

    private string GetDisabledPath(string fileName)
    {
        return Path.Combine(_paksDirectory, fileName + ".disabled");
    }

    private string GetLegacyDisabledPath(string fileName) =>
        Path.Combine(_settingsDirectory, "DisabledMods", GetInstallKey(_paksDirectory), fileName);

    private static string GetInstallKey(string paksDirectory)
    {
        var normalized = Path.GetFullPath(paksDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    private static bool IsSafePakFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal) &&
        Path.GetExtension(fileName).Equals(".pak", StringComparison.OrdinalIgnoreCase) &&
        fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private void SetStatus(string message) => _statusLabel.Text = message;

    private void ShowError(string context, Exception ex)
    {
        SetStatus(context);
        MessageBox.Show(
            this,
            $"{context}\r\n\r\n{ex.Message}",
            "Redout 2 Mod Manager",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private sealed class ManagerSettings
    {
        public string GameDirectory { get; set; } = "";
    }

    private sealed class ModEntry
    {
        public string FileName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool HasSignature { get; set; }
    }
}
