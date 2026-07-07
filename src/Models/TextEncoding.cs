using System;
using System.Text;

namespace SourceGit.Models
{
    public static class TextEncoding
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Lazy<Encoding> Gb18030 = new Lazy<Encoding>(CreateGb18030);

        public static string Decode(byte[] data)
        {
            return Decode(data.AsSpan());
        }

        public static string Decode(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
                return string.Empty;

            if (HasPrefix(data, 0xEF, 0xBB, 0xBF))
                return Encoding.UTF8.GetString(data[3..]);
            if (HasPrefix(data, 0xFF, 0xFE))
                return Encoding.Unicode.GetString(data[2..]);
            if (HasPrefix(data, 0xFE, 0xFF))
                return Encoding.BigEndianUnicode.GetString(data[2..]);

            if (TryDecode(StrictUtf8, data, out var text))
                return text;

            // GB18030 is a superset of GBK/GB2312, so it covers common legacy Chinese files
            // without forcing every repository to configure a specific working-tree encoding.
            if (TryDecode(Gb18030.Value, data, out text))
                return text;

            return Encoding.UTF8.GetString(data);
        }

        private static bool TryDecode(Encoding encoding, ReadOnlySpan<byte> data, out string text)
        {
            try
            {
                text = encoding.GetString(data);
                return true;
            }
            catch (DecoderFallbackException)
            {
                text = string.Empty;
                return false;
            }
        }

        private static Encoding CreateGb18030()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }

        private static bool HasPrefix(ReadOnlySpan<byte> data, byte first, byte second)
        {
            return data.Length >= 2 && data[0] == first && data[1] == second;
        }

        private static bool HasPrefix(ReadOnlySpan<byte> data, byte first, byte second, byte third)
        {
            return data.Length >= 3 && data[0] == first && data[1] == second && data[2] == third;
        }
    }
}
