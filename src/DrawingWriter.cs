using System;
using System.Collections.Generic;
using System.Security;
using Inventor;

namespace GroupSpecB
{
    /// <summary>Вывод спецификации на листы чертежа.
    /// Групповая по варианту Б: первый лист — форма 1б, последующие — форма 1а ГОСТ 2.113-75 (А4, альбомная ориентация).
    /// По ГОСТ 2.106: первый лист — форма 1, последующие — форма 1а (А4, книжная ориентация).</summary>
    public class DrawingWriter
    {
        public const string AttrSet = "GroupSpecB";
        public const string AttrData = "Data";
        public const string SheetAttrSet = "GroupSpecB_Sheet";
        public const string SheetPrefix = "ГС";
        public const string SheetPrefix2106 = "СП";

        // Рамка групповой: поле подшивки сверху 20 мм, остальные 5 мм (формы 1а/1б);
        // ГОСТ 2.106 (А4 книжный): поле подшивки слева 20 мм.
        readonly double FL = 5, FR = 292, FB = 5, FT = 190;
        // Границы граф таблицы, мм.
        static readonly double[] ColX = { 5, 11, 17, 25, 95, 158, 258, 292 };
        const double QtyX0 = 158, QtyW = 10;
        const double HeadTop = 190, HeadMid = 183, HeadBottom = 175, RowH = 8;
        // Форма 1 / 1а ГОСТ 2.106: графы 6 / 6 / 8 / 70 / 63 / 10 / 22 мм, головка 15 мм.
        static readonly double[] ColX2106 = { 20, 26, 32, 40, 110, 173, 183, 205 };
        const double Head2106Top = 292, Head2106Bottom = 277;
        const double Thick = 0.05, Thin = 0.025;   // толщина линий, см

        readonly Application app;
        readonly DrawingDocument dwg;
        readonly SpecData data;
        readonly TransientGeometry tg;
        DrawingSketch sk;

        /// <summary>Удалять пустые листы (нужно только для чертежа, созданного надстройкой).</summary>
        public bool RemoveEmptySheets;

        /// <summary>Предупреждения вёрстки после Write (например, запись длиннее листа).</summary>
        public readonly List<string> Warnings = new List<string>();

        public DrawingWriter(Application app, DrawingDocument dwg, SpecData data)
        {
            this.app = app; this.dwg = dwg; this.data = data;
            tg = app.TransientGeometry;
            gost2106 = data.Is2106;
            if (gost2106) { FL = 20; FR = 205; FB = 5; FT = 292; }
        }

        readonly bool gost2106;

        public static DrawingDocument CreateDrawing(Application app)
        {
            string tpl = "";
            try { tpl = app.FileManager.GetTemplateFile(DocumentTypeEnum.kDrawingDocumentObject); } catch { }
            return (DrawingDocument)app.Documents.Add(DocumentTypeEnum.kDrawingDocumentObject, tpl, true);
        }

        /// <summary>Обозначение формата листа по ГОСТ 2.301: А0…А4 и дополнительные (А4х3 и т. п.).</summary>
        public static string SheetFormat(Sheet sh)
        {
            double w = Math.Round(sh.Width * 10), h = Math.Round(sh.Height * 10);
            double s = Math.Min(w, h), l = Math.Max(w, h);
            string[] names = { "А0", "А1", "А2", "А3", "А4" };
            double[,] dims = { { 841, 1189 }, { 594, 841 }, { 420, 594 }, { 297, 420 }, { 210, 297 } };
            for (int i = 0; i < names.Length; i++)
                if (Math.Abs(s - dims[i, 0]) <= 2 && Math.Abs(l - dims[i, 1]) <= 2) return names[i];
            // дополнительные форматы: короткая сторона = длинная сторона основного, длинная = короткая × n
            for (int i = 0; i < names.Length; i++)
                for (int n = 2; n <= 9; n++)
                    if (Math.Abs(s - dims[i, 1]) <= 2 && Math.Abs(l - dims[i, 0] * n) <= 3) return names[i] + "х" + n;
            return "";
        }

        public static bool HasData(DrawingDocument d)
        {
            try { return d.AttributeSets.NameIsUsed[AttrSet]; } catch { return false; }
        }

        public static SpecData LoadData(DrawingDocument d)
        {
            if (!HasData(d)) return null;
            AttributeSet set = d.AttributeSets[AttrSet];
            if (!set.NameIsUsed[AttrData]) return null;
            return SpecData.FromXml((string)set[AttrData].Value);
        }

