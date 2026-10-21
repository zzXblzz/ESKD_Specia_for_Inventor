using System;
using System.Collections.Generic;
using System.Text;

namespace GroupSpecB
{
    public enum LineKind { Blank, Header, Data }

    /// <summary>Одна строка (8 мм) таблицы спецификации на листе.</summary>
    public class SpecLine
    {
        public LineKind Kind;
        public string Format = "", Zone = "", Pos = "", Designation = "", Name = "", Note = "";
        public string[] Qty = new string[10];
        public string AbbrevPrefix;      // сокращённое обозначение «-01» (ГОСТ 2.113 п. 5.3): пишется со смещением
                                         // на ширину этой части полного обозначения (базового, без номера исполнения)
        public SpecRow Row;              // строка-источник (для первой строки записи)
        public bool FirstOfRow;
    }

    public class SpecPage
    {
        public int Number;               // номер листа
        public int Decade;               // десяток исполнений: 0 — исполнения 1..10, 1 — 11..20 ...
        public List<Execution> Execs = new List<Execution>(); // исполнения в графах этого листа (до 10)
        public List<SpecLine> Lines = new List<SpecLine>();
        public bool Gost2106;            // лист спецификации по ГОСТ 2.106 (формы 1 / 1а), иначе групповой (1б / 1а)
        public bool First { get { return Number == 1; } }
    }

    /// <summary>Вёрстка: перенос текста по строкам, разделы, разбиение на листы и десятки исполнений.</summary>
    public static class Layout
    {
        // ширины граф по ГОСТ 2.113 формы 1а/1б, мм; у ГОСТ 2.106 формы 1/1а примечание — 22 мм
        public const double WDesignation = 70, WName = 63, WNote = 34, WNote2106 = 22;

        /// <summary>Разбивка текста графы на строки.
        /// Если в тексте есть «$» (или перевод строки) — это границы строк, задаваемые пользователем:
        /// каждая часть пишется отдельной строкой без переноса, а не влезающая по ширине — сжимается
        /// при выводе на чертёж. Без «$» текст переносится по словам по ширине графы.
        /// measure — ширина надписи на чертеже, мм (замер шрифтом листа); без него ширина оценивается
        /// по средней ширине символа (параметр «Средняя ширина символа»).</summary>
        public static List<string> Wrap(string text, double widthMm, SpecSettings s, Func<string, double> measure = null)
        {
            string src = (text ?? "").Replace("\r\n", "\n");
            if (HasManualBreaks(src))
            {
                List<string> parts = new List<string>();
                foreach (string p in src.Split('$', '\n')) parts.Add(p.Trim());
                return parts;
            }
            if (measure != null)
            {
                try { return WrapMeasured(src, widthMm - 2.0, measure); }
                catch (Exception ex) { Log.Write("Перенос по замеру текста", ex); }   // — по средней ширине символа
            }
            int max = Math.Max(4, (int)Math.Floor((widthMm - 2.0) / Math.Max(0.5, s.CharWidth)));
            List<string> lines = new List<string>();
            StringBuilder cur = new StringBuilder();
            foreach (string w0 in src.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string w = w0;
                while (w.Length > max)
                {
                    if (cur.Length > 0) { lines.Add(cur.ToString()); cur.Clear(); }
                    lines.Add(w.Substring(0, max));
                    w = w.Substring(max);
                }
                if (cur.Length == 0) cur.Append(w);
                else if (cur.Length + 1 + w.Length <= max) cur.Append(' ').Append(w);
                else { lines.Add(cur.ToString()); cur.Clear(); cur.Append(w); }
            }
            if (cur.Length > 0) lines.Add(cur.ToString());
            if (lines.Count == 0) lines.Add("");
            return lines;
        }

        /// <summary>Перенос по словам по фактической ширине текста: в строку добавляется слово, пока ширина
        /// строки не превысит avail. Слово шире графы режется по символам.</summary>
        static List<string> WrapMeasured(string src, double avail, Func<string, double> measure)
        {
            List<string> lines = new List<string>();
            string cur = "";
            foreach (string w0 in src.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string w = w0;
                string cand = cur == "" ? w : cur + " " + w;
                if (measure(cand) <= avail) { cur = cand; continue; }
                if (cur != "") { lines.Add(cur); cur = ""; }
                while (measure(w) > avail && w.Length > 1)
                {
                    int n = w.Length - 1;
                    while (n > 1 && measure(w.Substring(0, n)) > avail) n--;
                    lines.Add(w.Substring(0, n));
                    w = w.Substring(n);
                }
                cur = w;
            }
            if (cur != "") lines.Add(cur);
            if (lines.Count == 0) lines.Add("");
            return lines;
        }

