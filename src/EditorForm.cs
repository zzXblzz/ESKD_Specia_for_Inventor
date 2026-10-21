using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace GroupSpecB
{
    /// <summary>DataGridView, в которой Shift+Enter переносит строку внутри ячейки.</summary>
    class SpecGrid : DataGridView
    {
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Enter | Keys.Shift) && IsCurrentCellInEditMode)
            {
                TextBox tb = EditingControl as TextBox;
                if (tb != null)
                {
                    int p = tb.SelectionStart;
                    tb.Text = tb.Text.Remove(p, tb.SelectionLength).Insert(p, Environment.NewLine);
                    tb.SelectionStart = p + Environment.NewLine.Length;
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    /// <summary>Окно редактора групповой спецификации.</summary>
    public class EditorForm : Form
    {
        readonly SpecData data;
        readonly Func<Action<string>, string> refresh;
        readonly TitleState titleState;        // обновить из BOM → сообщение об ошибке или null
        SpecGrid grid;
        DataGridView execGrid;
        PropertyGrid props;
        ToolStripStatusLabel status;
        CheckBox showHidden;
        bool loading;

        static readonly string[] FieldCols = { "Section", "Format", "Zone", "Pos", "Designation", "Name" };

        public EditorForm(SpecData data, Func<Action<string>, string> refresh, TitleState titleState)
        {
            this.data = data;
            this.refresh = refresh;
            this.titleState = titleState ?? new TitleState();
            Text = "Групповая спецификация (ГОСТ 2.113-75, вариант Б)";
            Width = 1350; Height = 780;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            BuildUi();
            LoadAll();
        }

        // ------------------------------------------------------------------ интерфейс

        void BuildUi()
        {
            ToolStrip ts = new ToolStrip();
            ts.GripStyle = ToolStripGripStyle.Hidden;
            ts.Items.Add(Btn("Обновить из BOM", "Перечитать исполнения и BOM параметрической сборки (ручные правки сохраняются)", OnRefresh));
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(Btn("Добавить исполнение (3D-модель)…", "Добавить в групповую спецификацию ещё одно исполнение — сборку (.iam); её BOM станет отдельной графой «Кол. на исполн.»", OnAddExecution));
            ts.Items.Add(Btn("Добавить строку", "Пустая строка (документ, материал и т. п.) в разделе выделенной строки", OnAddRow));
            ts.Items.Add(Btn("Удалить", "Удалить строки, добавленные вручную (строки BOM — снять флажок «В СП»)", OnDelete));
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(Btn("▲", "Переместить выше (в пределах раздела)", (s, e) => MoveRows(-1)));
            ts.Items.Add(Btn("▼", "Переместить ниже (в пределах раздела)", (s, e) => MoveRows(1)));
            ts.Items.Add(Btn("Кол. во всех исп.…", "Записать одно количество во все исполнения для выделенных строк", OnFillQty));
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(Btn("Сортировать по ГОСТ", "Порядок записи по ГОСТ 2.106 / 2.113 и нумерация позиций", OnSort));
            ts.Items.Add(Btn("Перенумеровать", "Нумерация позиций в текущем порядке строк", OnRenumber));

            ToolStripButton ok = Btn("Сформировать спецификацию", "Вывести листы на чертёж", (s, e) => { Commit(); DialogResult = DialogResult.OK; Close(); });
            ok.Font = new Font(ok.Font, FontStyle.Bold);
            ok.Alignment = ToolStripItemAlignment.Right;
            ts.Items.Add(ok);

            TabControl tabs = new TabControl { Dock = DockStyle.Fill };
            TabPage tpSpec = new TabPage("Спецификация");
            TabPage tpExec = new TabPage("Исполнения");
            TabPage tpTitle = new TabPage("Основная надпись");
            TabPage tpSet = new TabPage("Параметры");
            tabs.TabPages.Add(tpSpec); tabs.TabPages.Add(tpExec); tabs.TabPages.Add(tpTitle); tabs.TabPages.Add(tpSet);
            BuildTitleTab(tpTitle);

            grid = new SpecGrid { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect, RowHeadersWidth = 28,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells };
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.CellValueChanged += Grid_CellValueChanged;
            grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.DataError += (s, e) => { e.ThrowException = false; };
            grid.EditingControlShowing += (s, e) =>
            {
                TextBox tb = e.Control as TextBox;
                if (tb != null) { tb.Multiline = true; tb.AcceptsReturn = false; }
            };
            showHidden = new CheckBox { Text = "Показывать строки, исключённые из спецификации", Dock = DockStyle.Bottom, Checked = true, Height = 24 };
            showHidden.CheckedChanged += (s, e) => FillGrid();
            Label hint = new Label { Dock = DockStyle.Bottom, Height = 34, ForeColor = Color.DimGray,
                Text = "Белые строки — из BOM исполнений, жёлтые — добавленные вручную. Жирным — исправлено вручную (не перезаписывается при обновлении из BOM).\r\n" +
                       "«Документация» — X в графах исполнений. Знак $ в тексте — конец строки (не влезающая строка сжимается). Изменения в 3D-модели (spec_section, обозначение, наименование, позиции BOM) вносятся только по кнопке «Сформировать»." };
            tpSpec.Controls.Add(grid);
            tpSpec.Controls.Add(showHidden);
            tpSpec.Controls.Add(hint);

            execGrid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false };
            execGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "On", HeaderText = "В СП", Width = 50 });
            execGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Key", HeaderText = "Исполнение (строка iAssembly / состояние модели / файл)", ReadOnly = true, Width = 430 });
            execGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Src", HeaderText = "Источник", ReadOnly = true, Width = 120 });
            execGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Des", HeaderText = "Обозначение", ReadOnly = true, Width = 260 });
            execGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Label", HeaderText = "Подзаголовок графы «Кол. на исполн.»", Width = 230 });
            execGrid.CellValueChanged += ExecGrid_CellValueChanged;
            execGrid.CurrentCellDirtyStateChanged += (s, e) => { if (execGrid.IsCurrentCellDirty) execGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            ToolStrip tsEx = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            tsEx.Items.Add(Btn("Добавить исполнение (3D-модель)…", "Добавить сборку исполнения (.iam)", OnAddExecution));
            tsEx.Items.Add(Btn("Удалить добавленное исполнение", "Убрать исполнение, добавленное вручную", OnRemoveExecution));
            tpExec.Controls.Add(execGrid);
            tpExec.Controls.Add(tsEx);
            Panel baseBar = new Panel { Dock = DockStyle.Bottom, Height = 34 };
            Label baseLbl = new Label { Text = "Базовое обозначение исполнений (пусто — автоматически):", AutoSize = true, Left = 8, Top = 9 };
            TextBox baseTb = new TextBox { Left = 360, Top = 5, Width = 260, Text = data.BaseDesignation ?? "" };
            baseTb.Leave += (s, e) =>
            {
                if ((data.BaseDesignation ?? "") == baseTb.Text.Trim()) return;
                data.BaseDesignation = baseTb.Text.Trim();
                string b = SpecLogic.BaseDesignation(data);
                foreach (Execution ex in data.Executions) ex.Label = SpecLogic.LabelFor(ex.Designation, b);
                SpecLogic.EnsureAssemblyDrawingRow(data);
                FillExecGrid();
                FillGrid();
            };
            baseBar.Controls.Add(baseLbl);
            baseBar.Controls.Add(baseTb);
            tpExec.Controls.Add(baseBar);

            props = new PropertyGrid { Dock = DockStyle.Fill, PropertySort = PropertySort.Categorized, ToolbarVisible = false };
            props.PropertyValueChanged += (s, e) =>
            {
                GlobalSettings.Save(data.Settings);   // параметры общие для всех чертежей
                SpecLogic.EnsureAssemblyDrawingRow(data);
                SpecLogic.Renumber(data);
                FillGrid();
            };
            tpSet.Controls.Add(props);

            StatusStrip ss = new StatusStrip();
            status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            ss.Items.Add(status);

            Controls.Add(tabs);
            Controls.Add(ts);
            Controls.Add(ss);
        }

        void BuildTitleTab(TabPage tp)
        {
            Panel host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(244, 245, 248) };
            Label head = new Label { AutoSize = false, Left = 20, Top = 12, Width = 980, Height = 50, ForeColor = Color.FromArgb(40, 40, 40),
                Text = "Значения граф — из свойств: 3D-модели специфицируемой сборки, если там пусто — чертежа, если пусто и там — по умолчанию (вкладка «Параметры»).\r\n" +
                       "Исправленные здесь значения по кнопке «Сформировать спецификацию» записываются в свойства 3D-модели и чертежа. На листе графы — ссылки на свойства чертежа." };
            host.Controls.Add(head);

            // дополнительные графы (свои у каждого чертежа)
            int x = 20, y = 66;
            Func<string, string, Action<string>, int, int> extra = (label, value, set, w) =>
            {
                Label l = new Label { Text = label, AutoSize = true, Left = x, Top = y + 4 };
                host.Controls.Add(l);
                TextBox tb2 = new TextBox { Left = x + l.PreferredWidth + 4, Top = y, Width = w, Text = value ?? "" };
                tb2.TextChanged += (s, e) => set(tb2.Text);
                host.Controls.Add(tb2);
                x = tb2.Right + 24;
                return 0;
            };
            extra("Инв. № подл.:", data.InvNo, v => data.InvNo = v, 120);
            extra("Перв. примен.:", data.FirstUse, v => data.FirstUse = v, 200);
            extra("Справ. №:", data.RefNo, v => data.RefNo = v, 160);

            TitleBlockPanel tbp = new TitleBlockPanel(titleState) { Left = 20, Top = 100 };
            host.Controls.Add(tbp);

            // легенда цветов
            int ly = tbp.Bottom + 14, lx = 20;
            Action<Color, string> legend = (c, txt) =>
            {
                Panel sw = new Panel { BackColor = c, Left = lx, Top = ly + 2, Width = 18, Height = 14, BorderStyle = BorderStyle.FixedSingle };
                Label l = new Label { Text = txt, AutoSize = true, Left = lx + 22, Top = ly };
                host.Controls.Add(sw); host.Controls.Add(l);
                lx = l.Left + l.PreferredWidth + 24;
            };
            legend(TitleBlockPanel.ModelColor, "из 3D-модели");
            legend(TitleBlockPanel.DrawingColor, "из чертежа");
            legend(TitleBlockPanel.DefaultColor, "по умолчанию");
            legend(TitleBlockPanel.EditedColor, "исправлено (будет записано в модель и чертёж)");

            Label map = new Label { AutoSize = false, Left = 20, Top = ly + 30, Width = 980, Height = 90, ForeColor = Color.DimGray,
                Text = "Обозначение — «Обозначение» (Part Number)        Наименование — «Описание» (Description), вкладка «Проект»        Организация — «Компания» (Company)\r\n" +
                       "Разраб. — «Проектировщик» (Designer), вкладка «Проект»        Пров. — «Проверил» (Checked By), вкладка «Статус»\r\n" +
                       "Т.контр. — «Т. контр.» (Engineer)        Н.контр. — «Нормоконтроль» (Engr Approved By), вкладка «Статус»\r\n" +
                       "Утв. — «Утвердил» (Mfg Approved By), вкладка «Статус»        Лит. (первая графа) — Litera, набор GOST Properties" };
            host.Controls.Add(map);
            tp.Controls.Add(host);
        }
        static ToolStripButton Btn(string text, string tip, EventHandler h)
        {
            ToolStripButton b = new ToolStripButton(text) { ToolTipText = tip, DisplayStyle = ToolStripItemDisplayStyle.Text };
            b.Click += h;
            return b;
        }

        void SetStatus(string s) { status.Text = s; Application.DoEvents(); }

        // ------------------------------------------------------------------ данные ↔ таблица

        void LoadAll()
        {
            FillExecGrid();
            props.SelectedObject = data.Settings;
            FillGrid();
        }

        void FillExecGrid()
        {
            loading = true;
            execGrid.Rows.Clear();
            foreach (Execution e in data.Executions)
            {
                string src = e.UserAdded ? "добавлено вручную" : e.Kind == BomCollector.KindRow ? "iAssembly" : e.Kind == BomCollector.KindState ? "состояние модели" : "вид чертежа / сборка";
                int i = execGrid.Rows.Add(e.Enabled, e.Kind == BomCollector.KindFile ? System.IO.Path.GetFileName(e.Key) : e.Key, src, e.Designation, e.Label);
                execGrid.Rows[i].Tag = e;
            }
            loading = false;
        }

        void BuildColumns()
        {
            grid.Columns.Clear();
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Use", HeaderText = "В СП", Width = 40, Frozen = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "Источник", ReadOnly = true, Width = 62 });
            DataGridViewComboBoxColumn sec = new DataGridViewComboBoxColumn { Name = "Section", HeaderText = "Раздел", Width = 165, FlatStyle = FlatStyle.Flat };
            sec.Items.AddRange(Sections.Order);
            grid.Columns.Add(sec);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Format", HeaderText = "Ф.", Width = 38 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Zone", HeaderText = "Зона", Width = 40 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Pos", HeaderText = "Поз.", Width = 40 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Designation", HeaderText = "Обозначение", Width = 210 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Наименование", Width = 220 });
            foreach (Execution e in data.EnabledExecutions())
            {
                DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn { Name = "Q:" + e.Key, HeaderText = e.Label, Width = 42,
                    ToolTipText = e.Designation + " (" + e.Key + ")" };
                c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                c.SortMode = DataGridViewColumnSortMode.NotSortable;
                grid.Columns.Add(c);
            }
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Note", HeaderText = "Примечание", Width = 140 });
            foreach (DataGridViewColumn c in grid.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
        }

        void FillGrid()
        {
            loading = true;
            int firstRow = grid.FirstDisplayedScrollingRowIndex;
            BuildColumns();
            Font bold = new Font(grid.Font, FontStyle.Bold);
            List<string> order = new List<string>(Sections.Order);
            foreach (SpecRow r in data.Rows) if (!order.Contains(r.Section)) order.Add(r.Section);
            foreach (string section in order)
            {
                List<SpecRow> rows = data.Rows.FindAll(r => r.Section == section && (!r.Hidden || showHidden.Checked));
                if (rows.Count == 0) continue;
                AddMarkerRow(section, true);
                foreach (SpecRow r in rows) AddSpecRow(r, bold);
                int blanks = Math.Max(1, data.Settings.ReserveLines);
                for (int k = 0; k < blanks; k++) AddMarkerRow(section, false);
            }
            if (firstRow >= 0 && firstRow < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = firstRow;
            loading = false;
            UpdateStatus();
        }

        /// <summary>Строка-заголовок раздела или пустая строка после раздела (только для просмотра).
        /// В Tag — название раздела, чтобы «Добавить строку» знала, куда добавлять.</summary>
        void AddMarkerRow(string section, bool header)
        {
            int i = grid.Rows.Add();
            DataGridViewRow gr = grid.Rows[i];
            gr.Tag = section;
            // флажок и список разделов в такой строке не нужны — заменяем ячейки текстовыми
            gr.Cells["Use"] = new DataGridViewTextBoxCell();
            gr.Cells["Section"] = new DataGridViewTextBoxCell();
            gr.ReadOnly = true;
            gr.DefaultCellStyle.BackColor = header ? Color.FromArgb(232, 236, 244) : Color.FromArgb(246, 247, 250);
            gr.DefaultCellStyle.SelectionBackColor = gr.DefaultCellStyle.BackColor;
            gr.DefaultCellStyle.SelectionForeColor = Color.Black;
            if (header)
            {
                DataGridViewCell c = gr.Cells["Name"];
                c.Value = section;
                c.Style.Font = new Font(grid.Font, FontStyle.Bold | FontStyle.Underline);
                c.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            }
        }

        void AddSpecRow(SpecRow r, Font bold)
        {
            int i = grid.Rows.Add();
            DataGridViewRow gr = grid.Rows[i];
            gr.Tag = r;
            gr.Cells["Use"].Value = !r.Hidden;
            gr.Cells["Source"].Value = r.Key == SpecLogic.AsmDrawingKey ? "Авто" : r.Source;
            gr.Cells["Section"].Value = r.Section;
            gr.Cells["Format"].Value = r.Format;
            gr.Cells["Zone"].Value = r.Zone;
            gr.Cells["Pos"].Value = r.Pos;
            gr.Cells["Designation"].Value = r.Designation;
            gr.Cells["Name"].Value = r.Name;
            gr.Cells["Note"].Value = r.Note;
            foreach (Execution e in data.EnabledExecutions()) gr.Cells["Q:" + e.Key].Value = r.GetQty(e.Key);
            Color back = r.Source == RowSource.Bom ? Color.White : r.Source == RowSource.Model ? Color.FromArgb(225, 240, 255) : Color.FromArgb(255, 250, 215);
            if (r.Hidden) back = Color.Gainsboro;
            gr.DefaultCellStyle.BackColor = back;
            if (r.Hidden) gr.DefaultCellStyle.ForeColor = Color.Gray;
            foreach (string f in r.Edited)
            {
                string col = f.StartsWith("Qty:") ? "Q:" + f.Substring(4) : f;
                if (grid.Columns.Contains(col)) gr.Cells[col].Style.Font = bold;
            }
        }
        void UpdateStatus()
        {
            int n = 0; foreach (SpecRow r in data.Rows) if (!r.Hidden) n++;
            List<string> layoutWarnings = new List<string>();
            List<SpecPage> pages = GroupSpecB.Layout.Build(data, layoutWarnings);
            SetStatus("Исполнений: " + data.EnabledExecutions().Count + " из " + data.Executions.Count +
                      "   Строк в спецификации: " + n + "   Листов: " + pages.Count +
                      "   Базовое обозначение: " + SpecLogic.BaseDesignation(data) +
                      (data.SourcePath != "" ? "   Источник: " + data.SourcePath : "") +
                      (SpecLogic.DuplicatePositions(data).Count > 0 ? "   ВНИМАНИЕ: повторяются позиции " + string.Join(", ", SpecLogic.DuplicatePositions(data)) : "") +
                      (layoutWarnings.Count > 0 ? "   ВНИМАНИЕ: " + layoutWarnings[0] : ""));
        }

        void Grid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (loading || e.RowIndex < 0) return;
            DataGridViewRow gr = grid.Rows[e.RowIndex];
            SpecRow r = gr.Tag as SpecRow;
            if (r == null) return;
            string col = grid.Columns[e.ColumnIndex].Name;
            object v = gr.Cells[e.ColumnIndex].Value;
            string s = v == null ? "" : v.ToString();
            if (col == "Use")
            {
                r.Hidden = !(v is bool && (bool)v);
                BeginInvoke((MethodInvoker)FillGrid);
                return;
            }
            if (col.StartsWith("Q:"))
            {
                string ex = col.Substring(2);
                double qv;
                string qs = s.Trim();
                if (SpecLogic.TryParseQty(qs, out qv)) { qs = SpecLogic.FormatQty(qv); loading = true; gr.Cells[e.ColumnIndex].Value = qs; loading = false; }
                r.SetQty(ex, qs);
                r.MarkEdited("Qty:" + ex);
            }
            else
            {
                switch (col)
                {
                    case "Section": r.Section = s; break;
                    case "Format": r.Format = s; break;
                    case "Zone": r.Zone = s; break;
                    case "Pos": r.Pos = s; break;
                    case "Designation": r.Designation = s; break;
                    case "Name": r.Name = s; break;
                    case "Note": r.Note = s; break;
                    default: return;
                }
                r.MarkEdited(col);
            }
            gr.Cells[e.ColumnIndex].Style.Font = new Font(grid.Font, FontStyle.Bold);
            if (col == "Section")
            {
                SpecLogic.GroupBySection(data);
                BeginInvoke((MethodInvoker)FillGrid);
            }
            else UpdateStatus();
        }

        void ExecGrid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (loading || e.RowIndex < 0) return;
            Execution ex = (Execution)execGrid.Rows[e.RowIndex].Tag;
            object v = execGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
            string col = execGrid.Columns[e.ColumnIndex].Name;
            if (col == "On") ex.Enabled = v is bool && (bool)v;
            else if (col == "Label") ex.Label = v == null ? "" : v.ToString();
            else return;
            FillGrid();
        }

        void Commit()
        {
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            if (execGrid.IsCurrentCellInEditMode) execGrid.EndEdit();
        }

        List<SpecRow> SelectedRows()
        {
            List<int> idx = new List<int>();
            foreach (DataGridViewCell c in grid.SelectedCells) if (!idx.Contains(c.RowIndex)) idx.Add(c.RowIndex);
            foreach (DataGridViewRow r in grid.SelectedRows) if (!idx.Contains(r.Index)) idx.Add(r.Index);
            idx.Sort();
            List<SpecRow> res = new List<SpecRow>();
            foreach (int i in idx) { SpecRow r = grid.Rows[i].Tag as SpecRow; if (r != null) res.Add(r); }
            return res;
        }

        void SelectSpecRows(List<SpecRow> rows)
        {
            grid.ClearSelection();
            foreach (DataGridViewRow gr in grid.Rows)
                if (gr.Tag is SpecRow && rows.Contains((SpecRow)gr.Tag))
                {
                    gr.Cells["Designation"].Selected = true;
                    if (grid.CurrentCell == null || !(grid.CurrentRow.Tag is SpecRow) || !rows.Contains((SpecRow)grid.CurrentRow.Tag))
                        grid.CurrentCell = gr.Cells["Designation"];
                }
        }

        // ------------------------------------------------------------------ команды

        void OnRefresh(object sender, EventArgs e)
        {
            Commit();
            Cursor = Cursors.WaitCursor;
            try
            {
                string err = refresh(SetStatus);
                if (err != null) MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
            LoadAll();
        }

        void OnAddExecution(object sender, EventArgs e)
        {
            Commit();
            OpenFileDialog dlg = new OpenFileDialog { Multiselect = true, Title = "3D-модели исполнений (сборки) для групповой спецификации",
                Filter = "Сборки Inventor (*.iam)|*.iam" };
            if (data.SourcePath != "") try { dlg.InitialDirectory = System.IO.Path.GetDirectoryName(data.SourcePath); } catch { }
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            List<string> skipped = new List<string>();
            int added = 0;
            foreach (string f in dlg.FileNames)
            {
                if (data.Executions.Exists(x => string.Equals(x.Path, f, StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(x.Key, f, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped.Add(System.IO.Path.GetFileName(f) + " — уже есть среди исполнений");
                    continue;
                }
                data.Executions.Add(new Execution { Kind = BomCollector.KindFile, Path = f, Key = f, UserAdded = true });
                added++;
            }
            if (added > 0) OnRefresh(sender, e);
            if (skipped.Count > 0)
                MessageBox.Show(this, "Пропущено:\r\n" + string.Join("\r\n", skipped), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void OnRemoveExecution(object sender, EventArgs e)
        {
            Commit();
            List<Execution> sel = new List<Execution>();
            foreach (DataGridViewCell c in execGrid.SelectedCells)
            {
                Execution ex = (Execution)execGrid.Rows[c.RowIndex].Tag;
                if (!sel.Contains(ex)) sel.Add(ex);
            }
            List<Execution> removable = sel.FindAll(x => x.UserAdded);
            if (removable.Count == 0)
            {
                MessageBox.Show(this, "Удалить можно только исполнения, добавленные кнопкой «Добавить исполнение». " +
                    "Остальные исключаются флажком «В СП».", Text);
                return;
            }
            foreach (Execution ex in removable)
            {
                data.Executions.Remove(ex);
                foreach (SpecRow r in data.Rows) r.SetQty(ex.Key, "");
            }
            OnRefresh(sender, e);
        }
        void OnAddRow(object sender, EventArgs e)
        {
            Commit();
            List<SpecRow> sel = SelectedRows();
            // выделен заголовок раздела или пустая строка после него — добавляем в конец этого раздела
            string markerSection = grid.CurrentRow != null ? grid.CurrentRow.Tag as string : null;
            string section = sel.Count > 0 ? sel[sel.Count - 1].Section : markerSection ?? Sections.Documentation;
            SpecRow r = new SpecRow { Source = RowSource.Manual, Section = section };
            r.MarkEdited("Section");
            int at = sel.Count > 0 ? data.Rows.IndexOf(sel[sel.Count - 1]) + 1 : data.Rows.Count;
            data.Rows.Insert(at, r);
            data.ManualOrder = true;
            SpecLogic.GroupBySection(data);
            FillGrid();
            SelectSpecRows(new List<SpecRow> { r });
        }

        void OnDelete(object sender, EventArgs e)
        {
            Commit();
            List<SpecRow> sel = SelectedRows();
            int bom = 0, del = 0;
            foreach (SpecRow r in sel)
            {
                if (r.Source == RowSource.Bom) { r.Hidden = true; bom++; }
                else { data.Rows.Remove(r); del++; }
            }
            if (sel.Exists(r => r.Key == SpecLogic.AsmDrawingKey)) data.Settings.AddAssemblyDrawing = false;
            SpecLogic.Renumber(data);
            FillGrid();
            if (bom > 0) SetStatus("Строки BOM (" + bom + ") исключены из спецификации; вернуть — флажок «В СП».");
        }

        void MoveRows(int dir)
        {
            Commit();
            List<SpecRow> sel = SelectedRows();
            if (sel.Count == 0) return;
            if (dir > 0) sel.Reverse();
            foreach (SpecRow r in sel)
            {
                int i = data.Rows.IndexOf(r), j = i + dir;
                if (j < 0 || j >= data.Rows.Count) return;
                if (data.Rows[j].Section != r.Section) return;
                data.Rows[i] = data.Rows[j]; data.Rows[j] = r;
            }
            data.ManualOrder = true;
            SpecLogic.Renumber(data);
            FillGrid();
            SelectSpecRows(sel);
        }

        void OnFillQty(object sender, EventArgs e)
        {
            Commit();
            List<SpecRow> sel = SelectedRows();
            if (sel.Count == 0) { SetStatus("Выделите строки."); return; }
            string def = sel[0].Section == Sections.Documentation ? "X" : "1";
            string v = Microsoft.VisualBasic.Interaction.InputBox(
                "Количество (для документов — X) во всех включённых исполнениях.\r\nПустое значение очищает графы.",
                "Количество на исполнение", def);
            foreach (SpecRow r in sel)
                foreach (Execution ex in data.EnabledExecutions())
                {
                    r.SetQty(ex.Key, v.Trim());
                    r.MarkEdited("Qty:" + ex.Key);
                }
            FillGrid();
            SelectSpecRows(sel);
        }

        void OnSort(object sender, EventArgs e)
        {
            Commit();
            data.ManualOrder = false;
            SpecLogic.SortAndNumber(data);
            FillGrid();
        }

        void OnRenumber(object sender, EventArgs e)
        {
            Commit();
            SpecLogic.Renumber(data);
            FillGrid();
        }
    }
}