        public static void SaveData(DrawingDocument d, SpecData data)
        {
            AttributeSet set = d.AttributeSets.NameIsUsed[AttrSet] ? d.AttributeSets[AttrSet] : d.AttributeSets.Add(AttrSet);
            string xml = data.ToXml();
            if (set.NameIsUsed[AttrData]) set[AttrData].Value = xml;
            else set.Add(AttrData, ValueTypeEnum.kStringType, xml);
        }

        /// <summary>Перестраивает листы спецификации. Листы, созданные ранее надстройкой, заменяются;
        /// прочие листы чертежа (кроме пустого листа нового чертежа) не трогаются.</summary>
        public int Write(Action<string> progress)
        {
            Warnings.Clear();
            List<Sheet> old = new List<Sheet>();
            List<Sheet> emptyForeign = new List<Sheet>();
            foreach (Sheet sh in dwg.Sheets)
            {
                bool ours = false;
                try { ours = sh.AttributeSets.NameIsUsed[SheetAttrSet]; } catch { }
                if (ours) old.Add(sh);
                else if (RemoveEmptySheets && sh.DrawingViews.Count == 0 && sh.Sketches.Count == 0 && sh.SketchedSymbols.Count == 0)
                    emptyForeign.Add(sh);
            }
            EnsureStyles();
            // вёрстка строится, когда открыт эскиз первого листа: перенос текста считается по фактической
            // ширине надписей шрифтом листа (MeasureMm), а не по средней ширине символа
            List<SpecPage> pages = null;
            List<string> note = null;
            Sheet firstNew = null;
            for (int i = 0; pages == null || i < pages.Count; i++)
            {
                if (progress != null) progress("Лист " + (i + 1) + (pages != null ? " из " + pages.Count : ""));
                Sheet sh = dwg.Sheets.Add(DrawingSheetSizeEnum.kA4DrawingSheetSize,
                    gost2106 ? PageOrientationTypeEnum.kPortraitPageOrientation : PageOrientationTypeEnum.kLandscapePageOrientation,
                    (gost2106 ? SheetPrefix2106 : SheetPrefix) + "-" + (i + 1), Type.Missing, Type.Missing);
                sh.AttributeSets.Add(SheetAttrSet);
                // листы спецификации — отдельный документ со своей нумерацией: в счёт листов чертежа не входят
                try { sh.ExcludeFromCount = true; } catch { }
                if (firstNew == null) firstNew = sh;
                sh.Activate();
                // у нового листа шаблон может добавить рамку и основную надпись — они не нужны
                try { if (sh.TitleBlock != null) sh.TitleBlock.Delete(); } catch { }
                try { if (sh.Border != null) sh.Border.Delete(); } catch { }
                sk = sh.Sketches.Add();
                sk.Name = gost2106 ? "Спецификация" : "Групповая спецификация";
                sk.Edit();
                sk.DeferUpdates = true;
                try
                {
                    if (pages == null)
                    {
                        double h = data.Settings.FontHeight;
                        pages = Layout.Build(data, Warnings, text => MeasureMm(text, h));
                        note = Layout.DecadeNote(pages);
                    }
                    DrawPage(pages[i], pages.Count, i == 0 ? note : null);
                }
                finally
                {
                    sk.DeferUpdates = false;
                    sk.ExitEdit();
                }
                PlaceSignatures(sh);
            }
            foreach (Sheet sh in old) sh.Delete(false);
            foreach (Sheet sh in emptyForeign) { try { sh.Delete(false); } catch { } }
            if (firstNew != null) firstNew.Activate();
            SaveData(dwg, data);
            return pages.Count;
        }

        // ---------------------------------------------------------------- стили и примитивы

        readonly Dictionary<string, TextStyle> styleCache = new Dictionary<string, TextStyle>();

        void EnsureStyles()
        {
            styleCache.Clear();
        }

        /// <summary>Текстовый стиль для выравнивания и высоты шрифта. Размер задаётся стилем, а не
        /// разметкой StyleOverride: её числа Inventor читает в формате локали (запятая в русской).</summary>
        TextStyle StyleFor(char align, double hMm)
        {
            string name = "ГС_" + (align == 'L' ? "Лев" : align == 'R' ? "Прав" : "Центр") + "_" +
                          ((int)Math.Round(hMm * 10)).ToString();
            TextStyle st;
            if (styleCache.TryGetValue(name, out st)) return st;
            TextStylesEnumerator styles = dwg.StylesManager.TextStyles;
            st = null;
            foreach (TextStyle t in styles) if (t.Name == name) { st = t; break; }
            if (st == null) st = (TextStyle)styles[1].Copy(name);
            try { st.Font = data.Settings.FontName; } catch { }
            try { st.Italic = data.Settings.Italic; } catch { }
            try { st.Bold = false; } catch { }
            try { st.WidthScale = 1.0; } catch { }
            st.FontSize = hMm / 10.0;
            st.HorizontalJustification = align == 'L' ? HorizontalTextAlignmentEnum.kAlignTextLeft
                : align == 'R' ? HorizontalTextAlignmentEnum.kAlignTextRight : HorizontalTextAlignmentEnum.kAlignTextCenter;
            st.VerticalJustification = VerticalTextAlignmentEnum.kAlignTextMiddle;
            styleCache[name] = st;
            return st;
        }
        Point2d P(double xMm, double yMm) { return tg.CreatePoint2d(xMm / 10.0, yMm / 10.0); }

