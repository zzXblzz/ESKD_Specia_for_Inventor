using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Inventor;
using Application = Inventor.Application;
using Color = System.Drawing.Color;

namespace GroupSpecB
{
    /// <summary>Надстройка-компаньон к ESKD Specia: групповая спецификация по ГОСТ 2.113-75, вариант Б.</summary>
    [Guid("6B3C2E1A-8F4D-4C9B-A7E2-3D51B0C4F7A9")]
    [ComVisible(true)]
    public class AddInServer : ApplicationAddInServer
    {
        public const string ClientId = "{6B3C2E1A-8F4D-4C9B-A7E2-3D51B0C4F7A9}";
        Application app;
        ButtonDefinition button;
        ApplicationEvents appEvents;

        public void Activate(ApplicationAddInSite site, bool firstTime)
        {
            app = site.Application;
            object small = IconConverter.ToPicture(MakeIcon(16));
            object large = IconConverter.ToPicture(MakeIcon(32));
            button = app.CommandManager.ControlDefinitions.AddButtonDefinition(
                "Групповая\nспецификация", "GroupSpecB_Run", CommandTypesEnum.kShapeEditCmdType, ClientId,
                "Групповая спецификация по ГОСТ 2.113-75, вариант Б (исполнения из BOM параметрической сборки)",
                "Групповая спецификация (вариант Б)", small, large, ButtonDisplayEnum.kAlwaysDisplayText);
            button.OnExecute += OnExecute;
            AddToRibbons();
            appEvents = app.ApplicationEvents;
            appEvents.OnSaveDocument += OnSaveDocument;
            appEvents.OnActivateDocument += OnActivateDocument;
            txTimer = new Timer { Interval = 200 };
            txTimer.Tick += OnTxTimer;
        }

        // ---- Временная заплатка для ESKD Specia (убрать вместе с Specia).
        // При активации чертежа Specia (WorkWithReferenceObjects.LoadFromBOM) открывает транзакцию
        // "CreateProperty" и добавляет свой набор свойств компонентам BOM без try/finally. На исполнении
        // iAssembly/iPart добавление падает, транзакция остаётся открытой, и обозреватель Inventor
        // перестаёт обновляться во всех документах. Прерываем её после того, как отработают все
        // обработчики активации (порядок вызова надстроек не определён — поэтому таймер).
        // Проверка идёт несколько тактов (Specia может отработать позже), только если Specia загружена и
        // параметр «Закрывать зависшую транзакцию ESKD Specia» включён; каждое прерывание пишется в журнал.
        const string SpeciaDanglingTx = "CreateProperty";
        const int TxTicks = 5;   // 5 × 200 мс
        Timer txTimer;
        int txTicksLeft;

        void OnActivateDocument(_Document doc, EventTimingEnum timing, NameValueMap context, out HandlingCodeEnum handling)
        {
            handling = HandlingCodeEnum.kEventNotHandled;
            if (timing != EventTimingEnum.kAfter) return;
            try
            {
                if (doc.DocumentType != DocumentTypeEnum.kDrawingDocumentObject) return;
                if (!GlobalSettings.Load().FixSpeciaTransaction || !SpeciaLoaded()) return;
                txTimer.Stop();
                txTicksLeft = TxTicks;
                txTimer.Start();
            }
            catch { }
        }

        void OnTxTimer(object sender, EventArgs e)
        {
            if (--txTicksLeft <= 0) txTimer.Stop();
            try
            {
                TransactionManager tm = app.TransactionManager;
                for (int i = 0; i < 20; i++)
                {
                    Transaction t = tm.CurrentTransaction;
                    if (t == null || t.DisplayName != SpeciaDanglingTx) break;
                    t.Abort();   // End() на такой транзакции ничего не делает
                    Log.Write("Прервана незакрытая транзакция ESKD Specia «" + SpeciaDanglingTx + "»");
                    txTimer.Stop();
                }
            }
            catch (Exception ex) { Log.Write("Заплатка Specia", ex); }
        }

        bool? speciaLoaded;

        /// <summary>Загружена ли надстройка ESKD Specia (по отображаемому имени; результат запоминается).</summary>
        bool SpeciaLoaded()
        {
            if (speciaLoaded.HasValue && speciaLoaded.Value) return true;
            bool found = false;
            try
            {
                foreach (ApplicationAddIn a in app.ApplicationAddIns)
                {
                    string n = a.DisplayName ?? "";
                    if (a.Activated && n.IndexOf("Specia", StringComparison.OrdinalIgnoreCase) >= 0) { found = true; break; }
                }
            }
            catch (Exception ex) { Log.Write("Поиск ESKD Specia", ex); found = true; }   // не смогли проверить — работаем как раньше
            speciaLoaded = found;
            return found;
        }

