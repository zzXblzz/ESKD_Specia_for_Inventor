using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Inventor;
using GroupSpecB;

// Проверка надстройки вне Inventor: подключается к запущенному Inventor, собирает исполнения
// (из сборки или из видов чертежа), формирует спецификацию и сохраняет чертёж + PDF.
// Использование: TestRunner.exe <сборка.iam | чертёж.idw> <выход.idw> [--form] [--add исполнение.iam]...
static class TestRunner
{
    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            Application app = (Application)Marshal.GetActiveObject("Inventor.Application");
            if (args[0] == "--material") return MaterialTest(app, args);
            string input = args[0], outPath = args[1];
            bool form = Array.IndexOf(args, "--form") >= 0;
            SpecData data = new SpecData();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--add")
                    data.Executions.Add(new Execution { Kind = BomCollector.KindFile, Path = args[i + 1], Key = args[i + 1], UserAdded = true });

            DrawingDocument dwg = null;
            bool dwgWasOpen = false;
            SpecSession session;
            if (input.EndsWith(".idw", StringComparison.OrdinalIgnoreCase) || input.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Document od in app.Documents)
                    if (string.Equals(od.FullFileName, input, StringComparison.OrdinalIgnoreCase)) dwgWasOpen = true;
                dwg = (DrawingDocument)app.Documents.Open(input, true);
                SpecData saved = DrawingWriter.LoadData(dwg);
                if (saved != null) { saved.Executions.AddRange(data.Executions); data = saved; }
                session = new SpecSession(app, data, dwg, null);
            }
            else session = new SpecSession(app, data, null, input);

            DateTime t0 = DateTime.Now;
            string warn = session.Refresh(s => Console.WriteLine("  " + s));
            Console.WriteLine("refresh " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s" + (warn != null ? "\nWARN: " + warn : ""));
            // --type 2.106 | 2.113 — тип спецификации, выбранный вручную (без флага — как сохранено / по числу 3D-моделей)
            int ti = Array.IndexOf(args, "--type");
            if (ti >= 0 && ti + 1 < args.Length) { data.SpecType = args[ti + 1]; SpecLogic.EnsureAssemblyDrawingRow(data); SpecLogic.Renumber(data); }
            // --ondrawing on|off — ГОСТ 2.106 над основной надписью листа чертежа
            int oi = Array.IndexOf(args, "--ondrawing");
            if (oi >= 0 && oi + 1 < args.Length) data.OnDrawingSheet = args[oi + 1] == "on";
            Console.WriteLine("TYPE " + data.EffectiveType() + (string.IsNullOrEmpty(data.SpecType) ? " (по числу 3D-моделей)" : "") +
                              (data.OnDrawing ? " на листе чертежа" : ""));
            if (Array.IndexOf(args, "--checks") >= 0)
            {
                // перенос строки знаком $ и сжатие длинной строки
                SpecRow res = data.Rows.Find(r => r.Name == "Резистор");
                if (res != null)
                {
                    res.Name = "Резистор переменный$проволочный однооборотный с длинным наименованием"; res.MarkEdited("Name");
                    res.Section = Sections.Other; res.MarkEdited("Section");     // ручной перенос в другой раздел
                    SpecLogic.GroupBySection(data); SpecLogic.Renumber(data);
                }
{
                Console.WriteLine(BomCollector.WriteModelProperties(app, data, null));
                Console.WriteLine("DrawingFormat=" + data.DrawingFormat + "  LastFormat: " + (data.DrawingFormat != "" ? (BomCollector.WriteAssemblyFormat(app, data) ?? "записан") : "-"));
            }
            }
            for (int i = 0; i < args.Length - 1; i++)
            {
                // --edit "старое наименование=новое наименование" | --editdes "старое обозначение=новое"
                if (args[i] != "--edit" && args[i] != "--editdes") continue;
                string[] kv = args[i + 1].Split('=');
                foreach (SpecRow r in data.Rows)
                {
                    if (args[i] == "--edit" && r.Name == kv[0]) { r.Name = kv[1]; r.MarkEdited("Name"); }
                    if (args[i] == "--editdes" && r.Designation == kv[0]) { r.Designation = kv[1]; r.MarkEdited("Designation"); }
                }
            }
            if (Array.IndexOf(args, "--write") >= 0)
{
                Console.WriteLine(BomCollector.WriteModelProperties(app, data, null));
                Console.WriteLine("DrawingFormat=" + data.DrawingFormat + "  LastFormat: " + (data.DrawingFormat != "" ? (BomCollector.WriteAssemblyFormat(app, data) ?? "записан") : "-"));
            }
            // --save — кнопка «Сохранить изменения» редактора: правки — в 3D-модели, затем BOM перечитывается
            if (Array.IndexOf(args, "--save") >= 0)
                Console.WriteLine("SAVE: " + (session.SaveToModel(s => Console.WriteLine("  " + s)) ?? "без замечаний"));
            if (Array.IndexOf(args, "--renumber") >= 0)
                Console.WriteLine(BomNumbering.Renumber(app, data, s => Console.WriteLine("  " + s)));
            foreach (Execution e in data.Executions)
                Console.WriteLine("EXEC " + e.Label + " | " + e.Designation + " | " + e.Kind + (e.UserAdded ? " (добавлено)" : "") + " | " + System.IO.Path.GetFileName(e.Key));
            foreach (SpecRow r in data.Rows)
            {
                StringBuilder q = new StringBuilder();
                foreach (Execution e in data.Executions) q.Append((r.GetQty(e.Key) == "" ? "." : r.GetQty(e.Key)) + " ");
                Console.WriteLine("ROW [" + r.Section + "] pos=" + r.Pos + " bom=" + r.BomItem + " | " +
                                  r.Designation + " | " + r.Name + " | " + q + "| " + r.Source);
            }
            List<SpecPage> pages = Layout.Build(data);
            Console.WriteLine("pages=" + pages.Count + " duplicates=" + string.Join(",", SpecLogic.DuplicatePositions(data)));

            if (form)
            {
                EditorForm f = new EditorForm(data, p => null, TestTitle(app, data, dwg));
                f.Show();
                System.Windows.Forms.Application.DoEvents();
                using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(System.IO.Path.ChangeExtension(outPath, ".form.png"));
                }
                foreach (System.Windows.Forms.Control c in f.Controls)
                {
                    System.Windows.Forms.TabControl tc = c as System.Windows.Forms.TabControl;
                    if (tc == null) continue;
                    tc.SelectedIndex = 2;
                    System.Windows.Forms.Application.DoEvents();
                    using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(System.IO.Path.ChangeExtension(outPath, ".title.png"));
                    }
                }
                f.Close();
            }

            if (Array.IndexOf(args, "--save") >= 0)
            {
                // только запись в модели: чертёж не меняется, открытый пользователем — не закрывается
                if (dwg != null && !dwgWasOpen) ((Document)dwg).Close(true);
                Console.WriteLine("OK");
                return 0;
            }

            bool created = dwg == null;
            if (created) dwg = DrawingWriter.CreateDrawing(app);
            if (Array.IndexOf(args, "--title") >= 0)
            {
                bool mo;
                Document model = SpecTitleBlock.ParentModel(app, data, dwg, out mo);
                TitleState ts = SpecTitleBlock.Resolve(model, dwg, data.Settings);
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "--tedit") { string[] kv = args[i + 1].Split(new[] { '=' }, 2); ts.SetByUser(kv[0], kv[1]); }
                Console.WriteLine("TITLE model: " + (SpecTitleBlock.WriteModel(app, data, dwg, ts) ?? "ok"));
                Console.WriteLine("TITLE drawing: " + (SpecTitleBlock.FillDrawing(dwg, model, data.Settings, ts) ?? "без изменений"));
                foreach (TitleField tf in SpecTitleBlock.All) Console.WriteLine("   " + tf.Label + " = " + SpecTitleBlock.Value((Document)dwg, tf) + "  [" + ts.Source(tf.Key) + (ts.Edited.Contains(tf.Key) ? ", исправлено" : "") + "]");
                if (mo && model != null) model.Close(true);
            }
            t0 = DateTime.Now;
            DrawingWriter writer = new DrawingWriter(app, dwg, data) { RemoveEmptySheets = created };
            int n = writer.Write(s => Console.WriteLine("  " + s));
            Console.WriteLine("sheets=" + n + " time=" + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s");
            foreach (string w in writer.Warnings) Console.WriteLine("LAYOUT WARN: " + w);
            List<string> names = new List<string>();
            foreach (Sheet sh in dwg.Sheets) names.Add(sh.Name);
            Console.WriteLine("drawing sheets: " + string.Join(", ", names));
            if (Array.IndexOf(args, "--onsave") >= 0)
            {
                // то, что делает надстройка перед сохранением чертежа
                SpecTitleBlock.CopyFromFirstViewModel(app, dwg);
                foreach (TitleField tf in SpecTitleBlock.All) Console.WriteLine("   ON SAVE " + tf.Label + " = " + SpecTitleBlock.Value((Document)dwg, tf));
            }
            ((Document)dwg).SaveAs(outPath, false);
            ExportPdf(app, dwg, System.IO.Path.ChangeExtension(outPath, ".pdf"));
            SpecData back = DrawingWriter.LoadData(dwg);
            Console.WriteLine("reload rows=" + back.Rows.Count + " execs=" + back.Executions.Count);
            ((Document)dwg).Close(true);
            Console.WriteLine("OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: " + ex);
            return 1;
        }
    }

    // TestRunner.exe --material <деталь.ipt> [имя материала] — новый чертёж с видом детали, свойства материала
    // до и после «-» в SingleMaterial и после удаления вида. Имя материала задаётся переименованием материала
    // детали (деталь закрывается без сохранения). Чертёж не сохраняется.
    static int MaterialTest(Application app, string[] args)
    {
        PartDocument part = (PartDocument)app.Documents.Open(args[1], false);
        try
        {
            if (args.Length > 2) part.ActiveMaterial.DisplayName = args[2];   // материал детали — ресурс самой детали
            Console.WriteLine("MATERIAL " + DrawingMaterial.PartMaterial(part));
            DrawingDocument dwg = DrawingWriter.CreateDrawing(app);
            try
            {
                Sheet sh = dwg.Sheets[1];
                DrawingView view = sh.DrawingViews.AddBaseView((_Document)part, app.TransientGeometry.CreatePoint2d(15, 15), 1,
                    ViewOrientationTypeEnum.kFrontViewOrientation, DrawingViewStyleEnum.kHiddenLineDrawingViewStyle);
                DrawingMaterial.Update(dwg);
                PrintMaterial(dwg, "VIEW");
                ((Document)dwg).PropertySets["{D5CDD505-2E9C-101B-9397-08002B2CF9AE}"][MaterialLogic.Single].Value = "-";
                DrawingMaterial.Update(dwg);
                PrintMaterial(dwg, "MANUAL");
                ((Document)dwg).PropertySets["{D5CDD505-2E9C-101B-9397-08002B2CF9AE}"][MaterialLogic.Single].Value = "";
                view.Delete();
                DrawingMaterial.Update(dwg);
                PrintMaterial(dwg, "NOVIEW");
            }
            finally { ((Document)dwg).Close(true); }
        }
        finally { part.Close(true); }
        Console.WriteLine("OK");
        return 0;
    }

    static void PrintMaterial(DrawingDocument dwg, string tag)
    {
        PropertySet ps = ((Document)dwg).PropertySets["{D5CDD505-2E9C-101B-9397-08002B2CF9AE}"];
        foreach (string n in MaterialLogic.All)
        {
            string v;
            try { v = "«" + ps[n].Value + "»"; } catch { v = "(нет)"; }
            Console.WriteLine(tag + " " + n + " = " + v);
        }
    }

    static TitleState TestTitle(Application app, SpecData data, DrawingDocument dwg)
    {
        bool mo;
        Document model = SpecTitleBlock.ParentModel(app, data, dwg, out mo);
        TitleState ts = SpecTitleBlock.Resolve(model, dwg, data.Settings);
        if (mo && model != null) model.Close(true);
        return ts;
    }

    static void ExportPdf(Application app, DrawingDocument dwg, string pdf)
    {
        TranslatorAddIn tr = (TranslatorAddIn)app.ApplicationAddIns.ItemById["{0AC6FD96-2F4D-42CE-8BE0-8AEA580399E4}"];
        TranslationContext ctx = app.TransientObjects.CreateTranslationContext();
        ctx.Type = IOMechanismEnum.kFileBrowseIOMechanism;
        NameValueMap opts = app.TransientObjects.CreateNameValueMap();
        if (tr.HasSaveCopyAsOptions[dwg, ctx, opts])
        {
            opts.Value["All_Color_AS_Black"] = 1;
            opts.Value["Sheet_Range"] = PrintRangeEnum.kPrintAllSheets;
        }
        DataMedium dm = app.TransientObjects.CreateDataMedium();
        dm.FileName = pdf;
        tr.SaveCopyAs(dwg, ctx, opts, dm);
    }
}
