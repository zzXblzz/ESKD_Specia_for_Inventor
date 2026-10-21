using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GroupSpecB
{
    /// <summary>Правила ГОСТ 2.113-75 (вариант Б) и ГОСТ 2.106: обозначения исполнений,
    /// сведение BOM исполнений в одну таблицу, порядок записи и нумерация позиций.</summary>
    public static class SpecLogic
    {
        static readonly Regex ExecRx = new Regex(@"^(.*\S)\s*-\s*(\d{1,3})$");
        static readonly Regex DocCodeRx = new Regex(@"[\s\.]*(СБ|ВО|ТЧ|ГЧ|МЭ|МЧ|УЧ)$");

        /// <summary>Обозначение без кода документа (например, «СБ»).</summary>
        public static string StripDocCode(string designation)
        {
            return DocCodeRx.Replace((designation ?? "").Trim(), "");
        }

        /// <summary>Разбирает «АБВГ.123456.789-01» на базовое обозначение и номер исполнения. Номер исполнения
        /// бывает только у обозначения вида ЕСКД — без пробелов и с точкой в базовой части; в «ГОСТ 7798-70»
        /// или «DIN 6921-8» число после дефиса — не номер исполнения.</summary>
        public static bool SplitExecution(string designation, out string baseDes, out string number)
        {
            string d = StripDocCode(designation);
            Match m = ExecRx.Match(d);
            if (m.Success && IsEskdBase(m.Groups[1].Value)) { baseDes = m.Groups[1].Value; number = m.Groups[2].Value; return true; }
            baseDes = d; number = "";
            return false;
        }

        static bool IsEskdBase(string b)
        {
            return b.IndexOf('.') > 0 && !Regex.IsMatch(b, @"\s");
        }

        public static string GuessBaseDesignation(List<Execution> execs)
        {
            Dictionary<string, int> cnt = new Dictionary<string, int>();
            string best = ""; int bestN = 0;
            foreach (Execution e in execs)
            {
                string b, n;
                SplitExecution(e.Designation, out b, out n);
                if (b == "") continue;
                int c; cnt.TryGetValue(b, out c); c++; cnt[b] = c;
                if (c > bestN) { bestN = c; best = b; }
            }
            return best;
        }

        public static string LabelFor(string designation, string baseDes)
        {
            string b, n;
            if (SplitExecution(designation, out b, out n) && b == baseDes)
            {
                return n.TrimStart('0') == "" ? "–" : n.PadLeft(2, '0');
            }
            return "–";
        }

        public static string BaseDesignation(SpecData data)
        {
            if (!string.IsNullOrEmpty(data.BaseDesignation)) return data.BaseDesignation.Trim();
            return GuessBaseDesignation(data.Executions);
        }

        public const string AsmDrawingKey = "auto:assembly-drawing";

        /// <summary>Номер исполнения по обозначению: основное (без номера) — 0, «-01» — 1 ...;
        /// обозначения другого базового — в конце.</summary>
        public static int ExecutionNumber(Execution e)
        {
            string b, n;
            if (SplitExecution(e.Designation, out b, out n)) return int.Parse(n);
            return 0;
        }

        /// <summary>Сводит BOM исполнений в данные спецификации, сохраняя ручные правки.</summary>
        public static void Merge(SpecData data, List<ExecutionBom> boms)
        {
            // Графы исполнений — по возрастанию номера исполнения («–» — основное — первым).
            StableSort(boms, (a, b) => ExecutionNumber(a.Execution).CompareTo(ExecutionNumber(b.Execution)));
            // Исполнения: сохраняем включение и подписи уже известных исполнений.
            Dictionary<string, Execution> oldEx = new Dictionary<string, Execution>();
            foreach (Execution e in data.Executions) oldEx[e.Key] = e;
            data.Executions = new List<Execution>();
            foreach (ExecutionBom eb in boms)
            {
                Execution e = eb.Execution;
                Execution old;
                if (oldEx.TryGetValue(e.Key, out old))
                {
                    e.Enabled = old.Enabled;
                    e.Label = old.Label;
                }
                data.Executions.Add(e);
            }
            string baseDes = BaseDesignation(data);
            foreach (Execution e in data.Executions)
                if (string.IsNullOrEmpty(e.Label)) e.Label = LabelFor(e.Designation, baseDes);

            // Строки из BOM. Элементы с одинаковым обозначением (без обозначения — с одинаковым
            // наименованием) сливаются в одну строку, количества суммируются — в т. ч. разные файлы
            // одной детали.
            Dictionary<string, SpecRow> existing = new Dictionary<string, SpecRow>(StringComparer.OrdinalIgnoreCase);
            foreach (SpecRow r in data.Rows)
            {
                if (r.Source != RowSource.Bom) continue;
                foreach (string k in r.AllKeys()) if (!existing.ContainsKey(k)) existing[k] = r;
            }
            Dictionary<string, SpecRow> byMerge = new Dictionary<string, SpecRow>();
            HashSet<SpecRow> used = new HashSet<SpecRow>();
            List<SpecRow> added = new List<SpecRow>();
            string mainKey = boms.Count > 0 ? boms[0].Execution.Key : "";
            foreach (ExecutionBom eb in boms)
            {
                Dictionary<SpecRow, double> sums = new Dictionary<SpecRow, double>();
                Dictionary<SpecRow, string> texts = new Dictionary<SpecRow, string>();
                foreach (BomItem it in eb.Items)
                {
                    string mk = MergeKey(it);
                    SpecRow row;
                    if (!byMerge.TryGetValue(mk, out row))
                    {
                        if (!existing.TryGetValue(it.Key, out row) || used.Contains(row))
                        {
                            row = new SpecRow();
                            row.Source = RowSource.Bom;
                            added.Add(row);
                        }
                        used.Add(row);
                        byMerge[mk] = row;
                        row.Key = it.Key;
                        row.MergedKeys.Clear();
                        row.InMain = false;
                        row.BomItem = it.ItemNumber;
                        if (!row.IsEdited("Section")) row.Section = it.Section;
                        if (!row.IsEdited("Format")) row.Format = Sections.HasFormat(row.Section) ? it.Format : "";
                        if (!row.IsEdited("Designation")) row.Designation = it.Designation;
                        if (!row.IsEdited("Name")) row.Name = it.Name;
                        if (!row.IsEdited("Note")) row.Note = it.Unit != "" && it.Note == "" ? it.Unit : it.Note;
                        row.Unit = it.Unit;
                        // количества пересчитываются целиком, кроме исправленных вручную
                        List<QtyEntry> keep = new List<QtyEntry>();
                        foreach (QtyEntry q in row.Qty) if (row.IsEdited("Qty:" + q.Exec)) keep.Add(q);
                        row.Qty = keep;
                    }
                    if (!string.Equals(it.Key, row.Key, StringComparison.OrdinalIgnoreCase) &&
                        !row.MergedKeys.Exists(k => string.Equals(k, it.Key, StringComparison.OrdinalIgnoreCase)))
                        row.MergedKeys.Add(it.Key);
                    if (eb.Execution.Key == mainKey)
                    {
                        row.InMain = true;
                        if (string.IsNullOrEmpty(row.BomItem)) row.BomItem = it.ItemNumber;
                    }
                    double qty;
                    if (TryParseQty(it.Qty, out qty))
                    {
                        double s; sums.TryGetValue(row, out s); sums[row] = s + qty;
                    }
                    else texts[row] = it.Qty;
                }
                foreach (KeyValuePair<SpecRow, double> kv in sums)
                    if (!kv.Key.IsEdited("Qty:" + eb.Execution.Key)) kv.Key.SetQty(eb.Execution.Key, FormatQty(kv.Value));
                foreach (KeyValuePair<SpecRow, string> kv in texts)
                    if (!sums.ContainsKey(kv.Key) && !kv.Key.IsEdited("Qty:" + eb.Execution.Key)) kv.Key.SetQty(eb.Execution.Key, kv.Value);
            }

            List<SpecRow> rows = new List<SpecRow>();
            foreach (SpecRow r in data.Rows)
            {
                if (r.Source == RowSource.Bom && !used.Contains(r)) continue; // больше нет в BOM
                rows.Add(r);
            }
            rows.AddRange(added);
            data.Rows = rows;
            EnsureAssemblyDrawingRow(data);
            if (data.ManualOrder)
            {
                GroupBySection(data);
                Renumber(data);
            }
            else SortAndNumber(data);
        }

        static string Norm(string s)
        {
            return System.Text.RegularExpressions.Regex.Replace((s ?? "").Replace('$', ' '), @"\s+", " ").Trim().ToUpperInvariant();
        }

        /// <summary>Ключ слияния: обозначение, а если его нет — наименование; плюс единица измерения, чтобы
        /// не складывать количества в разных единицах (один материал в мм и в кг — две строки).</summary>
        static string MergeKey(BomItem it)
        {
            string u = "|U:" + Norm(it.Unit);
            string d = Norm(it.Designation);
            if (d != "") return "D:" + d + u;
            string n = Norm(it.Name);
            if (n != "") return "N:" + n + u;
            return "K:" + it.Key;
        }

        static readonly System.Globalization.CultureInfo Ru = new System.Globalization.CultureInfo("ru-RU");

        public static bool TryParseQty(string s, out double v)
        {
            return double.TryParse((s ?? "").Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        /// <summary>Количество: дробное — с округлением до 3 знаков после запятой, без нулей в конце.</summary>
        public static string FormatQty(double v)
        {
            return Math.Round(v, 3, MidpointRounding.AwayFromZero).ToString("0.###", Ru);
        }

        /// <summary>Собирает строки по разделам, не меняя порядка внутри раздела.</summary>
        public static void GroupBySection(SpecData data)
        {
            List<SpecRow> rows = new List<SpecRow>(data.Rows);
            StableSort(rows, (a, b) => Sections.IndexOf(a.Section).CompareTo(Sections.IndexOf(b.Section)));
            data.Rows = rows;
        }

        public static void EnsureAssemblyDrawingRow(SpecData data)
        {
            SpecRow asmRow = data.Rows.Find(r => r.Key == AsmDrawingKey);
            if (!data.Settings.AddAssemblyDrawing)
            {
                if (asmRow != null) data.Rows.Remove(asmRow);
                return;
            }
            if (asmRow == null)
            {
                asmRow = new SpecRow();
                asmRow.Source = RowSource.Manual;
                asmRow.Key = AsmDrawingKey;
                asmRow.Section = Sections.Documentation;
                data.Rows.Insert(0, asmRow);
            }
            string spec = !string.IsNullOrEmpty(data.Settings.SpecDesignation)
                ? StripDocCode(data.Settings.SpecDesignation) : BaseDesignation(data);
            if (!asmRow.IsEdited("Designation")) asmRow.Designation = spec + "СБ";
            if (!asmRow.IsEdited("Name")) asmRow.Name = "Сборочный чертеж";
            if (!asmRow.IsEdited("Format"))
                asmRow.Format = !string.IsNullOrEmpty(data.DrawingFormat) ? data.DrawingFormat : data.Settings.AssemblyDrawingFormat ?? "";
            foreach (Execution e in data.Executions)
                if (!asmRow.IsEdited("Qty:" + e.Key)) asmRow.SetQty(e.Key, "X");
        }

        /// <summary>Естественное сравнение строк: «Деталь 2» &lt; «Деталь 10».</summary>
        public static int NaturalCompare(string a, string b)
        {
            a = a ?? ""; b = b ?? "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0'), nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                    int c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }

        static string SortKey(SpecRow r)
        {
            return Sections.SortByName(r.Section) ? r.Name + " " + r.Designation : r.Designation + " " + r.Name;
        }

        /// <summary>Ключ группы для порядка записи: исполнения одной составной части (одно базовое
        /// обозначение: «…519-02», «…519-03») записываются подряд, но каждое получает свой номер позиции.
        /// Номера позиций BOM для группировки не используются: надстройка сама их перенумеровывает.</summary>
        static string GroupKey(SpecRow r)
        {
            if (!Sections.HasExecutions(r.Section)) return "R:" + r.Id;
            string b, n;
            SplitExecution(r.Designation, out b, out n);
            if (!string.IsNullOrEmpty(b)) return "D:" + b.ToUpperInvariant();
            return "R:" + r.Id;
        }

        class Group
        {
            public string Key;
            public List<SpecRow> Rows = new List<SpecRow>();
            public bool InMain;
            public int FirstIndex;
        }

        /// <summary>Порядок записи по ГОСТ 2.106 и 2.113 п. 5.5.2 д): по разделам; в разделе — составные
        /// части основного исполнения, затем не применявшиеся в нём; варианты одной позиции подряд.
        /// Затем — сквозная нумерация позиций.</summary>
        public static void SortAndNumber(SpecData data)
        {
            List<SpecRow> result = new List<SpecRow>();
            foreach (string section in Sections.Order)
            {
                List<SpecRow> secRows = data.Rows.FindAll(r => r.Section == section);
                result.AddRange(OrderSection(section, secRows, data));
            }
            // строки с неизвестным разделом — в конец
            foreach (SpecRow r in data.Rows)
                if (Array.IndexOf(Sections.Order, r.Section) < 0) result.Add(r);
            data.Rows = result;
            Renumber(data);
        }

        static List<SpecRow> OrderSection(string section, List<SpecRow> rows, SpecData data)
        {
            if (section == Sections.Documentation)
            {
                List<SpecRow> docs = new List<SpecRow>(rows);
                StableSort(docs, (a, b) => NaturalCompare(a.Designation, b.Designation));
                SpecRow asm = docs.Find(r => r.Key == AsmDrawingKey);
                if (asm != null) { docs.Remove(asm); docs.Insert(0, asm); }
                return docs;
            }
            List<Group> groups = new List<Group>();
            Dictionary<string, Group> byKey = new Dictionary<string, Group>();
            for (int i = 0; i < rows.Count; i++)
            {
                SpecRow r = rows[i];
                string k = GroupKey(r);
                Group g;
                if (!byKey.TryGetValue(k, out g))
                {
                    g = new Group { Key = k, FirstIndex = i };
                    byKey[k] = g; groups.Add(g);
                }
                g.Rows.Add(r);
                if (r.InMain || r.Source != RowSource.Bom && HasQty(r, data.Executions.Count > 0 ? data.Executions[0].Key : ""))
                    g.InMain = true;
            }
            foreach (Group g in groups)
            {
                StableSort(g.Rows, (a, b) =>
                {
                    if (a.InMain != b.InMain) return a.InMain ? -1 : 1;
                    return NaturalCompare(SortKey(a), SortKey(b));
                });
            }
            StableSort(groups, (a, b) =>
            {
                if (a.InMain != b.InMain) return a.InMain ? -1 : 1;
                return NaturalCompare(SortKey(a.Rows[0]), SortKey(b.Rows[0]));
            });
            List<SpecRow> res = new List<SpecRow>();
            foreach (Group g in groups) res.AddRange(g.Rows);
            return res;
        }

        static bool HasQty(SpecRow r, string exec)
        {
            return exec != "" && r.GetQty(exec) != "";
        }

        /// <summary>Сквозная нумерация: у каждой составной части — свой номер позиции, который пишется
        /// в её строке. Номера, введённые вручную, сохраняются; остальные строки получают следующие
        /// свободные номера.</summary>
        public static void Renumber(SpecData data)
        {
            HashSet<int> manual = new HashSet<int>();
            foreach (SpecRow r in data.Rows)
            {
                int p;
                if (r.Section != Sections.Documentation && !r.Hidden && r.IsEdited("Pos") && int.TryParse(r.Pos, out p)) manual.Add(p);
            }
            int next = 0;
            string lastSection = null;
            int reserve = Math.Max(0, data.Settings.PosReserve);
            foreach (SpecRow r in data.Rows)
            {
                if (r.Section == Sections.Documentation) { r.Pos = ""; continue; }
                if (r.Hidden) { r.Pos = ""; continue; }
                // новый раздел начинается с пропуском номеров — резерв в конце предыдущего раздела
                if (lastSection != null && r.Section != lastSection) next += reserve;
                lastSection = r.Section;
                int p;
                if (r.IsEdited("Pos") && int.TryParse(r.Pos, out p)) { next = Math.Max(next, p); continue; }
                do { next++; } while (manual.Contains(next));
                r.Pos = next.ToString();
            }
        }

        /// <summary>Повторяющиеся номера позиций (возможны только при ручном вводе).</summary>
        public static List<string> DuplicatePositions(SpecData data)
        {
            Dictionary<string, int> cnt = new Dictionary<string, int>();
            foreach (SpecRow r in data.Rows)
            {
                if (r.Hidden || string.IsNullOrEmpty(r.Pos)) continue;
                int c; cnt.TryGetValue(r.Pos, out c); cnt[r.Pos] = c + 1;
            }
            List<string> res = new List<string>();
            foreach (KeyValuePair<string, int> kv in cnt) if (kv.Value > 1) res.Add(kv.Key);
            return res;
        }
        /// <summary>Сортировка, сохраняющая исходный порядок равных элементов.</summary>
        public static void StableSort<T>(List<T> list, Comparison<T> cmp)
        {
            List<KeyValuePair<int, T>> tmp = new List<KeyValuePair<int, T>>();
            for (int i = 0; i < list.Count; i++) tmp.Add(new KeyValuePair<int, T>(i, list[i]));
            tmp.Sort((a, b) =>
            {
                int c = cmp(a.Value, b.Value);
                return c != 0 ? c : a.Key.CompareTo(b.Key);
            });
            for (int i = 0; i < list.Count; i++) list[i] = tmp[i].Value;
        }
    }
}