        public static bool HasManualBreaks(string text)
        {
            return text != null && (text.IndexOf('$') >= 0 || text.IndexOf('\n') >= 0);
        }
        static List<SpecLine> RowLines(SpecRow r, List<Execution> execs, SpecSettings s, Func<string, double> measure, bool gost2106)
        {
            List<string> des = Wrap(r.Designation, WDesignation, s, measure);
            List<string> name = Wrap(r.Name, WName, s, measure);
            List<string> note = Wrap(r.Note, gost2106 ? WNote2106 : WNote, s, measure);
            int n = Math.Max(des.Count, Math.Max(name.Count, note.Count));
            List<SpecLine> res = new List<SpecLine>();
            for (int i = 0; i < n; i++)
            {
                SpecLine l = new SpecLine();
                l.Kind = LineKind.Data;
                l.Row = r;
                l.FirstOfRow = i == 0;
                if (i == 0)
                {
                    l.Format = Sections.HasFormat(r.Section) || r.IsEdited("Format") ? r.Format : "";
                    l.Zone = r.Zone; l.Pos = r.Pos;
                }
                l.Designation = i < des.Count ? des[i] : "";
                l.Name = i < name.Count ? name[i] : "";
                l.Note = i < note.Count ? note[i] : "";
                if (i == 0)
                {
                    // количество — в первой строке записи, в одной строке с номером позиции;
                    // у документов в спецификации по ГОСТ 2.106 графа «Кол.» не заполняется
                    if (!(gost2106 && r.Section == Sections.Documentation))
                        for (int k = 0; k < execs.Count && k < 10; k++) l.Qty[k] = r.GetQty(execs[k].Key);
                }
                res.Add(l);
            }
            return res;
        }

        static bool RowUsedIn(SpecRow r, List<Execution> execs)
        {
            foreach (Execution e in execs) if (r.GetQty(e.Key) != "") return true;
            return false;
        }

        static SpecLine Blank() { return new SpecLine { Kind = LineKind.Blank }; }

        /// <summary>Строит листы спецификации. При числе исполнений больше десяти запись повторяется
        /// для каждого следующего десятка (ГОСТ 2.113 п. 5.5.2 е, ж). Запись, которая длиннее целого листа,
        /// разрывается между листами (иначе её конец потерялся бы); об этом — строка в warnings.
        /// Спецификация по ГОСТ 2.106 — одна графа «Кол.» основного исполнения: строки, которые применяются
        /// только в других исполнениях, в неё не входят.</summary>
        public static List<SpecPage> Build(SpecData data, List<string> warnings = null, Func<string, double> measure = null)
        {
            SpecSettings s = data.Settings;
            bool gost2106 = data.Is2106;
            List<Execution> all = data.QtyExecutions();
            List<List<Execution>> decades = new List<List<Execution>>();
            for (int i = 0; i < all.Count; i += 10) decades.Add(all.GetRange(i, Math.Min(10, all.Count - i)));
            if (decades.Count == 0) decades.Add(new List<Execution>());

            List<SpecPage> pages = new List<SpecPage>();
            for (int d = 0; d < decades.Count; d++)
            {
                List<Execution> execs = decades[d];
                // блоки: заголовок раздела (с пустыми строками) привязан к первой записи раздела
                List<List<SpecLine>> blocks = new List<List<SpecLine>>();
                foreach (string section in Sections.Order)
                {
                    // строка без количества ни в одном исполнении (документ, свободная запись) — на листах
                    // первого десятка, иначе при больше чем 10 исполнениях она пропала бы со всех листов
                    List<SpecRow> rows = data.Rows.FindAll(r => r.Section == section && !r.Hidden &&
                        (gost2106 ? data.InSpec2106(r)
                                  : decades.Count == 1 || RowUsedIn(r, execs) || d == 0 && !RowUsedIn(r, all)));
                    if (rows.Count == 0) continue;
                    bool firstInSection = true;
                    foreach (SpecRow r in rows)
                    {
                        List<SpecLine> block = new List<SpecLine>();
                        if (firstInSection)
                        {
                            block.Add(Blank());
                            block.Add(new SpecLine { Kind = LineKind.Header, Name = section });
                            block.Add(Blank());
                            firstInSection = false;
                        }
                        block.AddRange(RowLines(r, execs, s, measure, gost2106));
                        blocks.Add(block);
                    }
                    for (int k = 0; k < s.ReserveLines; k++) blocks.Add(new List<SpecLine> { Blank() });
                }

                SpecPage page = NewPage(pages, d, execs, gost2106);
                foreach (List<SpecLine> block in blocks)
                {
                    int cap = Capacity(page, s);
                    if (page.Lines.Count + block.Count > cap && page.Lines.Count > 0)
                    {
                        // пустые строки в конце блока-резерва на новый лист не переносим
                        if (block.Count == 1 && block[0].Kind == LineKind.Blank) continue;
                        page = NewPage(pages, d, execs, gost2106);
                    }
                    // запись не помещается даже на пустой лист — разрываем
                    List<SpecLine> rest = block;
                    while (page.Lines.Count + rest.Count > Capacity(page, s))
                    {
                        int room = Capacity(page, s) - page.Lines.Count;
                        page.Lines.AddRange(rest.GetRange(0, room));
                        rest = rest.GetRange(room, rest.Count - room);
                        if (warnings != null)
                        {
                            SpecLine first = block.Find(l => l.Row != null);
                            string what = first == null ? "" : (first.Row.Designation != "" ? first.Row.Designation : first.Row.Name);
                            warnings.Add("Запись «" + what + "» длиннее листа и разорвана между листами " +
                                         page.Number + " и " + (page.Number + 1) + ". Сократите текст или задайте переносы «$».");
                        }
                        page = NewPage(pages, d, execs, gost2106);
                    }
                    page.Lines.AddRange(rest);
                }
            }
            foreach (SpecPage p in pages)
            {
                // сокращение «-02» — правило групповой спецификации (ГОСТ 2.113 п. 5.3)
                if (s.AbbreviateExecutions && !gost2106) Abbreviate(p);
            }
            return pages;
        }

