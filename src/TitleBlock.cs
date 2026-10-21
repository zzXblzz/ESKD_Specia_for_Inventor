using System;
using System.Collections.Generic;
using Inventor;
using Application = Inventor.Application;

namespace GroupSpecB
{
    /// <summary>Графа основной надписи, связанная со свойством Inventor.</summary>
    public class TitleField
    {
        public string Key;        // ключ графы (и значения по умолчанию в SpecSettings)
        public string Label;      // подпись в окне
        public string FormatId;   // набор свойств
        public int PropId;
        public string PropName;   // имя свойства (для создания)
        public string SetName;    // имя набора (для создания пользовательского набора)
        public bool ModelDoc;     // ссылка вида Document='model' (так Inventor хранит свойства набора GOST Properties)

        /// <summary>Разметка поля-ссылки на свойство: значение на листе меняется вместе со свойством.</summary>
        public string Markup()
        {
            if (ModelDoc)
                return "<Property Document='model' PropertySet='" + SetName + "' Property='" + PropName + "' FormatID='" +
                       FormatId + "' PropertyID='" + PropId + "'>" + Label + "</Property>";
            return "<Property Document='drawing' FormatID='" + FormatId + "' PropertyID='" + PropId + "'>" + Label + "</Property>";
        }
    }

    /// <summary>Значения граф основной надписи в окне: откуда взято значение и исправлено ли оно пользователем.</summary>
    public class TitleState
    {
        public const string FromModel = "model", FromDrawing = "drawing", FromDefault = "default";
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        public readonly Dictionary<string, string> Sources = new Dictionary<string, string>();
        public readonly HashSet<string> Edited = new HashSet<string>();

        public string Get(string key) { string v; return Values.TryGetValue(key, out v) ? v : ""; }
        public string Source(string key) { string v; return Sources.TryGetValue(key, out v) ? v : ""; }

        public void SetByUser(string key, string value)
        {
            Values[key] = value ?? "";
            Edited.Add(key);
        }
    }

    /// <summary>Основная надпись спецификации: графы — ссылки на свойства чертежа; свойства копируются
    /// в чертёж из 3D-модели специфицируемой сборки, пустые — заполняются значениями по умолчанию,
    /// исправленные в окне — записываются и в модель, и в чертёж.</summary>
    public static class SpecTitleBlock
    {
        const string DT = "{32853F0F-3444-11D1-9E93-0060B03C1CA6}";      // Свойства Каталога проекта
        const string SUM = "{F29F85E0-4FF9-1068-AB91-08002B27B3D9}";     // Общая информация
        const string DOCSUM = "{D5CDD502-2E9C-101B-9397-08002B2CF9AE}";  // Информация о документе
        public const string GOST = "{74B7E536-2FB1-487C-A0DD-F49DB5548267}";

        public static readonly TitleField Designation = new TitleField { Key = "Designation", Label = "Обозначение", FormatId = DT, PropId = 5, PropName = "Part Number" };
        public static readonly TitleField Name = new TitleField { Key = "Name", Label = "Наименование", FormatId = DT, PropId = 29, PropName = "Description" };
        public static readonly TitleField Company = new TitleField { Key = "Company", Label = "Организация", FormatId = DOCSUM, PropId = 15, PropName = "Company" };
        public static readonly TitleField Designer = new TitleField { Key = "Designer", Label = "Разраб.", FormatId = DT, PropId = 41, PropName = "Designer" };
        public static readonly TitleField Checker = new TitleField { Key = "Checker", Label = "Пров.", FormatId = DT, PropId = 10, PropName = "Checked By" };
        public static readonly TitleField TechControl = new TitleField { Key = "TechControl", Label = "Т.контр.", FormatId = DT, PropId = 42, PropName = "Engineer" };
        public static readonly TitleField NormControl = new TitleField { Key = "NormControl", Label = "Н.контр.", FormatId = DT, PropId = 12, PropName = "Engr Approved By" };
        public static readonly TitleField Approver = new TitleField { Key = "Approver", Label = "Утв.", FormatId = DT, PropId = 34, PropName = "Mfg Approved By" };
        public static readonly TitleField Litera = new TitleField { Key = "Litera", Label = "Лит.", FormatId = GOST, PropId = 6, PropName = "Litera", SetName = "GOST Properties", ModelDoc = true };

        public static readonly TitleField[] All = { Designation, Name, Company, Designer, Checker, TechControl, NormControl, Approver, Litera };

        /// <summary>Дата в строках с фамилией — «Дата создания» чертежа (в модель не копируется).</summary>
        public static readonly TitleField CreationDate = new TitleField { Key = "Date", Label = "Дата", FormatId = DT, PropId = 4, PropName = "Creation Time" };
        public const string DateFormat = "dd.MM.yy";

