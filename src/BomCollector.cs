using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Inventor;

namespace GroupSpecB
{
    /// <summary>Чтение исполнений и их BOM из параметрической сборки.</summary>
    public static class BomCollector
    {
        const string DesignTracking = "Design Tracking Properties";
        const string Summary = "Inventor Summary Information";
        const string UserDefined = "Inventor User Defined Properties";

        public static string Prop(PropertySets sets, string setName, string propName)
        {
            try
            {
                object v = sets[setName][propName].Value;
                return v == null ? "" : v.ToString().Trim();
            }
            catch { return ""; }
        }

        public static string UserProp(PropertySets sets, params string[] names)
        {
            foreach (string n in names)
            {
                string v = Prop(sets, UserDefined, n);
                if (v != "") return v;
            }
            return "";
        }

        /// <summary>Наименование: Description или Title — по настройке, с запасным вариантом.</summary>
        public static string NameOf(PropertySets sets, string nameProperty)
        {
            string desc = Prop(sets, DesignTracking, "Description");
            string title = Prop(sets, Summary, "Title");
            if (nameProperty == "Title") return title != "" ? title : desc;
            return desc != "" ? desc : title;
        }

        /// <summary>Пользовательское свойство модели: раздел спецификации, к которому отнесён элемент.</summary>
        public const string SectionProp = "spec_section";

