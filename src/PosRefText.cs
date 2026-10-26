using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GroupSpecB
{
    /// <summary>Ссылки на позиции в текстах чертежа: «поз.12», «поз. 12, 14 и 16», «поз.3-5».
    /// После перенумерации BOM старые номера заменяются новыми — все сразу по таблице старый → новый,
    /// без цепочек (12→14 и 14→16 дают «поз.14, 16», а не «поз.16, 16»). Текст может быть
    /// форматированным (FormattedText Inventor): теги между «поз.» и номерами пропускаются и не меняются.</summary>
    public static class PosRefText
    {
        const string Gap = @"(?:\s|&nbsp;|&#160;|<[^<>]*>)*";        // пробелы и теги форматирования
        const string Num = @"\d+(?![\d\p{L}]|\.\d)";                  // «12», но не «12а», «12.5», «M12»
        const string Sep = Gap + @"(?:,|;|-|–|—|÷|\bи\b)" + Gap;      // «12, 14», «12 и 14», «3-5»
        const string NotQty = @"(?!" + Gap + @"(?:шт|мм|мест|раз|ед\b|°|%))";   // «поз.12, 2 шт.» — 2 не позиция

        static readonly Regex Ref = new Regex(
            @"(?<![\p{L}\d])поз(?:иц\p{L}*|\.)?" + Gap + "(" + Num + NotQty + "(?:" + Sep + Num + NotQty + ")*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex TagOrNum = new Regex(@"<[^<>]*>|&[#\w]+;|\d+");

        /// <summary>Заменяет номера в ссылках «поз.» по таблице старый → новый. Возвращает новый текст
        /// (тот же объект, если замен нет); used — какие старые номера заменены.</summary>
        public static string Replace(string text, IDictionary<string, string> map, ICollection<string> used)
        {
            if (string.IsNullOrEmpty(text) || map == null || map.Count == 0) return text;
            if (text.IndexOf("поз", StringComparison.OrdinalIgnoreCase) < 0) return text;
            bool any = false;
            string res = Ref.Replace(text, m =>
            {
                Group g = m.Groups[1];
                string nums = TagOrNum.Replace(g.Value, t =>
                {
                    string to;
                    if (t.Value[0] == '<' || t.Value[0] == '&' || !map.TryGetValue(t.Value, out to) || to == t.Value) return t.Value;
                    any = true;
                    if (used != null && !used.Contains(t.Value)) used.Add(t.Value);
                    return to;
                });
                return m.Value.Substring(0, g.Index - m.Index) + nums + m.Value.Substring(g.Index - m.Index + g.Length);
            });
            return any ? res : text;
        }
    }
}
