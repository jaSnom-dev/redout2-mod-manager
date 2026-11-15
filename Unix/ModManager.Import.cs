using System.IO.Compression;
using Redout2ModManager;

namespace Redout2ModManager.Unix;

internal sealed partial class ModManager
{
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

    private (int Imported, int AlreadyManaged, Dictionary<string, bool> RestoredStates) InstallArchive(string archivePath)
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
        var restoredMods = new List<(
            ModEntry Mod,
            bool PreviousHasSignature,
            bool HasSignature,
            bool Enabled,
            bool PreviousHasAssetList,
            List<string> PreviousAssetPaths,
            bool HasAssetList,
            List<string> AssetPaths,
            bool PreviousHasPakScan,
            List<string> PreviousScannedAssetPaths,
            long PreviousScannedPakLength,
            long PreviousScannedPakLastWriteTimeUtcTicks,
            bool HasPakScan,
            List<string> ScannedAssetPaths,
            long ScannedPakLength,
            long ScannedPakLastWriteTimeUtcTicks)>();
        var alreadyManaged = 0;
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
            var managedMod = _mods.FirstOrDefault(mod =>
                mod.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            if (managedMod is not null)
            {
                if (IsManagedPakInstalled(managedMod))
                {
                    alreadyManaged++;
                    continue;
                }
                fileName = managedMod.FileName;
            }
            else
            {
                EnsureNotRegisteredOrInstalled(fileName);
            }

            var assetListEntry = FindAssetListEntry(archive, pakEntry, fileName);
            var assetPaths = assetListEntry is null
                ? null
                : ReadPakAssetList(assetListEntry);
            var signatureEntry = FindSignatureEntry(archive, pakEntry, fileName);
            var signatureName = Path.ChangeExtension(fileName, ".sig");
            var signatureTarget = isDisabledPak ? GetDisabledPath(signatureName) : Path.Combine(_paksDirectory, signatureName);
            if (managedMod is not null)
            {
                var hasSignature = ValidateRestoredSignature(managedMod, isDisabledPak, signatureEntry is not null);
                restoredMods.Add((
                    managedMod,
                    managedMod.HasSignature,
                    hasSignature,
                    !isDisabledPak,
                    managedMod.HasAssetList,
                    managedMod.AssetPaths,
                    assetPaths is null ? managedMod.HasAssetList : true,
                    assetPaths ?? managedMod.AssetPaths,
                    managedMod.HasPakScan,
                    managedMod.ScannedAssetPaths,
                    managedMod.ScannedPakLength,
                    managedMod.ScannedPakLastWriteTimeUtcTicks,
                    false,
                    [],
                    0,
                    0));
            }
            else
            {
                if (signatureEntry is not null &&
                    (File.Exists(Path.Combine(_paksDirectory, signatureName)) || File.Exists(GetDisabledPath(signatureName))))
                {
                    throw new IOException($"A signature file already exists: {Path.GetFileName(signatureTarget)}");
                }
                newMods.Add(new ModEntry
                {
                    FileName = fileName,
                    DisplayName = Path.GetFileNameWithoutExtension(fileName),
                    HasSignature = signatureEntry is not null,
                    HasAssetList = assetPaths is not null,
                    AssetPaths = assetPaths ?? []
                });
            }
            filesToCopy.Add((pakEntry, isDisabledPak ? GetDisabledPath(fileName) : Path.Combine(_paksDirectory, fileName)));
            if (signatureEntry is not null)
            {
                filesToCopy.Add((signatureEntry, signatureTarget));
            }
        }