        void Line(double x1, double y1, double x2, double y2, bool thick)
        {
            SketchLine l = sk.SketchLines.AddByTwoPoints(P(x1, y1), P(x2, y2));
            try { l.LineWeight = thick ? Thick : Thin; } catch { }
        }

        void Rect(double x1, double y1, double x2, double y2, bool thick)
        {
            Line(x1, y1, x2, y1, thick); Line(x2, y1, x2, y2, thick);
            Line(x2, y2, x1, y2, thick); Line(x1, y2, x1, y1, thick);
        }

        static string Esc(string s)
        {
            return SecurityElement.Escape(s ?? "") ?? "";
        }

        /// <summary>Текст в ячейке (x1..x2, y1..y2), по вертикали — по середине. h — высота шрифта, мм.</summary>
        void Text(double x1, double y1, double x2, double y2, string text, char align, double h, bool underline = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            string ft = Esc(text);
            if (underline) ft = "<StyleOverride Underline='True'>" + ft + "</StyleOverride>";
            TextStyle st = StyleFor(align, h);
            double pad = 1.0;
            double ym = (y1 + y2) / 2;
            double x = align == 'L' ? x1 + pad : align == 'R' ? x2 - pad : (x1 + x2) / 2;
            Inventor.TextBox tb = sk.TextBoxes.AddFitted(P(x, ym), ft, st);
            try { tb.HorizontalJustification = st.HorizontalJustification; } catch { }
            try { tb.VerticalJustification = VerticalTextAlignmentEnum.kAlignTextMiddle; } catch { }
            try { tb.Origin = P(x, ym); } catch { }
            FitWidth(tb, (x2 - x1) - 2 * pad, P(x, ym));
        }

        /// <summary>Графа основной надписи — ссылка на свойство Inventor (значение обновляется вместе со свойством).</summary>
        void FieldText(double x1, double y1, double x2, double y2, TitleField f, char align, double h)
        {
            const double pad = 1.0;
            double avail = (x2 - x1) - 2 * pad;
            // ширину поля Inventor считает по подписи, а не по значению — меряем текущее значение свойства
            string value = SpecTitleBlock.Value((Document)dwg, f);
            double w = value != "" ? MeasureMm(value, h) : 0;
            if (w > 0 && avail / w < 0.6 && h > 3.5) { h = 3.5; w = MeasureMm(value, h); }   // длинное — мельче шрифт
            TextStyle st = StyleFor(align, h);
            double ym = (y1 + y2) / 2;
            double x = align == 'L' ? x1 + pad : align == 'R' ? x2 - pad : (x1 + x2) / 2;
            Inventor.TextBox tb = sk.TextBoxes.AddFitted(P(x, ym), f.Markup(), st);
            try { tb.HorizontalJustification = st.HorizontalJustification; } catch { }
            try { tb.VerticalJustification = VerticalTextAlignmentEnum.kAlignTextMiddle; } catch { }
            try { tb.Origin = P(x, ym); } catch { }
            if (w > avail)
            {
                try { tb.WidthScale = Math.Max(0.3, avail / w * 0.98); tb.Origin = P(x, ym); } catch { }
            }
        }

        /// <summary>Графа-ссылка на свойство в прямоугольнике ячейки: Inventor сам переносит длинное значение
        /// по ширине (так сделана графа наименования в штампе «Надпись чертеж 1»). Помещаются две строки;
        /// для более длинного значения текст сжимается, а при необходимости уменьшается шрифт.</summary>
        void WrappedFieldText(double x1, double y1, double x2, double y2, TitleField f, double h)
        {
            const double pad = 1.5;
            double avail = (x2 - x1) - 2 * pad;
            string value = SpecTitleBlock.Value((Document)dwg, f);
            // наименование переносится на сколько угодно строк; шрифт подбирается по фактической ширине слов,
            // чтобы строки поместились в графу высотой 25 мм: 5 мм — до 3 строк, 3,5 мм — до 4, иначе 2,5 мм
            // (сжатие по ширине тут не помогает: перенос Inventor считает по несжатому тексту)
            if (value != "" && WrapLines(value, h, 1.0, avail) > 3)
                h = WrapLines(value, Math.Min(h, 3.5), 1.0, avail) <= 4 ? Math.Min(h, 3.5) : 2.5;
            TextStyle st = StyleFor('C', h);
            Inventor.TextBox tb = sk.TextBoxes.AddByRectangle(P(x1 + pad, y1 + pad), P(x2 - pad, y2 - pad), f.Markup(), st);
            try { tb.HorizontalJustification = HorizontalTextAlignmentEnum.kAlignTextCenter; } catch { }
            try { tb.VerticalJustification = VerticalTextAlignmentEnum.kAlignTextMiddle; } catch { }
        }

