using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Xml.Serialization;

namespace GroupSpecB
{
    /// <summary>Разделы спецификации в порядке ГОСТ 2.106.</summary>
    public static class Sections
    {
        public const string Documentation = "Документация";
        public const string Complexes = "Комплексы";
        public const string Assemblies = "Сборочные единицы";
        public const string Parts = "Детали";
        public const string Standard = "Стандартные изделия";
        public const string Other = "Прочие изделия";
        public const string Materials = "Материалы";
        public const string Kits = "Комплекты";

        public static readonly string[] Order =
        {
            Documentation, Complexes, Assemblies, Parts, Standard, Other, Materials, Kits
        };

        public static int IndexOf(string section)
        {
            int i = Array.IndexOf(Order, section);
            return i < 0 ? Order.Length : i;
        }

        /// <summary>Разделы, в которых заполняется графа «Формат» (из свойства LastFormat модели).</summary>
        public static bool HasFormat(string section)
        {
            return section == Documentation || section == Assemblies || section == Parts;
        }

        /// <summary>Разделы с обозначениями ЕСКД, где действуют исполнения: группировка исполнений одной части
        /// и сокращение «-02» (ГОСТ 2.113 п. 5.3). В стандартных и прочих изделиях, материалах «-70» в
        /// «ГОСТ 7798-70» — год стандарта, а не номер исполнения.</summary>
        public static bool HasExecutions(string section)
        {
            return section == Complexes || section == Assemblies || section == Parts || section == Kits;
        }

        /// <summary>Разделы, в которых составные части сортируются по наименованию, а не по обозначению.</summary>
        public static bool SortByName(string section)
        {
            return section == Standard || section == Other || section == Materials;
        }
    }

    /// <summary>Тип спецификации: по ГОСТ 2.106 (формы 1 и 1а, одно изделие) или групповая по ГОСТ 2.113 (вариант Б).</summary>
    public static class SpecTypes
    {
        public const string Auto = "";          // по числу 3D-моделей: одна — ГОСТ 2.106, несколько — ГОСТ 2.113
        public const string Gost2106 = "2.106";
        public const string Gost2113 = "2.113";

        public static string Title(string type)
        {
            return type == Gost2106 ? "Спецификация (ГОСТ 2.106, формы 1 и 1а)" : "Групповая спецификация (ГОСТ 2.113-75, вариант Б)";
        }
    }

    public static class RowSource
    {
        public const string Bom = "BOM";
        public const string Model = "Модель";
        public const string Manual = "Вручную";
    }

    /// <summary>Исполнение изделия (строка iAssembly, состояние модели или единственное исполнение).</summary>
    public class Execution
    {
        public string Key;          // уникальный ключ: имя члена iAssembly / состояния модели / полное имя файла
        public string Designation;  // обозначение исполнения (Part Number)
        public string Label;        // подзаголовок графы: "–", "01", "02" ...
        public bool Enabled = true;
        public string Kind = "";    // Row (строка iAssembly), State (состояние модели), File (отдельная модель)
        public string Path = "";    // файл фабрики / модели исполнения (для File — полное имя документа)
        public string Member = "";  // имя строки iAssembly или состояния модели
        public bool UserAdded;      // исполнение добавлено кнопкой «Добавить исполнение»
    }

    /// <summary>Сведения о составной части, прочитанные из BOM одного исполнения (заполняет BomCollector).</summary>
    public class BomItem
    {
        public string Key;
        public string Designation;
        public string Name;
        public string Format;
        public string Note;
        public string Section;
        public string Unit;
        public string ItemNumber;
        public string Qty;
        public string Material = "";    // материал детали (для записи дробью в наименовании)
    }

    public class ExecutionBom
    {
        public Execution Execution;
        public List<BomItem> Items = new List<BomItem>();
    }

    public class QtyEntry
    {
        [XmlAttribute] public string Exec;
        [XmlAttribute] public string Value;
        public QtyEntry() { }
        public QtyEntry(string exec, string value) { Exec = exec; Value = value; }
    }