        if (filesToCopy.Count == 0)
        {
            return (0, alreadyManaged, new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));
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
            foreach (var restoredMod in restoredMods)
            {
                restoredMod.Mod.HasSignature = restoredMod.HasSignature;
                restoredMod.Mod.HasAssetList = restoredMod.HasAssetList;
                restoredMod.Mod.AssetPaths = restoredMod.AssetPaths;
                restoredMod.Mod.HasPakScan = restoredMod.HasPakScan;
                restoredMod.Mod.ScannedAssetPaths = restoredMod.ScannedAssetPaths;
                restoredMod.Mod.ScannedPakLength = restoredMod.ScannedPakLength;
                restoredMod.Mod.ScannedPakLastWriteTimeUtcTicks = restoredMod.ScannedPakLastWriteTimeUtcTicks;
            }
            SaveManifest();
        }
        catch
        {
            foreach (var mod in newMods)
            {
                _mods.Remove(mod);
            }
            foreach (var restoredMod in restoredMods)
            {
                restoredMod.Mod.HasSignature = restoredMod.PreviousHasSignature;
                restoredMod.Mod.HasAssetList = restoredMod.PreviousHasAssetList;
                restoredMod.Mod.AssetPaths = restoredMod.PreviousAssetPaths;
                restoredMod.Mod.HasPakScan = restoredMod.PreviousHasPakScan;
                restoredMod.Mod.ScannedAssetPaths = restoredMod.PreviousScannedAssetPaths;
                restoredMod.Mod.ScannedPakLength = restoredMod.PreviousScannedPakLength;
                restoredMod.Mod.ScannedPakLastWriteTimeUtcTicks = restoredMod.PreviousScannedPakLastWriteTimeUtcTicks;
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
        return (newMods.Count + restoredMods.Count, alreadyManaged,
            restoredMods.ToDictionary(mod => mod.Mod.FileName, mod => mod.Enabled, StringComparer.OrdinalIgnoreCase));
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

        if (Path.GetDirectoryName(fullSourcePath)!.Equals(_paksDirectory, FilePathComparison))
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
        var assetPaths = ReadPakAssetListFile(fullSourcePath);
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

            AddManagedMod(fileName, hasSignature, assetPaths);
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

    private bool IsManagedPakInstalled(ModEntry mod)
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
        return isActive || isRenamedDisabled || isLegacyDisabled;
    }

    private bool ValidateRestoredSignature(ModEntry mod, bool isDisabledPak, bool hasSourceSignature)
    {
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
                targetSignature);
        }
        if (!mod.HasSignature && hasTargetSignature)
        {
            throw new IOException($"A signature file already exists: {Path.GetFileName(targetSignature)}");
        }
        return hasSourceSignature || hasTargetSignature;
    }

    private bool RestoreManagedPak(ModEntry mod, string sourcePath)
    {
        if (IsManagedPakInstalled(mod))
        {
            return false;
        }

        var isDisabledPak = IsDisabledPakPath(sourcePath);
        var sourceSignature = GetSourceSignaturePath(sourcePath, isDisabledPak);
        var hasSourceSignature = File.Exists(sourceSignature);
        var sourceAssetPaths = ReadPakAssetListFile(sourcePath);
        var targetPak = isDisabledPak ? GetDisabledPath(mod.FileName) : Path.Combine(_paksDirectory, mod.FileName);
        var signatureName = Path.ChangeExtension(mod.FileName, ".sig");
        var targetSignature = isDisabledPak ? GetDisabledPath(signatureName) : Path.Combine(_paksDirectory, signatureName);
        var hasSignature = ValidateRestoredSignature(mod, isDisabledPak, hasSourceSignature);

        var copiedFiles = new List<string>();
        var previousHasSignature = mod.HasSignature;
        var previousHasAssetList = mod.HasAssetList;
        var previousAssetPaths = mod.AssetPaths;
        var previousHasPakScan = mod.HasPakScan;
        var previousScannedAssetPaths = mod.ScannedAssetPaths;
        var previousScannedPakLength = mod.ScannedPakLength;
        var previousScannedPakLastWriteTimeUtcTicks = mod.ScannedPakLastWriteTimeUtcTicks;
        try
        {
            File.Copy(sourcePath, targetPak, overwrite: false);
            copiedFiles.Add(targetPak);
            if (hasSourceSignature)
            {
                File.Copy(sourceSignature, targetSignature, overwrite: false);
                copiedFiles.Add(targetSignature);
            }

            mod.HasSignature = hasSignature;
            if (sourceAssetPaths is not null)
            {
                mod.HasAssetList = true;
                mod.AssetPaths = sourceAssetPaths;
            }
            mod.HasPakScan = false;
            mod.ScannedAssetPaths = [];
            mod.ScannedPakLength = 0;
            mod.ScannedPakLastWriteTimeUtcTicks = 0;
            SaveManifest();
            return true;
        }
        catch
        {
            mod.HasSignature = previousHasSignature;
            mod.HasAssetList = previousHasAssetList;
            mod.AssetPaths = previousAssetPaths;
            mod.HasPakScan = previousHasPakScan;
            mod.ScannedAssetPaths = previousScannedAssetPaths;
            mod.ScannedPakLength = previousScannedPakLength;
            mod.ScannedPakLastWriteTimeUtcTicks = previousScannedPakLastWriteTimeUtcTicks;
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

    private static ZipArchiveEntry? FindAssetListEntry(ZipArchive archive, ZipArchiveEntry pakEntry, string pakFileName)
    {
        var normalizedPath = pakEntry.FullName.Replace('\\', '/');
        var directory = Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/') ?? "";
        var listName = pakFileName + ".list";
        var fullListPath = string.IsNullOrEmpty(directory) ? listName : $"{directory}/{listName}";
        var matches = archive.Entries.Where(entry =>
            entry.FullName.Replace('\\', '/').Equals(fullListPath, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        if (matches.Count > 1)
        {
            throw new InvalidDataException($"The ZIP contains multiple asset lists for '{pakFileName}'.");
        }
        return matches.SingleOrDefault();
    }

    private static List<string> ReadPakAssetList(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return PakAssetListReader.Read(stream, entry.Length);
    }

    private static List<string>? ReadPakAssetListFile(string pakPath)
    {
        var directory = Path.GetDirectoryName(pakPath)
            ?? throw new InvalidDataException($"Could not determine the directory for '{pakPath}'.");
        var listPath = Path.Combine(directory, GetPakFileName(pakPath) + ".list");
        if (!File.Exists(listPath))
        {
            return null;
        }

        using var stream = File.OpenRead(listPath);
        return PakAssetListReader.Read(stream, stream.Length);
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
        if (!Path.GetDirectoryName(fullPath)!.Equals(_paksDirectory, FilePathComparison))
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
        AddManagedMod(fileName, File.Exists(signaturePath), ReadPakAssetListFile(fullPath));
    }

    private void RegisterExistingDisabledPak(string fullPath, string fileName)
    {
        var disabledPak = GetDisabledPath(fileName);
        if (!Path.GetFullPath(fullPath).Equals(disabledPak, FilePathComparison))
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
        AddManagedMod(fileName, File.Exists(GetDisabledPath(signatureName)), ReadPakAssetListFile(disabledPak));
    }

    private void AddManagedMod(string fileName, bool hasSignature, List<string>? assetPaths)
    {
        var mod = new ModEntry
        {
            FileName = fileName,
            DisplayName = Path.GetFileNameWithoutExtension(fileName),
            HasSignature = hasSignature,
            HasAssetList = assetPaths is not null,
            AssetPaths = assetPaths ?? []
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

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

}
