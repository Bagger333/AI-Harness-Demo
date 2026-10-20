using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AIOrchestrator.Tools;

/// <summary>
/// Tool: list_directory — shows the subfolders and files in a folder,
/// so the model can find out what exists before reading anything.
/// </summary>
public sealed class ListDirectoryTool(FileAccessGuard guard, IOptions<FileAccessOptions> options) : IChatTool
{
    private readonly FileAccessOptions _options = options.Value;

    public string Name => "list_directory";

    // The allowed folders are included in the description, so the model knows where it may look.
    public string Description =>
        "Lists the subfolders and files in a folder on the user's computer, with file sizes and " +
        "last-modified dates. Use it to find out which files exist before reading them with read_file or read_files. " +
        $"Only these folders (and their subfolders) are accessible: {string.Join(", ", guard.Roots)}.";

    // JSON Schema describing the arguments: a single required string "path".
    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new
            {
                type = "string",
                description = "Full folder path, e.g. C:\\Users\\cbb\\Documents. " +
                              "A relative path is resolved against the first accessible folder."
            }
        },
        required = new[] { "path" }
    };

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        // The file APIs used here are synchronous (and fast), so wrap the result in a Task.
        return Task.FromResult(List(FileAccessGuard.GetStringArgument(arguments, "path")));
    }

    private string List(string? path)
    {
        if (!guard.TryResolve(path, out var fullPath, out var error))
            return error;

        if (File.Exists(fullPath))
            return $"Error: '{fullPath}' is a file, not a folder. Use read_file to read it.";
        if (!Directory.Exists(fullPath))
            return $"Error: folder not found: {fullPath}";

        try
        {
            // IgnoreInaccessible: skip entries we lack permission for instead of throwing.
            // Using EnumerationOptions also skips Hidden and System entries by default
            // (e.g. AppData, desktop.ini), which keeps listings short and avoids exposing
            // the hidden folders where apps store their data.
            var enumeration = new EnumerationOptions { IgnoreInaccessible = true };
            var dir = new DirectoryInfo(fullPath);

            var folders = dir.EnumerateDirectories("*", enumeration).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var files = dir.EnumerateFiles("*", enumeration).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

            if (folders.Count == 0 && files.Count == 0)
                return $"The folder {fullPath} is empty.";

            // Plain text is easiest for the model to read; it doesn't need JSON here.
            var sb = new StringBuilder();
            sb.AppendLine($"Contents of {fullPath} ({folders.Count} folders, {files.Count} files):");

            var shown = 0;
            foreach (var folder in folders)
            {
                if (shown++ >= _options.MaxDirectoryEntries) break;
                sb.AppendLine($"[DIR]  {folder.Name}");
            }
            foreach (var file in files)
            {
                if (shown++ >= _options.MaxDirectoryEntries) break;
                sb.AppendLine($"[FILE] {file.Name}  ({FileHelpers.FormatSize(file.Length)}, modified {file.LastWriteTime:yyyy-MM-dd HH:mm})");
            }

            var total = folders.Count + files.Count;
            if (total > _options.MaxDirectoryEntries)
                sb.AppendLine($"... and {total - _options.MaxDirectoryEntries} more entries not shown.");

            return sb.ToString();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return $"Error: could not list {fullPath}: {ex.Message}";
        }
    }
}
