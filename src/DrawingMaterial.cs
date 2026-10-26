using System;
using System.Collections.Generic;
using Inventor;

namespace GroupSpecB
{
    /// <summary>Пользовательские свойства материала в чертеже детали (SingleMaterial, RollingMaterial, MaterialUpLine,
    /// MaterialDownLine, MaterialLine) — из материала модели первого вида, как в ESKD Specia. Обновляются при
    /// активации чертежа, после вставки/замены/удаления вида и окна iProperties, перед печатью и сохранением.</summary>
    public static class DrawingMaterial
    {
        const string UserSet = "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}";   // Inventor User Defined Properties

        /// <summary>Команды, после которых свойства обновляются (как в Specia: AddPropertiesStart.OnTerminateCommand).</summary>
        public static readonly string[] Commands =
            { "DrawingBaseViewCmd", "DrawingReplaceModelReferenceCmd", "AppDeleteCmd", "DrawingiPropertiesCmd", "DrawingViewEditCtxCmd" };

        /// <summary>Записывает свойства материала. Для чертежа детали недостающие свойства создаются; в чертеже
        /// без детали существующие очищаются. Вручную заданный SingleMaterial («-», «см. …») не трогается.</summary>
        public static void Update(DrawingDocument dwg)
        {
            PropertySet ps = ((Document)dwg).PropertySets[UserSet];
            Document model = SpecTitleBlock.FirstViewModel(dwg);
            PartDocument part = model != null && model.DocumentType == DocumentTypeEnum.kPartDocumentObject ? (PartDocument)model : null;
            string single = Get(ps, MaterialLogic.Single);
            Dictionary<string, string> v;
            if (MaterialLogic.IsManual(single)) { v = MaterialLogic.Values(null); v[MaterialLogic.Single] = single; }
            else v = MaterialLogic.Values(part != null ? PartMaterial(part) : null);
            foreach (string n in MaterialLogic.All) Set(ps, n, v[n], part != null);
        }

        /// <summary>UserMass — масса модели первого вида (детали или сборки), UserScale — масштаб этого вида
        /// (см. MassScaleLogic). Недостающие свойства создаются, если вид есть; без вида существующие очищаются.
        /// Значение «-», «_» или «см. …» задано вручную и не перезаписывается.</summary>
        public static void UpdateMassScale(DrawingDocument dwg)
        {
            PropertySet ps = ((Document)dwg).PropertySets[UserSet];
            DrawingView view = SpecTitleBlock.FirstView(dwg);
            string mass = "", scale = "";
            if (view != null)
            {
                scale = MassScaleLogic.FormatScale(view.Scale);
                Document model = (Document)view.ReferencedDocumentDescriptor.ReferencedDocument;
                try { mass = MassScaleLogic.FormatMass(ModelMass(model)); }
                catch (Exception ex) { Log.Write("Масса модели " + model.DisplayName, ex); mass = null; }
            }
            if (mass != null && !MaterialLogic.IsManual(Get(ps, MassScaleLogic.Mass))) Set(ps, MassScaleLogic.Mass, mass, view != null);
            if (!MaterialLogic.IsManual(Get(ps, MassScaleLogic.Scale))) Set(ps, MassScaleLogic.Scale, scale, view != null);
        }

        /// <summary>Масса модели, кг (как Specia: ComponentDefinition.MassProperties.Mass).</summary>
        public static double ModelMass(Document model)
        {
            if (model.DocumentType == DocumentTypeEnum.kPartDocumentObject)
                return ((PartDocument)model).ComponentDefinition.MassProperties.Mass;
            return ((AssemblyDocument)model).ComponentDefinition.MassProperties.Mass;
        }

        /// <summary>Имя материала детали (как Specia: ComponentDefinition.Material.Name).</summary>
        public static string PartMaterial(PartDocument part)
        {
            try { return part.ComponentDefinition.Material.Name; }
            catch { }
            try { return part.ActiveMaterial.DisplayName; }
            catch (Exception ex) { Log.Write("Материал детали " + part.DisplayName, ex); return null; }
        }

        static Property Find(PropertySet ps, string name)
        {
            try { return ps[name]; }
            catch { return null; }
        }

        static string Get(PropertySet ps, string name)
        {
            Property p = Find(ps, name);
            if (p == null) return "";
            try { return p.Value == null ? "" : p.Value.ToString(); }
            catch { return ""; }
        }

        /// <summary>Записывает значение, только если оно изменилось (чтобы чертёж не помечался изменённым зря).</summary>
        static void Set(PropertySet ps, string name, string value, bool create)
        {
            Property p = Find(ps, name);
            if (p == null) { if (create) ps.Add(value, name); return; }
            if (Get(ps, name) != value) p.Value = value;
        }
    }
}
