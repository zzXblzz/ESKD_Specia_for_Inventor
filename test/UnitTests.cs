using System;
using System.Collections.Generic;
using System.Text;
using GroupSpecB;

// Проверки логики спецификации без Inventor: сведение BOM, разделы, позиции, исполнения, вёрстка.
// Собираются из Model.cs, SpecLogic.cs, Layout.cs, Log.cs (без Inventor API) — build.ps1 -Test.
// Код возврата: 0 — все проверки прошли, 1 — есть ошибки.
static class UnitTests
{
    static int failed, passed;

    static void Check(bool ok, string what)
    {
        if (ok) passed++;
        else { failed++; Console.WriteLine("ОШИБКА: " + what); }
    }

    static void Eq(object expected, object actual, string what)
    {
        Check(Equals(expected, actual), what + ": ожидалось «" + expected + "», получено «" + actual + "»");
    }

    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Run("Разбор номера исполнения", SplitExecution);
        Run("Количество", FormatQty);
        Run("Слияние строк и единицы измерения", MergeUnits);
        Run("Подписи граф исполнений", ExecutionLabels);
        Run("Позиции и резерв между разделами", Positions);
        Run("Сокращение «-02» только для обозначений ЕСКД", Abbreviation);
        Run("Запись длиннее листа", LongRecord);
        Run("Больше 10 исполнений", Decades);
        Run("Перенос по замеру текста", WrapMeasured);
        Run("Свойства материала в чертеже", Material);
        Run("Тип спецификации по числу 3D-моделей", SpecTypeAuto);
        Run("Спецификация по ГОСТ 2.106", Gost2106);
        Console.WriteLine("Пройдено: " + passed + ", ошибок: " + failed);
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("ОШИБКА: " + name + ": " + ex); }
    }

    // ------------------------------------------------------------------ помощники

    static Execution Exec(string designation)
    {
        return new Execution { Key = designation, Designation = designation, Kind = "File", Path = designation };
    }

    static BomItem Item(string key, string designation, string name, string section, string qty, string unit = "")
    {
        return new BomItem { Key = key, Designation = designation, Name = name, Section = section, Qty = qty, Unit = unit,
                             Format = "", Note = "", ItemNumber = "" };
    }

    static SpecData Merge(params ExecutionBom[] boms)
    {
        SpecData data = new SpecData();
        data.Settings.AddAssemblyDrawing = false;
        SpecLogic.Merge(data, new List<ExecutionBom>(boms));
        return data;
    }

    static ExecutionBom Bom(Execution e, params BomItem[] items)
    {
        return new ExecutionBom { Execution = e, Items = new List<BomItem>(items) };
    }

    static List<SpecLine> DataLines(List<SpecPage> pages)
    {
        List<SpecLine> r = new List<SpecLine>();
        foreach (SpecPage p in pages) foreach (SpecLine l in p.Lines) if (l.Kind == LineKind.Data) r.Add(l);
        return r;
    }

    // ------------------------------------------------------------------ проверки

    static void SplitExecution()
    {
        string b, n;
        Check(SpecLogic.SplitExecution("АБВГ.123456.789-02", out b, out n), "АБВГ.123456.789-02 — исполнение");
        Eq("АБВГ.123456.789", b, "базовое обозначение");
        Eq("02", n, "номер исполнения");
        Check(!SpecLogic.SplitExecution("АБВГ.123456.789", out b, out n), "без номера — основное исполнение");
        Eq("АБВГ.123456.789", SpecLogic.StripDocCode("АБВГ.123456.789СБ"), "код документа СБ отбрасывается");
        Check(!SpecLogic.SplitExecution("ГОСТ 7798-70", out b, out n), "«ГОСТ 7798-70» — не исполнение");
        Check(!SpecLogic.SplitExecution("Болт М8х20 ГОСТ 7798-70", out b, out n), "наименование с ГОСТ — не исполнение");
        Check(!SpecLogic.SplitExecution("DIN 6921-8", out b, out n), "«DIN 6921-8» — не исполнение");
        Eq("АБВГ.123456.789", SpecLogic.WithoutExecution("АБВГ.123456.789-01"), "обозначение фабрики без «-01»");
        Eq("АБВГ.123456.789", SpecLogic.WithoutExecution("АБВГ.123456.789"), "основное исполнение не меняется");
        Eq("DIN 6921-8", SpecLogic.WithoutExecution("DIN 6921-8"), "«DIN 6921-8» не меняется");
    }

    static void FormatQty()
    {
        Eq("666,667", SpecLogic.FormatQty(666.6666), "округление до 3 знаков");
        Eq("0,5", SpecLogic.FormatQty(0.5), "нули в конце не пишутся");
        Eq("2", SpecLogic.FormatQty(2), "целое");
    }

    static void MergeUnits()
    {
        Execution e = Exec("АБВГ.100000.001");
        SpecData data = Merge(Bom(e,
            Item("a.ipt", "АБВГ.200000.001", "Панель", Sections.Parts, "1"),
            Item("a_copy.ipt", "АБВГ.200000.001", "Панель", Sections.Parts, "2"),
            Item("m1", "", "Лист Б-ПН-2 ГОСТ 19903-2015", Sections.Materials, "0,25", "кг"),
            Item("m2", "", "Лист Б-ПН-2 ГОСТ 19903-2015", Sections.Materials, "120", "мм")));
        SpecRow panel = data.Rows.Find(r => r.Designation == "АБВГ.200000.001");
        Eq("3", panel.GetQty(e.Key), "копии одной детали сливаются, количества складываются");
        Eq(1, panel.MergedKeys.Count, "второй файл записан в MergedKeys");
        Eq(2, data.Rows.FindAll(r => r.Section == Sections.Materials).Count, "материал в кг и в мм — две строки");
    }

    static void ExecutionLabels()
    {
        Execution main = Exec("АБВГ.100000.001"), e2 = Exec("АБВГ.100000.001-02"), e1 = Exec("АБВГ.100000.001-01");
        SpecData data = Merge(Bom(e2, Item("x", "АБВГ.1.1", "Деталь", Sections.Parts, "1")),
                              Bom(main, Item("x", "АБВГ.1.1", "Деталь", Sections.Parts, "1")),
                              Bom(e1, Item("x", "АБВГ.1.1", "Деталь", Sections.Parts, "1")));
        Eq("АБВГ.100000.001", data.Executions[0].Designation, "основное исполнение — первая графа");
        Eq("–", data.Executions[0].Label, "подпись основного исполнения");
        Eq("01", data.Executions[1].Label, "подпись -01");
        Eq("02", data.Executions[2].Label, "подпись -02");
        Eq("АБВГ.100000.001", SpecLogic.BaseDesignation(data), "базовое обозначение");
    }

    static void Positions()
    {
        Execution e = Exec("АБВГ.100000.001");
        SpecData data = Merge(Bom(e,
            Item("p1", "АБВГ.200000.001", "Деталь А", Sections.Parts, "1"),
            Item("p2", "АБВГ.200000.002", "Деталь Б", Sections.Parts, "1"),
            Item("s1", "", "Болт М8х20 ГОСТ 7798-70", Sections.Standard, "4")));
        Eq("1", data.Rows.Find(r => r.Key == "p1").Pos, "первая деталь");
        Eq("2", data.Rows.Find(r => r.Key == "p2").Pos, "вторая деталь");
        Eq("5", data.Rows.Find(r => r.Key == "s1").Pos, "следующий раздел — после резерва в 2 номера");
        Eq(0, SpecLogic.DuplicatePositions(data).Count, "повторов позиций нет");
    }

    static void Abbreviation()
    {
        Execution e = Exec("АБВГ.100000.001");
        SpecData data = Merge(Bom(e,
            Item("d1", "АБВГ.300000.001", "Скоба", Sections.Parts, "1"),
            Item("d2", "АБВГ.300000.001-01", "Скоба", Sections.Parts, "1"),
            Item("d3", "ГОСТ 1050-88", "Пруток", Sections.Parts, "1"),
            Item("d4", "ГОСТ 1050-89", "Пруток", Sections.Parts, "1")));
        data.SpecType = SpecTypes.Gost2113;   // одна модель — по умолчанию ГОСТ 2.106, где «-01» не сокращается
        List<SpecLine> lines = DataLines(Layout.Build(data));
        SpecLine l2 = lines.Find(l => l.FirstOfRow && l.Row.Key == "d2");
        Eq("-01", l2.Designation, "исполнение той же детали подряд — «-01»");
        Eq("АБВГ.300000.001", l2.AbbrevPrefix, "смещение — на ширину базового обозначения");
        SpecLine l4 = lines.Find(l => l.FirstOfRow && l.Row.Key == "d4");
        Eq("ГОСТ 1050-89", l4.Designation, "обозначение со стандартом не сокращается");
    }

    static void LongRecord()
    {
        Execution e = Exec("АБВГ.100000.001");
        StringBuilder name = new StringBuilder();
        for (int i = 1; i <= 25; i++) name.Append(i == 1 ? "" : "$").Append("строка " + i);
        SpecData data = Merge(Bom(e, Item("long", "АБВГ.400000.001", name.ToString(), Sections.Parts, "1")));
        data.SpecType = SpecTypes.Gost2113;   // 16 строк на первом листе формы 1б
        List<string> warnings = new List<string>();
        List<SpecPage> pages = Layout.Build(data, warnings);
        Check(pages.Count >= 2, "запись длиннее листа продолжается на следующем листе");
        Eq(25, DataLines(pages).Count, "ни одна строка записи не потеряна");
        foreach (SpecPage p in pages) Check(p.Lines.Count <= Layout.Capacity(p, data.Settings), "лист " + p.Number + " не переполнен");
        Eq(1, warnings.Count, "предупреждение о разрыве записи");
    }

    static void Decades()
    {
        List<ExecutionBom> boms = new List<ExecutionBom>();
        for (int i = 0; i <= 11; i++)
        {
            Execution e = Exec("АБВГ.100000.001" + (i == 0 ? "" : "-" + i.ToString("00")));
            boms.Add(Bom(e, Item("p", "АБВГ.200000.001", "Деталь", Sections.Parts, "1")));
        }
        SpecData data = Merge(boms.ToArray());
        data.Rows.Add(new SpecRow { Source = RowSource.Manual, Section = Sections.Other, Name = "Запись без количества" });
        List<SpecPage> pages = Layout.Build(data);
        Check(pages.Exists(p => p.Decade == 1), "второй десяток исполнений — на отдельных листах");
        Check(DataLines(pages).Exists(l => l.Row != null && l.Row.Name == "Запись без количества"),
              "строка без количества не пропадает при больше чем 10 исполнениях");
    }

    static void WrapMeasured()
    {
        SpecSettings s = new SpecSettings();
        Func<string, double> measure = t => t.Length;   // 1 мм на символ
        List<string> lines = Layout.Wrap("aaaa bbbb cccc", 12, s, measure);   // доступно 10 мм
        Eq(2, lines.Count, "две строки");
        Eq("aaaa bbbb", lines[0], "первая строка");
        Eq("cccc", lines[1], "вторая строка");
        lines = Layout.Wrap("abcdefghijklmn", 7, s, measure);   // доступно 5 мм — слово режется
        Eq("abcde", lines[0], "длинное слово режется по ширине");
    }

    static void Material()
    {
        Dictionary<string, string> v = MaterialLogic.Values("Круг 20-h11\\Сталь 45-2-Т\\ГОСТ 1050-2013");
        Eq("Круг 20-h11", v[MaterialLogic.Rolling], "сортамент");
        Eq("Сталь 45-2-Т", v[MaterialLogic.UpLine], "над чертой");
        Eq("ГОСТ 1050-2013", v[MaterialLogic.DownLine], "под чертой");
        Eq(new string('_', 15), v[MaterialLogic.Line], "черта — длина большей строки + 1");
        Eq("", v[MaterialLogic.Single], "составной материал — SingleMaterial пуст");

        v = MaterialLogic.Values("Лист 2|Ст3|ГОСТ 16523-97");
        Eq("Ст3", v[MaterialLogic.UpLine], "разделитель «|»");
        Eq(new string('_', 14), v[MaterialLogic.Line], "черта по нижней строке");

        v = MaterialLogic.Values("Сталь 12Х18Н10Т");
        Eq("Сталь 12Х18Н10Т", v[MaterialLogic.Single], "простой материал — в SingleMaterial");
        Eq("", v[MaterialLogic.Rolling] + v[MaterialLogic.UpLine] + v[MaterialLogic.DownLine] + v[MaterialLogic.Line], "дробь пуста");
        Eq("A\\B", MaterialLogic.Values("A\\B")[MaterialLogic.Single], "две части — не дробь");

        v = MaterialLogic.Values(null);
        Eq(5, v.Count, "без детали — все пять свойств");
        Check(new List<string>(v.Values).TrueForAll(s => s == ""), "без детали — все пусты");

        Check(MaterialLogic.IsManual(" - "), "«-» — задано вручную");
        Check(MaterialLogic.IsManual("См. чертёж"), "«см. …» — задано вручную");
        Check(!MaterialLogic.IsManual("Сталь 45"), "материал — не вручную");
        Check(!MaterialLogic.IsManual(""), "пусто — не вручную");
    }

    static void SpecTypeAuto()
    {
        Execution main = Exec("АБВГ.100000.001"), e2 = Exec("АБВГ.100000.001-02");
        SpecData one = Merge(Bom(main, Item("x", "АБВГ.1.1", "Деталь", Sections.Parts, "1")));
        Eq(SpecTypes.Gost2106, one.EffectiveType(), "одна 3D-модель — ГОСТ 2.106");
        SpecData two = Merge(Bom(main, Item("x", "АБВГ.1.1", "Деталь", Sections.Parts, "1")),
                             Bom(e2, Item("x", "АБВГ.1.1", "Деталь", Sections.Parts, "2")));
        Eq(SpecTypes.Gost2113, two.EffectiveType(), "несколько 3D-моделей — ГОСТ 2.113");
        two.SpecType = SpecTypes.Gost2106;
        Eq(SpecTypes.Gost2106, two.EffectiveType(), "ручное переключение");

        Eq(SpecTypes.Auto, SpecData.FromXml(one.ToXml()).SpecType, "новая спецификация сохраняется с типом «по числу моделей»");
        string legacy = one.ToXml().Replace("<SpecType />", "");
        Check(!legacy.Contains("SpecType"), "данные без SpecType (сохранены до ГОСТ 2.106)");
        Eq(SpecTypes.Gost2113, SpecData.FromXml(legacy).SpecType, "старые данные — групповая спецификация");
    }

    static void Gost2106()
    {
        Execution main = Exec("АБВГ.100000.001-01"), e2 = Exec("АБВГ.100000.001-02");
        SpecData data = Merge(
            Bom(main, Item("d1", "АБВГ.300000.001", "Скоба", Sections.Parts, "2"),
                      Item("d2", "АБВГ.300000.001-01", "Скоба", Sections.Parts, "1")),
            Bom(e2, Item("d3", "АБВГ.300000.002", "Только в -02", Sections.Parts, "1")));
        data.SpecType = SpecTypes.Gost2106;
        data.Settings.AddAssemblyDrawing = true;
        SpecLogic.EnsureAssemblyDrawingRow(data);
        SpecLogic.Renumber(data);
        Eq("1", data.Rows.Find(r => r.Key == "d1").Pos, "позиции подряд: первая");
        Eq("2", data.Rows.Find(r => r.Key == "d2").Pos, "позиции подряд: вторая");
        Eq("", data.Rows.Find(r => r.Key == "d3").Pos, "строка только другого исполнения без позиции");
        SpecRow asm = data.Rows.Find(r => r.Key == SpecLogic.AsmDrawingKey);
        Eq("АБВГ.100000.001-01СБ", asm.Designation, "сборочный чертёж — полное обозначение изделия");

        List<SpecPage> pages = Layout.Build(data);
        Check(pages.TrueForAll(p => p.Gost2106), "листы ГОСТ 2.106");
        Eq(29, Layout.Capacity(pages[0], data.Settings), "строк на первом листе (форма 1)");
        Eq(1, pages[0].Execs.Count, "одна графа «Кол.»");
        List<SpecLine> lines = DataLines(pages);
        Check(!lines.Exists(l => l.Row != null && l.Row.Key == "d3"), "строка только другого исполнения не входит");
        SpecLine d1 = lines.Find(l => l.FirstOfRow && l.Row.Key == "d1");
        Eq("2", d1.Qty[0], "количество основного исполнения");
        SpecLine d2 = lines.Find(l => l.FirstOfRow && l.Row.Key == "d2");
        Eq("АБВГ.300000.001-01", d2.Designation, "обозначение не сокращается до «-01»");
        SpecLine doc = lines.Find(l => l.FirstOfRow && l.Row.Key == SpecLogic.AsmDrawingKey);
        Check(string.IsNullOrEmpty(doc.Qty[0]), "у документа графа «Кол.» пуста");

        SpecPage next = new SpecPage { Number = 2, Gost2106 = true };
        Eq(32, Layout.Capacity(next, data.Settings), "строк на последующих листах (форма 1а)");
    }
}
