using System;
using System.Collections.Generic;
using System.Globalization;
using Inventor;
using Application = Inventor.Application;

namespace GroupSpecB
{
    /// <summary>Запись количеств, исправленных в редакторе спецификации, в 3D-модели (по кнопке «Сформировать»):
    /// если количество детали задано параметром (базовое количество BOM — параметр), меняется значение этого
    /// параметра; иначе количество переопределяется в строке BOM сборки исполнения. Записанные количества
    /// снимаются с ручной правки — при следующем обновлении они читаются из BOM.</summary>
    public static class BomQuantity
    {
        /// <summary>Правка количества одной строки спецификации в одном исполнении.</summary>
        class Edit
        {
            public SpecRow Row;
            public Execution Exec;
            public double Value;
            public bool Done;
        }

        /// <summary>Новое значение параметра количества детали, рассчитанное по одной правке.</summary>
        class ParamPlan
        {
            public string Part;       // полное имя документа детали
            public double Value;      // новое значение параметра (во внутренних единицах Inventor)
            public Edit Edit;
        }

        /// <summary>Переопределение количества строки BOM сборки.</summary>
        class OverridePlan
        {
            public string Assembly;   // файл сборки (без состояния модели)
            public List<Edit> Edits = new List<Edit>();
        }

        public static string Write(Application app, SpecData data, Action<string> progress)
        {
            List<string> failed = new List<string>();
            List<Edit> edits = CollectEdits(data, failed);
            if (edits.Count == 0) return failed.Count == 0 ? null : "Количество в моделях не записано:\r\n" + string.Join("\r\n", failed);

            List<ParamPlan> paramPlans = new List<ParamPlan>();
            Dictionary<string, OverridePlan> overrides = new Dictionary<string, OverridePlan>(StringComparer.OrdinalIgnoreCase);
            int k = 0;
            foreach (Edit e in edits)
            {
                k++;
                if (progress != null) progress("Количество в моделях: " + k + " из " + edits.Count);
                try { Plan(app, e, paramPlans, overrides); }
                catch (Exception ex) { failed.Add(Label(e) + " — " + ex.Message); }
            }

            int parameters = WriteParameters(app, paramPlans, failed);
            int rows = 0;
            foreach (OverridePlan p in overrides.Values)
            {
                if (progress != null) progress("Количество в BOM: " + System.IO.Path.GetFileName(p.Assembly));
                rows += WriteOverrides(app, data, p, failed);
            }

            // модель совпадает со спецификацией — дальше количество снова берётся из BOM
            foreach (Edit e in edits) if (e.Done) e.Row.Edited.Remove("Qty:" + e.Exec.Key);
            string rep = "Количество в моделях: параметров — " + parameters + ", строк BOM — " + rows + ".";
            if (failed.Count > 0) rep += "\r\nКоличество в моделях не записано:\r\n" + string.Join("\r\n", failed);
            return rep;
        }

        /// <summary>Исправленные вручную количества строк BOM во включённых исполнениях.</summary>
        static List<Edit> CollectEdits(SpecData data, List<string> failed)
        {
            List<Edit> res = new List<Edit>();
            foreach (SpecRow r in data.Rows)
            {
                if (r.Source != RowSource.Bom || r.Hidden || string.IsNullOrEmpty(r.Key)) continue;
                foreach (Execution ex in data.EnabledExecutions())
                {
                    if (!r.IsEdited("Qty:" + ex.Key)) continue;
                    Edit e = new Edit { Row = r, Exec = ex };
                    string q = r.GetQty(ex.Key);
                    if (!SpecLogic.TryParseQty(q, out e.Value) || e.Value <= 0)
                        failed.Add(Label(e) + " — «" + q + "» не число: состав исполнения меняется в модели");
                    else if (r.MergedKeys.Count > 0)
                        failed.Add(Label(e) + " — строка слита из нескольких файлов (" + (r.MergedKeys.Count + 1) + "): исправьте количество в модели");
                    else res.Add(e);
                }
            }
            return res;
        }

