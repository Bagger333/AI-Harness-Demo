using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AIOrchestrator.Tools;

/// <summary>
/// Tool: write_file — creates a new text file, or replaces / adds to an existing one.
///
/// Safety:
///   - The path must pass FileAccessGuard.TryResolveForWrite (allowed folders, no hidden or
///     dot-folders, no executable file types).
///   - The default mode is "create", which refuses to touch an existing file. Replacing or
///     appending has to be asked for explicitly.
///   - Before an existing file is changed, a backup copy is saved (FileBackupService).
///   - Only plain-text files. Writing text into a .docx/.pdf would destroy it.
/// </summary>
public sealed class WriteFileTool(FileAccessGuard guard, FileBackupService backups, IOptions<FileAccessOptions> options) : IChatTool
{
    // UTF-8 without the invisible byte-order mark at the start, the usual choice for text and code files.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly FileAccessOptions _options = options.Value;

    public string Name => "write_file";

    public bool ModifiesFiles => true;

    public string Description =>
        "Creates a text file on the user's computer, or writes to an existing one. " +
        "mode \"create\" (default) makes a new file and fails if it already exists. " +
        "mode \"overwrite\" replaces the whole file: read it first so you keep anything that should stay. " +
        "mode \"append\" adds the content to the end. Missing folders are created automatically. " +
        "For small changes to an existing file, prefer edit_file. Only plain-text files (code, .txt, .md, " +
        ".json, .csv, .html, .xml, ...) can be written, not Word/Excel/PowerPoint/PDF. A backup is saved " +
        "before an existing file is changed. " +
        $"Only these folders (and their subfolders) are writable: {string.Join(", ", guard.Roots)}.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new
            {
                type = "string",
                description = "Full path of the file to write, e.g. C:\\Users\\cbb\\Documents\\notes.txt."
            },
            content = new { type = "string", description = "The complete text to write." },
            mode = new
            {
                type = "string",
                // "enum" tells the model these are the only valid values.
                @enum = new[] { "create", "overwrite", "append" },
                description = "create (default): new file only. overwrite: replace the existing file. append: add to the end."
            }
        },
        required = new[] { "path", "content" }
    };

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        if (!guard.TryResolveForWrite(FileAccessGuard.GetStringArgument(arguments, "path"), out var fullPath, out var error))
            return error;

        if (FileTextExtractor.IsDocument(fullPath))
            return WordDocumentEditor.IsWordDocument(fullPath) && File.Exists(fullPath)
                ? $"Error: {Path.GetExtension(fullPath)} files can't be written with write_file. Use edit_file to change text in an existing Word document."
                : $"Error: {Path.GetExtension(fullPath)} files can't be written. Only plain-text files are supported " +
                  "(e.g. save it as .txt, .md, .csv or .html instead).";

        var content = FileAccessGuard.GetStringArgument(arguments, "content");
        if (content is null)
            return "Error: no content was given.";
        if (content.Length > _options.MaxWriteCharacters)
            return $"Error: the content is too long ({content.Length:N0} characters; the limit is {_options.MaxWriteCharacters:N0}).";

        var mode = (FileAccessGuard.GetStringArgument(arguments, "mode") ?? "create").Trim().ToLowerInvariant();
        if (mode is not ("create" or "overwrite" or "append"))
            return $"Error: unknown mode '{mode}'. Use create, overwrite or append.";

        var exists = File.Exists(fullPath);
        if (mode == "create" && exists)
            return $"Error: {fullPath} already exists. Use mode \"overwrite\" to replace it, \"append\" to add to the end, " +
                   "or edit_file to change part of it.";

        try
        {
            string? backupPath = null;
            Encoding encoding = Utf8NoBom;

            if (exists)
            {
                // Make sure we're not about to write text over a binary file (an image, say).
                var existing = await FileTextExtractor.ExtractAsync(fullPath, ct);
                if (existing.Text is null)
                    return $"Error: {fullPath} is not a text file, so it can't be written to. ({existing.Error})";

                content = FileHelpers.MatchLineEndings(content, existing.Text);
                encoding = FileHelpers.DetectEncoding(fullPath);

                // When appending, start on a new line if the file doesn't already end with one.
                if (mode == "append" && existing.Text.Length > 0 && !existing.Text.EndsWith('\n'))
                    content = (existing.Text.Contains("\r\n") ? "\r\n" : "\n") + content;

                backupPath = backups.Backup(fullPath);
            }
            else
            {
                // write_file may create the folders the file goes in.
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            }

            if (mode == "append")
                await File.AppendAllTextAsync(fullPath, content, encoding, ct);
            else
                await File.WriteAllTextAsync(fullPath, content, encoding, ct);

            var size = FileHelpers.FormatSize(new FileInfo(fullPath).Length);
            var result = (exists, mode) switch
            {
                (false, _) => $"Created {fullPath} ({size}).",
                (true, "append") => $"Appended {content.Length:N0} characters to {fullPath} (now {size}).",
                _ => $"Overwrote {fullPath} ({size})."
            };
            return backupPath is null ? result : $"{result} A backup of the previous version was saved to {backupPath}.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return $"Error: could not write {fullPath}: {ex.Message}";
        }
    }
}