        /// <summary>Сколько строк займёт текст при переносе по словам в ширину avail (мм) шрифтом h со сжатием scale.</summary>
        int WrapLines(string text, double h, double scale, double avail)
        {
            double space = Math.Max(0.5, MeasureMm("Ш Ш", h) - 2 * MeasureMm("Ш", h)) * scale;
            int lines = 1;
            double cur = 0;
            foreach (string word in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double w = MeasureMm(word, h) * scale;
                if (cur == 0) cur = w;
                else if (cur + space + w <= avail) cur += space + w;
                else { lines++; cur = w; }
            }
            return lines;
        }

        /// <summary>Ширина надписи на чертеже, мм.</summary>
        static double WidthMm(Inventor.TextBox tb)
        {
            try
            {
                Box2d b = tb.RangeBox;
                return (b.MaxPoint.X - b.MinPoint.X) * 10.0;
            }
            catch { return tb.FittedTextWidth * 10.0; }
        }

        /// <summary>Сжимает текст по ширине (коэффициент сжатия надписи), если он не помещается в графу.</summary>
        static void FitWidth(Inventor.TextBox tb, double availMm, Point2d origin)
        {
            if (availMm <= 0) return;
            double w = WidthMm(tb);
            if (w <= availMm) return;
            try
            {
                double scale = tb.WidthScale <= 0 ? 1.0 : tb.WidthScale;
                tb.WidthScale = Math.Max(0.3, scale * availMm / w * 0.98);
                tb.Origin = origin;
            }
            catch (Exception ex) { Log.Write("Сжатие надписи по ширине", ex); }
        }

        readonly Dictionary<string, double> widthCache = new Dictionary<string, double>();

        /// <summary>Ширина текста при выводе стилем таблицы, мм (временная надпись создаётся и удаляется).</summary>
        double MeasureMm(string text, double h)
        {
            string key = h + "|" + text;
            double w;
            if (widthCache.TryGetValue(key, out w)) return w;
            Inventor.TextBox tb = sk.TextBoxes.AddFitted(P(0, 0), Esc(text), StyleFor('L', h));
            w = WidthMm(tb);
            tb.Delete();
            widthCache[key] = w;
            return w;
        }

        /// <summary>Сокращённое обозначение исполнения «-01»: начинается там, где у полного обозначения
        /// начинается номер исполнения, — со смещением на ширину базовой части.</summary>
        void TextAbbreviated(double x1, double y1, double x2, double y2, string prefix, string text, double h)
        {
            const double pad = 1.0;
            double x = x1 + pad + MeasureMm(prefix, h);
            if (x > x2 - pad - 1) x = x2 - pad - 1;
            double ym = (y1 + y2) / 2;
            TextStyle st = StyleFor('L', h);
            Inventor.TextBox tb = sk.TextBoxes.AddFitted(P(x, ym), Esc(text), st);
            try { tb.HorizontalJustification = HorizontalTextAlignmentEnum.kAlignTextLeft; } catch { }
            try { tb.VerticalJustification = VerticalTextAlignmentEnum.kAlignTextMiddle; } catch { }
            try { tb.Origin = P(x, ym); } catch { }
            FitWidth(tb, x2 - pad - x, P(x, ym));
        }

        /// <summary>Повёрнутый на 90° текст (подписи узких граф «Формат», «Зона», «Поз.»).</summary>
        void TextVertical(double xc, double yc, string text, double h)
        {
            Inventor.TextBox tb = sk.TextBoxes.AddFitted(P(xc, yc), Esc(text), StyleFor('C', h));
            try { tb.HorizontalJustification = HorizontalTextAlignmentEnum.kAlignTextCenter; } catch { }
            try { tb.VerticalJustification = VerticalTextAlignmentEnum.kAlignTextMiddle; } catch { }
            try { tb.Rotation = Math.PI / 2; } catch { }
            try { tb.Origin = P(xc, yc); } catch { }
        }