        public static string DrawingDate(DrawingDocument dwg)
        {
            if (dwg == null) return DateTime.Today.ToString(DateFormat);
            Property p = Find((Document)dwg, CreationDate);
            try { if (p != null && p.Value is DateTime) return ((DateTime)p.Value).ToString(DateFormat); } catch { }
            return DateTime.Today.ToString(DateFormat);
        }

        static Property Find(Document d, TitleField f)
        {
            try { return d.PropertySets[f.FormatId].ItemByPropId[f.PropId]; }
            catch { return null; }
        }

        public static string Value(Document d, TitleField f)
        {
            if (d == null) return "";
            Property p = Find(d, f);
            if (p == null) return "";
            try
            {
                object v = p.Value;
                if (v is DateTime) return ((DateTime)v).ToString(DateFormat);
                return v == null ? "" : v.ToString().Trim();
            }
            catch { return ""; }
        }

        /// <summary>Значение графы из 3D-модели. В фабрике iAssembly / iPart «Обозначение» — обозначение
        /// активного члена (например, «…-01»), а в основной надписи нужно обозначение без номера исполнения.</summary>
        static string ModelValue(Document model, TitleField f)
        {
            string v = Value(model, f);
            return f == Designation && IsFactory(model) ? SpecLogic.WithoutExecution(v) : v;
        }

        static bool IsFactory(Document d)
        {
            try
            {
                if (d.DocumentType == DocumentTypeEnum.kAssemblyDocumentObject) return ((AssemblyDocument)d).ComponentDefinition.IsiAssemblyFactory;
                if (d.DocumentType == DocumentTypeEnum.kPartDocumentObject) return ((PartDocument)d).ComponentDefinition.IsiPartFactory;
            }
            catch { }
            return false;
        }

        /// <summary>Записывает свойство (создаёт его и набор GOST Properties, если их нет).</summary>
        static void SetValue(Document d, TitleField f, string v)
        {
            Property p = Find(d, f);
            if (p != null) { p.Value = v; return; }
            PropertySet ps;
            try { ps = d.PropertySets[f.FormatId]; }
            catch { ps = d.PropertySets.Add(f.SetName ?? f.PropName, f.FormatId); }
            ps.Add(v, f.PropName, f.PropId);
        }

        /// <summary>Значения граф: из 3D-модели; если там пусто — из чертежа; если пусто и там — по умолчанию.</summary>
        public static TitleState Resolve(Document model, DrawingDocument dwg, SpecSettings s)
        {
            TitleState st = new TitleState();
            foreach (TitleField f in All)
            {
                string mv = ModelValue(model, f), dv = dwg != null ? Value((Document)dwg, f) : "";
                if (mv != "") { st.Values[f.Key] = mv; st.Sources[f.Key] = TitleState.FromModel; }
                else if (dv != "") { st.Values[f.Key] = dv; st.Sources[f.Key] = TitleState.FromDrawing; }
                else { st.Values[f.Key] = s.GetTitleDefault(f.Key); st.Sources[f.Key] = TitleState.FromDefault; }
            }
            st.Values[CreationDate.Key] = DrawingDate(dwg);
            st.Sources[CreationDate.Key] = TitleState.FromDrawing;
            return st;
        }

        /// <summary>Записывает значения граф в свойства чертежа: исправленные пользователем — как есть,
        /// остальные — по правилу «модель → чертёж → по умолчанию». Возвращает отчёт или null.</summary>
        public static string FillDrawing(DrawingDocument dwg, Document model, SpecSettings s, TitleState edited)
        {
            Document d = (Document)dwg;
            TitleState now = Resolve(model, dwg, s);
            List<string> changed = new List<string>();
            List<string> errors = new List<string>();
            foreach (TitleField f in All)
            {
                string v = edited != null && edited.Edited.Contains(f.Key) ? edited.Get(f.Key) : now.Get(f.Key);
                if (v == Value(d, f)) continue;
                try { SetValue(d, f, v); changed.Add(f.Label + " = " + v); }
                catch (Exception ex) { errors.Add(f.Label + ": " + ex.Message); }
            }
            // дата, выбранная в окне, — в «Дата создания» чертежа
            if (edited != null && edited.Edited.Contains(CreationDate.Key))
            {
                DateTime dt;
                if (DateTime.TryParseExact(edited.Get(CreationDate.Key), DateFormat, System.Globalization.CultureInfo.InvariantCulture,
                                           System.Globalization.DateTimeStyles.None, out dt))
                {
                    try { Find(d, CreationDate).Value = dt; changed.Add("Дата = " + edited.Get(CreationDate.Key)); }
                    catch (Exception ex) { errors.Add("Дата: " + ex.Message); }
                }
            }
            string rep = changed.Count > 0 ? "Свойства чертежа для основной надписи: " + string.Join("; ", changed) + "." : null;
            if (errors.Count > 0) rep = (rep ?? "") + "\r\nНе заполнено в чертеже: " + string.Join("; ", errors);
            return rep;
        }

