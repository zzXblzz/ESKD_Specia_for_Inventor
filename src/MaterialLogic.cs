using System;
using System.Collections.Generic;

namespace GroupSpecB
{
    /// <summary>Материал детали для основной надписи чертежа — как в ESKD Specia (PropertiesTools.UpdateMaterial).
    /// Материал из трёх частей «Сортамент\Марка\Стандарт» (разделитель «\» или «|») раскладывается в дробь:
    /// RollingMaterial — сортамент, MaterialUpLine — над чертой, MaterialDownLine — под чертой, MaterialLine — черта
    /// из «_». Остальные материалы — одной строкой в SingleMaterial. Без Inventor API (проверяется в UnitTests).</summary>
    public static class MaterialLogic
    {
        public const string Single = "SingleMaterial", Rolling = "RollingMaterial",
                            UpLine = "MaterialUpLine", DownLine = "MaterialDownLine", Line = "MaterialLine";

        public static readonly string[] All = { DownLine, Line, UpLine, Rolling, Single };

        /// <summary>SingleMaterial заполнен вручную и не перезаписывается: «-», «_», «__» или «см. …»
        /// (в Specia — IsOrderChange). Тогда дробь очищается, а SingleMaterial остаётся как есть.</summary>
        public static bool IsManual(string single)
        {
            string s = (single ?? "").Trim().ToLowerInvariant();
            return s == "_" || s == "-" || s == "__" || s.StartsWith("см.");
        }

        /// <summary>Значения всех пяти свойств для материала детали (null — деталь не найдена: всё пусто).</summary>
        public static Dictionary<string, string> Values(string material)
        {
            Dictionary<string, string> v = new Dictionary<string, string>();
            foreach (string n in All) v[n] = "";
            if (material == null) return v;
            string[] parts = SplitParts(material);
            if (parts == null) { v[Single] = material; return v; }
            v[Rolling] = parts[0];
            v[UpLine] = parts[1];
            v[DownLine] = parts[2];
            v[Line] = new string('_', Math.Max(parts[1].Length, parts[2].Length) + 1);
            return v;
        }

        /// <summary>Материал из трёх частей в наименовании позиции спецификации (например «Панель &lt;Material&gt;»):
        /// пишется дробью — сортамент, числитель над чертой, знаменатель под ней. Если материал модели известен
        /// и входит в наименование, текст до и после него — prefix и suffix; иначе дробью считается всё
        /// наименование из трёх частей, а сортамент — вся первая часть. null — в наименовании нет дроби.</summary>
        public static MaterialFraction Fraction(string name, string material, out string prefix, out string suffix)
        {
            prefix = suffix = "";
            if (string.IsNullOrEmpty(name)) return null;
            string[] parts;
            int at = string.IsNullOrEmpty(material) || SplitParts(material) == null ? -1 : name.IndexOf(material, StringComparison.Ordinal);
            if (at >= 0)
            {
                parts = SplitParts(material);
                prefix = name.Substring(0, at).Trim();
                suffix = name.Substring(at + material.Length).Trim();
            }
            else parts = SplitParts(name);
            if (parts == null) return null;
            MaterialFraction f = new MaterialFraction { Rolling = parts[0].Trim(), Up = parts[1].Trim(), Down = parts[2].Trim() };
            if (f.Up == "" || f.Down == "") { prefix = suffix = ""; return null; }
            return f;
        }

        /// <summary>Три части материала через «\», иначе через «|»; null — материал не составной.
        /// Пробелы не обрезаются — как в Specia, чтобы при загруженной Specia значения совпадали.</summary>
        public static string[] SplitParts(string material)
        {
            foreach (char sep in new[] { '\\', '|' })
            {
                string[] p = material.Split(sep);
                if (p.Length == 3) return p;
            }
            return null;
        }
    }

    /// <summary>Материал дробью в графе «Наименование»: сортамент, над чертой — Up, под чертой — Down.</summary>
    public class MaterialFraction
    {
        public string Rolling = "", Up = "", Down = "";
    }
}