        /// <summary>Открывает исполнение, находит строку BOM компонента и решает, что менять: параметр
        /// количества детали или количество в строке BOM.</summary>
        static void Plan(Application app, Edit e, List<ParamPlan> paramPlans, Dictionary<string, OverridePlan> overrides)
        {
            Execution ex = e.Exec;
            bool opened;
            AssemblyDocument asm = BomCollector.OpenAssembly(app, ex.Path, out opened);
            AssemblyComponentDefinition cd = asm.ComponentDefinition;
            bool wasDirty = ((Document)asm).Dirty;
            iAssemblyFactory factory = ex.Kind == BomCollector.KindRow && cd.IsiAssemblyFactory ? cd.iAssemblyFactory : null;
            iAssemblyTableRow originalRow = factory != null ? factory.DefaultRow : null;
            ModelState originalState = null;
            try
            {
                if (ex.Kind == BomCollector.KindRow)
                {
                    if (factory == null) throw new Exception("сборка больше не является фабрикой iAssembly");
                    iAssemblyTableRow row = null;
                    for (int i = 1; i <= factory.TableRows.Count; i++) if (factory.TableRows[i].MemberName == ex.Member) row = factory.TableRows[i];
                    if (row == null) throw new Exception("нет строки «" + ex.Member + "» в таблице iAssembly");
                    BomCollector.ReleaseBomReferences();
                    factory.DefaultRow = row;
                    asm.Update();
                }
                else if (ex.Kind == BomCollector.KindState)
                {
                    originalState = cd.ModelStates.ActiveModelState;
                    BomCollector.ReleaseBomReferences();
                    cd.ModelStates[ex.Member].Activate();
                    asm.Update();
                }
                PlanRow(asm, e, paramPlans, overrides);
            }
            finally
            {
                BomCollector.ReleaseBomReferences();
                try
                {
                    if (originalRow != null) { factory.DefaultRow = originalRow; asm.Update(); }
                    if (originalState != null) originalState.Activate();
                }
                catch (Exception err) { Log.Write("Возврат исходной строки iAssembly / состояния модели", err); }
                try { if (!wasDirty) ((Document)asm).Dirty = false; } catch { }
                if (opened) try { ((Document)asm).Close(true); } catch { }
            }
        }

        /// <summary>Обёртки строк BOM живут только внутри метода, чтобы их можно было освободить перед сменой
        /// строки iAssembly или состояния модели (см. BomCollector.ReleaseBomReferences).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void PlanRow(AssemblyDocument asm, Edit e, List<ParamPlan> paramPlans, Dictionary<string, OverridePlan> overrides)
        {
            BOMRow row = FindRow(asm, e.Row.Key);
            if (row == null) throw new Exception("компонента нет в BOM исполнения");
            if (row.ComponentDefinitions.Count > 1) throw new Exception("в BOM строка объединяет несколько компонентов: исправьте количество в модели");
            double cur = TotalNumber(row.TotalQuantity);
            if (Math.Abs(cur - e.Value) < 1e-9) { e.Done = true; return; }

            // количество по параметру детали: всего = число вхождений × параметр — параметр меняется пропорционально
            Parameter prm = QuantityParameter(row.ComponentDefinitions[1]);
            if (prm != null && cur > 0)
            {
                Document part = (Document)row.ComponentDefinitions[1].Document;
                if (part.IsModifiable)
                {
                    paramPlans.Add(new ParamPlan { Part = part.FullDocumentName, Value = (double)prm.Value * e.Value / cur, Edit = e });
                    return;
                }
            }
            if (e.Exec.Kind == BomCollector.KindRow || ((AssemblyComponentDefinition)asm.ComponentDefinition).IsiAssemblyMember)
                throw new Exception("исполнение iAssembly: количество задаётся составом члена в таблице фабрики");
            string file = ((Document)asm).FullFileName;
            OverridePlan p;
            if (!overrides.TryGetValue(file, out p)) { p = new OverridePlan { Assembly = file }; overrides[file] = p; }
            p.Edits.Add(e);
        }

        /// <summary>Параметр, которым задано базовое количество детали в BOM, иначе null.</summary>
        static Parameter QuantityParameter(ComponentDefinition def)
        {
            try
            {
                if (def.Type != ObjectTypeEnum.kPartComponentDefinitionObject && def.Type != ObjectTypeEnum.kSheetMetalComponentDefinitionObject) return null;
                BOMQuantityTypeEnum type;
                object value;
                ((PartComponentDefinition)def).BOMQuantity.GetBaseQuantity(out type, out value);
                return type == BOMQuantityTypeEnum.kParameterBOMQuantity ? value as Parameter : null;
            }
            catch { return null; }
        }

