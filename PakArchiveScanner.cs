using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Versions;

namespace Redout2ModManager;

internal static class PakArchiveScanner
{
    private const long MaximumIndexSize = 64 * 1024 * 1024;
    private const int MaximumPathCount = 100_000;

    public static List<string> Scan(string pakPath)
    {
        using var reader = new PakFileReader(pakPath, new VersionContainer(EGame.GAME_UE4_27));
        if (reader.Info.IndexSize > MaximumIndexSize)
        {
            throw new InvalidDataException(
                $"The PAK index exceeds the {MaximumIndexSize / 1024 / 1024} MB scan limit.");
        }
        if (reader.IsEncrypted)
        {
            throw new InvalidDataException("The PAK index is encrypted and cannot be scanned without its key.");
        }

        reader.Mount(StringComparer.OrdinalIgnoreCase);
        if (reader.Files.Count > MaximumPathCount)
        {
            throw new InvalidDataException($"The PAK contains more than {MaximumPathCount} indexed paths.");
        }

        return reader.Files.Keys
            .Select(PakAssetListReader.NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
