using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Redout2ModManager;

namespace Redout2ModManager.Unix;

internal sealed partial class ModManager
{
    private readonly string _settingsDirectory;
    private readonly List<ModEntry> _mods = [];
    private string _paksDirectory = "";
    private string _manifestPath = "";

    public string GameDirectory { get; private set; } = "";
    public string PaksDirectory => _paksDirectory;

    public ModManager(string? settingsDirectory = null)
    {
        _settingsDirectory = settingsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Redout2ModManager");
        Directory.CreateDirectory(_settingsDirectory);
    }

    public string? FindGame()
    {
        var settings = Path.Combine(_settingsDirectory, "settings.json");
        if (File.Exists(settings))
        {
            var saved = JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(settings))
                ?? throw new InvalidDataException("The saved settings are invalid.");
            if (FindPaksDirectory(saved.GameDirectory) is not null)
                return saved.GameDirectory;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"),
            Path.Combine(home, "Library", "Application Support", "Steam")
        };
        var libraries = new HashSet<string>(roots, StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
                continue;
            foreach (Match match in Regex.Matches(File.ReadAllText(vdf),
                "\"(?:path|\\d+)\"\\s+\"(?<path>(?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase))
            {
                var path = match.Groups["path"].Value.Replace(@"\\", @"\").Replace("\\\"", "\"");
                if (Path.IsPathFullyQualified(path))
                    libraries.Add(path);
            }
        }

        foreach (var library in libraries)
        {
            var apps = Path.Combine(library, "steamapps");
            var manifest = Path.Combine(apps, "appmanifest_1799930.acf");
            if (File.Exists(manifest))
            {
                var name = Regex.Match(File.ReadAllText(manifest),
                    "\"installdir\"\\s+\"(?<name>[^\"]+)\"", RegexOptions.IgnoreCase).Groups["name"].Value;
                if (!string.IsNullOrWhiteSpace(name) && Path.GetFileName(name) == name)
                {
                    var game = Path.Combine(apps, "common", name);
                    if (FindPaksDirectory(game) is not null)
                        return game;
                }
            }
            foreach (var name in new[] { "Redout2", "Redout 2" })
            {
                var game = Path.Combine(apps, "common", name);
                if (FindPaksDirectory(game) is not null)
                    return game;
            }
        }
        return null;
    }

    public void SelectGame(string path)
    {
        var paks = FindPaksDirectory(path)
            ?? throw new DirectoryNotFoundException("Could not find the game's Content/Paks folder.");
        var game = Path.GetFullPath(path);
        var manifest = Path.Combine(_settingsDirectory, $"{GetInstallKey(paks)}-mods.json");
        var entries = ReadManifest(manifest);
        var settings = Path.Combine(_settingsDirectory, "settings.json");
        WriteJson(settings, new ManagerSettings { GameDirectory = game });
        GameDirectory = game;
        _paksDirectory = paks;
        _manifestPath = manifest;
        _mods.Clear();
        _mods.AddRange(entries);
    }

    private static string? FindPaksDirectory(string path)
    {
        if (!Directory.Exists(path))
            return null;
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (directory.Name.Equals("Paks", StringComparison.OrdinalIgnoreCase) &&
            directory.Parent?.Name.Equals("Content", StringComparison.OrdinalIgnoreCase) == true)
            return directory.FullName;
        if (directory.Name.Equals("Content", StringComparison.OrdinalIgnoreCase))
        {
            var child = Path.Combine(directory.FullName, "Paks");
            if (Directory.Exists(child))
                return child;
        }
        return new[]
        {
            Path.Combine(directory.FullName, "redout2", "Content", "Paks"),
            Path.Combine(directory.FullName, "Redout2", "Content", "Paks"),
            Path.Combine(directory.FullName, "Content", "Paks")
        }.FirstOrDefault(Directory.Exists);
    }

    private static List<ModEntry> ReadManifest(string path)
    {
        if (!File.Exists(path))
            return [];
        var entries = JsonSerializer.Deserialize<List<ModEntry>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The saved mod list is invalid.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!IsSafePakFileName(entry.FileName) || !names.Add(entry.FileName))
                throw new InvalidDataException($"Invalid or duplicate PAK name in saved mod list: {entry.FileName}");
            entry.AssetPaths = Normalize(entry.AssetPaths ?? []);
            entry.ScannedAssetPaths = Normalize(entry.ScannedAssetPaths ?? []);
            entry.HasAssetList |= entry.AssetPaths.Count > 0;
        }
        return entries;
    }

    private static List<string> Normalize(IEnumerable<string> paths) => paths
        .Select(PakAssetListReader.NormalizePath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static void WriteJson<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }

    private void SaveManifest() => WriteJson(_manifestPath, _mods);

    public (int Imported, int Skipped, List<string> Errors) Import(IEnumerable<string> paths)
    {
        RequireGame();
        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (IsPakImportPath(path))
                {
                    if (ImportPak(path)) imported++; else skipped++;
                }
                else if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var result = InstallArchive(path);
                    imported += result.Imported;
                    skipped += result.AlreadyManaged;
                }
                else
                    throw new InvalidDataException("Choose a PAK, disabled PAK, or ZIP file.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return (imported, skipped, errors);
    }

    public void Refresh()
    {
        RequireGame();
        var entries = ReadManifest(_manifestPath);
        var present = entries.Where(mod => File.Exists(Path.Combine(_paksDirectory, mod.FileName)) ||
            File.Exists(GetDisabledPath(mod.FileName)) || File.Exists(GetLegacyDisabledPath(mod.FileName))).ToList();
        if (present.Count != entries.Count)
            WriteJson(_manifestPath, present);
        _mods.Clear();
        _mods.AddRange(present);
    }

    public void Apply(IReadOnlyDictionary<string, bool> desired)
    {
        RequireGame();
        foreach (var mod in _mods)
        {
            if (!desired.TryGetValue(mod.FileName, out var enabled))
                continue;
            var active = File.Exists(Path.Combine(_paksDirectory, mod.FileName));
            var disabled = File.Exists(GetDisabledPath(mod.FileName));
            var legacy = File.Exists(GetLegacyDisabledPath(mod.FileName));
            if ((active ? 1 : 0) + (disabled ? 1 : 0) + (legacy ? 1 : 0) != 1)
                throw new IOException($"Cannot determine the state of '{mod.FileName}'.");
            if (enabled != active)
                TransferModFiles(mod, enabled, legacy);
        }
    }

    public void Uninstall(string fileName)
    {
        RequireGame();
        var mod = _mods.Single(mod => mod.FileName == fileName);
        foreach (var name in mod.HasSignature
            ? new[] { mod.FileName, Path.ChangeExtension(mod.FileName, ".sig") }
            : new[] { mod.FileName })
        {
            DeleteIfExists(Path.Combine(_paksDirectory, name));
            DeleteIfExists(GetDisabledPath(name));
            DeleteIfExists(GetLegacyDisabledPath(name));
        }
        _mods.Remove(mod);
        SaveManifest();
    }

    public IReadOnlyList<ModRow> GetRows()
    {
        var owners = new Dictionary<string, List<ModEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in _mods.Where(m => File.Exists(Path.Combine(_paksDirectory, m.FileName))))
        {
            if (!TryGetPaths(mod, out var paths))
                continue;
            foreach (var path in paths)
            {
                if (!owners.TryGetValue(path, out var list))
                    owners.Add(path, list = []);
                list.Add(mod);
            }
        }
        return _mods.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).Select(mod =>
        {
            var active = File.Exists(Path.Combine(_paksDirectory, mod.FileName));
            var disabled = File.Exists(GetDisabledPath(mod.FileName));
            var legacy = File.Exists(GetLegacyDisabledPath(mod.FileName));
            var status = (active, disabled, legacy) switch
            {
                (true, false, false) => "Enabled",
                (false, true, false) => "Disabled",
                (false, false, true) => "Disabled (legacy)",
                _ => "Missing / conflict"
            };
            var hasPaths = TryGetPaths(mod, out var paths);
            var overlaps = active && hasPaths
                ? paths.Where(p => owners.TryGetValue(p, out var list) && list.Count > 1)
                    .Select(p => $"{p} (also in {string.Join(", ", owners[p].Where(m => m != mod).Select(m => m.DisplayName))})")
                    .ToList()
                : [];
            var conflicts = !hasPaths ? "Not scanned" : !active ? "Inactive" :
                paths.Count == 0 ? "No assets" : overlaps.Count == 0 ? "None" : $"{overlaps.Count} asset(s)";
            var details = !hasPaths ? "No asset paths are available. Scan the PAK or provide a .pak.list file." :
                !active ? "This mod is disabled and excluded from conflict detection." :
                overlaps.Count == 0 ? "No known overlaps with enabled, managed mods." :
                string.Join(Environment.NewLine, overlaps);
            return new ModRow(mod.FileName, mod.DisplayName, active, status, conflicts, details);
        }).ToList();
    }

    private bool TryGetPaths(ModEntry mod, out IReadOnlyList<string> paths)
    {
        if (mod.HasPakScan)
        {
            var path = GetManagedPakPath(mod);
            if (path is not null)
            {
                var file = new FileInfo(path);
                if (file.Exists && file.Length == mod.ScannedPakLength &&
                    file.LastWriteTimeUtc.Ticks == mod.ScannedPakLastWriteTimeUtcTicks)
                {
                    paths = mod.ScannedAssetPaths;
                    return true;
                }
            }
        }
        paths = mod.HasAssetList ? mod.AssetPaths : [];
        return mod.HasAssetList;
    }

    public List<string> Scan()
    {
        RequireGame();
        var errors = new List<string>();
        var previous = _mods.Select(mod => (mod, mod.HasPakScan, mod.ScannedAssetPaths,
            mod.ScannedPakLength, mod.ScannedPakLastWriteTimeUtcTicks)).ToList();
        foreach (var mod in _mods)
        {
            try
            {
                var path = GetManagedPakPath(mod) ?? throw new FileNotFoundException("PAK file is missing.");
                var before = new FileInfo(path);
                var length = before.Length;
                var ticks = before.LastWriteTimeUtc.Ticks;
                var paths = PakArchiveScanner.Scan(path);
                var after = new FileInfo(path);
                if (!after.Exists || after.Length != length || after.LastWriteTimeUtc.Ticks != ticks)
                    throw new IOException("The PAK changed during scanning.");
                mod.HasPakScan = true;
                mod.ScannedAssetPaths = paths;
                mod.ScannedPakLength = length;
                mod.ScannedPakLastWriteTimeUtcTicks = ticks;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Add($"{mod.FileName}: {ex.Message}");
            }
        }
        try { SaveManifest(); }
        catch
        {
            foreach (var scan in previous)
            {
                scan.mod.HasPakScan = scan.HasPakScan;
                scan.mod.ScannedAssetPaths = scan.ScannedAssetPaths;
                scan.mod.ScannedPakLength = scan.ScannedPakLength;
                scan.mod.ScannedPakLastWriteTimeUtcTicks = scan.ScannedPakLastWriteTimeUtcTicks;
            }
            throw;
        }
        return errors;
    }

    private string? GetManagedPakPath(ModEntry mod)
    {
        var paths = new[] { Path.Combine(_paksDirectory, mod.FileName), GetDisabledPath(mod.FileName), GetLegacyDisabledPath(mod.FileName) }
            .Where(File.Exists).ToArray();
        if (paths.Length > 1)
            throw new IOException($"Multiple copies of '{mod.FileName}' exist.");
        return paths.SingleOrDefault();
    }

    private void RequireGame()
    {
        if (!Directory.Exists(_paksDirectory))
            throw new DirectoryNotFoundException("Select the Redout 2 game folder first.");
    }

    private string GetDisabledPath(string name) => Path.Combine(_paksDirectory, name + ".disabled");
    private static StringComparison FilePathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private string GetLegacyDisabledPath(string name) => Path.Combine(_settingsDirectory, "DisabledMods", GetInstallKey(_paksDirectory), name);
    private static string GetInstallKey(string path) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar))))[..16];
    private static bool IsSafePakFileName(string name) =>
        !string.IsNullOrWhiteSpace(name) && Path.GetFileName(name) == name &&
        !name.Contains('\\') && Path.GetExtension(name).Equals(".pak", StringComparison.OrdinalIgnoreCase) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private sealed class ManagerSettings
    {
        public string GameDirectory { get; set; } = "";
    }
    private sealed class ModEntry
    {
        public string FileName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool HasSignature { get; set; }
        public bool HasAssetList { get; set; }
        public List<string> AssetPaths { get; set; } = [];
        public bool HasPakScan { get; set; }
        public List<string> ScannedAssetPaths { get; set; } = [];
        public long ScannedPakLength { get; set; }
        public long ScannedPakLastWriteTimeUtcTicks { get; set; }
    }
}

internal sealed record ModRow(string FileName, string DisplayName, bool Enabled, string Status, string Conflicts, string Details);