        /// <summary>Значения параметров количества. Одна деталь может входить в несколько исполнений —
        /// рассчитанные по ним значения должны совпасть.</summary>
        static int WriteParameters(Application app, List<ParamPlan> plans, List<string> failed)
        {
            int written = 0;
            Dictionary<string, List<ParamPlan>> byPart = new Dictionary<string, List<ParamPlan>>(StringComparer.OrdinalIgnoreCase);
            List<string> order = new List<string>();
            foreach (ParamPlan p in plans)
            {
                List<ParamPlan> l;
                if (!byPart.TryGetValue(p.Part, out l)) { l = new List<ParamPlan>(); byPart[p.Part] = l; order.Add(p.Part); }
                l.Add(p);
            }
            foreach (string part in order)
            {
                List<ParamPlan> l = byPart[part];
                double v = l[0].Value;
                if (l.Exists(p => Math.Abs(p.Value - v) > 1e-4 * Math.Max(1e-9, Math.Abs(v))))
                {
                    foreach (ParamPlan p in l)
                        failed.Add(Label(p.Edit) + " — количество задано параметром детали, а по исполнениям получаются разные значения параметра");
                    continue;
                }
                bool opened;
                Document d = null;
                try
                {
                    d = BomCollector.FindOrOpen(app, part, out opened);
                    try
                    {
                        Parameter prm = QuantityParameter((ComponentDefinition)((PartDocument)d).ComponentDefinition);
                        if (prm == null) throw new Exception("базовое количество больше не задано параметром");
                        bool wasDirty = d.Dirty;
                        prm.Value = v;
                        d.Update();
                        written++;
                        foreach (ParamPlan p in l) p.Edit.Done = true;
                        if (wasDirty)
                            failed.Add(System.IO.Path.GetFileName(d.FullFileName) + " — параметр «" + prm.Name + "» записан, но в детали были несохранённые изменения: сохраните её сами");
                        else d.Save2(false);
                    }
                    finally { if (opened) try { d.Close(true); } catch { } }
                }
                catch (Exception ex)
                {
                    foreach (ParamPlan p in l) failed.Add(Label(p.Edit) + " — параметр детали: " + ex.Message);
                }
            }
            return written;
        }

        /// <summary>Переопределение количества в строках BOM сборки. В сборке с состояниями модели оно общее
        /// для всех состояний, поэтому записывается, только если во всех исполнениях-состояниях этой сборки
        /// количество строки одинаковое.</summary>
        static int WriteOverrides(Application app, SpecData data, OverridePlan plan, List<string> failed)
        {
            List<Execution> sameFile = data.EnabledExecutions().FindAll(x => string.Equals(FileOf(x), plan.Assembly, StringComparison.OrdinalIgnoreCase));
            Dictionary<SpecRow, double> want = new Dictionary<SpecRow, double>();
            foreach (Edit e in plan.Edits)
            {
                bool conflict = false;
                foreach (Execution x in sameFile)
                {
                    double q;
                    string s = e.Row.GetQty(x.Key);
                    if (s != "" && (!SpecLogic.TryParseQty(s, out q) || Math.Abs(q - e.Value) > 1e-9)) conflict = true;
                }
                double prev;
                if (want.TryGetValue(e.Row, out prev) && Math.Abs(prev - e.Value) > 1e-9) conflict = true;
                if (conflict)
                {
                    failed.Add(Label(e) + " — в состояниях модели количество в BOM переопределяется сразу для всех состояний, а в них разные количества");
                    continue;
                }
                want[e.Row] = e.Value;
            }
            if (want.Count == 0) return 0;

            int written = 0;
            bool opened;
            AssemblyDocument asm = null;
            try
            {
                asm = BomCollector.OpenAssembly(app, plan.Assembly, out opened);
                try
                {
                    Document doc = (Document)asm;
                    if (!doc.IsModifiable) throw new Exception("сборка только для чтения");
                    bool wasDirty = doc.Dirty;
                    ModelStates ms = null;
                    bool scopeSet = false;
                    MemberEditScopeEnum oldScope = MemberEditScopeEnum.kEditActiveMember;
                    try { ms = asm.ComponentDefinition.ModelStates; if (ms.Count > 1) { oldScope = ms.MemberEditScope; ms.MemberEditScope = MemberEditScopeEnum.kEditAllMembers; scopeSet = true; } } catch { }
                    try
                    {
                        foreach (KeyValuePair<SpecRow, double> kv in want)
                        {
                            List<Edit> rowEdits = plan.Edits.FindAll(x => x.Row == kv.Key && Math.Abs(x.Value - kv.Value) < 1e-9);
                            try
                            {
                                BOMRow row = FindRow(asm, kv.Key.Key);
                                if (row == null) throw new Exception("компонента нет в BOM сборки");
                                SetTotal(row, kv.Value);
                                written++;
                                foreach (Edit e in rowEdits) e.Done = true;
                            }
                            catch (Exception ex) { foreach (Edit e in rowEdits) failed.Add(Label(e) + " — " + ex.Message); }
                        }
                    }
                    finally { if (scopeSet) try { ms.MemberEditScope = oldScope; } catch { } }
                    if (written > 0)
                    {
                        if (wasDirty) failed.Add(System.IO.Path.GetFileName(plan.Assembly) + " — количество записано, но в сборке были несохранённые изменения: сохраните её сами");
                        else asm.Save2(false);
                    }
                }
                finally { if (opened) try { ((Document)asm).Close(true); } catch { } }
            }
            catch (Exception ex)
            {
                foreach (Edit e in plan.Edits) if (!e.Done && want.ContainsKey(e.Row)) failed.Add(Label(e) + " — " + ex.Message);
            }
            return written;
        }

