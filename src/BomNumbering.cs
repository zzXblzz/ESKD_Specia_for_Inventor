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
            Dictionary<string, string> posMap;
            return Renumber(app, data, progress, out posMap);
        }

        /// <summary>То же; posMap — какие номера позиций BOM поменялись (старый → новый), для ссылок «поз.»
        /// на листах чертежа. Если в разных сборках один старый номер получил разные новые, берётся
        /// первая сборка (базовое исполнение), расхождение — в отчёте.</summary>
        public static string Renumber(Application app, SpecData data, Action<string> progress, out Dictionary<string, string> posMap)
        {
            map = new Dictionary<string, string>();
            mapConflicts = new Dictionary<string, List<string>>();
            try { return RenumberAll(app, data, progress); }
            finally
            {
                posMap = new Dictionary<string, string>();
                foreach (KeyValuePair<string, string> p in map) if (p.Key != p.Value) posMap[p.Key] = p.Value;
                map = null; mapConflicts = null;
            }
        }

        // старый номер → новый за время Renumber (метод вызывается из одного потока — команды Inventor)
        static Dictionary<string, string> map;
        static Dictionary<string, List<string>> mapConflicts;
        static bool record = true;

        static string RenumberAll(Application app, SpecData data, Action<string> progress)
        {
            Dictionary<string, string> posByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (SpecRow r in data.Rows)
            {
                if (r.Hidden || r.Source == RowSource.Manual || string.IsNullOrEmpty(r.Pos) || string.IsNullOrEmpty(r.Key)) continue;
                foreach (string k in r.AllKeys()) posByKey[k] = r.Pos;   // слитые файлы получают одну позицию
            }
            List<string> notes = new List<string>();
            List<Target> targets = CollectTargets(app, data, notes);
            int changedRows = 0, docs = 0, unchanged = 0;
            foreach (Target t in targets)
            {
                if (progress != null) progress("Перенумерация BOM: " + System.IO.Path.GetFileName(t.Path));
                try
                {
                    bool changed;
                    int n = RenumberTarget(app, t, posByKey, notes, out changed);
                    changedRows += n;
                    if (changed) docs++; else unchanged++;
                }
                catch (Exception ex) { notes.Add(System.IO.Path.GetFileName(t.Path) + ": " + ex.Message); }
            }
            string rep = "Позиции BOM: перенумеровано сборок — " + docs +
                         (unchanged > 0 ? ", уже совпадали — " + unchanged : "") +
                         ", строк BOM с номером из спецификации — " + changedRows + ".";
            foreach (KeyValuePair<string, List<string>> c in mapConflicts)
                notes.Add("Номер позиции " + c.Key + " в разных сборках стал разным (" + string.Join(", ", c.Value) +
                          ") — в ссылках «поз.» на листах взят " + map[c.Key] + ".");
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

        static int RenumberTarget(Application app, Target t, Dictionary<string, string> posByKey, List<string> notes, out bool changed)
        {
            changed = false;
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
                if (!bom.StructuredViewEnabled) { bom.StructuredViewEnabled = true; changed = true; }

                ModelStates ms = null;
                ModelState activeState = null;
                bool scopeSet = false;
                MemberEditScopeEnum oldScope = MemberEditScopeEnum.kEditActiveMember;
                if (t.States.Count > 0)
                {
                    ms = cd.ModelStates;
                    activeState = ms.ActiveModelState;
                    try { oldScope = ms.MemberEditScope; ms.MemberEditScope = MemberEditScopeEnum.kEditAllMembers; scopeSet = true; } catch { }
                }
                Transaction tr = app.TransactionManager.StartTransaction((_Document)asm, "Позиции по групповой спецификации");
                try
                {
                    if (t.States.Count > 0)
                    {
                        foreach (string s in t.States)
                        {
                            BomCollector.ReleaseBomReferences();
                            ms[s].Activate();
                            asm.Update();
                            bool c;
                            // номера общие для всех состояний: после первого в BOM уже новые, старые — только в первом
                            record = s == t.States[0];
                            try { numbered = Math.Max(numbered, NumberView(bom, posByKey, notes, System.IO.Path.GetFileName(t.Path) + " <" + s + ">", out c)); }
                            finally { record = true; }
                            changed |= c;
                        }
                    }
                    else if (cd.IsiAssemblyFactory && t.MemberRows.Count > 0)
                    {
                        bool c;
                        numbered = NumberFactory(asm, t.MemberRows, posByKey, notes, System.IO.Path.GetFileName(t.Path), out c);
                        changed |= c;
                    }
                    else
                    {
                        bool c;
                        numbered = NumberView(bom, posByKey, notes, System.IO.Path.GetFileName(t.Path), out c);
                        changed |= c;
                    }
                    tr.End();
                }
                catch { tr.Abort(); throw; }
                finally
                {
                    if (activeState != null) try { BomCollector.ReleaseBomReferences(); activeState.Activate(); } catch { }
                    // область правки состояний модели — как была у пользователя
                    if (scopeSet) try { ms.MemberEditScope = oldScope; } catch { }
                }

                if (!changed)
                {
                    // номера уже совпадают со спецификацией: не сохраняем сборку и не пересоздаём члены iAssembly
                    try { if (!wasDirty) doc.Dirty = false; } catch { }
                    return numbered;
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
        /// остальным — следующие свободные номера после наибольшего. Сначала номера рассчитываются; если они
        /// уже стоят (и заблокированы), BOM не меняется (changed = false). Иначе запись в два прохода, чтобы
        /// не было конфликтов номеров при перестановке.</summary>
        static int NumberView(BOM bom, Dictionary<string, string> posByKey, List<string> notes, string where, out bool changed)
        {
            List<BOMRow> rows = StructuredRows(bom);
            string[] desired = new string[rows.Count];
            Collect(rows, desired, false, posByKey, notes, where);
            return Apply(rows, desired, notes, where, out changed);
        }

        /// <summary>Фабрика iAssembly: в её BOM есть строки для компонентов всех членов (замена по таблице,
        /// включение/исключение), но компонент строки и её количество — по активному члену; строки других
        /// членов показывают компонент активного члена с количеством 0. Поэтому номера собираются по очереди
        /// для каждого члена: он делается активным, и номер из спецификации получают строки с ненулевым
        /// количеством. Затем активный член восстанавливается.</summary>
        static int NumberFactory(AssemblyDocument asm, List<string> members, Dictionary<string, string> posByKey,
                                 List<string> notes, string where, out bool changed)
        {
            AssemblyComponentDefinition cd = asm.ComponentDefinition;
            iAssemblyFactory f = cd.iAssemblyFactory;
            iAssemblyTableRow def = f.DefaultRow;
            iAssemblyTableRow active = def;
            string[] desired = null;
            try
            {
                foreach (string m in members)
                {
                    iAssemblyTableRow row = null;
                    for (int i = 1; i <= f.TableRows.Count; i++) if (f.TableRows[i].MemberName == m) row = f.TableRows[i];
                    if (row == null) continue;
                    if (row.MemberName != active.MemberName)
                    {
                        // иначе Inventor не пересчитает количества строк BOM (см. ReleaseBomReferences)
                        BomCollector.ReleaseBomReferences();
                        f.DefaultRow = row; active = row; asm.Update();
                    }
                    CollectFrom(cd.BOM, ref desired, posByKey, notes, where + " <" + m + ">");
                }
            }
            finally
            {
                if (active.MemberName != def.MemberName) { BomCollector.ReleaseBomReferences(); f.DefaultRow = def; asm.Update(); }
            }
            List<BOMRow> rows = StructuredRows(cd.BOM);
            if (desired == null) desired = new string[rows.Count];
            return Apply(rows, desired, notes, where, out changed);
        }

        /// <summary>Номера для строк BOM текущего члена фабрики. Обёртки строк живут только внутри метода,
        /// чтобы их можно было освободить перед сменой члена.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void CollectFrom(BOM bom, ref string[] desired, Dictionary<string, string> posByKey, List<string> notes, string where)
        {
            List<BOMRow> rows = StructuredRows(bom);
            if (desired == null) desired = new string[rows.Count];
            else if (desired.Length != rows.Count) throw new Exception("состав BOM фабрики меняется при смене члена (" + where + ")");
            Collect(rows, desired, true, posByKey, notes, where);
        }

        static List<BOMRow> StructuredRows(BOM bom)
        {
            BOMView view = null;
            foreach (BOMView v in bom.BOMViews) if (v.ViewType == BOMViewTypeEnum.kStructuredBOMViewType) view = v;
            if (view == null) throw new Exception("нет структурированного вида BOM");
            List<BOMRow> rows = new List<BOMRow>();
            foreach (BOMRow r in view.BOMRows) rows.Add(r);
            return rows;
        }

        /// <summary>Номера из спецификации для ещё не назначенных строк (activeOnly — только строки
        /// с ненулевым количеством). Несколько строк BOM, слитых в одну позицию спецификации (тот же
        /// компонент в разных строках, одинаковое наименование без обозначения), получают один номер —
        /// Inventor допускает повторяющиеся номера позиций.</summary>
        static void Collect(List<BOMRow> rows, string[] desired, bool activeOnly, Dictionary<string, string> posByKey,
                            List<string> notes, string where)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (desired[i] != null) continue;
                if (activeOnly) { try { if (rows[i].ItemQuantity == 0) continue; } catch { } }
                string key = BomCollector.RowKey(rows[i]);
                string pos = null;
                if (key != null) posByKey.TryGetValue(key, out pos);
                desired[i] = pos;
            }
        }

        static int Apply(List<BOMRow> rows, string[] desired, List<string> notes, string where, out bool changed)
        {
            changed = false;
            int max = 0;
            foreach (string pos in desired)
            {
                int p;
                if (pos != null && int.TryParse(pos, out p)) max = Math.Max(max, p);
            }
            // окончательные номера: из спецификации, остальным — следующие свободные
            List<string> plan = new List<string>();
            int next = max, count = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (desired[i] != null) { plan.Add(desired[i]); count++; }
                else { next++; plan.Add(next.ToString()); }
            }
            for (int i = 0; i < rows.Count; i++)
            {
                string old = rows[i].ItemNumber;
                Remember(old, plan[i]);
                if (changed) continue;
                if (old != plan[i]) changed = true;
                else if (desired[i] != null) { try { if (!rows[i].ItemNumberLocked) changed = true; } catch { } }
            }
            if (!changed) return count;

            // проход 1: снять блокировку и развести номера во временный диапазон
            for (int i = 0; i < rows.Count; i++)
            {
                try { if (rows[i].ItemNumberLocked) rows[i].ItemNumberLocked = false; } catch { }
                rows[i].ItemNumber = "~" + (i + 1);
            }
            // проход 2: окончательные номера
            for (int i = 0; i < rows.Count; i++)
            {
                try
                {
                    rows[i].ItemNumber = plan[i];
                    if (desired[i] != null) try { rows[i].ItemNumberLocked = true; } catch { }
                }
                catch (Exception ex)
                {
                    notes.Add(where + ": строке BOM не назначен номер " + plan[i] + " (" + ex.Message + ")");
                    Log.Write(where + ": номер " + plan[i], ex);
                }
            }
            return count;
        }

        static void Remember(string old, string now)
        {
            if (map == null || !record || string.IsNullOrEmpty(old) || string.IsNullOrEmpty(now)) return;
            string was;
            if (!map.TryGetValue(old, out was)) { map[old] = now; return; }
            if (was == now) return;
            List<string> c;
            if (!mapConflicts.TryGetValue(old, out c)) { c = new List<string> { was }; mapConflicts[old] = c; }
            if (!c.Contains(now)) c.Add(now);
        }
    }
}