        static SpecPage NewPage(List<SpecPage> pages, int decade, List<Execution> execs, bool gost2106)
        {
            SpecPage p = new SpecPage { Number = pages.Count + 1, Decade = decade, Execs = execs, Gost2106 = gost2106 };
            pages.Add(p);
            return p;
        }

        public static int Capacity(SpecPage p, SpecSettings s)
        {
            if (p.Gost2106) return p.First ? Math.Max(1, s.RowsFirst2106) : Math.Max(1, s.RowsNext2106);
            return p.First ? Math.Max(1, s.RowsFirst) : Math.Max(1, s.RowsNext);
        }

        /// <summary>ГОСТ 2.113 п. 5.3: при записи подряд на одном листе составных частей с одинаковым
        /// базовым обозначением у последующих указывают дефис и номер исполнения, формат — только у первой.</summary>
        static void Abbreviate(SpecPage p)
        {
            string prevBase = null;
            foreach (SpecLine l in p.Lines)
            {
                if (l.Kind != LineKind.Data) { prevBase = null; continue; }
                if (!l.FirstOfRow) continue;
                if (l.Row == null || !Sections.HasExecutions(l.Row.Section)) { prevBase = null; continue; }
                string b, n;
                bool hasNum = SpecLogic.SplitExecution(l.Row.Designation, out b, out n);
                if (b == "" || HasManualBreaks(l.Row.Designation)) { prevBase = null; continue; }
                if (prevBase != null && hasNum && b == prevBase)
                {
                    string full = l.Row.Designation.Trim();
                    l.AbbrevPrefix = full.Substring(0, full.LastIndexOf('-')).TrimEnd();
                    l.Designation = "-" + n;
                    l.Format = "";
                }
                prevBase = b;
            }
        }

        /// <summary>Запись на первом листе «Исполнения 10 ... 19 — см. листы 4, 5».</summary>
        public static List<string> DecadeNote(List<SpecPage> pages)
        {
            List<string> res = new List<string>();
            int maxDecade = 0;
            foreach (SpecPage p in pages) maxDecade = Math.Max(maxDecade, p.Decade);
            if (maxDecade == 0) return res;
            for (int d = 1; d <= maxDecade; d++)
            {
                List<SpecPage> ps = pages.FindAll(p => p.Decade == d);
                if (ps.Count == 0) continue;
                string first = ps[0].Execs[0].Label, last = ps[0].Execs[ps[0].Execs.Count - 1].Label;
                string sheets;
                if (ps.Count == 1) sheets = "лист " + ps[0].Number;
                else if (ps.Count == 2) sheets = "листы " + ps[0].Number + ", " + ps[1].Number;
                else sheets = "листы " + ps[0].Number + " ... " + ps[ps.Count - 1].Number;
                res.Add((res.Count == 0 ? "Исполнения " : "") + first + " ... " + last + " — см. " + sheets +
                        (d < maxDecade ? "," : ""));
            }
            return res;
        }
    }
}