        /// <summary>Количество строки BOM: совпадает с числом вхождений — переопределение снимается, иначе
        /// записывается. Строку Inventor принимает в формате текущих региональных настроек.</summary>
        static void SetTotal(BOMRow row, double value)
        {
            if (row.TotalQuantityOverridden && Math.Abs(row.ItemQuantity - value) < 1e-9 && QuantityParameter(row.ComponentDefinitions[1]) == null)
            {
                row.TotalQuantityOverridden = false;
                if (Math.Abs(TotalNumber(row.TotalQuantity) - value) < 1e-9) return;
            }
            bool wasOverridden = row.TotalQuantityOverridden;
            string was = row.TotalQuantity;
            string s = Math.Round(value, 6).ToString("0.######", CultureInfo.CurrentCulture);
            try { row.TotalQuantity = s; }
            catch { row.TotalQuantity = Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture); }
            // штучное количество Inventor округляет до целого — тогда строка возвращается как была
            string got = row.TotalQuantity;
            if (Math.Abs(TotalNumber(got) - value) > 1e-6)
            {
                try { if (wasOverridden) row.TotalQuantity = was; else row.TotalQuantityOverridden = false; } catch { }
                throw new Exception("Inventor не принял количество " + s + " (получилось " + got + ")");
            }
        }

        static BOMRow FindRow(AssemblyDocument asm, string key)
        {
            BOM bom = asm.ComponentDefinition.BOM;
            if (!bom.StructuredViewEnabled) bom.StructuredViewEnabled = true;
            foreach (BOMView v in bom.BOMViews)
            {
                if (v.ViewType != BOMViewTypeEnum.kStructuredBOMViewType) continue;
                foreach (BOMRow r in v.BOMRows)
                    if (string.Equals(BomCollector.RowKey(r), key, StringComparison.OrdinalIgnoreCase)) return r;
            }
            return null;
        }

        /// <summary>Число из «Кол.» строки BOM («666,667 мм» → 666.667).</summary>
        static double TotalNumber(string total)
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(total ?? "", @"^\s*([0-9]+(?:[.,][0-9]+)?)");
            double v;
            return m.Success && SpecLogic.TryParseQty(m.Groups[1].Value, out v) ? v : 0;
        }

        /// <summary>Файл сборки исполнения без состояния модели.</summary>
        static string FileOf(Execution e)
        {
            string p = e.Path ?? "";
            int lt = p.IndexOf('<');
            return lt > 0 ? p.Substring(0, lt) : p;
        }

        static string Label(Edit e)
        {
            string name = e.Row.Designation != "" ? e.Row.Designation : e.Row.Name != "" ? e.Row.Name : System.IO.Path.GetFileName(e.Row.Key);
            return name + " (исп. " + (string.IsNullOrEmpty(e.Exec.Label) ? e.Exec.Key : e.Exec.Label) + ")";
        }
    }
}
