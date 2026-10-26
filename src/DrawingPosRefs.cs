using System;
using System.Collections.Generic;
using Inventor;

namespace GroupSpecB
{
    /// <summary>Ссылки «поз.» на листах чертежа после перенумерации BOM: общие и выносные примечания,
    /// таблицы, тексты эскизов листов и видов, подсказки эскизных обозначений (технические требования).
    /// Листы спецификации пропускаются.</summary>
    public static class DrawingPosRefs
    {
        /// <summary>Заменяет старые номера позиций новыми. Возвращает отчёт или null, если замен не было.</summary>
        public static string Update(DrawingDocument dwg, IDictionary<string, string> map)
        {
            if (dwg == null || map == null || map.Count == 0) return null;
            List<string> used = new List<string>();
            List<string> errors = new List<string>();
            int texts = 0;
            foreach (Sheet sh in dwg.Sheets)
            {
                if (SpecSession.IsSpecSheet(sh)) continue;
                string where = sh.Name;
                try
                {
                    foreach (GeneralNote n in sh.DrawingNotes.GeneralNotes)
                        texts += Fix(() => n.FormattedText, v => n.FormattedText = v, map, used, errors, where);
                    foreach (LeaderNote n in sh.DrawingNotes.LeaderNotes)
                        texts += Fix(() => n.FormattedText, v => n.FormattedText = v, map, used, errors, where);
                }
                catch (Exception ex) { errors.Add(where + ": примечания — " + ex.Message); }

                try
                {
                    foreach (CustomTable t in sh.CustomTables)
                        for (int r = 1; r <= t.Rows.Count; r++)
                            for (int c = 1; c <= t.Columns.Count; c++)
                            {
                                Cell cell = t.Rows[r][c];
                                texts += Fix(() => cell.Value, v => cell.Value = v, map, used, errors, where + ", таблица «" + t.Title + "»");
                            }
                }
                catch (Exception ex) { errors.Add(where + ": таблицы — " + ex.Message); }

                try
                {
                    foreach (SketchedSymbol s in sh.SketchedSymbols)
                        foreach (Inventor.TextBox tb in s.Definition.Sketch.TextBoxes)
                        {
                            // постоянный текст общий для всех вставок обозначения — меняются только подсказки
                            string f;
                            try { f = tb.FormattedText; } catch { continue; }
                            if (f == null || f.IndexOf("<Prompt", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            texts += Fix(() => s.GetResultText(tb), v => s.SetPromptResultText(tb, v), map, used, errors, where + ", обозначение «" + s.Name + "»");
                        }
                }
                catch (Exception ex) { errors.Add(where + ": эскизные обозначения — " + ex.Message); }

                try
                {
                    foreach (DrawingSketch sk in sh.Sketches) texts += FixSketch(sk, map, used, errors, where);
                    foreach (DrawingView v in sh.DrawingViews)
                        foreach (DrawingSketch sk in v.Sketches) texts += FixSketch(sk, map, used, errors, where + ", вид " + v.Name);
                }
                catch (Exception ex) { errors.Add(where + ": эскизы — " + ex.Message); }
            }
            if (texts == 0 && errors.Count == 0) return null;
            used.Sort((a, b) => { int x, y; return int.TryParse(a, out x) && int.TryParse(b, out y) ? x.CompareTo(y) : string.CompareOrdinal(a, b); });
            List<string> pairs = used.ConvertAll(o => o + "→" + map[o]);
            string rep = "Ссылки «поз.» на листах: изменено текстов — " + texts + (pairs.Count > 0 ? " (" + string.Join(", ", pairs) + ")" : "") + ".";
            if (errors.Count > 0) rep += "\r\nНе изменены:\r\n" + string.Join("\r\n", errors);
            return rep;
        }

        static int FixSketch(DrawingSketch sk, IDictionary<string, string> map, List<string> used, List<string> errors, string where)
        {
            List<Inventor.TextBox> boxes = new List<Inventor.TextBox>();
            List<string> values = new List<string>();
            foreach (Inventor.TextBox tb in sk.TextBoxes)
            {
                string t = tb.FormattedText;
                string r = PosRefText.Replace(t, map, null);
                if (!ReferenceEquals(r, t)) { boxes.Add(tb); values.Add(r); }
            }
            if (boxes.Count == 0) return 0;
            int n = 0;
            bool edit = false;
            try
            {
                // тексты эскиза меняются только в режиме редактирования эскиза
                try { sk.Edit(); edit = true; } catch { }
                for (int i = 0; i < boxes.Count; i++)
                {
                    Inventor.TextBox tb = boxes[i];
                    n += Fix(() => tb.FormattedText, v => tb.FormattedText = v, map, used, errors, where + ", эскиз " + sk.Name);
                }
            }
            finally { if (edit) try { sk.ExitEdit(); } catch { } }
            return n;
        }

        /// <summary>Один текст: 1 — изменён, 0 — нет ссылок для замены или ошибка (в errors).</summary>
        static int Fix(Func<string> get, Action<string> set, IDictionary<string, string> map, List<string> used,
                       List<string> errors, string where)
        {
            string t = null;
            try
            {
                t = get();
                List<string> u = new List<string>();
                string r = PosRefText.Replace(t, map, u);
                if (ReferenceEquals(r, t)) return 0;
                set(r);
                foreach (string s in u) if (!used.Contains(s)) used.Add(s);
                return 1;
            }
            catch (Exception ex)
            {
                errors.Add(where + ": «" + Short(t) + "» — " + ex.Message);
                Log.Write("Ссылки поз.: " + where, ex);
                return 0;
            }
        }

        static string Short(string s)
        {
            if (s == null) return "";
            s = System.Text.RegularExpressions.Regex.Replace(s, "<[^<>]*>", " ").Trim();
            return s.Length > 60 ? s.Substring(0, 60) + "…" : s;
        }
    }
}
