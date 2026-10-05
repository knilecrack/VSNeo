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
        /// <summary>
        /// Length of the ASCII prefix, bounded by <paramref name="limit"/>.
        /// Reads four UTF-16 code units per 64-bit load and tests every lane's
        /// high bits at once - any lane with 0xFF80 set is non-ASCII - instead
        /// of branching per character. x64 tolerates the potentially unaligned
        /// load; the fallback walk re-checks the tail scalar. One load, not two:
        /// a second at p + i + 2 covered chars i+2..i+5 while the loop only
        /// guaranteed i+4 &lt;= limit, reading past limit and, at the end of the
        /// string, past the string.
        /// </summary>
        private static unsafe int AsciiPrefixLength(string line, int limit)
        {
            int i = 0;
            fixed (char* p = line)
            {
                while (i + 4 <= limit)
                {
                    ulong lanes = *(ulong*)(p + i);
                    if ((lanes & 0xFF80FF80FF80FF80UL) != 0) break;
                    i += 4;
                }
                while (i < limit && p[i] < 0x80) i++;
            }
            return i;
        }

        public static int ByteToChar(string line, int byteOffset)
        {
            if (string.IsNullOrEmpty(line) || byteOffset <= 0) return 0;

            // ASCII prefix: one byte per char, so char and byte offsets agree.
            // Nearly every line in a code file is pure ASCII up to the caret,
            // which makes this the common case by a wide margin.
            int n = line.Length;
            int i = AsciiPrefixLength(line, n < byteOffset ? n : byteOffset);
            if (i == n || i == byteOffset) return i;

            int bytes = i;
            for (; i < n; i++)
            {
                if (bytes >= byteOffset) return i;
                bytes += Utf8Length(line, ref i);
            }
            return n;
        }

        public static int CharToByte(string line, int charOffset)
        {
            if (string.IsNullOrEmpty(line) || charOffset <= 0) return 0;

            int n = line.Length;
            int i = AsciiPrefixLength(line, n < charOffset ? n : charOffset);
            if (i == n || i == charOffset) return i;

            int bytes = i;
            for (; i < n && i < charOffset; i++)
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

            // The indexer lives on ITextSnapshot, not on the line: absolute
            // positions, offset from the line start. Reading through it costs
            // no allocation, which is the whole point of these overloads.
            var snapshot = line.Snapshot;
            int start = line.Start.Position;

            int i = 0;
            while (i < line.Length && i < byteOffset && snapshot[start + i] < 0x80) i++;
            if (i == line.Length || i == byteOffset) return i;

            int bytes = i;
            for (; i < line.Length; i++)
            {
                if (bytes >= byteOffset) return i;
                bytes += Utf8Length(line, ref i);
            }
            return line.Length;
        }

        public static int CharToByte(Microsoft.VisualStudio.Text.ITextSnapshotLine line, int charOffset)
        {
            if (charOffset <= 0) return 0;

            var snapshot = line.Snapshot;
            int start = line.Start.Position;

            int i = 0;
            while (i < line.Length && i < charOffset && snapshot[start + i] < 0x80) i++;
            if (i == line.Length || i == charOffset) return i;

            int bytes = i;
            for (; i < line.Length && i < charOffset; i++)
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
