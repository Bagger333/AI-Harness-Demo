using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AIOrchestrator.Tools;

/// <summary>
/// Tool: read_files — reads every matching file in a folder in one go, so the model can
/// summarise or compare them without one tool round per file.
///
/// Everything returned must fit in the model's context window, so there's a total character
/// budget (MaxTotalCharacters). Once it's used up, the remaining files are listed by name
/// instead, and the model can read them individually or narrow the pattern.
/// </summary>
public sealed class ReadFilesTool(FileAccessGuard guard, IOptions<FileAccessOptions> options) : IChatTool
{
    private readonly FileAccessOptions _options = options.Value;

    public string Name => "read_files";

    public string Description =>
        "Reads the text of all files in a folder at once (optionally only those matching a pattern such as " +
        "\"*.md\" or \"*.docx, *.pdf\"), for summarising or comparing several files. Supports the same formats " +
        "as read_file: text files, Word, Excel, PowerPoint and PDF. There is a total size limit; files beyond it " +
        "are listed but not read. For a single file use read_file. " +
        $"Only these folders (and their subfolders) are accessible: {string.Join(", ", guard.Roots)}.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Full folder path, e.g. C:\\Users\\cbb\\Documents\\Reports." },
            pattern = new
            {
                type = "string",
                description = "Optional file name pattern, e.g. \"*.txt\" or \"*.cs, *.md\". Default: all files."
            },
            recursive = new
            {
                type = "boolean",
                description = "Also read files in subfolders. Default: false."
            }
        },
        required = new[] { "path" }
    };

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        if (!guard.TryResolve(FileAccessGuard.GetStringArgument(arguments, "path"), out var folder, out var error))
            return error;
        if (!Directory.Exists(folder))
            return File.Exists(folder)
                ? $"Error: '{folder}' is a file. Use read_file for a single file."
                : $"Error: folder not found: {folder}";

        var pattern = FileAccessGuard.GetStringArgument(arguments, "pattern");
        var recursive = FileAccessGuard.GetBoolArgument(arguments, "recursive") ?? false;

        // Take one more file than the limit, just to know whether there were more.
        var files = FileHelpers.EnumerateFiles(folder, pattern, recursive, _options.ExcludedFolderNames)
            .Take(_options.MaxFilesPerRead + 1)
            .ToList();

        if (files.Count == 0)
            return $"No files matching '{pattern ?? "*"}' in {folder}{(recursive ? " or its subfolders" : "")}.";

        var sb = new StringBuilder();
        var budget = _options.MaxTotalCharacters; // characters still allowed in the result
        var readCount = 0;
        var skipped = new List<string>();     // files that couldn't be read (binary, too large, ...)
        var notReached = new List<string>();  // files left out because the budget ran out

        foreach (var file in files.Take(_options.MaxFilesPerRead))
        {
            if (budget <= 0)
            {
                notReached.Add(file.FullName);
                continue;
            }
            if (file.Length > _options.MaxFileSizeBytes)
            {
                skipped.Add($"{file.FullName} (too large: {FileHelpers.FormatSize(file.Length)})");
                continue;
            }

            var extracted = await FileTextExtractor.ExtractAsync(file.FullName, ct);
            if (extracted.Error is not null)
            {
                skipped.Add($"{file.FullName} ({extracted.Error})");
                continue;
            }

            // Each file is limited by both the per-file limit and what's left of the total budget.
            var text = extracted.Text!;
            var limit = Math.Min(_options.MaxCharacters, budget);
            var truncated = text.Length > limit;
            if (truncated)
                text = text[..limit];

            sb.AppendLine($"===== File: {file.FullName} ({FileHelpers.FormatSize(file.Length)}) =====");
            sb.AppendLine(text);
            if (truncated)
                sb.AppendLine($"[Truncated: showing {limit:N0} of {extracted.Text!.Length:N0} characters.]");
            sb.AppendLine();

            budget -= text.Length;
            readCount++;
        }

        // A summary at the end, so the model knows exactly what it did and didn't get.
        sb.AppendLine($"Read {readCount} file(s) from {folder}.");
        if (skipped.Count > 0)
            sb.AppendLine("Skipped:\n  " + string.Join("\n  ", skipped));
        if (notReached.Count > 0)
            sb.AppendLine("Not read (size limit reached; use read_file for these):\n  " + string.Join("\n  ", notReached));
        if (files.Count > _options.MaxFilesPerRead)
            sb.AppendLine($"More than {_options.MaxFilesPerRead} files matched; only the first {_options.MaxFilesPerRead} were considered. Use a narrower pattern.");

        return sb.ToString();
    }
}