    /// <summary>Строка групповой спецификации.</summary>
    public class SpecRow
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Source = RowSource.Manual;
        public string Key = "";          // путь к файлу компонента (для BOM и моделей)
        public List<string> MergedKeys = new List<string>(); // другие файлы, слитые в эту строку (то же обозначение / наименование)
        public string Section = Sections.Parts;
        public string Format = "";
        public string Zone = "";
        public string Pos = "";
        public string Designation = "";
        public string Name = "";
        public string Note = "";
        public string Unit = "";         // единица измерения, если количество не в штуках
        public string BomItem = "";      // номер позиции в BOM (группирует взаимозаменяемые части)
        public string Material = "";     // материал детали: «Сортамент\Марка\Стандарт» в наименовании пишется дробью
        public bool InMain;              // применяется в основном (первом) исполнении
        public bool Hidden;              // исключена из спецификации пользователем (строки BOM не удаляются)
        public List<QtyEntry> Qty = new List<QtyEntry>();
        public List<string> Edited = new List<string>(); // поля, исправленные вручную

        public List<string> AllKeys()
        {
            List<string> r = new List<string>();
            if (!string.IsNullOrEmpty(Key)) r.Add(Key);
            r.AddRange(MergedKeys);
            return r;
        }

        public string GetQty(string exec)
        {
            foreach (QtyEntry q in Qty) if (q.Exec == exec) return q.Value ?? "";
            return "";
        }

        public void SetQty(string exec, string value)
        {
            for (int i = 0; i < Qty.Count; i++)
            {
                if (Qty[i].Exec == exec)
                {
                    if (string.IsNullOrEmpty(value)) Qty.RemoveAt(i); else Qty[i].Value = value;
                    return;
                }
            }
            if (!string.IsNullOrEmpty(value)) Qty.Add(new QtyEntry(exec, value));
        }

        public bool IsEdited(string field) { return Edited.Contains(field); }