        void AddToRibbons()
        {
            string[][] places =
            {
                new[] { "Assembly", "id_TabAssemble" },
                new[] { "Drawing", "id_TabPlaceViews" },
            };
            foreach (string[] p in places)
            {
                try
                {
                    RibbonTab tab = app.UserInterfaceManager.Ribbons[p[0]].RibbonTabs[p[1]];
                    RibbonPanel panel = null;
                    foreach (RibbonPanel rp in tab.RibbonPanels) if (rp.InternalName == "GroupSpecB_Panel") panel = rp;
                    if (panel == null) panel = tab.RibbonPanels.Add("Групповая СП", "GroupSpecB_Panel", ClientId, "", false);
                    bool has = false;
                    foreach (CommandControl c in panel.CommandControls) if (c.InternalName == button.InternalName) has = true;
                    if (!has) panel.CommandControls.AddButton(button, true, true, "", false);
                }
                catch { }
            }
        }

        /// <summary>Перед сохранением чертежа: свойства основной надписи — из 3D-модели первого вида
        /// (для члена iPart/iAssembly — из фабрики) в свойства чертежа. По умолчанию — только чертежи
        /// с групповой спецификацией; для всех чертежей — параметр «Свойства при сохранении: все чертежи».</summary>
        void OnSaveDocument(_Document doc, EventTimingEnum timing, NameValueMap context, out HandlingCodeEnum handling)
        {
            handling = HandlingCodeEnum.kEventNotHandled;
            if (timing != EventTimingEnum.kBefore) return;
            try
            {
                if (doc.DocumentType != DocumentTypeEnum.kDrawingDocumentObject) return;
                DrawingDocument dwg = (DrawingDocument)doc;
                if (DrawingWriter.HasData(dwg) || GlobalSettings.Load().SyncTitleOnSaveAllDrawings)
                    SpecTitleBlock.CopyFromFirstViewModel(app, dwg);
            }
            catch { }   // сохранение не должно срываться из-за надстройки
        }

        void OnExecute(NameValueMap context)
        {
            try { Commands.Run(app); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message + "\r\n\r\n" + ex.StackTrace, "Групповая спецификация", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void Deactivate()
        {
            if (txTimer != null) { txTimer.Stop(); txTimer.Tick -= OnTxTimer; txTimer.Dispose(); txTimer = null; }
            if (appEvents != null)
            {
                appEvents.OnSaveDocument -= OnSaveDocument;
                appEvents.OnActivateDocument -= OnActivateDocument;
                appEvents = null;
            }
            if (button != null) { button.OnExecute -= OnExecute; Marshal.ReleaseComObject(button); button = null; }
            app = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        public void ExecuteCommand(int commandID) { }

        public object Automation { get { return null; } }

        static Bitmap MakeIcon(int size)
        {
            Bitmap b = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float s = size / 16f;
                using (Pen pen = new Pen(Color.FromArgb(40, 70, 140), Math.Max(1f, s)))
                {
                    g.FillRectangle(Brushes.White, 1 * s, 1 * s, 14 * s, 14 * s);
                    g.DrawRectangle(pen, 1 * s, 1 * s, 14 * s - 1, 14 * s - 1);
                    for (int i = 0; i < 4; i++) g.DrawLine(pen, 1 * s, (5 + 3 * i) * s, 15 * s, (5 + 3 * i) * s);
                    for (int i = 0; i < 3; i++) g.DrawLine(pen, (9 + 2 * i) * s, 5 * s, (9 + 2 * i) * s, 15 * s);
                }
                using (Font f = new Font("Arial", 3.6f * s, FontStyle.Bold, GraphicsUnit.Pixel))
                    g.DrawString("ГС", f, Brushes.DarkRed, 1.5f * s, 0.2f * s);
            }
            return b;
        }
    }

    /// <summary>Image → IPictureDisp для кнопок ленты.</summary>
    class IconConverter : AxHost
    {
        IconConverter() : base("") { }
        public static object ToPicture(Image img) { return GetIPictureDispFromPicture(img); }
    }
}
