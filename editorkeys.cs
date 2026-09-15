// -----------------------------------------------------------------------------
//  VS Code-style line editing for Editor: cut, copy, paste, delete and insert lines,
//  plus word-border movement for Ctrl(+Shift)+Right.
// -----------------------------------------------------------------------------

using Terminal.Gui.Editor.Document;
using Terminal.Gui.Input;

internal static class EditorKeys
{
    private static readonly Key DeleteLine = Key.K.WithCtrl.WithShift;

    private static readonly Key LineBelow = Key.Enter.WithCtrl;

    private static readonly Key LineAbove = Key.Enter.WithCtrl.WithShift;

    private static readonly Key WordRight = Key.CursorRight.WithCtrl;

    private static readonly Key SelectWordRight = Key.CursorRight.WithCtrl.WithShift;

    public static void AddEditingKeys (this Editor editor)
    {
        string? lineClip = null;

        editor.KeyDown += (_, key) =>
        {
            if (key.Handled || editor.HasMultipleCarets)
            {
                return;
            }

            TextDocument doc = editor.Document!;

            if (key == WordRight || key == SelectWordRight)
            {
                MoveWordRight (editor, doc, extend: key == SelectWordRight);
                key.Handled = true;

                return;
            }

            if (!editor.Multiline)
            {
                return;
            }

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
                if (lineClip is { } clip && !editor.HasSelection && StillClipped (editor, clip))
                {
                    PasteLine (editor, doc, clip);
                    key.Handled = true;
                }
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

    /// <summary>
    ///     The next word border at or after <paramref name="caret" />. The Editor's own Ctrl+Right uses
    ///     <see cref="CaretPositioningMode.WordStartOrSymbol" />, which has no stop at the end of a word, so
    ///     moving right off a word runs through the trailing whitespace and lands on the next word's first
    ///     character &#8212; selecting that whitespace along with the word. Word borders stop at both ends.
    /// </summary>
    private static int WordBorderRight (TextDocument doc, int caret)
    {
        int next = TextUtilities.GetNextCaretPosition (doc, caret, LogicalDirection.Forward, CaretPositioningMode.WordBorderOrSymbol);

        return next < 0 ? doc.TextLength : Math.Min (next, doc.TextLength);
    }

    private static void MoveWordRight (Editor editor, TextDocument doc, bool extend)
    {
        int target = WordBorderRight (doc, editor.CaretOffset);

        if (!extend)
        {
            editor.ClearSelection ();
            editor.CaretOffset = target;

            return;
        }

        // RightExtend keeps the selection anchored where the user started, which a SelectRange built
        // from the current offsets cannot do once the selection runs backwards from the anchor. It
        // advances one grapheme cluster per call, so step until the caret reaches the border.
        while (editor.CaretOffset < target)
        {
            int before = editor.CaretOffset;

            editor.InvokeCommand (Command.RightExtend);

            if (editor.CaretOffset <= before)
            {
                break;
            }
        }
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

    /// <summary>
    ///     True while the line clip is still what a paste would deliver. Bodies are compared because the
    ///     OS round-trip does not return the trailing delimiter byte-identically. An unreadable clipboard
    ///     leaves the clip as the source of truth.
    /// </summary>
    private static bool StillClipped (Editor editor, string lineClip)
    {
        return editor.App?.Clipboard is not { } clipboard
               || !clipboard.TryGetClipboardData (out string text)
               || Body (text) == Body (lineClip);
    }

    /// <summary>Inserts the clipped line above the caret's line, the way a line-wise copy is meant to land.</summary>
    private static void PasteLine (Editor editor, TextDocument doc, string lineClip)
    {
        int caret = editor.CaretOffset;
        int lineOffset = doc.GetLineByOffset (caret).Offset;

        using (doc.RunUpdate ())
        {
            doc.Insert (lineOffset, lineClip);
        }

        editor.CaretOffset = caret + lineClip.Length;
    }

    private static string Flat (string text) => text.Replace ("\r\n", "\n");

    /// <summary>The clip without its line delimiter, for comparing a copy against the clipboard.</summary>
    private static string Body (string text) => Flat (text).TrimEnd ('\n');

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

