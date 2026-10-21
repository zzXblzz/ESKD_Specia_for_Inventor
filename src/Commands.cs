using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Inventor;
using Application = Inventor.Application;

namespace GroupSpecB
{
    class InventorWindow : IWin32Window
    {
        readonly IntPtr h;
        public InventorWindow(Application app) { h = new IntPtr(app.MainFrameHWND); }
        public IntPtr Handle { get { return h; } }
    }

    /// <summary>Сеанс работы со спецификацией: откуда берутся исполнения и как их перечитать.</summary>
    public class SpecSession
    {
        public readonly Application App;
        public readonly SpecData Data;
        public readonly DrawingDocument Drawing;     // чертёж, из которого запущена команда (или null)
        public readonly string AssemblyPath;         // параметрическая сборка, если запуск из сборки
        public List<string> Notes = new List<string>();

        public SpecSession(Application app, SpecData data, DrawingDocument drawing, string assemblyPath)
        {
            App = app; Data = data; Drawing = drawing; AssemblyPath = assemblyPath;
        }

        static bool IsSpecSheet(Sheet sh)
        {
            try { return sh.AttributeSets.NameIsUsed[DrawingWriter.SheetAttrSet]; } catch { return false; }
        }

        /// <summary>Определения исполнений:
        /// из чертежа — все сборки в видах на всех листах (кроме листов спецификации);
        /// из сборки — строки iAssembly / состояния модели;
        /// плюс исполнения, добавленные пользователем.</summary>
        public List<Execution> DiscoverExecutions()
        {
            Notes.Clear();
            List<Execution> defs = new List<Execution>();
            if (Drawing != null)
            {
                defs = BomCollector.DiscoverFromDrawing(Drawing, IsSpecSheet, Notes);
                // формат первого листа сборочного чертежа — для строки «Сборочный чертеж» и LastFormat сборки
                Data.DrawingFormat = "";
                foreach (Sheet sh in Drawing.Sheets)
                {
                    if (IsSpecSheet(sh)) continue;
                    Data.DrawingFormat = DrawingWriter.SheetFormat(sh);
                    break;
                }
                if (defs.Count > 0)
                {
                    Data.SourcePath = string.IsNullOrEmpty(((Document)Drawing).FullFileName)
                        ? "виды чертежа" : ((Document)Drawing).FullFileName;
                    Notes.Insert(0, "Исполнения взяты из видов чертежа: " + defs.Count + ".");
                }
                else
                {
                    // в чертеже нет видов сборок — повторяем сохранённые исполнения
                    foreach (Execution e in Data.Executions) if (!e.UserAdded) defs.Add(e);
                    if (defs.Count == 0)
                        Notes.Add("В видах чертежа нет сборок. Добавьте исполнения кнопкой «Добавить исполнение (3D-модель)».");
                }
            }
            else if (!string.IsNullOrEmpty(AssemblyPath))
            {
                bool opened;
                AssemblyDocument asm = BomCollector.OpenAssembly(App, AssemblyPath, out opened);
                try { defs = BomCollector.DiscoverFromAssembly(asm); }
                finally { if (opened) try { ((Document)asm).Close(true); } catch { } }
                Data.SourcePath = AssemblyPath;
            }
            foreach (Execution e in Data.Executions)
            {
                if (!e.UserAdded) continue;
                if (defs.Exists(d => string.Equals(d.Key, e.Key, StringComparison.OrdinalIgnoreCase))) continue;
                defs.Add(e);
            }
            return defs;
        }

        /// <summary>Перечитывает исполнения и их BOM. Возвращает текст предупреждений или null.</summary>
        public string Refresh(Action<string> progress)
        {
            try
            {
                List<Execution> defs = DiscoverExecutions();
                List<string> errors = new List<string>();
                List<ExecutionBom> boms = BomCollector.CollectAll(App, defs, Data.Settings, progress, errors);
                Data.SourceKind = defs.Count > 0 ? defs[0].Kind : "";
                SpecLogic.Merge(Data, boms);
                if (progress != null) progress("Прочитано исполнений: " + boms.Count);
                List<string> msg = new List<string>(Notes);
                if (errors.Count > 0) msg.Add("Не прочитаны:\r\n" + string.Join("\r\n", errors));
                List<string> dup = SpecLogic.DuplicatePositions(Data);
                if (dup.Count > 0) msg.Add("Повторяются номера позиций: " + string.Join(", ", dup));
                bool warn = errors.Count > 0 || dup.Count > 0 || boms.Count == 0;
                return warn ? string.Join("\r\n\r\n", msg) : null;
            }
            catch (Exception ex) { Log.Write("Чтение BOM", ex); return "Ошибка чтения BOM: " + ex.Message; }
        }
    }