        /// <summary>Записывает исправленные в окне графы в 3D-модель родительского компонента (модель первого
        /// вида чертежа; для члена iPart/iAssembly — фабрика). Модель сохраняется, если в ней не было
        /// несохранённых правок.</summary>
        public static string WriteModel(Application app, SpecData data, DrawingDocument dwg, TitleState edited)
        {
            if (edited == null || edited.Edited.Count == 0) return null;
            bool opened;
            Document d = ParentModel(app, data, dwg, out opened);
            if (d == null) return "Основная надпись: нет 3D-модели — правки записаны только в чертёж.";
            try
            {
                string name = System.IO.Path.GetFileName(d.FullFileName);
                if (!d.IsModifiable) return "Основная надпись: модель " + name + " только для чтения — правки записаны только в чертёж.";
                bool wasDirty = d.Dirty;
                List<string> errors = new List<string>();
                foreach (TitleField f in All)
                {
                    if (!edited.Edited.Contains(f.Key)) continue;
                    try { if (Value(d, f) != edited.Get(f.Key)) SetValue(d, f, edited.Get(f.Key)); }
                    catch (Exception ex) { errors.Add(f.Label + ": " + ex.Message); }
                }
                if (wasDirty) errors.Add("в модели были несохранённые изменения — сохраните её сами");
                else if (d.Dirty) d.Save2(false);
                return errors.Count == 0 ? null : "Основная надпись, модель " + name + ": " + string.Join("; ", errors);
            }
            catch (Exception ex) { return "Основная надпись: " + ex.Message; }
            finally { if (opened) try { d.Close(true); } catch { } }
        }

        static bool IsSpecSheet(Sheet sh)
        {
            try { return sh.AttributeSets.NameIsUsed[DrawingWriter.SheetAttrSet]; } catch { return false; }
        }

        /// <summary>Модель первого вида чертежа (листы спецификации пропускаются) или null.</summary>
        public static Document FirstViewModel(DrawingDocument dwg)
        {
            if (dwg == null) return null;
            foreach (Sheet sh in dwg.Sheets)
            {
                if (IsSpecSheet(sh)) continue;
                foreach (DrawingView v in sh.DrawingViews)
                {
                    try
                    {
                        Document m = (Document)v.ReferencedDocumentDescriptor.ReferencedDocument;
                        if (m != null && (m.DocumentType == DocumentTypeEnum.kAssemblyDocumentObject ||
                                          m.DocumentType == DocumentTypeEnum.kPartDocumentObject)) return m;
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>Родительский компонент: для члена iPart/iAssembly — его фабрика, иначе сама модель.</summary>
        static Document Parent(Application app, Document model, out bool opened)
        {
            opened = false;
            if (model == null) return null;
            string fp = BomCollector.FactoryPath(model);
            if (fp == null) return model;
            try { return BomCollector.FindOrOpen(app, fp, out opened); }
            catch { return model; }
        }

        /// <summary>3D-модель родительского компонента для основной надписи: модель основного (первого включённого)
        /// исполнения групповой спецификации, а если исполнений нет — модель первого вида чертежа;
        /// член iPart/iAssembly → фабрика. Первый вид может показывать не основное исполнение (например, «-02»),
        /// и тогда обозначение спецификации было бы обозначением этого исполнения.</summary>
        public static Document ParentModel(Application app, SpecData data, DrawingDocument dwg, out bool opened)
        {
            opened = false;
            if (data == null && dwg != null)
            {
                try { data = DrawingWriter.LoadData(dwg); } catch { data = null; }
            }
            Document model = null;
            bool modelOpened = false;
            List<Execution> ex = data != null ? data.EnabledExecutions() : new List<Execution>();
            if (ex.Count > 0)
            {
                try { model = (Document)BomCollector.OpenAssembly(app, ex[0].Path, out modelOpened); }
                catch (Exception e) { Log.Write("ParentModel: " + ex[0].Path, e); model = null; }
            }
            if (model == null) { model = FirstViewModel(dwg); modelOpened = false; }
            if (model == null) return null;
            bool parentOpened;
            Document p = Parent(app, model, out parentOpened);
            if (p != model && modelOpened) try { model.Close(true); } catch { }
            opened = p == model ? modelOpened : parentOpened;
            return p;
        }

        /// <summary>При сохранении чертежа: свойства основной надписи копируются в чертёж из родительского
        /// компонента (см. ParentModel). Пустые в модели — не переносятся.</summary>
        public static void CopyFromFirstViewModel(Application app, DrawingDocument dwg)
        {
            bool opened;
            Document p = ParentModel(app, null, dwg, out opened);
            if (p == null) return;
            try
            {
                Document d = (Document)dwg;
                foreach (TitleField f in All)
                {
                    string mv = ModelValue(p, f);
                    if (mv == "" || mv == Value(d, f)) continue;
                    try { SetValue(d, f, mv); } catch (Exception ex) { Log.Write("Свойство чертежа «" + f.Label + "» при сохранении", ex); }
                }
            }
            finally { if (opened) try { p.Close(true); } catch { } }
        }
    }
}