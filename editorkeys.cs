// -----------------------------------------------------------------------------
//  VS Code-style line editing for Editor: cut, copy, paste, delete and insert lines.
// -----------------------------------------------------------------------------

using Terminal.Gui.Editor.Document;
using Terminal.Gui.Input;

internal static class EditorKeys
{
    private static readonly Key DeleteLine = Key.K.WithCtrl.WithShift;

    private static readonly Key LineBelow = Key.Enter.WithCtrl;

    private static readonly Key LineAbove = Key.Enter.WithCtrl.WithShift;

    public static void AddEditingKeys (this Editor editor)
    {
        string? lineClip = null;

        editor.KeyDown += (_, key) =>
        {
            if (key.Handled || !editor.Multiline || editor.HasMultipleCarets)
            {
                return;
            }

            TextDocument doc = editor.Document!;

            if (key == Key.C.WithCtrl)
            {
                if (editor.HasSelection)
                {
                    lineClip = null;
                }
                else
                {
                    lineClip = CopyLines (editor, doc);
                    key.Handled = true;
                }

                return;
            }

            if (editor.ReadOnly)
            {
                return;
            }

            if (key == Key.X.WithCtrl)
            {
                if (editor.HasSelection)
                {
                    lineClip = null;
                }
                else
                {
                    lineClip = CutLines (editor, doc) ?? lineClip;
                    key.Handled = true;
                }
            }
            else if (key == Key.V.WithCtrl)
            {
                key.Handled = PasteLine (editor, doc, lineClip);
            }
            else if (key == DeleteLine)
            {
                RemoveBlock (editor, doc, Block (editor, doc));
                key.Handled = true;
            }
            else if (key == LineBelow)
            {
                InsertLine (editor, doc, below: true);
                key.Handled = true;
            }
            else if (key == LineAbove)
            {
                InsertLine (editor, doc, below: false);
                key.Handled = true;
            }
        };
    }

    /// <summary>The lines a line-wise command acts on: the caret line, or every line the selection touches.</summary>
    private readonly record struct LineBlock (DocumentLine First, DocumentLine Last)
    {
        public int Offset => First.Offset;

        public int End => Last.EndOffset + Last.DelimiterLength;

        public int Length => End - Offset;

        public bool AtDocumentEnd => Last.DelimiterLength == 0;
    }

    private static LineBlock Block (Editor editor, TextDocument doc)
    {
        int start = editor.SelectionStart;

        int last = editor.HasSelection ? Math.Max (start, editor.SelectionEnd - 1) : start;

        return new (doc.GetLineByOffset (start), doc.GetLineByOffset (last));
    }

    private static string NewLineFor (TextDocument doc, DocumentLine line)
    {
        for (DocumentLine? l = line; l is not null; l = l.PreviousLine)
        {
            if (l.DelimiterLength > 0)
            {
                return doc.GetText (l.EndOffset, l.DelimiterLength);
            }
        }

        return "\n";
    }

    private static string LineText (TextDocument doc, LineBlock block)
    {
        string body = doc.GetText (block.Offset, block.Last.EndOffset - block.Offset);

        string delimiter = block.AtDocumentEnd
                               ? NewLineFor (doc, block.Last)
                               : doc.GetText (block.Last.EndOffset, block.Last.DelimiterLength);

        return body + delimiter;
    }

    private static string CopyLines (Editor editor, TextDocument doc)
    {
        string text = LineText (doc, Block (editor, doc));

        editor.App?.Clipboard?.TrySetClipboardData (text);

        return text;
    }

    private static string? CutLines (Editor editor, TextDocument doc)
    {
        LineBlock block = Block (editor, doc);
        string text = LineText (doc, block);

        if (editor.App?.Clipboard?.TrySetClipboardData (text) is not true)
        {
            return null;
        }

        RemoveBlock (editor, doc, block);

        return text;
    }

    private static void RemoveBlock (Editor editor, TextDocument doc, LineBlock block)
    {
        int column = doc.GetLocation (editor.CaretOffset).Column;
        int blockOffset = block.Offset;
        int offset = blockOffset;
        int length = block.Length;

        if (block.AtDocumentEnd && block.First.PreviousLine is { } previous)
        {
            offset = previous.EndOffset;
            length = doc.TextLength - offset;
        }

        using (doc.RunUpdate ())
        {
            doc.Remove (offset, length);
        }

        DocumentLine landing = doc.GetLineByOffset (Math.Min (blockOffset, doc.TextLength));

        editor.ClearSelection ();
        editor.CaretOffset = landing.Offset + Math.Min (Math.Max (column - 1, 0), landing.Length);
    }

    private static bool PasteLine (Editor editor, TextDocument doc, string? lineClip)
    {
        if (lineClip is null
            || editor.HasSelection
            || editor.App?.Clipboard is not { } clipboard
            || !clipboard.TryGetClipboardData (out string text)
            || Flat (text) != Flat (lineClip))
        {
            return false;
        }

        int caret = editor.CaretOffset;
        int lineOffset = doc.GetLineByOffset (caret).Offset;

        using (doc.RunUpdate ())
        {
            doc.Insert (lineOffset, lineClip);
        }

        editor.CaretOffset = caret + lineClip.Length;

        return true;
    }

    private static string Flat (string text) => text.Replace ("\r\n", "\n");

    private static void InsertLine (Editor editor, TextDocument doc, bool below)
    {
        DocumentLine line = doc.GetLineByOffset (editor.CaretOffset);

        string indent = doc.GetText (TextUtilities.GetLeadingWhitespace (doc, line));
        string newLine = NewLineFor (doc, line);
        int lineOffset = line.Offset;
        int lineEnd = line.EndOffset;
        int caret;

        using (doc.RunUpdate ())
        {
            if (below)
            {
                doc.Insert (lineEnd, newLine + indent);
                caret = lineEnd + newLine.Length + indent.Length;
            }
            else
            {
                doc.Insert (lineOffset, indent + newLine);
                caret = lineOffset + indent.Length;
            }
        }

        editor.ClearSelection ();
        editor.CaretOffset = caret;
    }
}