    /// <summary>Команда «Спецификация»: из параметрической сборки создаёт чертёж со спецификацией,
    /// из чертежа — строит спецификацию по исполнениям, показанным в его видах, и выводит её в этот чертёж.
    /// Одна 3D-модель — спецификация по ГОСТ 2.106 (формы 1, 1а), несколько — групповая по ГОСТ 2.113;
    /// тип можно переключить в редакторе.</summary>
    public static class Commands
    {
        const string Title = "Спецификация";

        public static void Run(Application app)
        {
            Document active = app.ActiveDocument;
            if (active == null)
            {
                MessageBox.Show("Откройте параметрическую сборку (iAssembly или сборку с состояниями модели) " +
                                "или чертёж с видами исполнений.", Title);
                return;
            }
            IWin32Window owner = new InventorWindow(app);
            DrawingDocument dwg = null;
            SpecData data = null;
            string asmPath = null;

            if (active.DocumentType == DocumentTypeEnum.kDrawingDocumentObject)
            {
                dwg = (DrawingDocument)active;
                try { data = DrawingWriter.LoadData(dwg); }
                catch (Exception ex) { MessageBox.Show(owner, "Не удалось прочитать данные спецификации: " + ex.Message, Title); }
            }
            else if (active.DocumentType == DocumentTypeEnum.kAssemblyDocumentObject)
            {
                asmPath = active.FullFileName;
                if (string.IsNullOrEmpty(asmPath))
                {
                    MessageBox.Show(owner, "Сохраните сборку перед созданием спецификации.", Title);
                    return;
                }
            }
            else
            {
                MessageBox.Show(owner, "Команда работает в сборке или в чертеже.", Title);
                return;
            }
            if (data == null) data = new SpecData();
            PrepareSettings(data);

            SpecSession session = new SpecSession(app, data, dwg, asmPath);
            string warn = session.Refresh(null);
            if (warn != null && data.Executions.Count == 0 && dwg == null)
            {
                MessageBox.Show(owner, warn, Title);
                return;
            }

            // значения граф основной надписи: из 3D-модели, иначе из чертежа, иначе по умолчанию
            TitleState titleState;
            {
                bool mOpened;
                Document model = SpecTitleBlock.ParentModel(app, data, dwg, out mOpened);
                try { titleState = SpecTitleBlock.Resolve(model, dwg, data.Settings); }
                finally { if (mOpened && model != null) try { model.Close(true); } catch { } }
            }
            EditorForm form = new EditorForm(data, session.Refresh, titleState);
            form.Shown += (s, e) =>
            {
                if (warn != null) MessageBox.Show(form, warn, Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            DialogResult dr = form.ShowDialog(owner);
            GlobalSettings.Save(data.Settings);   // параметры — общие для всех чертежей
            if (dr != DialogResult.OK) return;
            if (data.EnabledExecutions().Count == 0)
            {
                MessageBox.Show(owner, "Нет ни одного исполнения — спецификация не сформирована.", Title);
                return;
            }

            // Все изменения 3D-моделей — только здесь, после нажатия «Сформировать спецификацию»:
            // spec_section, исправленные обозначения и наименования, затем номера позиций BOM.
            string sectionReport = null;
            try { sectionReport = BomCollector.WriteModelProperties(app, data, s => { try { app.StatusBarText = s; } catch { } }); }
            catch (Exception ex) { sectionReport = "Запись в модели: ошибка — " + ex.Message; }

            // LastFormat специфицируемой сборки = формат первого листа сборочного чертежа
            if (!string.IsNullOrEmpty(data.DrawingFormat))
            {
                string fr = BomCollector.WriteAssemblyFormat(app, data);
                if (fr != null) sectionReport = (sectionReport ?? "") + "\r\n" + fr;
            }

            // исправленные графы основной надписи — в 3D-модель (до перенумерации BOM, которая тоже пересоздаёт члены)
            string titleModelReport = SpecTitleBlock.WriteModel(app, data, dwg, titleState);

            // номера позиций BOM родительских сборок = позиции спецификации
            string bomReport = null;
            if (data.Settings.RenumberAssemblyBom)
            {
                try { bomReport = BomNumbering.Renumber(app, data, s => { try { app.StatusBarText = s; } catch { } }); }
                catch (Exception ex) { bomReport = "Позиции BOM: ошибка — " + ex.Message; }
            }

            bool created = dwg == null;
            if (created) dwg = DrawingWriter.CreateDrawing(app);

            // основная надпись: исправленные в окне графы — в 3D-модель, затем все графы — в свойства чертежа
            string titleReport = titleModelReport;
            try
            {
                bool mOpened;
                Document model = SpecTitleBlock.ParentModel(app, data, dwg, out mOpened);
                try
                {
                    string r = SpecTitleBlock.FillDrawing(dwg, model, data.Settings, titleState);
                    if (r != null && r.Contains("Не заполнено")) titleReport = (titleReport == null ? "" : titleReport + "\r\n") + r;
                }
                finally { if (mOpened && model != null) try { model.Close(true); } catch { } }
            }
            catch (Exception ex) { titleReport = "Основная надпись: " + ex.Message; }
            List<string> layoutWarnings = new List<string>();
            bool screen = app.ScreenUpdating;
            Transaction tr = app.TransactionManager.StartTransaction((_Document)dwg, "Спецификация");
            try
            {
                app.ScreenUpdating = false;
                DrawingWriter writer = new DrawingWriter(app, dwg, data) { RemoveEmptySheets = created };
                int n = writer.Write(s => { try { app.StatusBarText = s; } catch { } });
                layoutWarnings.AddRange(writer.Warnings);
                tr.End();
                app.StatusBarText = SpecTypes.Title(data.EffectiveType()) + (data.OnDrawing ? ": на листе чертежа" : ": листов — " + n);
            }
            catch (Exception ex)
            {
                try { tr.Abort(); } catch { }
                MessageBox.Show(owner, "Ошибка вывода спецификации: " + ex.Message + "\r\n\r\n" + ex.StackTrace, Title);
            }
            finally { app.ScreenUpdating = screen; }
            try { ((Document)dwg).Activate(); } catch { }
            List<string> reports = new List<string>();
            if (sectionReport != null && (sectionReport.Contains("Не записано") || sectionReport.Contains("ошибка"))) reports.Add(sectionReport);
            if (bomReport != null && bomReport.Contains("\r\n") || bomReport != null && bomReport.Contains("ошибка")) reports.Add(bomReport);
            if (titleReport != null) reports.Add(titleReport);
            if (layoutWarnings.Count > 0) reports.Add(string.Join("\r\n", layoutWarnings));
            if (reports.Count > 0)
                MessageBox.Show(owner, string.Join("\r\n\r\n", reports), Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else if (bomReport != null) try { app.StatusBarText = bomReport; } catch { }
        }
        /// <summary>Параметры — общие для всех чертежей (файл пользователя). Значения, которые раньше
        /// хранились в параметрах чертежа, но относятся к самому чертежу, переносятся в его данные.</summary>
        public static void PrepareSettings(SpecData data)
        {
            SpecSettings old = data.Settings ?? new SpecSettings();
            if (string.IsNullOrEmpty(data.BaseDesignation)) data.BaseDesignation = old.BaseDesignation ?? "";
            if (string.IsNullOrEmpty(data.InvNo)) data.InvNo = old.InvNo ?? "";
            if (string.IsNullOrEmpty(data.FirstUse)) data.FirstUse = old.FirstUse ?? "";
            if (string.IsNullOrEmpty(data.RefNo)) data.RefNo = old.RefNo ?? "";
            if (GlobalSettings.Exists) data.Settings = GlobalSettings.Load();
            else GlobalSettings.Save(old);   // первый запуск: параметры этого чертежа становятся общими
            data.Settings.BaseDesignation = ""; data.Settings.InvNo = ""; data.Settings.FirstUse = ""; data.Settings.RefNo = "";
        }
    }
}
