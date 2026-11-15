using System.Text;

namespace Redout2ModManager;

internal static class PakAssetListReader
{
    private const int MaximumFileSize = 4 * 1024 * 1024;
    private const int MaximumPathCount = 100_000;
    private const int MaximumPathLength = 8_192;

    public static List<string> Read(Stream stream, long length)
    {
        if (length > MaximumFileSize)
        {
            throw new InvalidDataException($"The .pak.list file exceeds the {MaximumFileSize / 1024 / 1024} MB limit.");
        }

        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        var lineCount = 0;
        var characterCount = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineCount++;
            characterCount += line.Length + 1;
            if (lineCount > MaximumPathCount * 2 || characterCount > MaximumFileSize)
            {
                throw new InvalidDataException("The .pak.list file exceeds its parsing limits.");
            }

            var value = line.Trim();
            if (value.Length == 0 || value.StartsWith('#'))
            {
                continue;
            }
            if (value.Length > MaximumPathLength)
            {
                throw new InvalidDataException($"A path in the .pak.list file exceeds {MaximumPathLength} characters.");
            }

            var normalized = NormalizePath(value);
            paths.Add(normalized);
            if (paths.Count > MaximumPathCount)
            {
                throw new InvalidDataException($"The .pak.list file contains more than {MaximumPathCount} paths.");
            }
        }

        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Manifests are written already normalized, so skip the allocation-heavy rebuild when nothing would change.
    public static List<string> NormalizeSortedPaths(List<string> paths)
    {
        var sorted = true;
        string? previous = null;
        foreach (var path in paths)
        {
            if (!IsCanonical(path))
            {
                sorted = false;
                break;
            }
            if (previous is not null && StringComparer.OrdinalIgnoreCase.Compare(previous, path) >= 0)
            {
                sorted = false;
                break;
            }
            previous = path;
        }
        return sorted
            ? paths
            : paths.Select(NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    private static bool IsCanonical(string path)
    {
        var segmentStart = 0;
        for (var i = 0; i <= path.Length; i++)
        {
            var c = i < path.Length ? path[i] : '/';
            if (c is '\\' or ':' or '\0')
            {
                return false;
            }
            if (c != '/')
            {
                continue;
            }

            var length = i - segmentStart;
            if (length == 0 ||
                (length == 1 && path[segmentStart] == '.') ||
                (length == 2 && path[segmentStart] == '.' && path[segmentStart + 1] == '.'))
            {
                return false;
            }
            segmentStart = i + 1;
        }
        return true;
    }

    public static string NormalizePath(string path)
    {
        if (IsCanonical(path))
        {
            return path;
        }

        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (normalized.StartsWith('/') ||
            normalized.Contains(':') ||
            normalized.Contains('\0') ||
            segments.Any(segment => segment == ".."))
        {
            throw new InvalidDataException($"The .pak.list path is not a safe relative asset path: {path}");
        }

        var safeSegments = segments.Where(segment => segment.Length > 0 && segment != ".").ToArray();
        if (safeSegments.Length == 0)
        {
            throw new InvalidDataException($"The .pak.list file contains an empty asset path: {path}");
        }

        return string.Join('/', safeSegments);
    }
}
