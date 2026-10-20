using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AIOrchestrator.Tools;

/// <summary>
/// Tool: read_file — returns the text of one file. Handles plain text files plus
/// Word, Excel, PowerPoint and PDF documents (the conversion is in FileTextExtractor).
/// </summary>
public sealed class ReadFileTool(FileAccessGuard guard, IOptions<FileAccessOptions> options) : IChatTool
{
    private readonly FileAccessOptions _options = options.Value;

    public string Name => "read_file";

    public string Description =>
        "Reads one file from the user's computer and returns its text. Works for text files " +
        "(source code, .txt, .md, .json, .csv, .xml, logs, etc.) and for documents: Word (.docx), " +
        "Excel (.xlsx, every sheet), PowerPoint (.pptx, every slide plus speaker notes) and PDF. " +
        "Images and other binary files cannot be read. If you don't know the exact file name, use " +
        "list_directory or search_files first. To read many files in a folder at once, use read_files. " +
        $"Only these folders (and their subfolders) are accessible: {string.Join(", ", guard.Roots)}.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new
            {
                type = "string",
                description = "Full path of the file, e.g. C:\\Users\\cbb\\Documents\\report.docx. " +
                              "A relative path is resolved against the first accessible folder."
            }
        },
        required = new[] { "path" }
    };

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        if (!guard.TryResolve(FileAccessGuard.GetStringArgument(arguments, "path"), out var fullPath, out var error))
            return error;

        if (Directory.Exists(fullPath))
            return $"Error: '{fullPath}' is a folder, not a file. Use list_directory to see its contents, or read_files to read the files in it.";
        if (!File.Exists(fullPath))
            return $"Error: file not found: {fullPath}";

        var info = new FileInfo(fullPath);
        if (info.Length > _options.MaxFileSizeBytes)
            return $"Error: {fullPath} is too large to read ({FileHelpers.FormatSize(info.Length)}; " +
                   $"the limit is {FileHelpers.FormatSize(_options.MaxFileSizeBytes)}).";

        var extracted = await FileTextExtractor.ExtractAsync(fullPath, ct);
        if (extracted.Error is not null)
            return $"Error: {extracted.Error}";

        // Cut very long files down so they don't push the conversation out of the model's
        // context window. The note tells the model it isn't seeing everything.
        var text = extracted.Text!;
        var truncatedNote = "";
        if (text.Length > _options.MaxCharacters)
        {
            truncatedNote = $"\n\n[Truncated: showing the first {_options.MaxCharacters:N0} of {text.Length:N0} characters. " +
                            "Use search_files to find specific content further in.]";
            text = text[.._options.MaxCharacters];
        }

        return $"File: {fullPath} ({FileHelpers.FormatSize(info.Length)})\n\n{text}{truncatedNote}";
    }
}
