// ============================================================================
// FileTextExtractor.cs — turns a file into plain text the model can read.
//
// Shared by read_file, read_files and search_files, so all three understand
// the same formats:
//   - Text files (.txt, .md, .cs, .json, .csv, ...) — read as-is
//   - Word (.docx), Excel (.xlsx), PowerPoint (.pptx) — via DocumentFormat.OpenXml,
//     Microsoft's official library for the modern Office formats (which are
//     really zip files full of XML)
//   - PDF — via PdfPig
// Anything else that looks binary (images, .exe, .zip, ...) is refused.
// ============================================================================

using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using A = DocumentFormat.OpenXml.Drawing;      // "A." = text inside PowerPoint shapes
using W = DocumentFormat.OpenXml.Wordprocessing; // "W." = Word elements

namespace AIOrchestrator.Tools;

/// <summary>The result of extracting a file: either Text, or an Error message for the model.</summary>
public sealed record ExtractedText(string? Text, string? Error)
{
    public static ExtractedText Ok(string text) => new(text, null);
    public static ExtractedText Fail(string error) => new(null, error);
}

public static class FileTextExtractor
{
    /// <summary>
    /// Old binary Office formats (pre-2007). These need a different, much heavier library,
    /// so we only explain how to convert them.
    /// </summary>
    private static readonly HashSet<string> LegacyOfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".xls", ".ppt" };

    /// <summary>Office and PDF formats: they can be read as text, but not written as plain text.</summary>
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".docx", ".docm", ".xlsx", ".xlsm", ".pptx", ".pptm", ".pdf", ".doc", ".xls", ".ppt" };

    /// <summary>
    /// True for Word/Excel/PowerPoint/PDF files. write_file refuses these, because writing
    /// plain text into one would destroy the document. (edit_file handles Word documents itself.)
    /// </summary>
    public static bool IsDocument(string path) => DocumentExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Extracts the text of <paramref name="fullPath"/>. The path must already have passed
    /// FileAccessGuard. Never throws for a bad file: problems come back as ExtractedText.Error.
    /// </summary>
    public static async Task<ExtractedText> ExtractAsync(string fullPath, CancellationToken ct)
    {
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();

        if (LegacyOfficeExtensions.Contains(extension))
            return ExtractedText.Fail($"{Path.GetFileName(fullPath)} is an old-style Office file ({extension}). " +
                                      "Only the modern formats (.docx, .xlsx, .pptx) can be read. Re-save it in the new format to read it.");

        try
        {
            // The Office and PDF libraries are synchronous and CPU-heavy, so run them on a
            // background thread (Task.Run) rather than blocking the web server's request thread.
            return extension switch
            {
                ".docx" or ".docm" => ExtractedText.Ok(await Task.Run(() => ExtractWord(fullPath), ct)),
                ".xlsx" or ".xlsm" => ExtractedText.Ok(await Task.Run(() => ExtractExcel(fullPath), ct)),
                ".pptx" or ".pptm" => ExtractedText.Ok(await Task.Run(() => ExtractPowerPoint(fullPath), ct)),
                ".pdf" => await Task.Run(() => ExtractPdf(fullPath), ct),
                _ => await ExtractPlainTextAsync(fullPath, ct)
            };
        }
        catch (OperationCanceledException)
        {
            throw; // the user clicked Stop — let that through
        }
        catch (Exception ex)
        {
            // Corrupt, password-protected or locked files end up here (e.g. a Word file
            // that's open in Word can be locked).
            return ExtractedText.Fail($"Could not read {Path.GetFileName(fullPath)}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- text files

    private static async Task<ExtractedText> ExtractPlainTextAsync(string fullPath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(fullPath);

        // Check the first 8 KB for binary content BEFORE reading the whole file,
        // so searching a folder full of .exe/.png files stays fast.
        var sample = new byte[Math.Min(8000, stream.Length)];
        await stream.ReadExactlyAsync(sample, ct);
        if (LooksBinary(sample))
            return ExtractedText.Fail($"{Path.GetFileName(fullPath)} is a binary file (not text) and can't be read.");

        // Back to the start and read it all. StreamReader detects the encoding from a
        // byte-order mark if there is one (UTF-8/UTF-16), and otherwise assumes UTF-8.
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return ExtractedText.Ok(await reader.ReadToEndAsync(ct));
    }

    /// <summary>
    /// A simple, widely used heuristic: text files almost never contain a zero byte, while
    /// binary files nearly always do within the first few KB. UTF-16 text is the exception
    /// (it has zero bytes everywhere), so files starting with a UTF-16 byte-order mark are treated as text.
    /// </summary>
    private static bool LooksBinary(byte[] sample)
    {
        if (sample is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
            return false;
        return Array.IndexOf(sample, (byte)0) >= 0;
    }

    // ---------------------------------------------------------------- Word

    // A .docx body is a list of paragraphs (<w:p>), each made up of text runs.
    // ParagraphText joins a paragraph's runs into one string. Table cells contain
    // paragraphs too, so their text is included automatically. edit_file uses the
    // same ParagraphText, so the text the model reads is exactly what it can edit.
    private static string ExtractWord(string fullPath)
    {
        using var doc = WordprocessingDocument.Open(fullPath, isEditable: false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
            return "";

        var sb = new StringBuilder();
        foreach (var paragraph in body.Descendants<W.Paragraph>())
            sb.AppendLine(WordDocumentEditor.ParagraphText(paragraph));
        return sb.ToString();
    }

    // ---------------------------------------------------------------- Excel

    // Each sheet becomes a section of rows, with cells separated by " | ".
    // Excel stores most text once in a shared table and cells only reference it by
    // number, so that table has to be looked up.
    private static string ExtractExcel(string fullPath)
    {
        using var doc = SpreadsheetDocument.Open(fullPath, isEditable: false);
        var workbook = doc.WorkbookPart;
        var sheets = workbook?.Workbook?.Sheets;
        if (workbook is null || sheets is null)
            return "";

        // Shared strings, as a list so they can be looked up by index.
        var sharedStrings = workbook.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>().Select(s => s.InnerText).ToList() ?? [];

        var sb = new StringBuilder();
        foreach (var sheet in sheets.Elements<Sheet>())
        {
            if (sheet.Id?.Value is not { } id
                || workbook.GetPartById(id) is not WorksheetPart { Worksheet: { } worksheet })
                continue;

            sb.AppendLine($"--- Sheet: {sheet.Name} ---");
            foreach (var row in worksheet.Descendants<Row>())
            {
                var values = row.Elements<Cell>().Select(cell => CellText(cell, sharedStrings));
                var line = string.Join(" | ", values);
                if (!string.IsNullOrWhiteSpace(line.Replace("|", "")))
                    sb.AppendLine(line);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string CellText(Cell cell, List<string> sharedStrings)
    {
        var raw = cell.CellValue?.Text ?? "";
        var type = cell.DataType?.Value;

        if (type == CellValues.SharedString)
            return int.TryParse(raw, out var index) && index < sharedStrings.Count ? sharedStrings[index] : raw;
        if (type == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? "";
        if (type == CellValues.Boolean)
            return raw == "1" ? "TRUE" : "FALSE";

        // Numbers, and the cached result of formulas. Note: dates are stored as numbers
        // (days since 1900), so they appear as e.g. 45567 rather than a formatted date.
        return raw;
    }

    // ---------------------------------------------------------------- PowerPoint

    // Text in slides lives in drawing paragraphs (<a:p>) inside shapes.
    // Speaker notes are included too, because they often hold the real content.
    private static string ExtractPowerPoint(string fullPath)
    {
        using var doc = PresentationDocument.Open(fullPath, isEditable: false);
        var presentation = doc.PresentationPart;
        var slideIds = presentation?.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>();
        if (presentation is null || slideIds is null)
            return "";

        var sb = new StringBuilder();
        var number = 0;
        foreach (var slideId in slideIds)
        {
            number++;
            if (slideId.RelationshipId?.Value is not { } relId || presentation.GetPartById(relId) is not SlidePart slide)
                continue;

            sb.AppendLine($"--- Slide {number} ---");
            AppendParagraphs(sb, slide.Slide?.Descendants<A.Paragraph>());

            var notes = slide.NotesSlidePart?.NotesSlide?.Descendants<A.Paragraph>().ToList();
            if (notes is { Count: > 0 } && notes.Any(p => !string.IsNullOrWhiteSpace(p.InnerText)))
            {
                sb.AppendLine("Speaker notes:");
                AppendParagraphs(sb, notes);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void AppendParagraphs(StringBuilder sb, IEnumerable<A.Paragraph>? paragraphs)
    {
        foreach (var p in paragraphs ?? [])
            if (!string.IsNullOrWhiteSpace(p.InnerText))
                sb.AppendLine(p.InnerText);
    }

    // ---------------------------------------------------------------- PDF

    // ContentOrderTextExtractor rebuilds lines and spaces from the positions of the
    // letters on the page. (A PDF stores where each glyph is drawn, not "text" as such.)
    private static ExtractedText ExtractPdf(string fullPath)
    {
        using var pdf = PdfDocument.Open(fullPath);

        var sb = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            sb.AppendLine($"--- Page {page.Number} ---");
            sb.AppendLine(ContentOrderTextExtractor.GetText(page));
        }

        // A scanned PDF is just pictures of pages: no text to extract without OCR.
        var text = sb.ToString();
        if (text.Replace("--- Page", "").Count(char.IsLetterOrDigit) < 20)
            return ExtractedText.Fail($"{Path.GetFileName(fullPath)} contains no extractable text. It's probably a scanned image, which would need OCR.");

        return ExtractedText.Ok(text);
    }
}
