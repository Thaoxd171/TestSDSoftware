using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SDSoftware.RevitTest.Extensions;

namespace SDSoftware.RevitTest.Features.Diagnostics
{
    /// <summary>
    /// Dumps everything the Bearing Plate tool needs to know about a model: categories present,
    /// assemblies and their local coordinate systems, candidate plate families, title blocks and
    /// view templates. Read-only — it never modifies the document.
    /// </summary>
    internal static class ModelProbeReport
    {
        private const int MaxRows = 40;

        /// <summary>Words that mark an element as a likely bearing plate (Danish "lejeplade").</summary>
        private static readonly string[] PlateKeywords = { "plate", "plade", "leje", "bearing" };

        public static string Build(UIApplication application)
        {
            var document = application.ActiveUIDocument.Document;
            var report = new StringBuilder();

            Section(report, "SD REVIT TEST - MODEL PROBE", () => Header(report, application, document));
            Section(report, "1. ELEMENT COUNT BY CATEGORY", () => CategoryCounts(report, document));
            Section(report, "2. ASSEMBLIES", () => Assemblies(report, document));
            Section(report, "3. CANDIDATE BEARING PLATES", () => CandidatePlates(report, document));
            Section(report, "4. PARAMETERS OF ONE SAMPLE PLATE", () => SampleParameters(report, document));
            Section(report, "5. TITLE BLOCKS", () => TitleBlocks(report, document));
            Section(report, "6. VIEW TEMPLATES", () => ViewTemplates(report, document));
            Section(report, "7. VIEWS AND SHEETS", () => ViewsAndSheets(report, document));
            Section(report, "8. VIEW FAMILY TYPES", () => ViewFamilyTypes(report, document));
            Section(report, "9. TAG FAMILIES", () => TagFamilies(report, document));
            Section(report, "10. DIMENSION AND TEXT TYPES", () => AnnotationTypes(report, document));

            return report.ToString();
        }

        // ------------------------------------------------------------------ sections

