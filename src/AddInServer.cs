using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Inventor;
using Application = Inventor.Application;
using Color = System.Drawing.Color;

namespace GroupSpecB
{
    /// <summary>Надстройка-компаньон к ESKD Specia: спецификация по ГОСТ 2.106 и групповая по ГОСТ 2.113-75, вариант Б.</summary>
    [Guid("6B3C2E1A-8F4D-4C9B-A7E2-3D51B0C4F7A9")]
    [ComVisible(true)]
    public class AddInServer : ApplicationAddInServer
    {
        public const string ClientId = "{6B3C2E1A-8F4D-4C9B-A7E2-3D51B0C4F7A9}";
        Application app;
        ButtonDefinition button;
        ApplicationEvents appEvents;
        UserInputEvents inputEvents;

        public void Activate(ApplicationAddInSite site, bool firstTime)
        {
            app = site.Application;
            object small = IconConverter.ToPicture(MakeIcon(16));
            object large = IconConverter.ToPicture(MakeIcon(32));
            button = app.CommandManager.ControlDefinitions.AddButtonDefinition(
                "Спецификация", "GroupSpecB_Run", CommandTypesEnum.kShapeEditCmdType, ClientId,
                "Спецификация по ГОСТ 2.106 (одна 3D-модель) или групповая по ГОСТ 2.113-75, вариант Б (несколько исполнений); тип переключается в редакторе",
                "Спецификация ГОСТ 2.106 / групповая ГОСТ 2.113", small, large, ButtonDisplayEnum.kAlwaysDisplayText);
            button.OnExecute += OnExecute;
            AddToRibbons();
            appEvents = app.ApplicationEvents;
            appEvents.OnSaveDocument += OnSaveDocument;
            appEvents.OnActivateDocument += OnActivateDocument;
            inputEvents = app.CommandManager.UserInputEvents;
            inputEvents.OnTerminateCommand += OnTerminateCommand;
            inputEvents.OnActivateCommand += OnActivateCommand;
            txTimer = new Timer { Interval = 200 };
            txTimer.Tick += OnTxTimer;
            titleTimer = new Timer { Interval = TitleDelay };
            titleTimer.Tick += OnTitleTimer;
        }

        // Свойства основной надписи при активации чертежа (обозначение и др. могли измениться в 3D-модели,
        // а чертёж без правок Inventor не сохраняет — OnSaveDocument тогда не приходит). Копируем с задержкой,
        // после заплатки Specia: свойство, записанное внутри её незакрытой транзакции, откатится при Abort().
        const int TitleDelay = TxTicks * 200 + 200;
        const int TitleRetries = 5;
        Timer titleTimer;
        DrawingDocument titleDwg;
        int titleRetriesLeft;

        void OnTitleTimer(object sender, EventArgs e)
        {
            titleTimer.Stop();
            DrawingDocument dwg = titleDwg;
            try
            {
                Transaction t = app.TransactionManager.CurrentTransaction;
                if (t != null && t.DisplayName == SpeciaDanglingTx && titleRetriesLeft-- > 0) { titleTimer.Start(); return; }
                titleDwg = null;
                SyncTitle(dwg, "Свойства основной надписи при активации чертежа");
            }
            catch (Exception ex) { titleDwg = null; Log.Write("Свойства основной надписи при активации чертежа", ex); }
        }

