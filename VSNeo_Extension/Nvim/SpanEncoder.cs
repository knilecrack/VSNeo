using System;

namespace VSNeo_Extension.Nvim
{
    /// <summary>
    /// Writes nvim_buf_set_text-shaped spans straight into a request frame.
    /// Building a span as object[] first cost the array and four boxed ints -
    /// per typed character, since every edit Visual Studio makes rides one of
    /// these through apply_spans.
    /// </summary>
    internal static class SpanEncoder
    {
        /// <summary>
        /// One change as [startRow, startCol, endRow, endCol, lines]. Rows are
        /// 0-based; columns are UTF-8 byte offsets (the caller runs ColumnMapper).
        /// </summary>
        public static void WriteSpan(
            MsgPackWriter w, int startRow, int startCol, int endRow, int endCol, string newText)
        {
            w.WriteArrayHeader(5);
            w.WriteInt64(startRow);
            w.WriteInt64(startCol);
            w.WriteInt64(endRow);
            w.WriteInt64(endCol);
            WriteLines(w, newText);
        }

        /// <summary>
        /// The replacement as one str per line. Empty text is a pure deletion:
        /// a single empty line, which joins the two ends together. The breaks
        /// are counted up front because the array header needs the line count;
        /// text.Split would hand back a string[] that exists only to be encoded.
        /// </summary>
        public static void WriteLines(MsgPackWriter w, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                w.WriteArrayHeader(1);
                w.WriteValue(string.Empty);
                return;
            }

            int lines = 1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n') lines++;
                else if (c == '\r') { lines++; if (i + 1 < text.Length && text[i + 1] == '\n') i++; }
            }

            w.WriteArrayHeader(lines);
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\r' && text[i] != '\n') continue;
                w.WriteValue(text.Substring(start, i - start));
                if (i < text.Length && text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
        }
    }
}
