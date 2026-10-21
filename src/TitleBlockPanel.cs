using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace GroupSpecB
{
    /// <summary>Основная надпись по ГОСТ 2.104 (форма 2, 185×40 мм) в окне. Поля показывают значения
    /// свойств (из 3D-модели, чертежа или по умолчанию); исправленные поля по кнопке «Сформировать»
    /// записываются в 3D-модель и в чертёж.</summary>
    public class TitleBlockPanel : Panel
    {
        public static readonly Color ModelColor = Color.White;
        public static readonly Color DrawingColor = Color.FromArgb(225, 238, 255);
        public static readonly Color DefaultColor = Color.FromArgb(255, 248, 210);
        public static readonly Color EditedColor = Color.FromArgb(214, 245, 214);

        const float K = 5f;        // пикселей на мм
        const float W = 185, H = 40;
        readonly TitleState state;
        readonly ToolTip tip = new ToolTip();

        public TitleBlockPanel(TitleState state)
        {
            this.state = state;
            DoubleBuffered = true;
            BackColor = Color.White;
            Size = new Size((int)(W * K) + 2, (int)(H * K) + 2);

            string[] head = { "Изм.", "Лист", "№ докум.", "Подп.", "Дата" };
            float[] cx = { 0, 7, 17, 40, 55, 65 };
            for (int c = 0; c < 5; c++) AddLabel(head[c], cx[c], 25, cx[c + 1], 30, ContentAlignment.MiddleCenter);
            string[] roles = { "Разраб.", "Пров.", "Т.контр.", "Н.контр.", "Утв." };
            TitleField[] people = { SpecTitleBlock.Designer, SpecTitleBlock.Checker, SpecTitleBlock.TechControl, SpecTitleBlock.NormControl, SpecTitleBlock.Approver };
            for (int i = 0; i < 5; i++)
            {
                float yt = 25 - 5 * i;
                AddLabel(roles[i], 0, yt - 5, 17, yt, ContentAlignment.MiddleLeft);
                AddBox(people[i], 17, yt - 5, 40, yt, 8f, false, HorizontalAlignment.Left);
                AddDate(people[i], 55, yt - 5, 65, yt);
            }
            AddBox(SpecTitleBlock.Designation, 65, 25, 185, 40, 14f, false, HorizontalAlignment.Center);
            AddBox(SpecTitleBlock.Name, 65, 0, 135, 25, 12f, true, HorizontalAlignment.Center);
            AddLabel("Лит.", 135, 20, 150, 25, ContentAlignment.MiddleCenter);
            AddLabel("Лист", 150, 20, 165, 25, ContentAlignment.MiddleCenter);
            AddLabel("Листов", 165, 20, 185, 25, ContentAlignment.MiddleCenter);
            AddBox(SpecTitleBlock.Litera, 135, 15, 140, 20, 8f, false, HorizontalAlignment.Center);
            AddLabel("—", 150, 15, 165, 20, ContentAlignment.MiddleCenter);
            AddLabel("—", 165, 15, 185, 20, ContentAlignment.MiddleCenter);
            AddBox(SpecTitleBlock.Company, 135, 0, 185, 15, 11f, true, HorizontalAlignment.Center);
        }

        static Rectangle R(float x1, float y1, float x2, float y2)
        {
            return Rectangle.FromLTRB((int)(x1 * K) + 2, (int)((H - y2) * K) + 2, (int)(x2 * K) - 1, (int)((H - y1) * K) - 1);
        }

        void AddLabel(string text, float x1, float y1, float x2, float y2, ContentAlignment align)
        {
            Controls.Add(new Label { Text = text, TextAlign = align, BackColor = Color.Transparent, Bounds = R(x1, y1, x2, y2),
                                     Font = new Font("Segoe UI", 7.5f, FontStyle.Italic) });
        }

        Color ColorFor(TitleField f)
        {
            if (state.Edited.Contains(f.Key)) return EditedColor;
            string s = state.Source(f.Key);
            return s == TitleState.FromModel ? ModelColor : s == TitleState.FromDrawing ? DrawingColor : DefaultColor;
        }

        string TipFor(TitleField f)
        {
            string s = state.Edited.Contains(f.Key) ? "исправлено — по кнопке «Сформировать» будет записано в 3D-модель и в чертёж"
                     : state.Source(f.Key) == TitleState.FromModel ? "значение из 3D-модели"
                     : state.Source(f.Key) == TitleState.FromDrawing ? "в 3D-модели пусто — значение из чертежа"
                     : "в модели и чертеже пусто — значение по умолчанию (вкладка «Параметры»)";
            return f.Label + " — свойство «" + f.PropName + "»: " + s;
        }

        void AddBox(TitleField f, float x1, float y1, float x2, float y2, float fontSize, bool multiline, HorizontalAlignment align)
        {
            Rectangle r = R(x1, y1, x2, y2);
            TextBox tb = new TextBox { BorderStyle = BorderStyle.None, Multiline = multiline, TextAlign = align,
                                       Font = new Font("Segoe UI", fontSize, FontStyle.Italic) };
            tb.Text = state.Get(f.Key);
            tb.BackColor = ColorFor(f);
            tb.Bounds = multiline ? r : new Rectangle(r.X, r.Y + Math.Max(0, (r.Height - tb.PreferredHeight) / 2), r.Width, Math.Min(r.Height, tb.PreferredHeight));
            tip.SetToolTip(tb, TipFor(f));
            tb.TextChanged += (s, e) =>
            {
                state.SetByUser(f.Key, tb.Text);
                tb.BackColor = ColorFor(f);
                tip.SetToolTip(tb, TipFor(f));
                RefreshDates();
            };
            Controls.Add(tb);
        }

        readonly Dictionary<TitleField, Label> dateCells = new Dictionary<TitleField, Label>();
        readonly ToolStripDropDown calendarDrop = new ToolStripDropDown { Padding = Padding.Empty };
        MonthCalendar calendar;

        /// <summary>Графа «Дата» строки подписи: «Дата создания» чертежа, показывается только при заполненной
        /// фамилии. Правая кнопка мыши — календарь; выбранная дата по кнопке «Сформировать» записывается
        /// в «Дата создания» чертежа (одна дата на все строки).</summary>
        void AddDate(TitleField person, float x1, float y1, float x2, float y2)
        {
            Label l = new Label { TextAlign = ContentAlignment.MiddleCenter, Bounds = R(x1, y1, x2, y2), Cursor = Cursors.Hand,
                                  Font = new Font("Segoe UI", 7.5f, FontStyle.Italic) };
            tip.SetToolTip(l, "Дата — свойство чертежа «Дата создания». Правая кнопка мыши — выбрать дату в календаре.");
            l.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowCalendar(l); };
            dateCells[person] = l;
            Controls.Add(l);
            RefreshDates();
        }

        void RefreshDates()
        {
            foreach (KeyValuePair<TitleField, Label> kv in dateCells)
            {
                bool has = state.Get(kv.Key.Key).Trim() != "";
                kv.Value.Text = has ? state.Get(SpecTitleBlock.CreationDate.Key) : "";
                kv.Value.BackColor = has && state.Edited.Contains(SpecTitleBlock.CreationDate.Key) ? EditedColor : Color.Transparent;
            }
        }

        void ShowCalendar(Control near)
        {
            if (calendar == null)
            {
                calendar = new MonthCalendar { MaxSelectionCount = 1, ShowTodayCircle = true };
                calendar.DateSelected += (s, e) =>
                {
                    state.SetByUser(SpecTitleBlock.CreationDate.Key, e.Start.ToString(SpecTitleBlock.DateFormat));
                    RefreshDates();
                    calendarDrop.Close();
                };
                calendarDrop.Items.Add(new ToolStripControlHost(calendar) { Margin = Padding.Empty, Padding = Padding.Empty });
            }
            calendar.SetDate(DateTime.Today);   // по умолчанию — сегодня
            calendarDrop.Show(near, new Point(0, near.Height));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            using (Pen thick = new Pen(Color.Black, 2f))
            using (Pen thin = new Pen(Color.Black, 1f))
            {
                Action<float, float, float, float, bool> line = (x1, y1, x2, y2, t) =>
                    g.DrawLine(t ? thick : thin, x1 * K + 1, (H - y1) * K + 1, x2 * K + 1, (H - y2) * K + 1);
                g.DrawRectangle(thick, 1, 1, W * K, H * K);
                float[] cx = { 0, 7, 17, 40, 55, 65 };
                for (int r = 1; r < 8; r++) line(0, 5 * r, 65, 5 * r, r == 5 || r == 6);
                for (int c = 1; c < 5; c++) line(cx[c], c == 1 ? 25 : 0, cx[c], 40, true);
                line(65, 0, 65, 40, true);
                line(65, 25, 185, 25, true);
                line(135, 0, 135, 25, true);
                line(135, 20, 185, 20, true);
                line(135, 15, 185, 15, true);
                line(150, 15, 150, 25, true);
                line(165, 15, 165, 25, true);
                line(140, 15, 140, 20, false);
                line(145, 15, 145, 20, false);
            }
        }
    }
}
