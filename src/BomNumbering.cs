using System;
using System.Collections.Generic;
using Inventor;
using Application = Inventor.Application;

namespace GroupSpecB
{
    /// <summary>Перенумерация позиций в BOM родительских сборок по групповой спецификации:
    /// номер позиции BOM (структурированный вид) = номер позиции в спецификации.
    /// Выноски на чертежах берут номера из BOM и обновляются вслед за ним.</summary>
    public static class BomNumbering
    {
        class Target
        {
            public string Path;                                  // файл сборки, BOM которой меняется
            public List<string> States = new List<string>();     // состояния модели, которые надо обойти
            public List<string> MemberRows = new List<string>(); // члены iAssembly для регенерации после правки фабрики
            public List<string> Executions = new List<string>(); // для отчёта
        }

        /// <summary>Перенумеровывает BOM сборок всех включённых исполнений. Возвращает отчёт.</summary>
        public static string Renumber(Application app, SpecData data, Action<string> progress)
        {
            Dictionary<string, string> posByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (SpecRow r in data.Rows)
            {
                if (r.Hidden || r.Source == RowSource.Manual || string.IsNullOrEmpty(r.Pos) || string.IsNullOrEmpty(r.Key)) continue;
                foreach (string k in r.AllKeys()) posByKey[k] = r.Pos;   // слитые файлы получают одну позицию
            }
            List<string> notes = new List<string>();
            List<Target> targets = CollectTargets(app, data, notes);
            int changedRows = 0, docs = 0;
            foreach (Target t in targets)
            {
                if (progress != null) progress("Перенумерация BOM: " + System.IO.Path.GetFileName(t.Path));
                try
                {
                    int n = RenumberTarget(app, t, posByKey, notes);
                    changedRows += n;
                    docs++;
                }
                catch (Exception ex) { notes.Add(System.IO.Path.GetFileName(t.Path) + ": " + ex.Message); }
            }
            string rep = "Позиции BOM: перенумеровано сборок — " + docs + ", строк BOM с номером из спецификации — " + changedRows + ".";
            if (notes.Count > 0) rep += "\r\n" + string.Join("\r\n", notes);
            return rep;
        }

        /// <summary>Какие сборки править: фабрику iAssembly (для строк таблицы и её членов),
        /// сборку с состояниями модели, отдельную сборку исполнения.</summary>
        static List<Target> CollectTargets(Application app, SpecData data, List<string> notes)
        {
            Dictionary<string, Target> byPath = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
            List<Target> order = new List<Target>();
            Func<string, Target> get = p =>
            {
                Target t;
                if (!byPath.TryGetValue(p, out t)) { t = new Target { Path = p }; byPath[p] = t; order.Add(t); }
                return t;
            };
            foreach (Execution e in data.EnabledExecutions())
            {
                string label = e.Label + " " + e.Designation;
                if (e.Kind == BomCollector.KindRow)
                {
                    Target t = get(e.Path);
                    t.MemberRows.Add(e.Member);
                    t.Executions.Add(label);
                    continue;
                }
                if (e.Kind == BomCollector.KindState)
                {
                    Target t = get(e.Path);
                    t.States.Add(e.Member);
                    t.Executions.Add(label);
                    continue;
                }
                // отдельная модель: член iAssembly → правится фабрика; состояние модели «файл<состояние>»
                bool opened;
                Document d;
                try { d = (Document)BomCollector.OpenAssembly(app, e.Path, out opened); }
                catch (Exception ex) { notes.Add(label + ": " + ex.Message); continue; }
                try
                {
                    AssemblyComponentDefinition cd = ((AssemblyDocument)d).ComponentDefinition;
                    if (cd.IsiAssemblyMember)
                    {
                        string fp = cd.iAssemblyMember.ReferencedDocumentDescriptor.FullDocumentName;
                        Target t = get(fp);
                        t.MemberRows.Add(cd.iAssemblyMember.Row.MemberName);
                        t.Executions.Add(label);
                    }
                    else
                    {
                        string path = d.FullFileName;
                        Target t = get(path);
                        int lt = e.Path.IndexOf('<');
                        if (lt > 0 && e.Path.EndsWith(">")) t.States.Add(e.Path.Substring(lt + 1, e.Path.Length - lt - 2));
                        t.Executions.Add(label);
                    }
                }
                finally { if (opened) try { d.Close(true); } catch { } }
            }
            return order;
        }