        // ---------------------------------------------------------------- лист

        void DrawPage(SpecPage page, int totalSheets, List<string> decadeNote)
        {
            if (gost2106) { DrawPage2106(page, totalSheets); return; }
            SpecSettings s = data.Settings;
            double h = s.FontHeight;
            int cap = Layout.Capacity(page, s);
            double yb = HeadBottom - RowH * cap;

            Rect(FL, FB, FR, FT, true);
            DrawExtraGraphs(page.First);

            // --- головка таблицы
            Line(FL, HeadBottom, FR, HeadBottom, true);
            Line(QtyX0, HeadMid, QtyX0 + 10 * QtyW, HeadMid, true);
            for (int c = 1; c < ColX.Length - 1; c++) Line(ColX[c], HeadTop, ColX[c], yb, true);
            for (int k = 1; k < 10; k++) Line(QtyX0 + k * QtyW, HeadMid, QtyX0 + k * QtyW, yb, true);
            TextVertical((ColX[0] + ColX[1]) / 2, (HeadTop + HeadBottom) / 2, "Формат", 2.5);
            TextVertical((ColX[1] + ColX[2]) / 2, (HeadTop + HeadBottom) / 2, "Зона", 2.5);
            TextVertical((ColX[2] + ColX[3]) / 2, (HeadTop + HeadBottom) / 2, "Поз.", 2.5);
            Text(ColX[3], HeadBottom, ColX[4], HeadTop, "Обозначение", 'C', h);
            Text(ColX[4], HeadBottom, ColX[5], HeadTop, "Наименование", 'C', h);
            Text(ColX[6], HeadBottom, ColX[7], HeadTop, "Примечание", 'C', h);
            string baseDes = SpecLogic.BaseDesignation(data);
            Text(QtyX0, HeadMid, QtyX0 + 10 * QtyW, HeadTop, "Кол. на исполн. " + baseDes + (baseDes != "" ? "-" : ""), 'L', h);
            for (int k = 0; k < page.Execs.Count && k < 10; k++)
                Text(QtyX0 + k * QtyW, HeadBottom, QtyX0 + (k + 1) * QtyW, HeadMid, page.Execs[k].Label, 'C', h);

            // --- строки
            for (int r = 1; r <= cap; r++)
                Line(FL, HeadBottom - r * RowH, FR, HeadBottom - r * RowH, r == cap);
            for (int i = 0; i < page.Lines.Count && i < cap; i++)
            {
                SpecLine l = page.Lines[i];
                double y2 = HeadBottom - i * RowH, y1 = y2 - RowH;
                if (l.Kind == LineKind.Blank) continue;
                if (l.Kind == LineKind.Header)
                {
                    Text(ColX[4], y1, ColX[5], y2, l.Name, 'C', h, true);
                    continue;
                }
                Text(ColX[0], y1, ColX[1], y2, l.Format, 'C', Math.Min(h, 2.5));
                Text(ColX[1], y1, ColX[2], y2, l.Zone, 'C', Math.Min(h, 2.5));
                Text(ColX[2], y1, ColX[3], y2, l.Pos, 'C', h);
                if (!string.IsNullOrEmpty(l.AbbrevPrefix)) TextAbbreviated(ColX[3], y1, ColX[4], y2, l.AbbrevPrefix, l.Designation, h);
                else Text(ColX[3], y1, ColX[4], y2, l.Designation, 'L', h);
                Text(ColX[4], y1, ColX[5], y2, l.Name, 'L', h);
                for (int k = 0; k < 10; k++)
                    Text(QtyX0 + k * QtyW, y1, QtyX0 + (k + 1) * QtyW, y2, l.Qty[k], 'C', h);
                Text(ColX[6], y1, ColX[7], y2, l.Note, 'L', h);
            }

            // --- основная надпись
            if (page.First) TitleBlockForm2(page, totalSheets);
            else TitleBlockForm2a(page);

            // --- запись о десятках исполнений левее основной надписи (п. 5.5.2 е)
            if (decadeNote != null && decadeNote.Count > 0)
            {
                double y = 40;
                foreach (string t in decadeNote)
                {
                    Text(FL + 2, y - 3, 105, y + 3, t, 'L', 3.5);
                    y -= 7;
                }
            }
        }

