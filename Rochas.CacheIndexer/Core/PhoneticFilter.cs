using System.Text;
using Rochas.Extensions;

namespace Rochas.CacheIndexer.Core
{
    /// <summary>
    /// Filtro fonético Soundex adaptado para PT-BR.
    /// Gera um código de 4 chars a partir da pronúncia aproximada da palavra.
    /// </summary>
    internal static class PhoneticFilter
    {
        public static string Generate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var clean = text.FilterSpecialChars();
            if (string.IsNullOrWhiteSpace(clean))
                return string.Empty;

            var str = clean.Trim().ToUpperInvariant();
            if (str.Length == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.Append(str[0]);

            for (int i = 1; i < str.Length; i++)
            {
                char c = str[i];
                char code = GetCode(c);
                if (code != '0' && code != GetCode(str[i - 1]))
                {
                    sb.Append(code);
                }
            }

            return sb.ToString().PadRight(4, '0').Substring(0, 4);
        }

        private static char GetCode(char c)
        {
            return c switch
            {
                'B' or 'F' or 'P' or 'V' => '1',
                'C' or 'G' or 'J' or 'K' or 'Q' or 'S' or 'X' or 'Z' => '2',
                'D' or 'T' => '3',
                'L' => '4',
                'M' or 'N' => '5',
                'R' => '6',
                _ => '0'
            };
        }
    }
}