        static int RenumberTarget(Application app, Target t, Dictionary<string, string> posByKey, List<string> notes)
        {
            bool opened;
            AssemblyDocument asm = BomCollector.OpenAssembly(app, t.Path, out opened);
            int numbered = 0;
            try
            {
                Document doc = (Document)asm;
                if (!doc.IsModifiable) throw new Exception("сборка только для чтения — номера позиций не изменены");
                bool wasDirty = doc.Dirty;
                AssemblyComponentDefinition cd = asm.ComponentDefinition;
                BOM bom = cd.BOM;
                if (!bom.StructuredViewEnabled) bom.StructuredViewEnabled = true;

                ModelStates ms = null;
                ModelState activeState = null;
                if (t.States.Count > 0)
                {
                    ms = cd.ModelStates;
                    activeState = ms.ActiveModelState;
                    try { ms.MemberEditScope = MemberEditScopeEnum.kEditAllMembers; } catch { }
                }
                Transaction tr = app.TransactionManager.StartTransaction((_Document)asm, "Позиции по групповой спецификации");
                try
                {
                    Dictionary<string, string> assigned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (t.States.Count > 0)
                    {
                        foreach (string s in t.States)
                        {
                            BomCollector.ReleaseBomReferences();
                            ms[s].Activate();
                            asm.Update();
                            numbered = Math.Max(numbered, NumberView(bom, posByKey, assigned, notes, System.IO.Path.GetFileName(t.Path) + " <" + s + ">"));
                        }
                    }
                    else numbered = NumberView(bom, posByKey, assigned, notes, System.IO.Path.GetFileName(t.Path));
                    tr.End();
                }
                catch { tr.Abort(); throw; }
                finally
                {
                    if (activeState != null) try { BomCollector.ReleaseBomReferences(); activeState.Activate(); } catch { }
                }

                if (wasDirty)
                {
                    notes.Add(System.IO.Path.GetFileName(t.Path) + ": в сборке были несохранённые изменения — номера записаны, сохраните её сами.");
                    return numbered;
                }
                asm.Save2(false);

                // члены iAssembly — пересоздать, чтобы получили новые номера
                if (cd.IsiAssemblyFactory && t.MemberRows.Count > 0)
                {
                    iAssemblyFactory f = cd.iAssemblyFactory;
                    List<string> failed = new List<string>();
                    foreach (string m in t.MemberRows)
                    {
                        iAssemblyTableRow row = null;
                        for (int i = 1; i <= f.TableRows.Count; i++) if (f.TableRows[i].MemberName == m) row = f.TableRows[i];
                        if (row == null) continue;
                        try { f.CreateMember(row); }
                        catch (Exception ex) { failed.Add(m + " (" + ex.Message + ")"); }
                    }
                    if (failed.Count > 0) notes.Add("Не пересозданы члены iAssembly: " + string.Join(", ", failed));
                }
            }
            finally { if (opened) try { ((Document)asm).Close(true); } catch { } }
            return numbered;
        }

        /// <summary>Номера строк структурированного BOM: строкам из спецификации — их позиция (с блокировкой),
        /// остальным — следующие свободные номера после наибольшего. Два прохода, чтобы не было
        /// конфликтов номеров при перестановке.</summary>
        static int NumberView(BOM bom, Dictionary<string, string> posByKey, Dictionary<string, string> assigned,
                              List<string> notes, string where)
        {
            BOMView view = null;
            foreach (BOMView v in bom.BOMViews) if (v.ViewType == BOMViewTypeEnum.kStructuredBOMViewType) view = v;
            if (view == null) throw new Exception("нет структурированного вида BOM");

            List<BOMRow> rows = new List<BOMRow>();
            List<string> desired = new List<string>();
            int max = 0;
            foreach (BOMRow r in view.BOMRows)
            {
                rows.Add(r);
                string key = BomCollector.RowKey(r);
                string pos = null;
                if (key != null) posByKey.TryGetValue(key, out pos);
                desired.Add(pos);
                int p;
                if (pos != null && int.TryParse(pos, out p)) max = Math.Max(max, p);
            }
            // проход 1: снять блокировку и развести номера во временный диапазон
            for (int i = 0; i < rows.Count; i++)
            {
                try { if (rows[i].ItemNumberLocked) rows[i].ItemNumberLocked = false; } catch { }
                rows[i].ItemNumber = "~" + (i + 1);
            }
            // проход 2: окончательные номера
            int next = max, count = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                string pos = desired[i];
                if (pos != null)
                {
                    try
                    {
                        rows[i].ItemNumber = pos;
                        try { rows[i].ItemNumberLocked = true; } catch { }
                        count++;
                        continue;
                    }
                    catch
                    {
                        // тот же компонент в двух строках BOM — второй строке номер не достанется
                        notes.Add(where + ": номер " + pos + " не назначен второй строке BOM того же компонента");
                    }
                    next++;
                    rows[i].ItemNumber = next.ToString();
                }
                else
                {
                    next++;
                    rows[i].ItemNumber = next.ToString();
                }
            }
            return count;
        }
    }
}
