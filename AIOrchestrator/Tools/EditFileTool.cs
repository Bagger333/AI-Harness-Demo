using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;

namespace AIOrchestrator.Tools;

/// <summary>
/// Tool: edit_file — changes part of an existing text file or Word document by replacing an
/// exact piece of text ("find and replace"). Word documents are handled by WordDocumentEditor.
///
/// Why have this as well as write_file? To change one line with write_file, the model has to
/// reproduce the ENTIRE file perfectly. Small models often drop or alter lines along the way,
/// and it costs many tokens. Here it only sends the part that changes.
///
/// The old text must appear exactly once (unless replace_all is set). If it appears several
/// times we'd be guessing which one was meant, so the model is asked to include more context.
///
/// Small models rarely copy text perfectly, and a bare "not found" makes them re-read the file
/// and try again, often with the same mistake, until they run out of tool rounds. So when the
/// exact text isn't found we try a few safe fallbacks, and if those fail too, the error shows
/// the model the file's real text so it can copy it without another read_file.
/// </summary>
public sealed class EditFileTool(FileAccessGuard guard, FileBackupService backups) : IChatTool
{
    public string Name => "edit_file";

    public bool ModifiesFiles => true;

    public string Description =>
        "Changes part of an existing text file by replacing an exact piece of text with new text. " +
        "old_text must match the file exactly, including spaces, indentation and line breaks, so use " +
        "read_file first and copy it. Keep old_text short: just the lines that change, plus a line of " +
        "context if needed to make it unique. Unless replace_all is true, old_text must appear exactly once. " +
        "new_text can be empty to delete old_text. Prefer this over write_file for small changes. " +
        "A backup is saved first. After a successful edit there is no need to read the file again. " +
        "Word documents (.docx) can be edited too: copy old_text from what read_file shows, where each line is " +
        "one paragraph. Formatting is kept, and line breaks in new_text start new paragraphs. " +
        $"Only files in these folders (and their subfolders) can be edited: {string.Join(", ", guard.Roots)}.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Full path of the file to edit." },
            old_text = new { type = "string", description = "The exact existing text to replace." },
            new_text = new { type = "string", description = "The text to put in its place (empty to delete)." },
            replace_all = new { type = "boolean", description = "Replace every occurrence instead of exactly one. Default: false." }
        },
        required = new[] { "path", "old_text", "new_text" }
    };

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        if (!guard.TryResolveForWrite(FileAccessGuard.GetStringArgument(arguments, "path"), out var fullPath, out var error))
            return error;

        if (!File.Exists(fullPath))
            return $"Error: file not found: {fullPath}. To create a new file, use write_file.";
        var isWord = WordDocumentEditor.IsWordDocument(fullPath);
        if (!isWord && FileTextExtractor.IsDocument(fullPath))
            return $"Error: {Path.GetExtension(fullPath)} documents can't be edited, only plain-text files and Word documents (.docx).";

        var oldText = FileAccessGuard.GetStringArgument(arguments, "old_text");
        var newText = FileAccessGuard.GetStringArgument(arguments, "new_text");
        if (string.IsNullOrEmpty(oldText))
            return "Error: old_text is empty. Give the exact text to replace (use write_file to add to a file).";
        if (newText is null)
            return "Error: new_text is missing (use an empty string to delete old_text).";
        if (oldText == newText)
            return "Error: old_text and new_text are identical, so there is nothing to change. Put the changed text in new_text.";
        var replaceAll = FileAccessGuard.GetBoolArgument(arguments, "replace_all") ?? false;

        // The Office library is synchronous and CPU-heavy, so run it on a background thread.
        if (isWord)
            return await Task.Run(() => EditWordDocument(fullPath, oldText, newText, replaceAll), ct);

        try
        {
            var extracted = await FileTextExtractor.ExtractAsync(fullPath, ct);
            if (extracted.Text is null)
                return $"Error: {fullPath} is not a text file and can't be edited. ({extracted.Error})";
            var text = extracted.Text;

            // The model writes "\n" line breaks. If the file uses "\r\n" (common on Windows),
            // a multi-line old_text would never match, so convert both to the file's style.
            oldText = FileHelpers.MatchLineEndings(oldText, text);
            newText = FileHelpers.MatchLineEndings(newText, text);

            var count = CountMatches(text, ref oldText, ref newText);
            if (count > 1 && !replaceAll)
                return TooManyMatchesError(fullPath, count);

            string updated;
            int line;
            var note = "";

            if (count > 0)
            {
                var index = text.IndexOf(oldText, StringComparison.Ordinal);
                line = LineNumberAt(text, index);
                updated = replaceAll
                    ? text.Replace(oldText, newText, StringComparison.Ordinal)
                    : string.Concat(text.AsSpan(0, index), newText, text.AsSpan(index + oldText.Length));
            }
            else if (TryWhitespaceTolerantReplace(text, oldText, newText, out updated, out line))
            {
                count = 1;
                note = " (old_text matched after ignoring differences in indentation/trailing spaces)";
            }
            else
            {
                return NotFoundError(fullPath, text, oldText, newText);
            }

            var backupPath = backups.Backup(fullPath);
            await File.WriteAllTextAsync(fullPath, updated, FileHelpers.DetectEncoding(fullPath), ct);

            var result = $"Edited {fullPath}: replaced {count} occurrence(s), starting at line {line}{note}. " +
                         "The change is saved; no need to read the file again to check.";
            return backupPath is null ? result : $"{result} A backup of the previous version was saved to {backupPath}.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return $"Error: could not edit {fullPath}: {ex.Message}";
        }
    }

    /// <summary>
    /// edit_file for Word documents. The model edits the text as read_file shows it (one line per
    /// paragraph); WordDocumentEditor maps that back onto the document so formatting is kept.
    /// The whitespace-tolerant fallback isn't used here: paragraphs have no indentation to get wrong.
    /// </summary>
    private string EditWordDocument(string fullPath, string oldText, string newText, bool replaceAll)
    {
        try
        {
            using var editor = WordDocumentEditor.Open(fullPath);
            var text = editor.Text;

            oldText = oldText.ReplaceLineEndings("\n");
            newText = newText.ReplaceLineEndings("\n");
            var count = CountMatches(text, ref oldText, ref newText);
            if (count > 1 && !replaceAll)
                return TooManyMatchesError(fullPath, count);
            if (count == 0)
                return NotFoundError(fullPath, text, oldText, newText);

            var starts = new List<int>();
            for (var i = text.IndexOf(oldText, StringComparison.Ordinal); i >= 0; i = text.IndexOf(oldText, i + oldText.Length, StringComparison.Ordinal))
            {
                starts.Add(i);
                if (!replaceAll)
                    break;
            }
            if (editor.Replace(starts, oldText.Length, newText) is { } error)
                return $"Error: could not edit {fullPath}: {error}";

            var updated = editor.Save();
            var backupPath = backups.Backup(fullPath);
            File.WriteAllBytes(fullPath, updated);

            var result = $"Edited {fullPath}: replaced {starts.Count} occurrence(s), starting at paragraph {LineNumberAt(text, starts[0])}. " +
                         "The formatting around it is kept. The change is saved; no need to read the file again to check.";
            return backupPath is null ? result : $"{result} A backup of the previous version was saved to {backupPath}.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // IOException includes the file being locked because it's open in Word.
            return $"Error: could not edit {fullPath}: {ex.Message}";
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException)
        {
            return $"Error: {fullPath} could not be opened as a Word document (it may be corrupt or password-protected): {ex.Message}";
        }
    }

    /// <summary>
    /// Counts old_text in the file's text. Some models escape line breaks twice, sending a literal
    /// backslash + "n" instead of a real line break; if the text as sent isn't found and has no real
    /// line breaks, that is undone (in both old_text and new_text) and tried again.
    /// </summary>
    private static int CountMatches(string text, ref string oldText, ref string newText)
    {
        var count = CountOccurrences(text, oldText);
        if (count == 0 && !oldText.Contains('\n') && oldText.Contains("\\n"))
        {
            var unescapedOld = FileHelpers.MatchLineEndings(Unescape(oldText), text);
            var unescapedCount = CountOccurrences(text, unescapedOld);
            if (unescapedCount > 0)
            {
                oldText = unescapedOld;
                newText = FileHelpers.MatchLineEndings(Unescape(newText), text);
                count = unescapedCount;
            }
        }
        return count;
    }

    private static string TooManyMatchesError(string fullPath, int count) =>
        $"Error: old_text appears {count} times in {fullPath}. Include more surrounding text so it matches " +
        "only once, or set replace_all to true to change all of them.";

    /// <summary>
    /// The error for "old_text not found", made as useful as possible so the model can fix its
    /// next attempt straight away instead of looping on read_file + edit_file.
    /// </summary>
    private static string NotFoundError(string fullPath, string text, string oldText, string newText)
    {
        // A common loop: the edit already succeeded earlier, the model forgot, and it tries again.
        if (newText.Length > 0 && text.Contains(newText, StringComparison.Ordinal))
            return $"Error: old_text was not found in {fullPath}, but new_text is already in the file, so this change " +
                   "has probably been made already. Don't repeat it; tell the user it's done.";

        var message = $"Error: old_text was not found in {fullPath}. It must match exactly (spaces and indentation too).";
        var snippet = FindClosestSnippet(text, oldText);
        return snippet is null
            ? $"{message} Read the file again and copy the text precisely."
            : $"{message} The most similar text in the file is at line {snippet.Value.Line}, shown below exactly as it " +
              $"is in the file. If this is what you meant, copy old_text from it (no need to call read_file):\n```\n{snippet.Value.Text}\n```";
    }

    /// <summary>
    /// Matches old_text line by line while ignoring leading/trailing whitespace on each line,
    /// which is the usual way small models get it wrong (tabs vs spaces, wrong indent depth,
    /// trailing spaces). Only succeeds if exactly one place matches, so it never guesses.
    /// new_text is re-indented by the same amount the model's indentation was off by.
    /// </summary>
    private static bool TryWhitespaceTolerantReplace(string text, string oldText, string newText, out string updated, out int line)
    {
        updated = text;
        line = 0;

        var oldLines = TrimBlankLines(SplitLines(oldText));
        if (oldLines.Count == 0)
            return false;

        var fileLines = SplitLines(text);
        var match = -1;
        for (var i = 0; i + oldLines.Count <= fileLines.Count; i++)
        {
            var matches = true;
            for (var j = 0; j < oldLines.Count && matches; j++)
                matches = fileLines[i + j].Trim() == oldLines[j].Trim();
            if (!matches)
                continue;
            if (match >= 0)
                return false; // matches in more than one place: don't guess
            match = i;
        }
        if (match < 0)
            return false;

        // Re-indent: if the model wrote its first line with "  " but the file has "\t\t",
        // swap that prefix on every line of new_text that has it.
        var fileIndent = LeadingWhitespace(fileLines[match]);
        var modelIndent = LeadingWhitespace(oldLines[0]);
        var newLines = SplitLines(newText.Trim('\r', '\n'))
            .Select(l => l.Length > 0 && l.StartsWith(modelIndent, StringComparison.Ordinal)
                ? fileIndent + l[modelIndent.Length..]
                : l);
        var lineBreak = text.Contains("\r\n") ? "\r\n" : "\n";

        // Replace whole lines match .. match+count-1, keeping the line break after the last one.
        var start = LineStart(text, match);
        var end = LineStart(text, match + oldLines.Count - 1) + fileLines[match + oldLines.Count - 1].Length;
        updated = string.Concat(text.AsSpan(0, start), string.Join(lineBreak, newLines), text.AsSpan(end));
        line = match + 1;
        return true;
    }

    /// <summary>
    /// Finds the part of the file that looks most like old_text, judged by its first non-blank
    /// line, and returns it with a couple of lines of context. Null if nothing is reasonably close.
    /// </summary>
    private static (int Line, string Text)? FindClosestSnippet(string text, string oldText)
    {
        var oldLines = TrimBlankLines(SplitLines(oldText));
        if (oldLines.Count == 0)
            return null;
        var target = oldLines[0].Trim();
        var fileLines = SplitLines(text);

        var best = -1;
        var bestScore = 0.0;
        for (var i = 0; i < fileLines.Count; i++)
        {
            var candidate = fileLines[i].Trim();
            if (candidate.Length == 0 || candidate.Length > 500)
                continue;
            var score = Similarity(candidate, target);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        if (best < 0 || bestScore < 0.6)
            return null;

        const int context = 2;
        var from = Math.Max(0, best - context);
        var to = Math.Min(fileLines.Count, best + oldLines.Count + context);
        return (best + 1, string.Join("\n", fileLines.Skip(from).Take(to - from)));
    }

    /// <summary>1.0 = identical, 0.0 = nothing in common, based on edit distance (Levenshtein).</summary>
    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return 0;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return 1.0 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }

    // Lines without their line breaks ("\r\n" or "\n").
    private static List<string> SplitLines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    private static List<string> TrimBlankLines(List<string> lines) =>
        lines.SkipWhile(string.IsNullOrWhiteSpace).Reverse().SkipWhile(string.IsNullOrWhiteSpace).Reverse().ToList();

    private static string LeadingWhitespace(string line) =>
        line[..(line.Length - line.TrimStart().Length)];

    // Character position where line number lineIndex (0-based) starts.
    private static int LineStart(string text, int lineIndex)
    {
        var position = 0;
        for (var i = 0; i < lineIndex; i++)
            position = text.IndexOf('\n', position) + 1;
        return position;
    }

    private static int LineNumberAt(string text, int index) =>
        text.AsSpan(0, index).Count('\n') + 1;

    private static string Unescape(string text) =>
        text.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"");

    // Ordinal = exact character-by-character comparison (case-sensitive, no culture rules).
    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
