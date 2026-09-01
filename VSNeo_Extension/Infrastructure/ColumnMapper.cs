namespace VSNeo_Extension.Infrastructure
{
    /// <summary>
    /// Neovim reports cursor columns as byte offsets into UTF-8. Visual Studio
    /// wants UTF-16 character offsets. Any non-ASCII in the file and the caret
    /// lands in the wrong place, so both conversions live here and nowhere else.
    /// Test this with emoji and accented Latin on day one, not day ninety.
    /// </summary>
    internal static class ColumnMapper
    {
        public static int ByteToChar(string line, int byteOffset)
        {
            if (string.IsNullOrEmpty(line) || byteOffset <= 0) return 0;

            int bytes = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (bytes >= byteOffset) return i;
                bytes += Utf8Length(line, ref i);
            }
            return line.Length;
        }

        public static int CharToByte(string line, int charOffset)
        {
            if (string.IsNullOrEmpty(line) || charOffset <= 0) return 0;

            int bytes = 0;
            for (int i = 0; i < line.Length && i < charOffset; i++)
                bytes += Utf8Length(line, ref i);
            return bytes;
        }

        // Snapshot-line overloads: same conversions without materializing the
        // whole line as a string first. GetText() allocates one string per call,
        // and the key-adjacent call sites (buffer sync per typed character,
        // caret push per move) only ever read the prefix up to the offset.
        public static int ByteToChar(Microsoft.VisualStudio.Text.ITextSnapshotLine line, int byteOffset)
        {
            if (byteOffset <= 0) return 0;

            int bytes = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (bytes >= byteOffset) return i;
                bytes += Utf8Length(line, ref i);
            }
            return line.Length;
        }

        public static int CharToByte(Microsoft.VisualStudio.Text.ITextSnapshotLine line, int charOffset)
        {
            if (charOffset <= 0) return 0;

            int bytes = 0;
            for (int i = 0; i < line.Length && i < charOffset; i++)
                bytes += Utf8Length(line, ref i);
            return bytes;
        }

        private static int Utf8Length(Microsoft.VisualStudio.Text.ITextSnapshotLine line, ref int i)
        {
            // The indexer lives on ITextSnapshot, not on the line: absolute
            // positions, offset from the line start. Reading through it costs
            // no allocation, which is the whole point of these overloads.
            var snapshot = line.Snapshot;
            int start = line.Start.Position;

            if (char.IsHighSurrogate(snapshot[start + i]) && i + 1 < line.Length
                && char.IsLowSurrogate(snapshot[start + i + 1]))
            {
                i++; // consume the pair; caller's loop advances past the low surrogate
                return 4;
            }
            char c = snapshot[start + i];
            if (c < 0x80) return 1;
            if (c < 0x800) return 2;
            return 3;
        }

        private static int Utf8Length(string line, ref int i)
        {
            if (char.IsHighSurrogate(line[i]) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]))
            {
                i++; // consume the pair; caller's loop advances past the low surrogate
                return 4;
            }
            // Single BMP code point. A lone surrogate falls into the 3-byte bucket,
            // which is what UTF8Encoding would emit for its replacement char anyway.
            char c = line[i];
            if (c < 0x80) return 1;
            if (c < 0x800) return 2;
            return 3;
        }
    }
}
