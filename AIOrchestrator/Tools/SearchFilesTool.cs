using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AIOrchestrator.Tools;

/// <summary>
/// Tool: search_files — finds a word or phrase inside the files of a folder (and its
/// subfolders), like Windows search or "grep". Returns each matching line with its file
/// and line number, so the model can answer "which files mention X?" or decide which
/// file to read in full.
///
/// Documents (Word, Excel, PowerPoint, PDF) are searched too, via FileTextExtractor.
/// For those, "line" means a line of the extracted text (e.g. a paragraph in Word).
/// </summary>
public sealed class SearchFilesTool(FileAccessGuard guard, IOptions<FileAccessOptions> options) : IChatTool
{
    private const int MaxLineLength = 200; // long matching lines are shortened around the match

    private readonly FileAccessOptions _options = options.Value;

    public string Name => "search_files";

    public string Description =>
        "Searches inside the files in a folder and its subfolders for a word or phrase (case-insensitive), " +
        "and returns every matching line with its file path and line number. Searches text files and " +
        "Word, Excel, PowerPoint and PDF documents. Use it to find which files mention something, or where. " +
        "Then use read_file to read a whole file. " +
        $"Only these folders (and their subfolders) are accessible: {string.Join(", ", guard.Roots)}.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Full path of the folder to search, e.g. C:\\Users\\cbb\\Documents." },
            query = new { type = "string", description = "The text to look for. Matched as an exact phrase, ignoring upper/lower case." },
            pattern = new
            {
                type = "string",
                description = "Optional: only search files matching this pattern, e.g. \"*.cs\" or \"*.docx, *.pdf\". Default: all files."
            },
            recursive = new { type = "boolean", description = "Search subfolders too. Default: true." }
        },
        required = new[] { "path", "query" }
    };

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        if (!guard.TryResolve(FileAccessGuard.GetStringArgument(arguments, "path"), out var folder, out var error))
            return error;
        if (!Directory.Exists(folder))
            return $"Error: folder not found: {folder}";

        var query = FileAccessGuard.GetStringArgument(arguments, "query")?.Trim();
        if (string.IsNullOrEmpty(query))
            return "Error: no search text (query) was given.";

        var pattern = FileAccessGuard.GetStringArgument(arguments, "pattern");
        var recursive = FileAccessGuard.GetBoolArgument(arguments, "recursive") ?? true;

        // Matches grouped per file: file path -> list of "line number: text".
        var matchesByFile = new Dictionary<string, List<string>>();
        var totalMatches = 0;
        var filesSearched = 0;
        var hitFileLimit = false;

        foreach (var file in FileHelpers.EnumerateFiles(folder, pattern, recursive, _options.ExcludedFolderNames))
        {
            if (filesSearched >= _options.MaxFilesToSearch)
            {
                hitFileLimit = true;
                break;
            }
            if (file.Length == 0 || file.Length > _options.MaxFileSizeBytes)
                continue;

            // Binary and unreadable files come back with an Error; just skip them.
            var extracted = await FileTextExtractor.ExtractAsync(file.FullName, ct);
            if (extracted.Text is null)
                continue;
            filesSearched++;

            var lines = extracted.Text.Split('\n');
            for (var i = 0; i < lines.Length && totalMatches < _options.MaxSearchResults; i++)
            {
                var line = lines[i].Trim();
                var index = line.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    continue;

                if (!matchesByFile.TryGetValue(file.FullName, out var list))
                    matchesByFile[file.FullName] = list = [];
                list.Add($"{i + 1}: {Snippet(line, index)}");
                totalMatches++;
            }

            if (totalMatches >= _options.MaxSearchResults)
                break;
        }

        if (totalMatches == 0)
            return $"No matches for \"{query}\" in {filesSearched} file(s) searched under {folder}." +
                   (hitFileLimit ? $" (Stopped after {_options.MaxFilesToSearch} files; try a more specific folder or pattern.)" : "");

        var sb = new StringBuilder();
        sb.AppendLine($"Found {totalMatches} matching line(s) for \"{query}\" in {matchesByFile.Count} file(s) ({filesSearched} searched):");
        foreach (var (path, lines) in matchesByFile)
        {
            sb.AppendLine();
            sb.AppendLine(path);
            foreach (var line in lines)
                sb.AppendLine("  " + line);
        }

        if (totalMatches >= _options.MaxSearchResults)
            sb.AppendLine($"\n[Stopped at {_options.MaxSearchResults} matches; there may be more. Use a more specific query, folder or pattern.]");
        else if (hitFileLimit)
            sb.AppendLine($"\n[Stopped after searching {_options.MaxFilesToSearch} files; there may be more matches.]");

        return sb.ToString();
    }

    /// <summary>
    /// Trims a matching line for display. A very long line (minified code, a whole Word
    /// paragraph) is cut to a window around the match, so results stay readable and
    /// don't fill the context window.
    /// </summary>
    private static string Snippet(string line, int matchIndex)
    {
        if (line.Length <= MaxLineLength)
            return line;

        // Start the window half its width before the match, but never before the start
        // of the line or so late that the window runs past its end.
        var start = Math.Clamp(matchIndex - MaxLineLength / 2, 0, Math.Max(0, line.Length - MaxLineLength));
        var snippet = line.Substring(start, Math.Min(MaxLineLength, line.Length - start));
        return (start > 0 ? "…" : "") + snippet + (start + MaxLineLength < line.Length ? "…" : "");
    }
}