        /// <summary>Записывает в 3D-модели элементов спецификации (выполняется по кнопке «Сформировать»):
        /// пользовательское свойство spec_section — раздел, к которому элемент отнесён (в т. ч. перенесённый
        /// пользователем); обозначение (Part Number), наименование (Description или Title), формат (LastFormat)
        /// и примечание, если их исправили в окне предпросмотра. Для строки, слитой из нескольких файлов, пишется во все файлы. Модель
        /// сохраняется, если в ней не было несохранённых правок. Возвращает отчёт.</summary>
        public static string WriteModelProperties(Application app, SpecData data, Action<string> progress)
        {
            int written = 0, same = 0, unsaved = 0, viaFactory = 0, props = 0;
            List<string> failed = new List<string>();
            List<SpecRow> rows = data.Rows.FindAll(r => r.Source != RowSource.Manual && !r.Hidden && !string.IsNullOrEmpty(r.Key) &&
                                                        Array.IndexOf(Sections.Order, r.Section) >= 0);
            bool title = data.Settings.NameProperty == "Title";
            int k = 0;
            foreach (SpecRow r in rows)
            {
                k++;
                if (progress != null) progress("Запись в модели: " + k + " из " + rows.Count);
                bool desOk = true, nameOk = true, secOk = true, fmtOk = true, noteOk = true;
                foreach (string key in r.AllKeys())
                {
                    if (key.StartsWith("virtual:"))
                    {
                        // свойства виртуального компонента хранятся в сборках исполнений
                        int vProps;
                        string err = WriteVirtual(app, data, r, key, title, out vProps);
                        if (err == null) { if (vProps < 0) same++; else { written++; props += vProps; } }
                        else
                        {
                            failed.Add(Name(r) + " — виртуальный компонент: " + err);
                            desOk = nameOk = secOk = fmtOk = noteOk = false;
                        }
                        continue;
                    }
                    Document d = null;
                    bool opened = false;
                    string step = "открытие файла";
                    try
                    {
                        d = FindOrOpen(app, key, out opened);
                        string wantFile = key.IndexOf('<') > 0 ? key.Substring(0, key.IndexOf('<')) : key;
                        if (!string.Equals(d.FullFileName, wantFile, StringComparison.OrdinalIgnoreCase))
                        {
                            // файл-копия с тем же внутренним именем: Inventor отдаёт уже загруженный оригинал
                            failed.Add(System.IO.Path.GetFileName(wantFile) + " — вместо него Inventor открыл " +
                                       System.IO.Path.GetFileName(d.FullFileName) +
                                       " (файлы-копии с одинаковым внутренним именем); исправьте свойства этого файла вручную");
                            desOk = nameOk = secOk = fmtOk = noteOk = false;
                            continue;
                        }
                        step = "проверка фабрики";
                        string fp = FactoryPath(d);
                        bool modifiable = fp == null;
                        if (modifiable) { step = "проверка доступа"; modifiable = d.IsModifiable; }
                        step = "чтение свойств";
                        List<string[]> changes;
                        try { changes = Changes(d.PropertySets, r, title); }
                        catch (Exception ex)
                        {
                            if (fp == null) throw;
                            // свойства члена iPart / iAssembly Inventor иногда не отдаёт (E_FAIL) — раздел всё равно
                            // пишется в фабрику, остальное члену задаётся её таблицей
                            Log.Write("Свойства члена " + key, ex);
                            changes = Changes(null, r, title);
                        }
                        if (changes.Count == 0) { same++; continue; }

                        if (!modifiable)
                        {
                            // член iPart / iAssembly изменить нельзя: spec_section — в фабрику,
                            // обозначение и наименование члена задаются таблицей фабрики
                            step = "запись в фабрику";
                            foreach (string[] c in changes)
                            {
                                if (c[1] == SectionProp && fp != null)
                                {
                                    string err = WriteToDocument(app, fp, r.Section);
                                    if (err == null) { written++; viaFactory++; } else { failed.Add(Name(r) + " — фабрика: " + err); secOk = false; }
                                }
                                else
                                {
                                    failed.Add(Name(r) + " — " + (fp != null ? "член iPart/iAssembly: «" + c[1] + "» задаётся в таблице фабрики" : "модель только для чтения"));
                                    if (c[1] == "Part Number") desOk = false;
                                    else if (c[1] == SectionProp) secOk = false;
                                    else if (c[1] == "LastFormat") fmtOk = false;
                                    else if (c[1] == "Примечание") noteOk = false;
                                    else nameOk = false;
                                }
                            }
                            continue;
                        }
                        step = "запись свойств";
                        bool wasDirty = d.Dirty;
                        props += Apply(d.PropertySets, changes);
                        written++;
                        if (!wasDirty)
                        {
                            try { d.Save2(false); }
                            catch (Exception ex) { failed.Add(Name(r) + " — записано, но не сохранено: " + ex.Message); }
                        }
                        else unsaved++;
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Запись в модель " + key + " (" + step + ")", ex);
                        failed.Add(Name(r) + " — " + step + ": " + ex.Message);
                        desOk = nameOk = secOk = fmtOk = noteOk = false;
                    }
                    finally { if (opened && d != null) try { d.Close(true); } catch { } }
                }
                // модель теперь совпадает со спецификацией — дальше значения снова берутся из модели
                // (и в редакторе больше не выделяются жирным)
                if (desOk) r.Edited.Remove("Designation");
                if (nameOk) r.Edited.Remove("Name");
                if (secOk) r.Edited.Remove("Section");
                if (fmtOk && Sections.HasFormat(r.Section)) r.Edited.Remove("Format");
                if (noteOk) r.Edited.Remove("Note");
            }
            string rep = "Запись в модели: изменено моделей — " + written +
                         (viaFactory > 0 ? " (в т. ч. фабрик iPart/iAssembly — " + viaFactory + ")" : "") +
                         ", исправлено обозначений/наименований — " + props + ", без изменений — " + same + ".";
            if (unsaved > 0) rep += "\r\nВ " + unsaved + " моделях уже были несохранённые изменения — сохраните их сами.";
            if (failed.Count > 0) rep += "\r\nНе записано:\r\n" + string.Join("\r\n", failed);
            return rep;
        }
        /// <summary>Свойства, которые нужно изменить, чтобы модель совпала со строкой спецификации:
        /// {набор, свойство, значение}.</summary>
        static List<string[]> Changes(PropertySets sets, SpecRow r, bool title)
        {
            List<string[]> changes = new List<string[]>();
            if (UserProp(sets, SectionProp) != r.Section) changes.Add(new[] { UserDefined, SectionProp, r.Section });
            if (r.IsEdited("Designation") && Prop(sets, DesignTracking, "Part Number") != r.Designation)
                changes.Add(new[] { DesignTracking, "Part Number", r.Designation });
            if (r.IsEdited("Name"))
            {
                if (title && Prop(sets, Summary, "Title") != r.Name) changes.Add(new[] { Summary, "Title", r.Name });
                if (!title && Prop(sets, DesignTracking, "Description") != r.Name) changes.Add(new[] { DesignTracking, "Description", r.Name });
            }
            // формат и примечание читаются из пользовательских свойств (ReadBom)
            if (r.IsEdited("Format") && Sections.HasFormat(r.Section) && UserProp(sets, "LastFormat", "Формат") != r.Format)
                changes.Add(new[] { UserDefined, "LastFormat", r.Format });
            if (r.IsEdited("Note") && UserProp(sets, "Примечание") != r.Note)
                changes.Add(new[] { UserDefined, "Примечание", r.Note });
            return changes;
        }

        /// <summary>Записывает свойства; возвращает число исправленных (кроме spec_section).</summary>
        static int Apply(PropertySets sets, List<string[]> changes)
        {
            int n = 0;
            foreach (string[] c in changes)
            {
                PropertySet ps = sets[c[0]];
                Property p = null;
                foreach (Property x in ps) if (x.Name == c[1]) { p = x; break; }
                if (p != null) p.Value = c[2]; else ps.Add(c[2], c[1]);
                if (c[1] != SectionProp) n++;
            }
            return n;
        }