        /// <summary>Свойства основной надписи — из 3D-модели первого вида (для члена iPart/iAssembly — из фабрики)
        /// в свойства чертежа. По умолчанию — только чертежи со спецификацией надстройки; для всех чертежей —
        /// параметр «Свойства при сохранении: все чертежи».</summary>
        void SyncTitle(DrawingDocument dwg, string what)
        {
            if (dwg == null) return;
            try
            {
                if (DrawingWriter.HasData(dwg) || GlobalSettings.Load().SyncTitleOnSaveAllDrawings)
                    SpecTitleBlock.CopyFromFirstViewModel(app, dwg);
            }
            catch (Exception ex) { Log.Write(what, ex); }
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
                UpdateMaterial((DrawingDocument)doc, "Свойства материала при активации чертежа");
                titleTimer.Stop();
                titleDwg = (DrawingDocument)doc;
                titleRetriesLeft = TitleRetries;
                titleTimer.Start();
                if (!GlobalSettings.Load().FixSpeciaTransaction || !SpeciaLoaded()) return;
                txTimer.Stop();
                txTicksLeft = TxTicks;
                txTimer.Start();
            }
            catch (Exception ex) { Log.Write("Активация чертежа", ex); }
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
                    if (panel == null) panel = tab.RibbonPanels.Add("Спецификация ГОСТ", "GroupSpecB_Panel", ClientId, "", false);
                    bool has = false;
                    foreach (CommandControl c in panel.CommandControls) if (c.InternalName == button.InternalName) has = true;
                    if (!has) panel.CommandControls.AddButton(button, true, true, "", false);
                }
                catch (Exception ex) { Log.Write("Кнопка на ленте", ex); }
            }
        }

        /// <summary>Перед сохранением чертежа: свойства материала и основной надписи (см. SyncTitle).</summary>
        void OnSaveDocument(_Document doc, EventTimingEnum timing, NameValueMap context, out HandlingCodeEnum handling)
        {
            handling = HandlingCodeEnum.kEventNotHandled;
            if (timing != EventTimingEnum.kBefore) return;
            try
            {
                if (doc.DocumentType != DocumentTypeEnum.kDrawingDocumentObject) return;
                DrawingDocument dwg = (DrawingDocument)doc;
                UpdateMaterial(dwg, "Свойства материала при сохранении");
                SyncTitle(dwg, "Свойства основной надписи при сохранении");
            }
            catch (Exception ex) { Log.Write("Свойства основной надписи при сохранении", ex); }   // сохранение не должно срываться из-за надстройки
        }

        /// <summary>Свойства материала в активном чертеже — после вставки/замены/удаления вида и окна iProperties.</summary>
        void OnTerminateCommand(string command, NameValueMap context)
        {
            if (Array.IndexOf(DrawingMaterial.Commands, command) < 0) return;
            UpdateMaterial(ActiveDrawing(), "Свойства материала после " + command);
        }

        /// <summary>Свойства материала и основной надписи перед печатью чертежа.</summary>
        void OnActivateCommand(string command, NameValueMap context)
        {
            if (command != "AppFilePrintCmd") return;
            UpdateMaterial(ActiveDrawing(), "Свойства материала перед печатью");
            SyncTitle(ActiveDrawing(), "Свойства основной надписи перед печатью");
        }

        DrawingDocument ActiveDrawing()
        {
            try
            {
                Document d = app.ActiveDocument;
                return d != null && d.DocumentType == DocumentTypeEnum.kDrawingDocumentObject ? (DrawingDocument)d : null;
            }
            catch { return null; }
        }

        void UpdateMaterial(DrawingDocument dwg, string what)
        {
            if (dwg == null) return;
            try { if (GlobalSettings.Load().FillMaterialProps) DrawingMaterial.Update(dwg); }
            catch (Exception ex) { Log.Write(what, ex); }
        }

        void OnExecute(NameValueMap context)
        {
            try { Commands.Run(app); }
            catch (Exception ex)
            {
                Log.Write("Команда «Спецификация»", ex);
                MessageBox.Show(ex.Message + "\r\n\r\n" + ex.StackTrace, "Спецификация", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void Deactivate()
        {
            if (txTimer != null) { txTimer.Stop(); txTimer.Tick -= OnTxTimer; txTimer.Dispose(); txTimer = null; }
            if (titleTimer != null) { titleTimer.Stop(); titleTimer.Tick -= OnTitleTimer; titleTimer.Dispose(); titleTimer = null; }
            titleDwg = null;
            if (appEvents != null)
            {
                appEvents.OnSaveDocument -= OnSaveDocument;
                appEvents.OnActivateDocument -= OnActivateDocument;
                appEvents = null;
            }
            if (inputEvents != null)
            {
                inputEvents.OnTerminateCommand -= OnTerminateCommand;
                inputEvents.OnActivateCommand -= OnActivateCommand;
                inputEvents = null;
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