        /// <summary>Лист спецификации по ГОСТ 2.106: форма 1 (первый лист, основная надпись форма 2) или
        /// форма 1а (последующие, форма 2а). Таблица доходит до основной надписи: остаток меньше строки
        /// входит в последнюю строку.</summary>
        void DrawPage2106(SpecPage page, int totalSheets)
        {
            double h = data.Settings.FontHeight;
            double[] cx = ColX2106;
            int cap = Layout.Capacity(page, data.Settings);
            double titleTop = FB + (page.First ? 40 : 15);
            double yb = Head2106Bottom - RowH * cap;
            if (yb < titleTop + RowH) yb = titleTop;

            Rect(FL, FB, FR, FT, true);
            DrawExtraGraphs2106(page.First);

            // --- головка таблицы
            Line(FL, Head2106Bottom, FR, Head2106Bottom, true);
            Line(FL, yb, FR, yb, true);
            for (int c = 1; c < cx.Length - 1; c++) Line(cx[c], Head2106Top, cx[c], yb, true);
            double hy = (Head2106Top + Head2106Bottom) / 2;
            TextVertical((cx[0] + cx[1]) / 2, hy, "Формат", 2.5);
            TextVertical((cx[1] + cx[2]) / 2, hy, "Зона", 2.5);
            TextVertical((cx[2] + cx[3]) / 2, hy, "Поз.", 2.5);
            Text(cx[3], Head2106Bottom, cx[4], Head2106Top, "Обозначение", 'C', h);
            Text(cx[4], Head2106Bottom, cx[5], Head2106Top, "Наименование", 'C', h);
            TextVertical((cx[5] + cx[6]) / 2, hy, "Кол.", h);
            Text(cx[6], hy, cx[7], Head2106Top, "Приме-", 'C', h);   // в графу 22 мм — в две строки
            Text(cx[6], Head2106Bottom, cx[7], hy, "чание", 'C', h);

            // --- строки
            for (int r = 1; r < cap; r++)
            {
                double y = Head2106Bottom - r * RowH;
                if (y > yb + 0.01) Line(FL, y, FR, y, false);
            }
            for (int i = 0; i < page.Lines.Count && i < cap; i++)
            {
                SpecLine l = page.Lines[i];
                double y2 = Head2106Bottom - i * RowH, y1 = Math.Max(y2 - RowH, yb);
                if (l.Kind == LineKind.Blank) continue;
                if (l.Kind == LineKind.Header)
                {
                    Text(cx[4], y1, cx[5], y2, l.Name, 'C', h, true);
                    continue;
                }
                Text(cx[0], y1, cx[1], y2, l.Format, 'C', Math.Min(h, 2.5));
                Text(cx[1], y1, cx[2], y2, l.Zone, 'C', Math.Min(h, 2.5));
                Text(cx[2], y1, cx[3], y2, l.Pos, 'C', h);
                Text(cx[3], y1, cx[4], y2, l.Designation, 'L', h);
                Text(cx[4], y1, cx[5], y2, l.Name, 'L', h);
                Text(cx[5], y1, cx[6], y2, l.Qty[0], 'C', h);
                Text(cx[6], y1, cx[7], y2, l.Note, 'L', h);
            }

            // --- основная надпись
            if (page.First) TitleBlockForm2(page, totalSheets);
            else TitleBlockForm2a(page);
        }

        /// <summary>Дополнительные графы по ГОСТ 2.104 в поле подшивки слева (А4 книжный): снизу — «Инв. № подл.»,
        /// «Подп. и дата», «Взам. инв. №», «Инв. № дубл.», «Подп. и дата»; на первом листе сверху — «Перв. примен.»
        /// и «Справ. №». Графы 5 мм (подпись) + 7 мм (значение).</summary>
        void DrawExtraGraphs2106(bool first)
        {
            double x0 = FL - 12, xm = FL - 7;
            double[] hs = { 25, 35, 25, 25, 35 };
            string[] labels = { "Инв. № подл.", "Подп. и дата", "Взам. инв. №", "Инв. № дубл.", "Подп. и дата" };
            double y = FB;
            Rect(x0, FB, FL, FB + 145, true);
            Line(xm, FB, xm, FB + 145, true);
            for (int i = 0; i < hs.Length; i++)
            {
                if (i > 0) Line(x0, y, FL, y, true);
                TextVertical((x0 + xm) / 2, y + hs[i] / 2, labels[i], 2.5);
                y += hs[i];
            }
            if (!string.IsNullOrEmpty(data.InvNo)) TextVertical((xm + FL) / 2, FB + 12.5, data.InvNo, 3.5);

            if (first)
            {
                double yt = FT, ymid = FT - 60, ybot = FT - 120;
                Rect(x0, ybot, FL, yt, true);
                Line(x0, ymid, FL, ymid, true);
                Line(xm, ybot, xm, yt, true);
                TextVertical((x0 + xm) / 2, (ymid + yt) / 2, "Перв. примен.", 2.5);
                TextVertical((x0 + xm) / 2, (ybot + ymid) / 2, "Справ. №", 2.5);
                if (!string.IsNullOrEmpty(data.FirstUse)) TextVertical((xm + FL) / 2, (ymid + yt) / 2, data.FirstUse, 3.5);
                if (!string.IsNullOrEmpty(data.RefNo)) TextVertical((xm + FL) / 2, (ybot + ymid) / 2, data.RefNo, 3.5);
            }
        }

