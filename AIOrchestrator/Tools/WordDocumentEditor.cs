// ============================================================================
// WordDocumentEditor.cs — find-and-replace inside a Word (.docx) document.
//
// Used by edit_file. The model sees a .docx the way read_file shows it: one
// line per paragraph. Text in Word is split into "runs" (pieces with the same
// formatting), and one word can be spread over several runs, so a plain string
// replace on the XML doesn't work. Instead the paragraphs are joined into the
// same text read_file shows, the match is found in that, and then mapped back
// onto the runs it covers:
//   - the replacement goes into the first run of the match, so it takes that
//     run's formatting (bold, font, ...); the rest of the matched text is cut
//     out of the following runs
//   - line breaks in new_text become new paragraphs, copying the paragraph
//     style of the one being edited
//   - removing a line break merges two paragraphs into one
// The document is edited in memory and only written to disk by the caller.
// ============================================================================

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace AIOrchestrator.Tools;

public sealed class WordDocumentEditor : IDisposable
{
    private readonly MemoryStream _stream;
    private readonly WordprocessingDocument _document;
    private readonly List<W.Paragraph> _paragraphs;

    // Where each paragraph starts in Text, so a position in Text can be turned into (paragraph, offset).
    private readonly List<int> _paragraphStarts = [];

    /// <summary>The document's text as read_file shows it: paragraphs separated by "\n".</summary>
    public string Text { get; }

