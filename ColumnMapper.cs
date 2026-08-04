using System.Text;

namespace VSNeo.Infrastructure
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

        private static int Utf8Length(string line, ref int i)
        {
            if (char.IsHighSurrogate(line[i]) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]))
            {
                i++; // consume the pair; caller's loop advances past the low surrogate
                return 4;
            }
            return Encoding.UTF8.GetByteCount(line, i, 1);
        }
    }
}
