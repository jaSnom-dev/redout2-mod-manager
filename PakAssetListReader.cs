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

    public static string NormalizePath(string path)
    {
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