        public void MarkEdited(string field)
        {
            if (!Edited.Contains(field)) Edited.Add(field);
        }
    }

    /// <summary>Свободные строки раздела на листах: перед заголовком, после заголовка, после раздела.</summary>
    public enum GapKind { Before, After, Reserve }

    /// <summary>Число свободных строк раздела, заданное в этом чертеже (-1 — по умолчанию: одна строка
    /// перед заголовком и после него, после раздела — параметр «Свободных строк после раздела»).</summary>
    public class SectionGap
    {
        [XmlAttribute] public string Section;
        [XmlAttribute] public int Before = -1;
        [XmlAttribute] public int After = -1;
        [XmlAttribute] public int Reserve = -1;
    }

    /// <summary>Настройки оформления и данные основной надписи.</summary>
    public class SpecSettings
    {
        [Browsable(false)]
        public string SpecDesignation { get; set; }

        [Browsable(false)]
        public string BaseDesignation { get; set; }

        [Category("1. Спецификация"), DisplayName("Наименование берётся из"),
         Description("Description или Title (iProperties компонентов).")]
        public string NameProperty { get; set; }

        [Category("1. Спецификация"), DisplayName("Добавлять «Сборочный чертеж»"),
         Description("Строка раздела «Документация» с обозначением <базовое>СБ.")]
        public bool AddAssemblyDrawing { get; set; }

        [Category("1. Спецификация"), DisplayName("Формат сборочного чертежа")]
        public string AssemblyDrawingFormat { get; set; }

        [Category("1. Спецификация"), DisplayName("Сокращать обозначения исполнений"),
         Description("ГОСТ 2.113 п. 5.3: вторая и следующие подряд идущие части с тем же базовым обозначением пишутся как «-01».")]
        public bool AbbreviateExecutions { get; set; }

        // Дополнительные графы раньше хранились в параметрах; остались только для переноса старых данных
        // чертежа (Commands.PrepareSettings). Графы основной надписи — свойства чертежа (TitleBlock.cs).
        [Browsable(false)] public string InvNo { get; set; }
        [Browsable(false)] public string FirstUse { get; set; }
        [Browsable(false)] public string RefNo { get; set; }

        [Category("3. Вёрстка"), DisplayName("ГОСТ 2.113: строк на первом листе (форма 1б)")] public int RowsFirst { get; set; }
        [Category("3. Вёрстка"), DisplayName("ГОСТ 2.113: строк на последующих листах (форма 1а)")] public int RowsNext { get; set; }
        [Category("3. Вёрстка"), DisplayName("ГОСТ 2.106: строк на первом листе (форма 1)")] public int RowsFirst2106 { get; set; }
        [Category("3. Вёрстка"), DisplayName("ГОСТ 2.106: строк на последующих листах (форма 1а)")] public int RowsNext2106 { get; set; }
        [Category("3. Вёрстка"), DisplayName("Высота шрифта, мм")] public double FontHeight { get; set; }
        [Category("3. Вёрстка"), DisplayName("Шрифт")] public string FontName { get; set; }
        [Category("3. Вёрстка"), DisplayName("Наклонный шрифт")] public bool Italic { get; set; }
        [Category("3. Вёрстка"), DisplayName("Средняя ширина символа, мм"),
         Description("Оценка ширины текста для предпросмотра в редакторе. При формировании листов перенос считается по фактической ширине надписей.")]
        public double CharWidth { get; set; }
        [Category("3. Вёрстка"), DisplayName("Свободных строк после раздела")] public int ReserveLines { get; set; }

        [Category("1. Спецификация"), DisplayName("Резерв номеров позиций в конце раздела"),
         Description("Сколько номеров позиций пропустить перед первым номером следующего раздела (резерв для последующих записей).")]
        public int PosReserve { get; set; }

        [Category("1. Спецификация"), DisplayName("Перенумеровать позиции в сборке"),
         Description("При формировании записать номера позиций спецификации в BOM родительских сборок исполнений (структурированный вид, номера блокируются). Фабрика iAssembly сохраняется, её члены пересоздаются.")]
        public bool RenumberAssemblyBom { get; set; }

        [Category("1. Спецификация"), DisplayName("Свойства при сохранении: все чертежи"),
         Description("Нет — при активации, сохранении и печати свойства основной надписи копируются из 3D-модели только в чертежи со спецификацией. " +
                     "Да — во все чертежи (если ассоциирование свойств в ESKD Specia отключено и надстройка его заменяет).")]
        public bool SyncTitleOnSaveAllDrawings { get; set; }

        [Category("1. Спецификация"), DisplayName("Закрывать зависшую транзакцию ESKD Specia"),
         Description("Временная заплатка: при открытии чертежа ESKD Specia может оставить открытой транзакцию «CreateProperty», " +
                     "и обозреватель Inventor перестаёт обновляться. Да — надстройка прерывает её (если Specia загружена).")]
        public bool FixSpeciaTransaction { get; set; }

        [Category("1. Спецификация"), DisplayName("Свойства материала в чертеже детали"),
         Description("Да — как в ESKD Specia: в пользовательские свойства чертежа записывается материал детали первого вида. " +
                     "Материал «Сортамент\\Марка\\Стандарт» (или через «|») — в RollingMaterial, MaterialUpLine, MaterialDownLine, " +
                     "черта из «_» — в MaterialLine; иначе — в SingleMaterial. SingleMaterial «-», «_» или «см. …» не перезаписывается.")]
        public bool FillMaterialProps { get; set; }

        [Category("1. Спецификация"), DisplayName("Масса и масштаб в чертеже"),
         Description("Да — как в ESKD Specia: в пользовательские свойства чертежа UserMass и UserScale записываются масса " +
                     "модели первого вида (кг, от 10 т — «т») и масштаб этого вида («1:2»). Обновляются при активации, " +
                     "сохранении и печати чертежа, после вставки и правки вида. Значение «-», «_» или «см. …» не перезаписывается.")]
        public bool FillMassScaleProps { get; set; }

        [Category("4. Основная надпись: значения по умолчанию"), DisplayName("Обозначение"),
         Description("Подставляется, если свойство «Обозначение» пусто и в 3D-модели, и в чертеже.")]
        public string DefDesignation { get; set; }
        [Category("4. Основная надпись: значения по умолчанию"), DisplayName("Наименование")] public string DefName { get; set; }
        [Category("4. Основная надпись: значения по умолчанию"), DisplayName("Организация")] public string DefCompany { get; set; }
        [Category("4. Основная надпись: значения по умолчанию"), DisplayName("Разраб.")] public string DefDesigner { get; set; }
        [Category("4. Основная надпись: значения по умолчанию"), DisplayName("Пров.")] public string DefChecker { get; set; }
        [Category("4. Основная надпись: значения по умолчанию"), DisplayName("Т.контр.")] public string DefTechControl { get; set; }
        [Category("4. Основная надпись: значения по умолчанию"), DisplayName("Н.контр.")] public string DefNormControl { get; set; }
        [Category("4. Основная надпись: значения по умолчанию"), DisplayName("Утв.")] public string DefApprover { get; set; }
        [Category("4. Основная надпись: значения по умолчанию"), DisplayName("Лит.")] public string DefLitera { get; set; }

        /// <summary>Значение графы основной надписи по умолчанию по ключу графы (TitleField.Key).</summary>
        public string GetTitleDefault(string key)
        {
            System.Reflection.PropertyInfo p = typeof(SpecSettings).GetProperty("Def" + key);
            object v = p != null ? p.GetValue(this, null) : null;
            return v == null ? "" : v.ToString();
        }

        public SpecSettings()
        {
            NameProperty = "Description";
            AddAssemblyDrawing = true;
            AssemblyDrawingFormat = "А4";
            AbbreviateExecutions = true;
            RowsFirst = 16;
            RowsNext = 19;
            RowsFirst2106 = 29;
            RowsNext2106 = 32;
            FontHeight = 3.5;
            FontName = "GOST Common";
            Italic = true;
            CharWidth = 2.3;
            ReserveLines = 0;
            PosReserve = 2;
            RenumberAssemblyBom = true;
            FixSpeciaTransaction = true;
            FillMaterialProps = true;
            FillMassScaleProps = true;
            SpecDesignation = ""; BaseDesignation = "";
            DefDesignation = ""; DefName = ""; DefCompany = ""; DefDesigner = ""; DefChecker = "";
            DefTechControl = ""; DefNormControl = ""; DefApprover = ""; DefLitera = "";
            InvNo = ""; FirstUse = ""; RefNo = "";
        }
    }

    /// <summary>Полное состояние спецификации. Хранится в атрибутах чертежа.</summary>
    public class SpecData
    {
        // Тип спецификации, выбранный вручную (SpecTypes); пусто — по числу 3D-моделей. null — данные, сохранённые
        // до появления ГОСТ 2.106: в них групповая спецификация (FromXml).
        public string SpecType;
        public string SourcePath = "";
        public string SourceKind = "";   // iAssembly / ModelStates / Single
        public bool ManualOrder;         // порядок строк задан пользователем — при обновлении не пересортировывать
        public string DrawingFormat = "";  // формат первого листа сборочного чертежа (при запуске из чертежа)
        public string BaseDesignation = "";  // базовое обозначение исполнений, заданное вручную (пусто — автоматически)
        public string InvNo = "", FirstUse = "", RefNo = "";   // дополнительные графы: Инв. № подл., Перв. примен., Справ. №
        public bool OnDrawingSheet;      // ГОСТ 2.106: таблица на листе чертежа над основной надписью, а не на листах «СП-N»
        public List<Execution> Executions = new List<Execution>();
        public List<SpecRow> Rows = new List<SpecRow>();
        public List<SectionGap> Gaps = new List<SectionGap>();   // свободные строки, удалённые в этом чертеже
        public SpecSettings Settings = new SpecSettings();

        /// <summary>Число свободных строк раздела на листах: заданное в чертеже, иначе по умолчанию.</summary>
        public int GapLines(string section, GapKind kind)
        {
            SectionGap g = Gaps.Find(x => x.Section == section);
            int n = g == null ? -1 : kind == GapKind.Before ? g.Before : kind == GapKind.After ? g.After : g.Reserve;
            if (n >= 0) return n;
            return kind == GapKind.Reserve ? Math.Max(0, Settings.ReserveLines) : 1;
        }

        /// <summary>Число свободных строк раздела только для этого чертежа (параметры не меняются).</summary>
        public void SetGapLines(string section, GapKind kind, int n)
        {
            SectionGap g = Gaps.Find(x => x.Section == section);
            if (g == null) Gaps.Add(g = new SectionGap { Section = section });
            n = Math.Max(0, n);
            if (kind == GapKind.Before) g.Before = n; else if (kind == GapKind.After) g.After = n; else g.Reserve = n;
        }

        /// <summary>Свободные строки раздела (null — всех разделов) — снова по умолчанию.</summary>
        public void ResetGaps(string section)
        {
            Gaps.RemoveAll(x => section == null || x.Section == section);
        }

        public List<Execution> EnabledExecutions()
        {
            List<Execution> r = new List<Execution>();
            foreach (Execution e in Executions) if (e.Enabled) r.Add(e);
            return r;
        }

        /// <summary>Действующий тип спецификации: выбранный вручную, иначе по числу 3D-моделей (исполнений):
        /// одна — ГОСТ 2.106, несколько — групповая по ГОСТ 2.113.</summary>
        public string EffectiveType()
        {
            if (SpecType == SpecTypes.Gost2106 || SpecType == SpecTypes.Gost2113) return SpecType;
            return Executions.Count > 1 ? SpecTypes.Gost2113 : SpecTypes.Gost2106;
        }

        [XmlIgnore] public bool Is2106 { get { return EffectiveType() == SpecTypes.Gost2106; } }

        /// <summary>Спецификация выводится на лист чертежа над основной надписью (только ГОСТ 2.106;
        /// групповая всегда на отдельных листах).</summary>
        [XmlIgnore] public bool OnDrawing { get { return OnDrawingSheet && Is2106; } }

        /// <summary>Исполнения в графах «Кол.»: для ГОСТ 2.106 — одна графа основного (первого включённого)
        /// исполнения, для ГОСТ 2.113 — все включённые.</summary>
        public List<Execution> QtyExecutions()
        {
            List<Execution> r = EnabledExecutions();
            if (Is2106 && r.Count > 1) r.RemoveRange(1, r.Count - 1);
            return r;
        }

        /// <summary>Входит ли строка в спецификацию по ГОСТ 2.106: применяется в основном исполнении или не имеет
        /// количества ни в одном исполнении (документ, свободная запись). В групповую входят все строки.</summary>
        public bool InSpec2106(SpecRow r)
        {
            if (!Is2106) return true;
            List<Execution> q = QtyExecutions();
            if (q.Count > 0 && r.GetQty(q[0].Key) != "") return true;
            foreach (Execution e in EnabledExecutions()) if (r.GetQty(e.Key) != "") return false;
            return true;
        }

        public string ToXml()
        {
            if (SpecType == null) SpecType = SpecTypes.Auto;   // новая спецификация: тип по числу 3D-моделей
            XmlSerializer xs = new XmlSerializer(typeof(SpecData));
            using (StringWriter sw = new StringWriter())
            {
                xs.Serialize(sw, this);
                return sw.ToString();
            }
        }

        public static SpecData FromXml(string xml)
        {
            XmlSerializer xs = new XmlSerializer(typeof(SpecData));
            using (StringReader sr = new StringReader(xml))
            {
                SpecData d = (SpecData)xs.Deserialize(sr);
                if (d.SpecType == null) d.SpecType = SpecTypes.Gost2113;   // сохранено до ГОСТ 2.106 — групповая
                return d;
            }
        }
    }

    /// <summary>Параметры (вкладка «Параметры») — общие для всех чертежей:
    /// %APPDATA%\GroupSpecB\Settings.xml.</summary>
    public static class GlobalSettings
    {
        static string FilePath
        {
            get
            {
                return System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                                              "GroupSpecB", "Settings.xml");
            }
        }

        public static bool Exists { get { return File.Exists(FilePath); } }

        public static SpecSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (StreamReader r = new StreamReader(FilePath))
                        return (SpecSettings)new XmlSerializer(typeof(SpecSettings)).Deserialize(r);
            }
            catch (Exception ex) { Log.Write("Чтение Settings.xml", ex); }
            return new SpecSettings();
        }

        public static void Save(SpecSettings s)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                using (StreamWriter w = new StreamWriter(FilePath))
                    new XmlSerializer(typeof(SpecSettings)).Serialize(w, s);
            }
            catch (Exception ex) { Log.Write("Запись Settings.xml", ex); }
        }
    }
}