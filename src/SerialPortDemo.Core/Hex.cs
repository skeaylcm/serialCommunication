using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SerialPortDemo
{
    /// <summary>
    /// 十六进制文本与字节数组的互转，以及报文打印。
    /// </summary>
    public static class Hex
    {
        private static readonly char[] Separators = { ' ', ',', ';', '-', ':', '\t', '\r', '\n' };

        /// <summary>转成 "AA 01 00 00" 形式。</summary>
        public static string ToString(byte[] data) => ToString(data, 0, data?.Length ?? 0);

        /// <summary>转成 "AA 01 00 00" 形式。</summary>
        public static string ToString(byte[] data, int offset, int count)
        {
            if (data == null) return string.Empty;
            var builder = new StringBuilder(count * 3);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) builder.Append(' ');
                builder.Append(data[offset + i].ToString("X2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        /// <summary>
        /// 解析十六进制文本，允许 "AA 01"、"AA0100"、"0xAA,0x01" 等多种写法。
        /// </summary>
        public static bool TryParse(string text, out byte[] bytes, out string? error)
        {
            bytes = new byte[0];
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "内容为空";
                return false;
            }

            var tokens = text!.Split(Separators, System.StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 1)
            {
                // 支持不带分隔符的连续写法，如 "AA0100"
                string single = StripPrefix(tokens[0]);
                if (single.Length > 2 && single.Length % 2 == 0 && IsHex(single))
                {
                    var split = new string[single.Length / 2];
                    for (int i = 0; i < split.Length; i++) split[i] = single.Substring(i * 2, 2);
                    tokens = split;
                }
            }

            var result = new List<byte>(tokens.Length);
            foreach (var raw in tokens)
            {
                string token = StripPrefix(raw);
                if (token.Length == 0) continue;
                if (token.Length > 2 || !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
                {
                    error = "无法识别的十六进制片段: \"" + raw + "\"";
                    return false;
                }
                result.Add(value);
            }

            if (result.Count == 0)
            {
                error = "内容为空";
                return false;
            }

            bytes = result.ToArray();
            return true;
        }

        /// <summary>16 字节一行的报文转储（偏移 + HEX + ASCII），用于日志排查。</summary>
        public static string Dump(byte[] data, int offset, int count)
        {
            var builder = new StringBuilder();
            for (int line = 0; line < count; line += 16)
            {
                int length = Math.Min(16, count - line);
                builder.Append((offset + line).ToString("X4", CultureInfo.InvariantCulture)).Append("  ");
                var ascii = new StringBuilder();
                for (int i = 0; i < length; i++)
                {
                    byte value = data[offset + line + i];
                    builder.Append(value.ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                    ascii.Append(value >= 0x20 && value <= 0x7E ? (char)value : '.');
                }
                for (int i = length; i < 16; i++) builder.Append("   ");
                builder.Append(' ').Append(ascii).AppendLine();
            }
            return builder.ToString();
        }

        public static string Dump(byte[] data) => Dump(data, 0, data?.Length ?? 0);

        private static string StripPrefix(string token)
        {
            if (token.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith("\\x", System.StringComparison.Ordinal))
            {
                return token.Substring(2);
            }
            return token;
        }

        private static bool IsHex(string text)
        {
            foreach (char c in text)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return text.Length > 0;
        }
    }
}