        private static void Header(StringBuilder report, UIApplication application, Document document)
        {
            var app = application.Application;
            report.AppendLine($"Generated      : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"Revit          : {app.VersionName} (build {app.VersionBuild})");
            report.AppendLine($"Document       : {document.Title}");
            report.AppendLine($"Path           : {document.PathName}");
            report.AppendLine($"Workshared     : {document.IsWorkshared}");

            var view = document.ActiveView;
            report.AppendLine($"Active view    : \"{view.Name}\" ({view.ViewType}) scale 1:{view.Scale}");
            report.AppendLine($"View template  : {TemplateNameOf(document, view)}");

            var lengthUnit = document.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();
            report.AppendLine($"Length unit    : {lengthUnit.TypeId}");
        }

        private static void CategoryCounts(StringBuilder report, Document document)
        {
            var groups = new FilteredElementCollector(document)
                .WhereElementIsNotElementType()
                .Where(e => e.Category != null)
                .GroupBy(e => e.Category.Name)
                .OrderByDescending(g => g.Count())
                .ToList();

            report.AppendLine($"{"Category",-42}{"Count",8}   BuiltInCategory");
            foreach (var group in groups.Take(MaxRows))
            {
                var category = group.First().Category;
                var builtIn = (BuiltInCategory)category.Id.ToLong();
                report.AppendLine($"{Trim(group.Key, 42),-42}{group.Count(),8}   {builtIn}");
            }

            if (groups.Count > MaxRows)
            {
                report.AppendLine($"... and {groups.Count - MaxRows} more categories");
            }
        }

        private static void Assemblies(StringBuilder report, Document document)
        {
            var assemblies = document.OfClass<AssemblyInstance>().ToList();
            report.AppendLine($"AssemblyInstance count : {assemblies.Count}");

            if (assemblies.Count == 0)
            {
                report.AppendLine("(no assemblies in this model)");
                return;
            }

            report.AppendLine();
            report.AppendLine("Assembly types:");
            foreach (var group in assemblies.GroupBy(a => a.AssemblyTypeName).OrderBy(g => g.Key))
            {
                report.AppendLine($"  \"{group.Key}\" x {group.Count()} instance(s)");
            }

            report.AppendLine();
            foreach (var assembly in assemblies.Take(MaxRows))
            {
                var members = assembly.GetMemberIds();
                var transform = assembly.GetTransform();
                var box = assembly.get_BoundingBox(null);

                report.AppendLine($"[{assembly.Id.ToLong()}] Name=\"{assembly.Name}\" Type=\"{assembly.AssemblyTypeName}\"");
                report.AppendLine($"    NamingCategory : {NamingCategoryOf(document, assembly)}");
                report.AppendLine($"    Members        : {members.Count}");
                report.AppendLine($"    Origin (mm)    : {Mm(transform.Origin)}");
                report.AppendLine($"    BasisX/Y/Z     : {Vec(transform.BasisX)}  {Vec(transform.BasisY)}  {Vec(transform.BasisZ)}");
                report.AppendLine($"    BBox size (mm) : {BoxSize(box)}");

                foreach (var id in members.Take(8))
                {
                    var member = document.GetElement(id);
                    report.AppendLine($"      - {Describe(member)}");
                }

                if (members.Count > 8)
                {
                    report.AppendLine($"      ... and {members.Count - 8} more members");
                }

                report.AppendLine();
            }

            if (assemblies.Count > MaxRows)
            {
                report.AppendLine($"... and {assemblies.Count - MaxRows} more assemblies");
            }
        }

        private static void CandidatePlates(StringBuilder report, Document document)
        {
            var candidates = FindPlates(document);
            report.AppendLine($"Elements whose family/type/category matches [{string.Join(", ", PlateKeywords)}] : {candidates.Count}");

            if (candidates.Count == 0)
            {
                report.AppendLine("(none - the plates are probably named differently; see section 1)");
                return;
            }

            report.AppendLine();
            report.AppendLine($"{"Category",-26}{"Family",-30}{"Type",-26}{"Count",6}   BBox LxWxH (mm)");

            foreach (var group in candidates
                         .GroupBy(e => new
                         {
                             Category = e.Category?.Name ?? "?",
                             Family = FamilyNameOf(e),
                             Type = document.GetElement(e.GetTypeId())?.Name ?? "?",
                         })
                         .OrderByDescending(g => g.Count())
                         .Take(MaxRows))
            {
                var box = group.First().get_BoundingBox(null);
                report.AppendLine(
                    $"{Trim(group.Key.Category, 26),-26}{Trim(group.Key.Family, 30),-30}" +
                    $"{Trim(group.Key.Type, 26),-26}{group.Count(),6}   {BoxSize(box)}");
            }

            report.AppendLine();
            report.AppendLine("Per-instance detail (first 12):");
            foreach (var element in candidates.Take(12))
            {
                var assembly = element.AssemblyInstanceId != null && element.AssemblyInstanceId != ElementId.InvalidElementId
                    ? document.GetElement(element.AssemblyInstanceId)?.Name
                    : null;

                report.AppendLine($"  {Describe(element)}");
                report.AppendLine($"      Assembly   : {assembly ?? "(none)"}");
                report.AppendLine($"      Location   : {LocationOf(element)}");
                report.AppendLine($"      Facing/Hand: {OrientationOf(element)}");
                report.AppendLine($"      Level      : {LevelNameOf(document, element)}");
                report.AppendLine($"      Host       : {HostOf(element)}");
            }
        }

        private static void SampleParameters(StringBuilder report, Document document)
        {
            var sample = FindPlates(document).FirstOrDefault();
            if (sample == null)
            {
                report.AppendLine("(no candidate plate found - skipped)");
                return;
            }

            report.AppendLine($"Sample element : {Describe(sample)}");
            report.AppendLine();
            report.AppendLine("INSTANCE PARAMETERS");
            DumpParameters(report, sample);

            var type = document.GetElement(sample.GetTypeId());
            if (type != null)
            {
                report.AppendLine();
                report.AppendLine($"TYPE PARAMETERS  (type \"{type.Name}\")");
                DumpParameters(report, type);
            }
        }

        private static void TitleBlocks(StringBuilder report, Document document)
        {
            var titleBlocks = new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .Cast<FamilySymbol>()
                .ToList();

            report.AppendLine($"Title block types : {titleBlocks.Count}");
            foreach (var titleBlock in titleBlocks.Take(MaxRows))
            {
                var width = titleBlock.GetLength(BuiltInParameter.SHEET_WIDTH);
                var height = titleBlock.GetLength(BuiltInParameter.SHEET_HEIGHT);
                report.AppendLine(
                    $"  [{titleBlock.Id.ToLong()}] {titleBlock.FamilyName} : {titleBlock.Name}" +
                    $"   sheet {width.FeetToMm():0} x {height.FeetToMm():0} mm");
            }
        }

        private static void ViewTemplates(StringBuilder report, Document document)
        {
            var templates = document.OfClass<View>()
                .Where(v => v.IsTemplate)
                .OrderBy(v => v.ViewType.ToString())
                .ToList();

            report.AppendLine($"View templates : {templates.Count}");
            foreach (var template in templates.Take(MaxRows))
            {
                report.AppendLine($"  [{template.Id.ToLong()}] {template.ViewType,-18} \"{template.Name}\"  scale 1:{template.Scale}");
            }
        }

        private static void ViewsAndSheets(StringBuilder report, Document document)
        {
            var views = document.OfClass<View>().Where(v => !v.IsTemplate).ToList();

            report.AppendLine("Views by type:");
            foreach (var group in views.GroupBy(v => v.ViewType).OrderByDescending(g => g.Count()))
            {
                report.AppendLine($"  {group.Key,-22}{group.Count(),5}");
            }

            var sheets = document.OfClass<ViewSheet>().OrderBy(s => s.SheetNumber).ToList();
            report.AppendLine();
            report.AppendLine($"Sheets : {sheets.Count}");
            foreach (var sheet in sheets.Take(MaxRows))
            {
                var viewports = sheet.GetAllViewports().Count;
                report.AppendLine($"  [{sheet.Id.ToLong()}] {sheet.SheetNumber,-14} \"{sheet.Name}\"  viewports={viewports}");
            }
        }

        private static void ViewFamilyTypes(StringBuilder report, Document document)
        {
            var types = document.OfClass<ViewFamilyType>()
                .OrderBy(t => t.ViewFamily.ToString())
                .ToList();

            report.AppendLine($"View family types : {types.Count}");
            foreach (var type in types.Take(MaxRows))
            {
                report.AppendLine($"  [{type.Id.ToLong()}] {type.ViewFamily,-20} \"{type.Name}\"");
            }
        }

        private static void TagFamilies(StringBuilder report, Document document)
        {
            var tags = new FilteredElementCollector(document)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Category != null && s.Category.Name.IndexOf("tag", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(s => s.Category.Name)
                .ToList();

            report.AppendLine($"Tag types : {tags.Count}");
            foreach (var tag in tags.Take(MaxRows))
            {
                report.AppendLine($"  [{tag.Id.ToLong()}] {Trim(tag.Category.Name, 28),-28} {tag.FamilyName} : {tag.Name}");
            }
        }

        private static void AnnotationTypes(StringBuilder report, Document document)
        {
            var dimensionTypes = document.OfClass<DimensionType>()
                .Where(t => !string.IsNullOrEmpty(t.Name))
                .ToList();

            report.AppendLine($"Dimension types : {dimensionTypes.Count}");
            foreach (var type in dimensionTypes.Take(20))
            {
                report.AppendLine($"  [{type.Id.ToLong()}] {type.StyleType,-16} \"{type.Name}\"");
            }

            var textTypes = document.OfClass<TextNoteType>().ToList();
            report.AppendLine();
            report.AppendLine($"Text types : {textTypes.Count}");
            foreach (var type in textTypes.Take(20))
            {
                report.AppendLine($"  [{type.Id.ToLong()}] \"{type.Name}\"");
            }
        }

        // ------------------------------------------------------------------ helpers

        private static List<Element> FindPlates(Document document)
        {
            return new FilteredElementCollector(document)
                .WhereElementIsNotElementType()
                .Where(e => e.Category != null && Matches(document, e))
                .ToList();
        }

        private static bool Matches(Document document, Element element)
        {
            var typeName = document.GetElement(element.GetTypeId())?.Name ?? string.Empty;
            var haystack = $"{element.Category.Name} {FamilyNameOf(element)} {typeName} {element.Name}";
            return PlateKeywords.Any(k => haystack.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string FamilyNameOf(Element element)
        {
            if (element is FamilyInstance instance)
            {
                return instance.Symbol?.FamilyName ?? "?";
            }

            return element.GetTypeAs<ElementType>()?.FamilyName ?? "(system)";
        }

        private static void DumpParameters(StringBuilder report, Element element)
        {
            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter?.Definition == null)
                {
                    continue;
                }

                var builtIn = parameter.Definition is InternalDefinition internalDefinition
                    ? internalDefinition.BuiltInParameter.ToString()
                    : "(shared)";

                report.AppendLine(
                    $"  {Trim(parameter.Definition.Name, 34),-34} = {Trim(ValueOf(parameter), 30),-30}" +
                    $" [{parameter.StorageType}] {builtIn}");
            }
        }

        private static string ValueOf(Parameter parameter)
        {
            if (!parameter.HasValue)
            {
                return "(none)";
            }

            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    return $"{parameter.AsValueString()} ({parameter.AsDouble().FeetToMm():0.##} mm)";
                case StorageType.Integer:
                    return parameter.AsValueString() ?? parameter.AsInteger().ToString();
                case StorageType.String:
                    return parameter.AsString();
                case StorageType.ElementId:
                    return $"id {parameter.AsElementId().ToLong()}";
                default:
                    return "(none)";
            }
        }

        private static string Describe(Element element)
        {
            if (element == null)
            {
                return "(null)";
            }

            var typeName = element.Document.GetElement(element.GetTypeId())?.Name ?? "?";
            return $"id={element.Id.ToLong()} cat=\"{element.Category?.Name}\" family=\"{FamilyNameOf(element)}\" type=\"{typeName}\"";
        }

        private static string LocationOf(Element element)
        {
            var point = element.GetLocationPoint();
            if (point != null)
            {
                return $"point {Mm(point)}";
            }

            var line = element.GetLocationLine();
            if (line != null)
            {
                return $"line {Mm(line.GetEndPoint(0))} -> {Mm(line.GetEndPoint(1))}";
            }

            return "(no location)";
        }

        private static string OrientationOf(Element element)
        {
            if (element is FamilyInstance instance)
            {
                var rotation = (element.Location as LocationPoint)?.Rotation ?? 0;
                return $"facing={Vec(instance.FacingOrientation)} hand={Vec(instance.HandOrientation)} " +
                       $"rotation={rotation.RadiansToDegrees():0.##} deg  mirrored={instance.Mirrored}";
            }

            return "(not a family instance)";
        }

        private static string LevelNameOf(Document document, Element element)
        {
            return document.GetElement(element.LevelId)?.Name ?? "(none)";
        }

        private static string HostOf(Element element)
        {
            var host = (element as FamilyInstance)?.Host;
            return host == null ? "(none)" : Describe(host);
        }

        private static string NamingCategoryOf(Document document, AssemblyInstance assembly)
        {
            var id = assembly.NamingCategoryId;
            if (id == null || id == ElementId.InvalidElementId)
            {
                return "(none)";
            }

            return Category.GetCategory(document, id)?.Name ?? id.ToLong().ToString();
        }

        private static string TemplateNameOf(Document document, View view)
        {
            var id = view.ViewTemplateId;
            return id == null || id == ElementId.InvalidElementId
                ? "(none)"
                : document.GetElement(id)?.Name ?? "?";
        }

        private static string Mm(XYZ point)
        {
            return point == null
                ? "(null)"
                : $"({point.X.FeetToMm():0.#}, {point.Y.FeetToMm():0.#}, {point.Z.FeetToMm():0.#})";
        }

        private static string Vec(XYZ vector)
        {
            return vector == null ? "(null)" : $"({vector.X:0.###}, {vector.Y:0.###}, {vector.Z:0.###})";
        }

        private static string BoxSize(BoundingBoxXYZ box)
        {
            if (box == null)
            {
                return "(no bbox)";
            }

            var size = box.Max - box.Min;
            return $"{size.X.FeetToMm():0.#} x {size.Y.FeetToMm():0.#} x {size.Z.FeetToMm():0.#}";
        }

        private static string Trim(string value, int length)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= length ? value : value.Substring(0, length - 1) + "~";
        }

        private static void Section(StringBuilder report, string title, Action body)
        {
            report.AppendLine();
            report.AppendLine("=======================================================================");
            report.AppendLine("  " + title);
            report.AppendLine("=======================================================================");

            try
            {
                body();
            }
            catch (Exception ex)
            {
                report.AppendLine("!! section failed: " + ex.Message);
            }
        }
    }
}