        /// <summary>Записывает свойства виртуального компонента во все сборки включённых исполнений, где он есть
        /// в BOM. Ключ строки содержит обозначение — после его исправления ключ строки обновляется.
        /// props — число исправленных свойств, -1 — менять нечего. Возвращает ошибку или null.</summary>
        static string WriteVirtual(Application app, SpecData data, SpecRow r, string key, bool title, out int props)
        {
            props = -1;
            List<string> files = new List<string>();
            foreach (Execution ex in data.EnabledExecutions())
            {
                string f = ex.Path ?? "";
                if (f.IndexOf('<') > 0) f = f.Substring(0, f.IndexOf('<'));
                if (f != "" && !files.Exists(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase))) files.Add(f);
            }
            bool found = false;
            string newKey = null;
            List<string> errors = new List<string>();
            List<string> done = new List<string>();
            foreach (string execFile in files)
            {
                string file = execFile;
                bool opened = false;
                AssemblyDocument asm = null;
                try
                {
                    asm = OpenAssembly(app, file, out opened);
                    Document doc = (Document)asm;
                    if (asm.ComponentDefinition.IsiAssemblyMember)
                    {
                        // член iAssembly изменить нельзя (E_FAIL), а его копию виртуальных компонентов Inventor
                        // из фабрики не обновляет — пишем в фабрику, оттуда же они и читаются (VirtualProps)
                        string fp = FactoryPath(doc);
                        if (fp == null) throw new Exception("член iAssembly, фабрика не найдена");
                        if (opened) try { doc.Close(true); } catch { }
                        asm = null;
                        file = fp;
                        if (done.Exists(x => string.Equals(x, file, StringComparison.OrdinalIgnoreCase))) continue;
                        asm = OpenAssembly(app, file, out opened);
                        doc = (Document)asm;
                    }
                    if (done.Exists(x => string.Equals(x, file, StringComparison.OrdinalIgnoreCase))) continue;
                    done.Add(file);
                    bool wasDirty = doc.Dirty;
                    int n = WriteVirtualRows(asm, r, key, title, ref found, ref newKey);
                    if (n < 0) continue;
                    if (props < 0) props = 0;
                    props += n;
                    if (wasDirty) errors.Add("записано в " + System.IO.Path.GetFileName(file) + ", но в сборке были несохранённые изменения — сохраните её сами");
                    else asm.Save2(false);
                }
                catch (Exception ex) { errors.Add(System.IO.Path.GetFileName(file) + ": " + ex.Message); }
                finally { if (opened && asm != null) try { ((Document)asm).Close(true); } catch { } }
            }
            if (newKey != null && newKey != key)
            {
                if (r.Key == key) r.Key = newKey;
                else { int i = r.MergedKeys.IndexOf(key); if (i >= 0) r.MergedKeys[i] = newKey; }
            }
            if (!found && errors.Count == 0) errors.Add("не найден в BOM сборок исполнений");
            return errors.Count == 0 ? null : string.Join("; ", errors);
        }

        /// <summary>Строки BOM сборки с этим виртуальным компонентом: записывает свойства. Возвращает число
        /// исправленных свойств, -1 — компонента нет или менять нечего.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int WriteVirtualRows(AssemblyDocument asm, SpecRow r, string key, bool title, ref bool found, ref string newKey)
        {
            BOM bom = asm.ComponentDefinition.BOM;
            if (!bom.StructuredViewEnabled) bom.StructuredViewEnabled = true;
            int n = -1;
            // свойства виртуального компонента — общие для всех состояний модели
            ModelStates ms = null;
            bool scopeSet = false;
            MemberEditScopeEnum oldScope = MemberEditScopeEnum.kEditActiveMember;
            try { ms = asm.ComponentDefinition.ModelStates; if (ms.Count > 1) { oldScope = ms.MemberEditScope; ms.MemberEditScope = MemberEditScopeEnum.kEditAllMembers; scopeSet = true; } } catch { }
            try
            {
                foreach (BOMRow row in StructuredView(bom).BOMRows)
                {
                    if (!string.Equals(RowKey(row), key, StringComparison.OrdinalIgnoreCase)) continue;
                    found = true;
                    foreach (ComponentDefinition def in row.ComponentDefinitions)
                    {
                        if (def.Type != ObjectTypeEnum.kVirtualComponentDefinitionObject) continue;
                        VirtualComponentDefinition vd = (VirtualComponentDefinition)def;
                        List<string[]> changes = Changes(vd.PropertySets, r, title);
                        if (changes.Count == 0) continue;
                        if (n < 0) n = 0;
                        n += Apply(vd.PropertySets, changes);
                    }
                    newKey = RowKey(row);
                }
            }
            finally { if (scopeSet) try { ms.MemberEditScope = oldScope; } catch { } }
            return n;
        }

        /// <summary>Файл фабрики для члена iPart / iAssembly, иначе null.</summary>
        public static string FactoryPath(Document d)
        {
            try
            {
                if (d.DocumentType == DocumentTypeEnum.kPartDocumentObject)
                {
                    PartComponentDefinition pd = ((PartDocument)d).ComponentDefinition;
                    if (pd.IsiPartMember) return pd.iPartMember.ReferencedDocumentDescriptor.FullDocumentName;
                }
                else if (d.DocumentType == DocumentTypeEnum.kAssemblyDocumentObject)
                {
                    AssemblyComponentDefinition ad = ((AssemblyDocument)d).ComponentDefinition;
                    if (ad.IsiAssemblyMember) return ad.iAssemblyMember.ReferencedDocumentDescriptor.FullDocumentName;
                }
            }
            catch { }
            return null;
        }