        /// <summary>Дополнительные графы по ГОСТ 2.104 в поле подшивки (над рамкой).</summary>
        void DrawExtraGraphs(bool first)
        {
            double y0 = FT, ym = FT + 7, y1 = FT + 12;
            double[] w = { 25, 35, 25, 25, 35 };
            string[] labels = { "Инв. № подл.", "Подп. и дата", "Взам. инв. №", "Инв. № дубл.", "Подп. и дата" };
            double x = FL;
            Rect(FL, y0, FL + 145, y1, true);
            Line(FL, ym, FL + 145, ym, true);
            for (int i = 0; i < w.Length; i++)
            {
                if (i > 0) Line(x, y0, x, y1, true);
                Text(x, ym, x + w[i], y1, labels[i], 'C', 2.5);
                x += w[i];
            }
            Text(FL, y0, FL + 25, ym, data.InvNo, 'C', 3.5);

            if (first)
            {
                double xa = FR - 120, xm = FR - 60;
                Rect(xa, y0, FR, y1, true);
                Line(xa, ym, FR, ym, true);
                Line(xm, y0, xm, y1, true);
                Text(xa, ym, xm, y1, "Справ. №", 'C', 2.5);
                Text(xm, ym, FR, y1, "Перв. примен.", 'C', 2.5);
                Text(xa, y0, xm, ym, data.RefNo, 'C', 3.5);
                Text(xm, y0, FR, ym, data.FirstUse, 'C', 3.5);
            }
        }

        string SpecDesignation()
        {
            string d = data.Settings.SpecDesignation;
            return string.IsNullOrEmpty(d) ? SpecLogic.BaseDesignation(data) : d;
        }

        readonly List<KeyValuePair<SketchedSymbolDefinition, Point2d>> pendingSignatures = new List<KeyValuePair<SketchedSymbolDefinition, Point2d>>();