    public static bool IsWordDocument(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".docm";

    /// <summary>
    /// The text of one paragraph. Only its own w:t elements: deleted text (tracked changes) and
    /// field codes are separate element types and left out, and text in a text box belongs to the
    /// text box's own paragraph. Shared with FileTextExtractor so reading and editing agree.
    /// </summary>
    public static string ParagraphText(W.Paragraph paragraph) =>
        string.Concat(OwnTexts(paragraph).Select(t => t.Text));

    /// <summary>Loads a copy of the file into memory, so nothing touches the file until the caller saves.</summary>
    public static WordDocumentEditor Open(string fullPath) => new(File.ReadAllBytes(fullPath));

    private WordDocumentEditor(byte[] bytes)
    {
        // An expandable MemoryStream (not new MemoryStream(bytes), which is fixed-size), as the
        // edited document can be bigger than the original.
        _stream = new MemoryStream();
        _stream.Write(bytes);
        _stream.Position = 0;
        _document = WordprocessingDocument.Open(_stream, isEditable: true);

        var body = _document.MainDocumentPart?.Document?.Body;
        _paragraphs = body?.Descendants<W.Paragraph>().ToList() ?? [];

        var texts = new List<string>();
        var position = 0;
        foreach (var paragraph in _paragraphs)
        {
            var text = ParagraphText(paragraph);
            _paragraphStarts.Add(position);
            texts.Add(text);
            position += text.Length + 1; // + 1 for the "\n" between paragraphs
        }
        Text = string.Join("\n", texts);
    }

    /// <summary>
    /// Replaces the text at each of <paramref name="starts"/> (positions in Text, each
    /// <paramref name="length"/> long, not overlapping) with <paramref name="newText"/>.
    /// Returns an error message, or null on success. Nothing is changed if there's an error.
    /// </summary>
    public string? Replace(IReadOnlyList<int> starts, int length, string newText)
    {
        var matches = starts.Order().Select(s => (Start: Locate(s), End: Locate(s + length))).ToList();

        // Joining paragraphs from different table cells (or a cell and the text after the table)
        // would wreck the table, so a match must stay within paragraphs that sit side by side.
        foreach (var (start, end) in matches)
        {
            var parent = _paragraphs[start.Paragraph].Parent;
            for (var i = start.Paragraph + 1; i <= end.Paragraph; i++)
                if (_paragraphs[i].Parent != parent)
                    return "old_text runs across different table cells (or into or out of a table). " +
                           "Edit each cell's text separately.";
        }

        // Last match first: an edit only changes its own paragraphs and the text after its start,
        // so the positions of the earlier matches stay valid.
        var lines = newText.Split('\n');
        for (var i = matches.Count - 1; i >= 0; i--)
            ReplaceSpan(matches[i].Start, matches[i].End, lines);
        return null;
    }

    /// <summary>Writes the edited document and returns the new file contents.</summary>
    public byte[] Save()
    {
        _document.Save();
        _document.Dispose();
        return _stream.ToArray();
    }

    public void Dispose()
    {
        _document.Dispose();
        _stream.Dispose();
    }

    private (int Paragraph, int Offset) Locate(int position)
    {
        var index = _paragraphStarts.BinarySearch(position);
        if (index < 0)
            index = ~index - 1; // not a paragraph start: the paragraph before the insertion point
        return (index, position - _paragraphStarts[index]);
    }

    private void ReplaceSpan((int Paragraph, int Offset) start, (int Paragraph, int Offset) end, string[] lines)
    {
        var first = _paragraphs[start.Paragraph];

        if (start.Paragraph == end.Paragraph)
        {
            if (lines.Length == 1)
                ReplaceInParagraph(first, start.Offset, end.Offset, lines[0]);
            else
                SplitParagraph(first, start.Offset, end.Offset, lines);
            return;
        }

        var last = _paragraphs[end.Paragraph];

        // Start of the match to the end of the first paragraph -> first line;
        // start of the last paragraph to the end of the match -> last line.
        ReplaceInParagraph(first, start.Offset, ParagraphText(first).Length, lines[0]);
        ReplaceInParagraph(last, 0, end.Offset, lines.Length > 1 ? lines[^1] : "");

        // Paragraphs in between are reused for the middle lines while they last, then removed.
        var middle = _paragraphs.Skip(start.Paragraph + 1).Take(end.Paragraph - start.Paragraph - 1).ToList();
        var middleLines = lines.Length > 2 ? lines[1..^1] : [];
        for (var i = 0; i < middle.Count; i++)
        {
            if (i < middleLines.Length)
                SetParagraphText(middle[i], middleLines[i]);
            else
                middle[i].Remove();
        }

        // More middle lines than paragraphs: add new ones, styled like the one before them.
        OpenXmlElement previous = middle.Count > 0 && middleLines.Length > 0 ? middle[Math.Min(middle.Count, middleLines.Length) - 1] : first;
        foreach (var line in middleLines.Skip(middle.Count))
            previous = previous.InsertAfterSelf(CloneWithText((W.Paragraph)previous, line));

        // A single line: the paragraph break was removed, so join the last paragraph onto the first.
        if (lines.Length == 1)
        {
            foreach (var child in last.ChildElements.Where(c => c is not W.ParagraphProperties).ToList())
            {
                child.Remove();
                first.AppendChild(child);
            }
            last.Remove();
        }
    }

    /// <summary>
    /// A match inside one paragraph, replaced by several lines: the paragraph is split in two.
    /// The first keeps the text before the match plus the first line, the second gets the last line
    /// plus the text after the match, and any lines in between become paragraphs of their own.
    /// </summary>
    private static void SplitParagraph(W.Paragraph paragraph, int from, int to, string[] lines)
    {
        var second = (W.Paragraph)paragraph.CloneNode(true);
        RemoveBookmarks(second); // bookmark names must be unique in a document

        // Replace the match before cutting off the front, so the last line keeps the match's formatting.
        ReplaceInParagraph(second, from, to, lines[^1]);
        ReplaceInParagraph(second, 0, from, "");
        ReplaceInParagraph(paragraph, from, ParagraphText(paragraph).Length, lines[0]);

        OpenXmlElement previous = paragraph;
        foreach (var line in lines[1..^1])
            previous = previous.InsertAfterSelf(CloneWithText(paragraph, line));
        previous.InsertAfterSelf(second);
    }

    private static void SetParagraphText(W.Paragraph paragraph, string text) =>
        ReplaceInParagraph(paragraph, 0, ParagraphText(paragraph).Length, text);

    /// <summary>A copy of the paragraph (same style and first-run formatting) holding just <paramref name="text"/>.</summary>
    private static W.Paragraph CloneWithText(W.Paragraph template, string text)
    {
        var clone = (W.Paragraph)template.CloneNode(true);
        RemoveBookmarks(clone); // bookmark names must be unique in a document
        SetParagraphText(clone, text);
        return clone;
    }

    private static void RemoveBookmarks(W.Paragraph paragraph)
    {
        foreach (var bookmark in paragraph.Descendants().Where(e => e is W.BookmarkStart or W.BookmarkEnd).ToList())
            bookmark.Remove();
    }

    /// <summary>
    /// Replaces characters from..to of the paragraph's text with <paramref name="replacement"/>
    /// (no line breaks). The replacement goes into the run where the match starts, so it gets that
    /// run's formatting; the rest of the matched text is cut from the runs after it.
    /// </summary>
    private static void ReplaceInParagraph(W.Paragraph paragraph, int from, int to, string replacement)
    {
        var placed = false;
        var position = 0;
        foreach (var t in OwnTexts(paragraph).ToList())
        {
            var s = position;
            var e = position + t.Text.Length;
            position = e;

            if (!placed)
            {
                // The run holding the start of the match. For a pure insert (from == to) a run that
                // ends exactly at the insert point will do too, so text can be added at the very end.
                if (e < from || (e == from && from != to))
                    continue;
                t.Text = t.Text[..(from - s)] + replacement + (to < e ? t.Text[(to - s)..] : "");
                t.Space = SpaceProcessingModeValues.Preserve; // keep leading/trailing spaces
                placed = true;
            }
            else if (s < to)
            {
                // A later run the match continues into: cut the matched part off its front.
                t.Text = to < e ? t.Text[(to - s)..] : "";
                t.Space = SpaceProcessingModeValues.Preserve;
            }
            else
            {
                break;
            }

            if (t.Text.Length == 0)
            {
                // Remove the emptied text, and its run too if nothing else (a tab, a picture, ...) is left in it.
                var run = t.Parent;
                t.Remove();
                if (run is W.Run && run.ChildElements.All(c => c is W.RunProperties))
                    run.Remove();
            }
        }

        // An empty paragraph has no run to put the text in, so add one.
        if (!placed && replacement.Length > 0)
            paragraph.AppendChild(new W.Run(new W.Text(replacement) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static IEnumerable<W.Text> OwnTexts(W.Paragraph paragraph) =>
        paragraph.Descendants<W.Text>().Where(t => t.Ancestors<W.Paragraph>().First() == paragraph);
}
