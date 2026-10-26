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

    /// <summary>Масса и масштаб для основной надписи чертежа — как в ESKD Specia (PropertiesTools.CorrectMass,
    /// CorrectScale): UserMass — масса модели первого вида в кг (от 10 т — в тоннах с «т»), UserScale — «1:2»,
    /// «2:1». Десятичный разделитель — запятая. Без Inventor API (проверяется в UnitTests).</summary>
    public static class MassScaleLogic
    {
        public const string Mass = "UserMass", Scale = "UserScale";
        static readonly System.Globalization.CultureInfo Ru = new System.Globalization.CultureInfo("ru-RU");

        /// <summary>Масса, кг: до 1 кг — 3 знака (но не 0 у ненулевой массы), до 10 — 2, до 100 — 1, до 10 т — целые,
        /// до 100 т — тонны с 1 знаком, дальше — целые тонны. В Specia до 1 кг к массе прибавлялся 0,001
        /// (0,5 кг → «0,501»), здесь округление обычное.</summary>
        public static string FormatMass(double kg)
        {
            string unit = "";
            double v;
            if (kg >= 100000) { v = R(kg / 1000, 0); unit = "т"; }
            else if (kg >= 10000) { v = R(kg / 1000, 1); unit = "т"; }
            else if (kg >= 100) v = R(kg, 0);
            else if (kg >= 10) v = R(kg, 1);
            else if (kg >= 1) v = R(kg, 2);
            else { v = R(kg, 3); if (v == 0 && kg > 0) v = 0.001; }
            return v.ToString(Ru) + unit;
        }

        /// <summary>Масштаб вида: увеличение — «2:1», уменьшение — «1:2,5» (знаменатель до десятых, отбрасыванием).</summary>
        public static string FormatScale(double scale)
        {
            if (scale <= 0) return "";
            if (scale >= 1) return R(scale, 3).ToString(Ru) + ":1";
            return "1:" + (Math.Floor(1 / scale * 10 + 1e-6) / 10).ToString(Ru);
        }

        static double R(double v, int digits) { return Math.Round(v, digits, MidpointRounding.AwayFromZero); }
    }

    /// <summary>Материал дробью в графе «Наименование»: сортамент, над чертой — Up, под чертой — Down.</summary>
    public class MaterialFraction
    {
        public string Rolling = "", Up = "", Down = "";
    }
}
