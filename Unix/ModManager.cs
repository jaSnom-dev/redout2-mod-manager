using System.Runtime.InteropServices;
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

    private static List<string> Normalize(List<string> paths) => PakAssetListReader.NormalizeSortedPaths(paths);

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

    public Dictionary<string, string> ApplyLoadOrder(IReadOnlyList<string> orderedNames)
    {
        RequireGame();
        if (orderedNames.Count != _mods.Count || orderedNames.Count > 999)
            throw new InvalidOperationException("Reordering requires the complete mod list and supports at most 999 mods.");
        var modsByName = _mods.ToDictionary(mod => mod.FileName, StringComparer.OrdinalIgnoreCase);
        if (orderedNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != _mods.Count ||
            orderedNames.Any(name => !modsByName.ContainsKey(name)))
            throw new InvalidOperationException("The mod list changed; refresh it before changing order.");

        var changes = orderedNames.Select((name, index) =>
        {
            var mod = modsByName[name];
            var newName = $"{index + 1:D3}_{Regex.Replace(mod.FileName, @"^\d{3}_", "")}";
            if (!IsSafePakFileName(newName))
                throw new InvalidDataException($"Invalid reordered PAK name: {newName}");
            return (Mod: mod, OldName: mod.FileName, OldDisplay: mod.DisplayName, NewName: newName);
        }).ToList();
        if (changes.Select(change => change.NewName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != changes.Count)
            throw new IOException("The reordered mods would have duplicate filenames.");

        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var sourcePaths = new HashSet<string>(pathComparer);
        var files = new List<LoadOrderFile>();
        void AddFile(string source, string destination)
        {
            if (!sourcePaths.Add(source))
                throw new IOException($"Multiple mods refer to the same file: {source}");
            files.Add(new LoadOrderFile(source, destination));
        }

        foreach (var change in changes)
        {
            var oldPaks = GetLoadOrderPaths(change.OldName);
            var newPaks = GetLoadOrderPaths(change.NewName);
            var present = Enumerable.Range(0, oldPaks.Length).Where(index => File.Exists(oldPaks[index])).ToArray();
            if (present.Length != 1)
                throw new IOException($"Cannot determine the state of '{change.OldName}'; resolve missing or duplicate files first.");
            AddFile(oldPaks[present[0]], newPaks[present[0]]);
            if (change.Mod.HasSignature)
            {
                var oldSignatures = GetLoadOrderPaths(Path.ChangeExtension(change.OldName, ".sig"));
                var newSignatures = GetLoadOrderPaths(Path.ChangeExtension(change.NewName, ".sig"));
                if (!File.Exists(oldSignatures[present[0]]) ||
                    oldSignatures.Where((_, index) => index != present[0]).Any(File.Exists))
                    throw new IOException($"The matching signature for '{change.OldName}' is missing or has conflicting copies.");
                AddFile(oldSignatures[present[0]], newSignatures[present[0]]);
            }
            else if (GetLoadOrderPaths(Path.ChangeExtension(change.OldName, ".sig")).Any(File.Exists))
                throw new IOException($"An unregistered signature exists for '{change.OldName}'; resolve it before reordering.");
        }
        foreach (var change in changes)
        {
            foreach (var target in GetLoadOrderPaths(change.NewName)
                .Concat(GetLoadOrderPaths(Path.ChangeExtension(change.NewName, ".sig"))))
            {
                if ((File.Exists(target) || Directory.Exists(target)) && !sourcePaths.Contains(target))
                    throw new IOException($"Refusing to overwrite an existing file or folder: {target}");
            }
        }

        var moving = files.Where(file => !file.Source.Equals(file.Destination, FilePathComparison)).ToList();
        try
        {
            foreach (var file in moving)
            {
                File.Move(file.Source, file.Temporary);
                file.Current = file.Temporary;
            }
            foreach (var file in moving)
            {
                File.Move(file.Temporary, file.Destination);
                file.Current = file.Destination;
            }
            foreach (var change in changes)
            {
                change.Mod.FileName = change.NewName;
                change.Mod.DisplayName = Path.GetFileNameWithoutExtension(change.NewName);
            }
            SaveManifest();
        }
        catch (Exception operationError)
        {
            foreach (var change in changes)
            {
                change.Mod.FileName = change.OldName;
                change.Mod.DisplayName = change.OldDisplay;
            }
            var rollbackErrors = new List<Exception>();
            foreach (var file in moving.Where(file => file.Current == file.Destination).Reverse())
            {
                try { File.Move(file.Destination, file.Temporary); file.Current = file.Temporary; }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }
            foreach (var file in moving.Where(file => file.Current == file.Temporary).Reverse())
            {
                try { File.Move(file.Temporary, file.Source); file.Current = file.Source; }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }
            if (rollbackErrors.Count > 0)
            {
                rollbackErrors.Insert(0, operationError);
                throw new AggregateException("Changing the mod order failed and rollback was incomplete.", rollbackErrors);
            }
            throw;
        }
        return changes.ToDictionary(change => change.OldName, change => change.NewName, StringComparer.OrdinalIgnoreCase);
    }

    private string[] GetLoadOrderPaths(string name) =>
        [Path.Combine(_paksDirectory, name), GetDisabledPath(name), GetLegacyDisabledPath(name)];

    private List<ModSnapshot> CaptureSnapshots(bool includeInactivePaths)
    {
        var snapshots = new List<ModSnapshot>(_mods.Count);
        foreach (var mod in _mods)
        {
            var active = File.Exists(Path.Combine(_paksDirectory, mod.FileName));
            IReadOnlyList<string> paths = [];
            var hasPaths = (active || includeInactivePaths) && TryGetPaths(mod, out paths);
            snapshots.Add(new ModSnapshot(mod.FileName, mod.DisplayName, active, hasPaths, paths));
        }
        return snapshots;
    }

    // One point-in-time overlap index serves counts, details and the report so they cannot disagree.
    private static OverlapIndex BuildOverlapIndex(IReadOnlyList<ModSnapshot> snapshots)
    {
        var capacity = 0;
        foreach (var snapshot in snapshots)
            if (snapshot.Active && snapshot.HasPaths)
                capacity = Math.Max(capacity, snapshot.Paths.Count);

        var firstOwners = new Dictionary<string, FirstPathOwner>(capacity, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SharedPathBuilder>? repeated = null;
        var sequence = 0;
        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            if (!snapshot.Active || !snapshot.HasPaths)
                continue;
            var position = 0;
            foreach (var path in snapshot.Paths)
            {
                ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(firstOwners, path, out var exists);
                if (!exists)
                    slot = new FirstPathOwner(path, index, position, sequence++);
                else
                {
                    repeated ??= new Dictionary<string, SharedPathBuilder>(StringComparer.OrdinalIgnoreCase);
                    if (!repeated.TryGetValue(slot.Path, out var builder))
                    {
                        builder = new SharedPathBuilder(slot.Path, slot.Sequence);
                        builder.Owners.Add(slot.Owner);
                        builder.Positions.Add(slot.Position);
                        repeated.Add(slot.Path, builder);
                    }
                    builder.Owners.Add(index);
                    builder.Positions.Add(position);
                }
                position++;
            }
        }

        var shared = new List<SharedPath>(repeated?.Count ?? 0);
        if (repeated is not null)
        {
            foreach (var builder in repeated.Values.OrderBy(builder => builder.Sequence))
            {
                var owners = builder.Owners.ToArray();
                var byName = owners.OrderBy(owner => snapshots[owner].FileName, StringComparer.OrdinalIgnoreCase).ToArray();
                shared.Add(new SharedPath(builder.Path, owners, builder.Positions.ToArray(), byName));
            }
        }

        var byMod = new List<(int Position, int Shared)>?[snapshots.Count];
        for (var s = 0; s < shared.Count; s++)
        {
            var item = shared[s];
            for (var k = 0; k < item.Owners.Length; k++)
                (byMod[item.Owners[k]] ??= []).Add((item.Positions[k], s));
        }
        return new OverlapIndex(snapshots, shared, byMod);
    }

    public IReadOnlyList<ModRow> GetRows()
    {
        var snapshots = CaptureSnapshots(includeInactivePaths: true);
        var overlaps = BuildOverlapIndex(snapshots);
        return Enumerable.Range(0, snapshots.Count)
            .OrderBy(i => snapshots[i].DisplayName, StringComparer.OrdinalIgnoreCase).Select(modIndex =>
        {
            var snapshot = snapshots[modIndex];
            var active = snapshot.Active;
            var disabled = File.Exists(GetDisabledPath(snapshot.FileName));
            var legacy = File.Exists(GetLegacyDisabledPath(snapshot.FileName));
            var status = (active, disabled, legacy) switch
            {
                (true, false, false) => "Enabled",
                (false, true, false) => "Disabled",
                (false, false, true) => "Disabled (legacy)",
                _ => "Missing / conflict"
            };
            var hasPaths = snapshot.HasPaths;
            var paths = snapshot.Paths;
            var fileName = snapshot.FileName;
            var entries = overlaps.ByMod[modIndex];
            var overlapCount = entries?.Count ?? 0;
            var otherCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (entries is not null)
            {
                foreach (var (_, sharedIndex) in entries)
                    foreach (var other in overlaps.Shared[sharedIndex].OwnersByName)
                        if (other != modIndex)
                        {
                            ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(
                                otherCounts, snapshots[other].FileName, out _);
                            count++;
                        }
            }
            var counts = otherCounts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Value} file(s) with {pair.Key}");
            var conflicts = !hasPaths ? "Not scanned" : !active ? "Inactive" :
                paths.Count == 0 ? "No assets" : overlapCount == 0 ? "None" : string.Join("; ", counts);
            // Full overlap text can be very large, so it is built only when requested for this row snapshot.
            var details = new Lazy<string>(() => !hasPaths ? "No asset paths are available. Scan the PAK or provide a .pak.list file." :
                !active ? "This mod is disabled and excluded from conflict detection." :
                overlapCount == 0 ? "No known overlaps with enabled, managed mods." :
                string.Join(Environment.NewLine, entries!.OrderBy(entry => entry.Position).Select(entry =>
                    $"{paths[entry.Position]} (also in {string.Join(", ", overlaps.OtherOwnerNames(entry.Shared, modIndex))})")));
            return new ModRow(fileName, snapshot.DisplayName, active, status, conflicts, details, overlapCount > 0);
        }).ToList();
    }

    public string BuildConflictList()
    {
        RequireGame();
        var snapshots = CaptureSnapshots(includeInactivePaths: false);
        var overlaps = BuildOverlapIndex(snapshots);
        var report = new StringBuilder();
        report.AppendLine("Potential overlaps between enabled, managed archives.");
        report.AppendLine("This reflects applied file states, not pending checkbox changes. Load order is not inferred.");
        report.AppendLine();
        if (overlaps.Shared.Count == 0)
            report.AppendLine("No known asset-path overlaps.");
        else
        {
            var byArchive = new List<int>?[snapshots.Count];
            foreach (var sharedIndex in Enumerable.Range(0, overlaps.Shared.Count)
                .OrderBy(i => overlaps.Shared[i].Path, StringComparer.OrdinalIgnoreCase))
                foreach (var owner in overlaps.Shared[sharedIndex].Owners)
                    (byArchive[owner] ??= []).Add(sharedIndex);
            foreach (var modIndex in Enumerable.Range(0, snapshots.Count).Where(i => byArchive[i] is not null)
                .OrderBy(i => snapshots[i].FileName, StringComparer.OrdinalIgnoreCase))
            {
                report.Append("Archive: ").AppendLine(snapshots[modIndex].FileName);
                foreach (var sharedIndex in byArchive[modIndex]!)
                {
                    report.Append("  ").AppendLine(overlaps.Shared[sharedIndex].Path);
                    report.Append("    Conflicts with: ")
                        .AppendJoin(", ", overlaps.OtherOwnerNames(sharedIndex, modIndex)).AppendLine();
                }
                report.AppendLine();
            }
        }
        var unscanned = snapshots.Where(snapshot => snapshot.Active && !snapshot.HasPaths)
            .Select(snapshot => snapshot.FileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        if (unscanned.Count > 0)
        {
            report.AppendLine("Not scanned (conflicts are unknown):");
            foreach (var name in unscanned) report.AppendLine($"  {name}");
        }
        return report.ToString();
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
    private readonly record struct ModSnapshot(string FileName, string DisplayName, bool Active, bool HasPaths, IReadOnlyList<string> Paths);
    private readonly record struct FirstPathOwner(string Path, int Owner, int Position, int Sequence);
    private sealed record SharedPathBuilder(string Path, int Sequence)
    {
        public List<int> Owners { get; } = [];
        public List<int> Positions { get; } = [];
    }
    private sealed record SharedPath(string Path, int[] Owners, int[] Positions, int[] OwnersByName);
    private sealed class OverlapIndex(IReadOnlyList<ModSnapshot> snapshots, List<SharedPath> shared,
        List<(int Position, int Shared)>?[] byMod)
    {
        public List<SharedPath> Shared { get; } = shared;
        // Per owner, one entry per listed path (position in that mod's own path list); only enabled, overlapping mods have entries.
        public List<(int Position, int Shared)>?[] ByMod { get; } = byMod;

        public IEnumerable<string> OtherOwnerNames(int sharedIndex, int modIndex) =>
            Shared[sharedIndex].OwnersByName.Where(other => other != modIndex).Select(other => snapshots[other].FileName);
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
    private sealed class LoadOrderFile(string source, string destination)
    {
        public string Source { get; } = source;
        public string Destination { get; } = destination;
        public string Temporary { get; } = Path.Combine(Path.GetDirectoryName(source)!, $".redout2-order-{Guid.NewGuid():N}.tmp");
        public string Current { get; set; } = source;
    }
}

internal sealed class ModRow(string fileName, string displayName, bool enabled, string status, string conflicts,
    Lazy<string> details, bool hasConflicts)
{
    public string FileName { get; } = fileName;
    public string DisplayName { get; } = displayName;
    public bool Enabled { get; } = enabled;
    public string Status { get; } = status;
    public string Conflicts { get; } = conflicts;
    public string Details => details.Value;
    public bool HasConflicts { get; } = hasConflicts;
}
