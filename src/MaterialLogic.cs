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
}