        /// <summary>Эскизное обозначение-подпись для фамилии: имя обозначения совпадает с фамилией
        /// (или с её первым словом, если в свойстве записаны ещё и инициалы).</summary>
        SketchedSymbolDefinition FindSignature(string surname)
        {
            string full = surname.Trim();
            string first = full.Split(new[] { ' ', '.', ',' }, StringSplitOptions.RemoveEmptyEntries)[0];
            foreach (SketchedSymbolDefinition d in dwg.SketchedSymbolDefinitions)
            {
                string n = (d.Name ?? "").Trim();
                if (string.Equals(n, full, StringComparison.OrdinalIgnoreCase) || string.Equals(n, first, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }

        /// <summary>Подписи вставляются поверх эскиза листа (эскизные обозначения добавляются только на активный лист).</summary>
        void PlaceSignatures(Sheet sh)
        {
            foreach (KeyValuePair<SketchedSymbolDefinition, Point2d> s in pendingSignatures)
            {
                try { sh.SketchedSymbols.Add(s.Key, s.Value, 0, 1, Type.Missing); } catch { }
            }
            pendingSignatures.Clear();
        }

        /// <summary>Основная надпись по ГОСТ 2.104, форма 2 (185×40) — первый лист.</summary>
        void TitleBlockForm2(SpecPage page, int totalSheets)
        {
            SpecSettings s = data.Settings;
            double x0 = FR - 185, y0 = FB;
            double[] cx = { x0, x0 + 7, x0 + 17, x0 + 40, x0 + 55, x0 + 65 };
            Rect(x0, y0, FR, y0 + 40, true);
            // левая часть: 2 строки изменений, строка заголовков, 5 строк подписей
            for (int r = 1; r < 8; r++)
            {
                double y = y0 + 5 * r;
                Line(x0, y, cx[5], y, r == 5 || r == 6);
            }
            for (int c = 1; c < 5; c++)
            {
                double yFrom = (c == 1) ? y0 + 25 : y0;   // граница «Изм.|Лист» только в зоне изменений
                Line(cx[c], yFrom, cx[c], y0 + 40, true);
            }
            Line(cx[5], y0, cx[5], y0 + 40, true);
            string[] head = { "Изм.", "Лист", "№ докум.", "Подп.", "Дата" };
            for (int c = 0; c < 5; c++) Text(cx[c], y0 + 25, cx[c + 1], y0 + 30, head[c], 'C', 2.5);
            string[] roles = { "Разраб.", "Пров.", "Т.контр.", "Н.контр.", "Утв." };
            TitleField[] names = { SpecTitleBlock.Designer, SpecTitleBlock.Checker, SpecTitleBlock.TechControl, SpecTitleBlock.NormControl, SpecTitleBlock.Approver };
            for (int i = 0; i < 5; i++)
            {
                double yt = y0 + 25 - 5 * i, yb2 = yt - 5;
                Text(cx[0], yb2, cx[2], yt, roles[i], 'L', 2.5);
                FieldText(cx[2], yb2, cx[3], yt, names[i], 'L', 2.5);
                string surname = SpecTitleBlock.Value((Document)dwg, names[i]);
                if (surname != "")
                {
                    // дата — ссылка на свойство чертежа «Дата создания» (Inventor выводит её как дд.мм.гг)
                    FieldText(cx[4], yb2, cx[5], yt, SpecTitleBlock.CreationDate, 'C', 2.5);
                    // подпись — эскизное обозначение с именем-фамилией; точка вставки — левый нижний угол графы «Подп.»
                    SketchedSymbolDefinition sig = FindSignature(surname);
                    if (sig != null) pendingSignatures.Add(new KeyValuePair<SketchedSymbolDefinition, Point2d>(sig, P(cx[3], yb2)));
                }
            }

            // правая часть
            double xr = cx[5];
            Line(xr, y0 + 25, FR, y0 + 25, true);
            FieldText(xr, y0 + 25, FR, y0 + 40, SpecTitleBlock.Designation, 'C', 5);
            double xs = xr + 70;
            Line(xs, y0, xs, y0 + 25, true);
            WrappedFieldText(xr, y0, xs, y0 + 25, SpecTitleBlock.Name, 5);   // как в «Надпись чертеж 1»: перенос по ширине графы
            // Лит. | Лист | Листов
            Line(xs, y0 + 20, FR, y0 + 20, true);
            Line(xs, y0 + 15, FR, y0 + 15, true);
            Line(xs + 15, y0 + 15, xs + 15, y0 + 25, true);
            Line(xs + 30, y0 + 15, xs + 30, y0 + 25, true);
            Line(xs + 5, y0 + 15, xs + 5, y0 + 20, false);
            Line(xs + 10, y0 + 15, xs + 10, y0 + 20, false);
            Text(xs, y0 + 20, xs + 15, y0 + 25, "Лит.", 'C', 2.5);
            Text(xs + 15, y0 + 20, xs + 30, y0 + 25, "Лист", 'C', 2.5);
            Text(xs + 30, y0 + 20, FR, y0 + 25, "Листов", 'C', 2.5);
            FieldText(xs, y0 + 15, xs + 5, y0 + 20, SpecTitleBlock.Litera, 'C', 2.5);   // первая графа «Лит.»
            if (totalSheets > 1) Text(xs + 15, y0 + 15, xs + 30, y0 + 20, page.Number.ToString(), 'C', 2.5);
            Text(xs + 30, y0 + 15, FR, y0 + 20, totalSheets.ToString(), 'C', 2.5);
            FieldText(xs, y0, FR, y0 + 15, SpecTitleBlock.Company, 'C', 3.5);
        }

        /// <summary>Основная надпись по ГОСТ 2.104, форма 2а (185×15) — последующие листы.</summary>
        void TitleBlockForm2a(SpecPage page)
        {
            double x0 = FR - 185, y0 = FB;
            double[] cx = { x0, x0 + 7, x0 + 17, x0 + 40, x0 + 55, x0 + 65 };
            Rect(x0, y0, FR, y0 + 15, true);
            Line(x0, y0 + 5, cx[5], y0 + 5, true);
            Line(x0, y0 + 10, cx[5], y0 + 10, false);
            for (int c = 1; c <= 5; c++) Line(cx[c], y0, cx[c], y0 + 15, true);
            string[] head = { "Изм.", "Лист", "№ докум.", "Подп.", "Дата" };
            for (int c = 0; c < 5; c++) Text(cx[c], y0, cx[c + 1], y0 + 5, head[c], 'C', 2.5);
            double xl = FR - 10;
            Line(xl, y0, xl, y0 + 15, true);
            Line(xl, y0 + 8, FR, y0 + 8, true);
            FieldText(cx[5], y0, xl, y0 + 15, SpecTitleBlock.Designation, 'C', 5);
            Text(xl, y0 + 8, FR, y0 + 15, "Лист", 'C', 2.5);
            Text(xl, y0, FR, y0 + 8, page.Number.ToString(), 'C', 3.5);
        }
    }
}
