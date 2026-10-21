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
            string input = args[0], outPath = args[1];
            bool form = Array.IndexOf(args, "--form") >= 0;
            SpecData data = new SpecData();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--add")
                    data.Executions.Add(new Execution { Kind = BomCollector.KindFile, Path = args[i + 1], Key = args[i + 1], UserAdded = true });

            DrawingDocument dwg = null;
            SpecSession session;
            if (input.EndsWith(".idw", StringComparison.OrdinalIgnoreCase))
            {
                dwg = (DrawingDocument)app.Documents.Open(input, true);
                SpecData saved = DrawingWriter.LoadData(dwg);
                if (saved != null) { saved.Executions.AddRange(data.Executions); data = saved; }
                session = new SpecSession(app, data, dwg, null);
            }
            else session = new SpecSession(app, data, null, input);

            DateTime t0 = DateTime.Now;
            string warn = session.Refresh(s => Console.WriteLine("  " + s));
            Console.WriteLine("refresh " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s" + (warn != null ? "\nWARN: " + warn : ""));
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
            int n = new DrawingWriter(app, dwg, data) { RemoveEmptySheets = created }.Write(s => Console.WriteLine("  " + s));
            Console.WriteLine("sheets=" + n + " time=" + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s");
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
