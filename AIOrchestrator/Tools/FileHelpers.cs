using System.Text;

namespace AIOrchestrator.Tools;

/// <summary>Small helpers shared by the file tools.</summary>
public static class FileHelpers
{
    // UTF-8 without the invisible byte-order mark at the start, the usual choice for text and code files.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The encoding to use when writing back to an existing file, judged from its byte-order mark.
    /// Visual Studio saves many files as UTF-8 *with* a BOM, and some Windows tools write UTF-16.
    /// Reading strips the BOM, so without this every edit would silently change the file's encoding.
    /// </summary>
    public static Encoding DetectEncoding(string fullPath)
    {
        using var stream = File.OpenRead(fullPath);
        var bom = new byte[3];
        var read = stream.Read(bom, 0, bom.Length);
        return bom.AsSpan(0, read) switch
        {
            [0xEF, 0xBB, 0xBF] => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            [0xFF, 0xFE, ..] => Encoding.Unicode,
            [0xFE, 0xFF, ..] => Encoding.BigEndianUnicode,
            _ => Utf8NoBom
        };
    }

    /// <summary>1536 -> "1.5 KB", for showing file sizes to the model and the user.</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB"
    };

    /// <summary>
    /// Models write "\n" line endings, but many Windows files use "\r\n". When changing an
    /// existing file, convert the new text to match, so the file doesn't end up with a mix
    /// (which shows as a change on every line in tools like git).
    /// </summary>
    public static string MatchLineEndings(string text, string existingFileText) =>
        existingFileText.Contains("\r\n") ? text.ReplaceLineEndings("\r\n") : text.ReplaceLineEndings("\n");

    /// <summary>
    /// Lists the files in <paramref name="folder"/> matching <paramref name="pattern"/>, optionally
    /// including subfolders. Files are returned lazily (one at a time), so a caller that stops
    /// early never walks the rest of the tree.
    ///
    /// Skipped automatically: hidden and system files/folders, folders we lack permission for,
    /// and any folder whose name is in <paramref name="excludedFolders"/> (e.g. bin, obj,
    /// node_modules). Those are full of generated files that would drown out the real results.
    /// </summary>
    public static IEnumerable<FileInfo> EnumerateFiles(string folder, string? pattern, bool recursive, IReadOnlyCollection<string> excludedFolders)
    {
        var patterns = ParsePatterns(pattern);
        var options = new EnumerationOptions { IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        var excluded = new HashSet<string>(excludedFolders, StringComparer.OrdinalIgnoreCase);

        // Walk the tree one folder at a time with a queue (breadth-first), so files in the
        // top folder come first and we can skip excluded folders before entering them.
        var pending = new Queue<DirectoryInfo>();
        pending.Enqueue(new DirectoryInfo(folder));

        while (pending.Count > 0)
        {
            var dir = pending.Dequeue();

            var files = patterns
                .SelectMany(p => dir.EnumerateFiles(p, options))
                .DistinctBy(f => f.FullName) // two patterns (e.g. "*.md, readme*") can match the same file
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
                yield return file;

            if (!recursive)
                continue;

            foreach (var sub in dir.EnumerateDirectories("*", options).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                if (!excluded.Contains(sub.Name))
                    pending.Enqueue(sub);
        }
    }

    /// <summary>
    /// Turns what the model sends into proper search patterns. Models are inconsistent here,
    /// so all of these work: "*.md", ".md", "md", "*.cs, *.md" (several, comma/semicolon separated),
    /// "readme.txt" (an exact name), or nothing at all (= every file).
    /// </summary>
    private static string[] ParsePatterns(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return ["*"];

        return pattern
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p switch
            {
                _ when p.Contains('*') || p.Contains('?') => p, // already a wildcard pattern
                _ when p.StartsWith('.') => "*" + p,            // ".md"  -> "*.md"
                _ when !p.Contains('.') => "*." + p,            // "md"   -> "*.md"
                _ => p                                          // "readme.txt" -> exact name
            })
            .ToArray();
    }
}