        public static Document FindOrOpen(Application app, string path, out bool opened)
        {
            opened = false;
            try { Document byName = app.Documents.ItemByName[path]; if (byName != null) return byName; } catch { }
            foreach (Document od in app.Documents)
            {
                // документ, который Inventor в этот момент выгружает, может не отдать имя (E_FAIL) — пропускаем
                try
                {
                    if (string.Equals(od.FullDocumentName, path, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(od.FullFileName, path, StringComparison.OrdinalIgnoreCase)) return od;
                }
                catch { }
            }
            opened = true;
            return app.Documents.Open(path, false);
        }

        /// <summary>Записывает spec_section в документ (фабрику) и сохраняет его, если в нём не было несохранённых правок.</summary>
        static string WriteToDocument(Application app, string path, string section)
        {
            return WriteUserProp(app, path, SectionProp, section);
        }

        /// <summary>Записывает LastFormat «А4» (формат спецификации, как в ESKD Specia) в модели исполнений:
        /// в сам файл сборки, а для членов iAssembly — в фабрику. Возвращает текст ошибок или null.</summary>
        public static string WriteAssemblyFormat(Application app, SpecData data)
        {
            List<string> paths = new List<string>();
            List<string> errors = new List<string>();
            foreach (Execution e in data.EnabledExecutions())
            {
                string p = e.Path;
                if (e.Kind == KindFile)
                {
                    try
                    {
                        bool opened;
                        Document d = FindOrOpen(app, e.Path, out opened);
                        try { p = FactoryPath(d) ?? d.FullFileName; }
                        finally { if (opened) try { d.Close(true); } catch { } }
                    }
                    catch (Exception ex) { errors.Add(System.IO.Path.GetFileName(e.Path) + ": " + ex.Message); continue; }
                }
                if (p.IndexOf('<') > 0) p = p.Substring(0, p.IndexOf('<'));
                if (!paths.Exists(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) paths.Add(p);
            }
            foreach (string p in paths)
            {
                string err = WriteUserProp(app, p, "LastFormat", SpecFormat);
                if (err != null) errors.Add(System.IO.Path.GetFileName(p) + ": " + err);
            }
            return errors.Count == 0 ? null : "LastFormat не записан:\r\n" + string.Join("\r\n", errors);
        }

        /// <summary>Формат спецификации — LastFormat сборки (как в ESKD Specia).</summary>
        public const string SpecFormat = "А4";

        /// <summary>Формат листов чертежа для LastFormat, как в ESKD Specia (SaveFormatSize.GetSheetFormat): формат
        /// первого листа, а если листы разного формата или формат нестандартный — «*)». Листы, исключённые из
        /// нумерации, и листы спецификации надстройки не учитываются; «» — таких листов нет.</summary>
        public static string DrawingSheetsFormat(DrawingDocument dwg)
        {
            string fmt = null;
            foreach (Sheet sh in dwg.Sheets)
            {
                if (DrawingWriter.IsSpecSheet(sh) || sh.ExcludeFromCount) continue;
                string f = DrawingWriter.SheetFormat(sh);
                if (f == "" || (fmt != null && f != fmt)) return "*)";
                fmt = f;
            }
            return fmt ?? "";
        }

        /// <summary>При сохранении чертежа, как в ESKD Specia (SaveFormatSize): LastFormat 3D-модели первого вида —
        /// формат листов чертежа (см. DrawingSheetsFormat), для сборки — «А4» (формат спецификации); для члена
        /// iPart/iAssembly — в фабрику. Совпадающее значение не перезаписывается. Возвращает текст ошибки или null.</summary>
        public static string WriteDrawingFormat(Application app, DrawingDocument dwg)
        {
            Document model = SpecTitleBlock.FirstViewModel(dwg);
            if (model == null) return null;
            string fmt = model.DocumentType == DocumentTypeEnum.kAssemblyDocumentObject ? SpecFormat : DrawingSheetsFormat(dwg);
            if (fmt == "") return null;
            string p = FactoryPath(model) ?? model.FullFileName;
            if (p.IndexOf('<') > 0) p = p.Substring(0, p.IndexOf('<'));
            string err = WriteUserProp(app, p, "LastFormat", fmt);
            return err == null ? null : "LastFormat не записан в " + System.IO.Path.GetFileName(p) + ": " + err;
        }

        /// <summary>Записывает пользовательское свойство и сохраняет документ, если в нём не было несохранённых правок.</summary>
        static string WriteUserProp(Application app, string path, string propName, string value)
        {
            bool opened;
            Document d = null;
            try
            {
                d = FindOrOpen(app, path, out opened);
                try
                {
                    if (!d.IsModifiable) return "только для чтения";
                    PropertySet ps = d.PropertySets[UserDefined];
                    Property p = null;
                    foreach (Property x in ps) if (x.Name == propName) { p = x; break; }
                    if (p != null && (p.Value ?? "").ToString() == value) return null;
                    bool wasDirty = d.Dirty;
                    if (p != null) p.Value = value; else ps.Add(value, propName);
                    if (!wasDirty) d.Save2(false);
                    return null;
                }
                finally { if (opened) try { d.Close(true); } catch { } }
            }
            catch (Exception ex) { return ex.Message; }
        }

        static Application readApp;
        static Dictionary<string, string> factorySectionCache;

        /// <summary>spec_section фабрики (для членов iPart / iAssembly, у которых своего значения нет).</summary>
        static string FactorySection(Document member)
        {
            if (readApp == null) return "";
            string fp = FactoryPath(member);
            if (fp == null) return "";
            string v;
            if (factorySectionCache.TryGetValue(fp, out v)) return v;
            v = "";
            try
            {
                bool opened;
                Document f = FindOrOpen(readApp, fp, out opened);
                try { v = UserProp(f.PropertySets, SectionProp); }
                finally { if (opened) try { f.Close(true); } catch { } }
            }
            catch (Exception ex) { Log.Write("spec_section фабрики", ex); }
            factorySectionCache[fp] = v;
            return v;
        }

        static string Name(SpecRow r)
        {
            return (r.Designation != "" ? r.Designation : System.IO.Path.GetFileName(r.Key));
        }

        public const string KindRow = "Row";       // строка таблицы iAssembly
        public const string KindState = "State";   // состояние модели
        public const string KindFile = "File";     // отдельная модель исполнения (из вида чертежа или добавленная вручную)

        /// <summary>Исполнения параметрической сборки: строки iAssembly, иначе состояния модели,
        /// иначе сама сборка как единственное исполнение.</summary>
        public static List<Execution> DiscoverFromAssembly(AssemblyDocument asm)
        {
            AssemblyComponentDefinition cd = asm.ComponentDefinition;
            string path = asm.FullFileName;
            List<Execution> res = new List<Execution>();
            if (cd.IsiAssemblyFactory)
            {
                iAssemblyFactory f = cd.iAssemblyFactory;
                int pnCol = FindColumn(f, "Part Number", "Обозначение");
                for (int r = 1; r <= f.TableRows.Count; r++)
                {
                    iAssemblyTableRow row = f.TableRows[r];
                    Execution e = new Execution { Kind = KindRow, Path = path, Member = row.MemberName, Key = row.MemberName };
                    e.Designation = pnCol > 0 ? CellValue(row, pnCol) : "";
                    res.Add(e);
                }
                return res;
            }
            try
            {
                ModelStates ms = cd.ModelStates;
                for (int i = 1; i <= ms.Count; i++)
                {
                    ModelState s = ms[i];
                    if (s.ModelStateType == ModelStateTypeEnum.kSubstituteModelStateType) continue;
                    res.Add(new Execution { Kind = KindState, Path = path, Member = s.Name, Key = s.Name });
                }
                if (res.Count >= 2) return res;
            }
            catch { }
            res.Clear();
            res.Add(new Execution { Kind = KindFile, Path = asm.FullDocumentName, Key = asm.FullDocumentName,
                                    Designation = Prop(asm.PropertySets, DesignTracking, "Part Number") });
            return res;
        }

        /// <summary>Все сборки, показанные в видах на листах чертежа. Каждая — отдельное исполнение;
        /// номер исполнения определяется по обозначению (Part Number). Листы, для которых skip вернёт true
        /// (листы самой спецификации), пропускаются.</summary>
        public static List<Execution> DiscoverFromDrawing(DrawingDocument dwg, Func<Sheet, bool> skip, List<string> notes)
        {
            List<Execution> res = new List<Execution>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            int parts = 0;
            foreach (Sheet sh in dwg.Sheets)
            {
                if (skip != null && skip(sh)) continue;
                foreach (DrawingView v in sh.DrawingViews)
                {
                    Document d;
                    try { d = (Document)v.ReferencedDocumentDescriptor.ReferencedDocument; }
                    catch { continue; }
                    if (d == null) continue;
                    if (d.DocumentType != DocumentTypeEnum.kAssemblyDocumentObject)
                    {
                        if (d.DocumentType == DocumentTypeEnum.kPartDocumentObject) parts++;
                        continue;
                    }
                    string key = d.FullDocumentName;
                    if (seen.ContainsKey(key)) continue;
                    seen[key] = true;
                    res.Add(new Execution { Kind = KindFile, Path = key, Key = key,
                                            Designation = Prop(d.PropertySets, DesignTracking, "Part Number") });
                }
            }
            if (notes != null && parts > 0)
                notes.Add("Виды деталей (" + parts + ") пропущены: исполнения групповой спецификации — сборки.");
            return res;
        }

        /// <summary>Читает BOM каждого исполнения. Состояние сборок (строка iAssembly, состояние модели,
        /// признак изменения) восстанавливается после чтения; открытые надстройкой документы закрываются.</summary>
        public static List<ExecutionBom> CollectAll(Application app, List<Execution> defs, SpecSettings settings,
                                                    Action<string> progress, List<string> errors)
        {
            readApp = app;
            factorySectionCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<ExecutionBom> result = new List<ExecutionBom>();
            Dictionary<Execution, ExecutionBom> byExec = new Dictionary<Execution, ExecutionBom>();
            // строки iAssembly и состояния модели — пакетами по файлу фабрики
            Dictionary<string, List<Execution>> groups = new Dictionary<string, List<Execution>>(StringComparer.OrdinalIgnoreCase);
            List<string> order = new List<string>();
            foreach (Execution e in defs)
            {
                if (e.Kind != KindRow && e.Kind != KindState) continue;
                List<Execution> g;
                if (!groups.TryGetValue(e.Path, out g)) { g = new List<Execution>(); groups[e.Path] = g; order.Add(e.Path); }
                g.Add(e);
            }
            int done = 0, total = defs.Count;
            foreach (string path in order)
            {
                bool opened;
                AssemblyDocument asm;
                try { asm = OpenAssembly(app, path, out opened); }
                catch (Exception ex) { errors.Add(System.IO.Path.GetFileName(path) + ": " + ex.Message); continue; }
                try
                {
                    foreach (ExecutionBom eb in ReadVariants(asm, groups[path], settings, progress, ref done, total, errors))
                        byExec[eb.Execution] = eb;
                }
                finally { if (opened) try { ((Document)asm).Close(true); } catch { } }
            }
            // отдельные модели исполнений
            foreach (Execution e in defs)
            {
                if (e.Kind == KindRow || e.Kind == KindState) continue;
                done++;
                if (progress != null) progress("Исполнение " + done + " из " + total + ": " + System.IO.Path.GetFileName(e.Path));
                bool opened;
                AssemblyDocument asm;
                try { asm = OpenAssembly(app, e.Path, out opened); }
                catch (Exception ex) { errors.Add(System.IO.Path.GetFileName(e.Path) + ": " + ex.Message); continue; }
                try
                {
                    string pn = Prop(asm.PropertySets, DesignTracking, "Part Number");
                    if (pn != "") e.Designation = pn;
                    byExec[e] = ReadBomSafe(asm, e, settings);
                }
                catch (Exception ex) { errors.Add(System.IO.Path.GetFileName(e.Path) + ": " + ex.Message); }
                finally { if (opened) try { ((Document)asm).Close(true); } catch { } }
            }
            foreach (Execution e in defs)
            {
                ExecutionBom eb;
                if (byExec.TryGetValue(e, out eb)) result.Add(eb);
            }
            return result;
        }

        static List<ExecutionBom> ReadVariants(AssemblyDocument asm, List<Execution> execs, SpecSettings settings,
                                               Action<string> progress, ref int done, int total, List<string> errors)
        {
            List<ExecutionBom> res = new List<ExecutionBom>();
            AssemblyComponentDefinition cd = asm.ComponentDefinition;
            bool wasDirty = asm.Dirty;
            iAssemblyFactory factory = cd.IsiAssemblyFactory ? cd.iAssemblyFactory : null;
            iAssemblyTableRow originalRow = factory != null ? factory.DefaultRow : null;
            ModelState originalState = null;
            try { if (factory == null) originalState = cd.ModelStates.ActiveModelState; } catch { }
            try
            {
                foreach (Execution e in execs)
                {
                    done++;
                    if (progress != null) progress("Исполнение " + done + " из " + total + ": " + e.Member);
                    try
                    {
                        ReleaseBomReferences();
                        if (e.Kind == KindRow)
                        {
                            if (factory == null) throw new Exception("сборка больше не является фабрикой iAssembly");
                            iAssemblyTableRow row = FindRow(factory, e.Member);
                            if (row == null) throw new Exception("нет строки «" + e.Member + "» в таблице iAssembly");
                            factory.DefaultRow = row;
                            int pnCol = FindColumn(factory, "Part Number", "Обозначение");
                            string pn = pnCol > 0 ? CellValue(row, pnCol) : "";
                            e.Designation = pn != "" ? pn : Prop(asm.PropertySets, DesignTracking, "Part Number");
                        }
                        else
                        {
                            ModelState s = cd.ModelStates[e.Member];
                            s.Activate();
                        }
                        asm.Update();
                        if (e.Kind == KindState) e.Designation = Prop(asm.PropertySets, DesignTracking, "Part Number");
                        res.Add(ReadBomSafe(asm, e, settings));
                    }
                    catch (Exception ex) { errors.Add(e.Member + ": " + ex.Message); }
                }
            }
            finally
            {
                ReleaseBomReferences();
                try
                {
                    if (originalRow != null) { factory.DefaultRow = originalRow; asm.Update(); }
                    if (originalState != null) originalState.Activate();
                }
                catch (Exception ex) { Log.Write("Возврат исходной строки iAssembly / состояния модели", ex); }
                try { if (!wasDirty) asm.Dirty = false; } catch { }
            }
            return res;
        }

        static iAssemblyTableRow FindRow(iAssemblyFactory f, string member)
        {
            for (int r = 1; r <= f.TableRows.Count; r++)
                if (f.TableRows[r].MemberName == member) return f.TableRows[r];
            return null;
        }

        /// <summary>Читает BOM, при необходимости включив структурированный вид (признак изменения документа не меняется).</summary>
        static ExecutionBom ReadBomSafe(AssemblyDocument asm, Execution e, SpecSettings settings)
        {
            bool wasDirty = asm.Dirty;
            BOM bom = asm.ComponentDefinition.BOM;
            if (!bom.StructuredViewEnabled)
            {
                try { bom.StructuredViewEnabled = true; }
                catch { throw new Exception("в BOM не включён структурированный вид, а документ нельзя изменить"); }
            }
            try { return ReadBom(asm, e, settings); }
            finally { try { if (!wasDirty) asm.Dirty = false; } catch { } }
        }

        /// <summary>Открытый документ по полному имени (с состоянием модели) или открытие невидимо.</summary>
        public static AssemblyDocument OpenAssembly(Application app, string fullDocumentName, out bool opened)
        {
            opened = false;
            foreach (Document d in app.Documents)
            {
                if (d.DocumentType != DocumentTypeEnum.kAssemblyDocumentObject) continue;
                if (string.Equals(d.FullDocumentName, fullDocumentName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.FullFileName, fullDocumentName, StringComparison.OrdinalIgnoreCase))
                    return (AssemblyDocument)d;
            }
            opened = true;
            return (AssemblyDocument)app.Documents.Open(fullDocumentName, false);
        }
        /// <summary>Пока в .NET живы обёртки над BOMView/BOMRow, Inventor не пересчитывает BOM после смены
        /// строки iAssembly или состояния модели — количества «застывают» на первом прочитанном исполнении.
        /// Поэтому перед каждым переключением обёртки освобождаются.</summary>
        public static void ReleaseBomReferences()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        static int FindColumn(iAssemblyFactory f, params string[] names)
        {
            for (int c = 1; c <= f.TableColumns.Count; c++)
            {
                string h = f.TableColumns[c].Heading ?? "";
                foreach (string n in names)
                    if (h.StartsWith(n, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return 0;
        }

        static string CellValue(iAssemblyTableRow row, int col)
        {
            try { return (row[col].Value ?? "").ToString().Trim(); }
            catch { return ""; }
        }

        static BOMView StructuredView(BOM bom)
        {
            foreach (BOMView v in bom.BOMViews)
                if (v.ViewType == BOMViewTypeEnum.kStructuredBOMViewType) return v;
            throw new Exception("В сборке нет структурированного вида BOM.");
        }

        static readonly Regex QtyRx = new Regex(@"^\s*([0-9]+(?:[.,][0-9]+)?)\s*(.*?)\s*$");

        public static ExecutionBom ReadBom(AssemblyDocument asm, Execution ex, SpecSettings settings)
        {
            ExecutionBom eb = new ExecutionBom();
            eb.Execution = ex;
            BOMView view = StructuredView(asm.ComponentDefinition.BOM);
            foreach (BOMRow row in view.BOMRows)
            {
                if (row.BOMStructure == BOMStructureEnum.kReferenceBOMStructure) continue;
                string total = row.TotalQuantity ?? "";
                Match m = QtyRx.Match(total);
                string qty, unit;
                if (m.Success) { qty = m.Groups[1].Value; unit = m.Groups[2].Value; }
                else { qty = row.ItemQuantity.ToString(); unit = ""; }
                double q;
                if (double.TryParse(qty.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out q) && q == 0) continue;
                if (IsEachUnit(unit)) unit = "";

                ComponentDefinition def = row.ComponentDefinitions[1];
                BomItem it = new BomItem();
                it.ItemNumber = row.ItemNumber ?? "";
                it.Qty = qty;
                it.Unit = unit;
                PropertySets ps;
                bool isAsm = false, isContent = false, isVirtual = false;
                Document compDoc = null;
                if (def.Type == ObjectTypeEnum.kVirtualComponentDefinitionObject)
                {
                    VirtualComponentDefinition vd = (VirtualComponentDefinition)def;
                    ps = VirtualProps(vd);
                    isVirtual = true;
                    it.Key = "virtual:" + Prop(ps, DesignTracking, "Part Number") + "|" + vd.DisplayName;
                }
                else
                {
                    Document d = (Document)def.Document;
                    compDoc = d;
                    ps = d.PropertySets;
                    it.Key = d.FullDocumentName;
                    isAsm = d.DocumentType == DocumentTypeEnum.kAssemblyDocumentObject;
                    if (!isAsm)
                    {
                        try { isContent = ((PartComponentDefinition)def).IsContentMember; } catch { }
                        if (isContent && Prop(ps, "ContentCenter", "IsCustomPart") == "1") isContent = false;
                        if (d.DocumentType == DocumentTypeEnum.kPartDocumentObject) it.Material = DrawingMaterial.PartMaterial((PartDocument)d) ?? "";
                    }
                }
                it.Designation = Prop(ps, DesignTracking, "Part Number");
                it.Name = NameOf(ps, settings.NameProperty);
                it.Format = UserProp(ps, "LastFormat", "Формат");
                it.Note = UserProp(ps, "Примечание");

                // для членов iPart / iAssembly действует значение фабрики (туда пишет надстройка),
                // своя копия у члена может быть устаревшей
                string sec = compDoc != null ? FactorySection(compDoc) : "";
                if (Array.IndexOf(Sections.Order, sec) < 0) sec = UserProp(ps, SectionProp, "Раздел спецификации", "Раздел СП", "Раздел");
                if (Array.IndexOf(Sections.Order, sec) < 0) sec = "";
                if (sec == "")
                {
                    // единица измерения проверяется первой: у материалов в наименовании почти всегда есть
                    // ГОСТ («Лист Б-ПН-2 ГОСТ 19903-2015», м²), но это не стандартные изделия
                    if (unit != "") sec = Sections.Materials;
                    else if (isContent || IsStandardByName(it.Name, it.Designation)) sec = Sections.Standard;
                    else if (row.BOMStructure == BOMStructureEnum.kPurchasedBOMStructure) sec = Sections.Other;
                    else if (isVirtual) sec = Sections.Other;
                    else if (isAsm) sec = Sections.Assemblies;
                    else sec = Sections.Parts;
                }
                it.Section = sec;
                eb.Items.Add(it);
            }
            return eb;
        }

        /// <summary>Ключ строки BOM — тот же, что у строки спецификации (полное имя документа компонента
        /// или «virtual:обозначение|имя» для виртуального компонента).</summary>
        public static string RowKey(BOMRow row)
        {
            try
            {
                ComponentDefinition def = row.ComponentDefinitions[1];
                if (def.Type == ObjectTypeEnum.kVirtualComponentDefinitionObject)
                {
                    VirtualComponentDefinition vd = (VirtualComponentDefinition)def;
                    return "virtual:" + Prop(VirtualProps(vd), DesignTracking, "Part Number") + "|" + vd.DisplayName;
                }
                return ((Document)def.Document).FullDocumentName;
            }
            catch { return null; }
        }

        /// <summary>Свойства виртуального компонента. У члена iAssembly это копия из фабрики, которую Inventor
        /// не даёт изменить и при правке фабрики не обновляет, — действуют свойства одноимённого компонента
        /// фабрики (туда пишет надстройка).</summary>
        public static PropertySets VirtualProps(VirtualComponentDefinition vd)
        {
            try
            {
                AssemblyDocument owner = vd.Document as AssemblyDocument;
                if (owner != null && owner.ComponentDefinition.IsiAssemblyMember)
                {
                    AssemblyComponentDefinition fcd = (AssemblyComponentDefinition)owner.ComponentDefinition.iAssemblyMember.ParentFactory.Parent;
                    foreach (ComponentOccurrence o in fcd.Occurrences)
                    {
                        if (o.Definition.Type != ObjectTypeEnum.kVirtualComponentDefinitionObject) continue;
                        VirtualComponentDefinition fv = (VirtualComponentDefinition)o.Definition;
                        if (fv.DisplayName == vd.DisplayName) return fv.PropertySets;
                    }
                }
            }
            catch (Exception ex) { Log.Write("Виртуальный компонент " + vd.DisplayName + " в фабрике iAssembly", ex); }
            return vd.PropertySets;
        }

        static readonly Regex StandardRx = new Regex(@"(?<![\p{L}\p{Nd}])(ГОСТ|ИСО|ISO|DIN)(?![\p{L}])",
                                                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Стандартное изделие по наименованию или обозначению: есть слово ГОСТ, ИСО, ISO или DIN
        /// (например, «Болт М10×40 ГОСТ 7798-70», «DIN 6921-M8x20»).</summary>
        public static bool IsStandardByName(string name, string designation)
        {
            return StandardRx.IsMatch(name ?? "") || StandardRx.IsMatch(designation ?? "");
        }

        static bool IsEachUnit(string u)
        {
            if (string.IsNullOrEmpty(u)) return true;
            string s = u.Trim().ToLowerInvariant().TrimEnd('.');
            return s == "шт" || s == "each" || s == "ea" || s == "штука" || s == "штуки";
        }
    }
}
