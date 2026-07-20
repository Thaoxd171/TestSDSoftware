using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SDSoftware.RevitTest.Extensions;
using static SDSoftware.RevitTest.Features.Diagnostics.ProbeFormat;

namespace SDSoftware.RevitTest.Features.Diagnostics
{
    /// <summary>
    /// Deep dive into one bearing plate assembly and the drawing produced for it: the assembly members,
    /// the views bound to the assembly, the sheet layout down to viewport centres in mm, and the
    /// dimensions, tags and schedules placed on it. This is the blueprint the generator must reproduce.
    /// </summary>
    internal static class BearingPlateProbeReport
    {
        public static string Build(UIApplication application)
        {
            var uiDocument = application.ActiveUIDocument;
            var document = uiDocument.Document;
            var report = new StringBuilder();

            var assembly = PickAssembly(uiDocument);
            if (assembly == null)
            {
                return "No AssemblyInstance found in this model.";
            }

            Section(report, "SD REVIT TEST - BEARING PLATE DEEP DIVE", () =>
            {
                report.AppendLine($"Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                report.AppendLine($"Document  : {document.Title}");
                report.AppendLine($"Assembly  : \"{assembly.Name}\" (id {assembly.Id.ToLong()})");
                report.AppendLine("Tip       : select an assembly before running to inspect that one instead.");
            });

            Section(report, "A. ASSEMBLY MEMBERS", () => Members(report, document, assembly));
            Section(report, "B. VIEWS BOUND TO THE ASSEMBLY", () => AssemblyViews(report, document, assembly));
            Section(report, "C. SHEET LAYOUT", () => SheetLayout(report, document, assembly));
            Section(report, "D. ANNOTATION IN THE ASSEMBLY VIEWS", () => Annotation(report, document, assembly));
            Section(report, "E. SCHEDULE DEFINITIONS", () => Schedules(report, document, assembly));
            Section(report, "F. NAMING ACROSS ALL ASSEMBLIES", () => NamingSummary(report, document));
            Section(report, "G. TITLE BLOCK AND LAYOUT ON EVERY SHEET", () => LayoutSummary(report, document));

            return report.ToString();
        }

        // ------------------------------------------------------------------ sections

        private static void Members(StringBuilder report, Document document, AssemblyInstance assembly)
        {
            var transform = assembly.GetTransform();
            report.AppendLine($"Origin (mm) : {Mm(transform.Origin)}");
            report.AppendLine($"BasisX/Y/Z  : {Vec(transform.BasisX)}  {Vec(transform.BasisY)}  {Vec(transform.BasisZ)}");
            report.AppendLine($"Naming cat  : {NameOf(document, assembly.NamingCategoryId)}");
            report.AppendLine();

            var members = assembly.GetMemberIds().Select(document.GetElement).Where(e => e != null).ToList();
            report.AppendLine($"Members ({members.Count}):");
            foreach (var member in members)
            {
                var box = member.get_BoundingBox(null);
                report.AppendLine($"  {Describe(member)}");
                report.AppendLine($"      bbox {BoxSize(box)}   min {Mm(box?.Min)}   {LocationOf(member)}");
            }

            // Full parameters for one element of each distinct family - that is where the plate
            // dimensions, component data and mark values live.
            report.AppendLine();
            foreach (var group in members.GroupBy(FamilyNameOf))
            {
                var sample = group.First();
                report.AppendLine($"--- FULL PARAMETERS: family \"{group.Key}\" ({group.Count()} member(s)), sample {Describe(sample)}");
                DumpParameters(report, sample, "      ");

                var type = document.GetElement(sample.GetTypeId());
                if (type != null)
                {
                    report.AppendLine($"      TYPE \"{type.Name}\"");
                    DumpParameters(report, type, "        ");
                }

                report.AppendLine();
            }
        }

        private static void AssemblyViews(StringBuilder report, Document document, AssemblyInstance assembly)
        {
            var views = ViewsOf(document, assembly);
            report.AppendLine($"Views with AssociatedAssemblyInstanceId = {assembly.Id.ToLong()} : {views.Count}");

            foreach (var view in views)
            {
                report.AppendLine();
                report.AppendLine($"[{view.Id.ToLong()}] {view.ViewType,-14} \"{view.Name}\"");
                report.AppendLine($"    Template    : {NameOf(document, view.ViewTemplateId)}");
                report.AppendLine($"    Scale       : 1:{view.Scale}    DetailLevel: {view.DetailLevel}    Display: {view.DisplayStyle}");

                TryLine(report, "    Origin (mm) : ", () => Mm(view.Origin));
                TryLine(report, "    ViewDir     : ", () => Vec(view.ViewDirection));
                TryLine(report, "    RightDir    : ", () => Vec(view.RightDirection));
                TryLine(report, "    UpDir       : ", () => Vec(view.UpDirection));

                report.AppendLine($"    CropActive  : {view.CropBoxActive}   CropVisible: {view.CropBoxVisible}");
                TryLine(report, "    CropBox     : ", () =>
                {
                    var crop = view.CropBox;
                    return $"min {Mm(crop.Min)}  max {Mm(crop.Max)}  size {BoxSize(crop)}";
                });

                if (view is ViewSection section)
                {
                    TryLine(report, "    Section box : ", () => BoxSize(section.get_BoundingBox(null)));
                }
            }
        }

        private static void SheetLayout(StringBuilder report, Document document, AssemblyInstance assembly)
        {
            var viewIds = ViewsOf(document, assembly).Select(v => v.Id.ToLong()).ToHashSet();

            var sheets = document.OfClass<ViewSheet>()
                .Where(s => s.GetAllViewports()
                    .Select(id => (document.GetElement(id) as Viewport)?.ViewId.ToLong() ?? -1)
                    .Any(viewIds.Contains))
                .ToList();

            report.AppendLine($"Sheets carrying views of this assembly : {sheets.Count}");

            foreach (var sheet in sheets)
            {
                report.AppendLine();
                report.AppendLine($"[{sheet.Id.ToLong()}] number=\"{sheet.SheetNumber}\"  name=\"{sheet.Name}\"");
                report.AppendLine($"    AssociatedAssembly : {NameOf(document, sheet.AssociatedAssemblyInstanceId)}");

                var titleBlock = new FilteredElementCollector(document, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .FirstOrDefault();

                // Sheet coordinates have no fixed origin, so everything is also reported relative to
                // the lower-left corner of the title block - that is what a layout can be rebuilt from.
                var paperOrigin = XYZ.Zero;

                if (titleBlock != null)
                {
                    var width = titleBlock.GetLength(BuiltInParameter.SHEET_WIDTH);
                    var height = titleBlock.GetLength(BuiltInParameter.SHEET_HEIGHT);
                    var titleBlockBox = titleBlock.get_BoundingBox(sheet);
                    paperOrigin = titleBlockBox?.Min ?? XYZ.Zero;

                    report.AppendLine($"    Title block        : {Describe(titleBlock)}");
                    report.AppendLine($"    Sheet size (mm)    : {width.FeetToMm():0.#} x {height.FeetToMm():0.#}");
                    report.AppendLine($"    Title block origin : {Mm(titleBlock.GetLocationPoint())}");
                    report.AppendLine($"    Title block bbox   : min {Mm(titleBlockBox?.Min)}  max {Mm(titleBlockBox?.Max)}");
                }

                TryLine(report, "    Sheet outline      : ", () =>
                {
                    var outline = sheet.Outline;
                    return $"min ({outline.Min.U.FeetToMm():0.#}, {outline.Min.V.FeetToMm():0.#})  " +
                           $"max ({outline.Max.U.FeetToMm():0.#}, {outline.Max.V.FeetToMm():0.#})";
                });

                report.AppendLine("    VIEWPORTS (\"paper\" = mm from the title block lower-left corner):");
                foreach (var id in sheet.GetAllViewports())
                {
                    var viewport = document.GetElement(id) as Viewport;
                    var view = document.GetElement(viewport?.ViewId) as View;
                    if (viewport == null || view == null)
                    {
                        continue;
                    }

                    var outline = viewport.GetBoxOutline();
                    var centre = viewport.GetBoxCenter();
                    report.AppendLine($"      \"{view.Name}\" ({view.ViewType}, 1:{view.Scale}) template=\"{NameOf(document, view.ViewTemplateId)}\"");
                    report.AppendLine($"          centre  {Mm(centre)}   paper {Mm(centre - paperOrigin)}");
                    report.AppendLine($"          box     min {Mm(outline.MinimumPoint)}  max {Mm(outline.MaximumPoint)}");
                    report.AppendLine($"          size    {(outline.MaximumPoint.X - outline.MinimumPoint.X).FeetToMm():0.#} x " +
                                      $"{(outline.MaximumPoint.Y - outline.MinimumPoint.Y).FeetToMm():0.#} mm");
                    report.AppendLine($"          vp type \"{NameOf(document, viewport.GetTypeId())}\"");
                }

                var scheduleInstances = new FilteredElementCollector(document, sheet.Id)
                    .OfClass(typeof(ScheduleSheetInstance))
                    .Cast<ScheduleSheetInstance>()
                    .ToList();

                report.AppendLine($"    SCHEDULE INSTANCES ({scheduleInstances.Count}):");
                foreach (var instance in scheduleInstances)
                {
                    var schedule = document.GetElement(instance.ScheduleId) as ViewSchedule;
                        report.AppendLine(
                        $"      \"{schedule?.Name}\"  point {Mm(instance.Point)}   " +
                        $"paper {Mm(instance.Point - paperOrigin)}  segment={instance.SegmentIndex}");
                }
            }
        }

        private static void Annotation(StringBuilder report, Document document, AssemblyInstance assembly)
        {
            foreach (var view in ViewsOf(document, assembly).Where(v => v.ViewType != ViewType.Schedule))
            {
                var dimensions = new FilteredElementCollector(document, view.Id)
                    .OfClass(typeof(Dimension))
                    .Cast<Dimension>()
                    .ToList();

                var tags = new FilteredElementCollector(document, view.Id)
                    .OfClass(typeof(IndependentTag))
                    .Cast<IndependentTag>()
                    .ToList();

                var texts = new FilteredElementCollector(document, view.Id)
                    .OfClass(typeof(TextNote))
                    .Cast<TextNote>()
                    .ToList();

                report.AppendLine();
                report.AppendLine($"VIEW \"{view.Name}\" : {dimensions.Count} dimension(s), {tags.Count} tag(s), {texts.Count} text note(s)");

                foreach (var dimension in dimensions.Take(12))
                {
                    DumpDimension(report, document, dimension);
                }

                if (dimensions.Count > 12)
                {
                    report.AppendLine($"  ... and {dimensions.Count - 12} more dimensions");
                }

                foreach (var tag in tags.Take(12))
                {
                    report.AppendLine(
                        $"  TAG  type=\"{NameOf(document, tag.GetTypeId())}\"  head {Mm(tag.TagHeadPosition)}" +
                        $"  leader={tag.HasLeader}  orientation={tag.TagOrientation}");

                    foreach (var id in tag.GetTaggedLocalElementIds())
                    {
                        var target = document.GetElement(id);
                        report.AppendLine(
                            $"         -> mark=\"{target?.GetString(BuiltInParameter.ALL_MODEL_MARK)}\" " +
                            $"name=\"{target?.LookupParameter("DATA-Navn")?.AsString()}\" {Describe(target)}");
                    }
                }

                if (tags.Count > 12)
                {
                    report.AppendLine($"  ... and {tags.Count - 12} more tags");
                }

                foreach (var text in texts.Take(8))
                {
                    report.AppendLine($"  TEXT type=\"{NameOf(document, text.GetTypeId())}\" at {Mm(text.Coord)} : \"{Trim(text.Text?.Trim(), 60)}\"");
                }
            }
        }

        private static void Schedules(StringBuilder report, Document document, AssemblyInstance assembly)
        {
            var schedules = ViewsOf(document, assembly).OfType<ViewSchedule>().ToList();
            report.AppendLine($"Schedules bound to the assembly : {schedules.Count}");

            foreach (var schedule in schedules)
            {
                var definition = schedule.Definition;
                report.AppendLine();
                report.AppendLine($"[{schedule.Id.ToLong()}] \"{schedule.Name}\"");
                report.AppendLine($"    Template : {NameOf(document, schedule.ViewTemplateId)}");
                report.AppendLine($"    Category : {NameOf(document, definition.CategoryId)}");
                report.AppendLine($"    Itemised : {definition.IsItemized}   Filters: {definition.GetFilterCount()}");

                var fields = definition.GetFieldOrder()
                    .Select(id => definition.GetField(id))
                    .Select(f => $"{f.GetName()} [{f.FieldType}]")
                    .ToList();

                report.AppendLine($"    Fields   : {string.Join(", ", fields)}");
            }
        }

        private static void NamingSummary(StringBuilder report, Document document)
        {
            var assemblies = document.OfClass<AssemblyInstance>().OrderBy(a => a.Name).ToList();
            report.AppendLine($"{"Assembly",-12}{"Sheet number",-16}{"Sheet name",-30}Views");

            foreach (var assembly in assemblies)
            {
                var sheet = document.OfClass<ViewSheet>()
                    .FirstOrDefault(s => s.AssociatedAssemblyInstanceId == assembly.Id);

                var viewNames = ViewsOf(document, assembly)
                    .Where(v => v.ViewType != ViewType.DrawingSheet)
                    .Select(v => $"{v.Name} ({v.ViewType})");

                report.AppendLine(
                    $"{Trim(assembly.Name, 12),-12}{Trim(sheet?.SheetNumber, 16),-16}" +
                    $"{Trim(sheet?.Name, 30),-30}{string.Join(" | ", viewNames)}");
            }
        }

        /// <summary>
        /// The same measurements as section C, but for every assembly at once. This is what shows
        /// whether the paper size varies with the plate, and what the layout is for each size.
        /// </summary>
        private static void LayoutSummary(StringBuilder report, Document document)
        {
            foreach (var assembly in document.OfClass<AssemblyInstance>().OrderBy(a => a.Name))
            {
                var sheet = document.OfClass<ViewSheet>()
                    .FirstOrDefault(s => s.AssociatedAssemblyInstanceId == assembly.Id);

                if (sheet == null)
                {
                    report.AppendLine($"{assembly.Name}: no sheet");
                    continue;
                }

                var plate = assembly.GetMemberIds()
                    .Select(document.GetElement)
                    .FirstOrDefault(e => e?.Category?.Id.ToLong() == (long)BuiltInCategory.OST_GenericModel);

                var plateBox = plate?.get_BoundingBox(null);
                var description = plate?.GetTypeAs<ElementType>()?
                    .get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.AsString();

                var titleBlock = new FilteredElementCollector(document, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .FirstOrDefault();

                var corner = titleBlock?.get_BoundingBox(sheet)?.Min ?? XYZ.Zero;
                var width = titleBlock.GetLength(BuiltInParameter.SHEET_WIDTH).FeetToMm();
                var height = titleBlock.GetLength(BuiltInParameter.SHEET_HEIGHT).FeetToMm();

                report.AppendLine();
                report.AppendLine($"{assembly.Name}  sheet=\"{sheet.SheetNumber}\"");
                report.AppendLine($"    plate bbox   : {BoxSize(plateBox)}   \"{description}\"");
                report.AppendLine($"    title block  : {width:0.#} x {height:0.#} mm - {NameOf(document, titleBlock?.GetTypeId())}");

                foreach (var id in sheet.GetAllViewports())
                {
                    var viewport = document.GetElement(id) as Viewport;
                    var view = document.GetElement(viewport?.ViewId) as View;
                    if (viewport == null || view == null)
                    {
                        continue;
                    }

                    var outline = viewport.GetBoxOutline();
                    var paper = viewport.GetBoxCenter() - corner;
                    report.AppendLine(
                        $"    vp {view.Name,-10} paper ({paper.X.FeetToMm():0.#}, {paper.Y.FeetToMm():0.#})   " +
                        $"size {(outline.MaximumPoint.X - outline.MinimumPoint.X).FeetToMm():0.#} x " +
                        $"{(outline.MaximumPoint.Y - outline.MinimumPoint.Y).FeetToMm():0.#}   1:{view.Scale}");
                }

                foreach (var instance in new FilteredElementCollector(document, sheet.Id)
                             .OfClass(typeof(ScheduleSheetInstance))
                             .Cast<ScheduleSheetInstance>())
                {
                    var schedule = document.GetElement(instance.ScheduleId) as ViewSchedule;
                    var paper = instance.Point - corner;
                    var name = (schedule?.Name ?? string.Empty).Replace("REVGEN_CRH_Schedule_BearingPlate ", string.Empty);
                    report.AppendLine($"    sc {name,-32} paper ({paper.X.FeetToMm():0.#}, {paper.Y.FeetToMm():0.#})");
                }
            }
        }

        /// <summary>
        /// Dimension members throw for several shapes - Origin is only valid on a single segment
        /// dimension, Value only when there is exactly one - so every read is guarded individually.
        /// </summary>
        private static void DumpDimension(StringBuilder report, Document document, Dimension dimension)
        {
            try
            {
                report.AppendLine(
                    $"  DIM  type=\"{NameOf(document, dimension.GetTypeId())}\"  shape={dimension.DimensionShape}" +
                    $"  segments={dimension.NumberOfSegments}");

                TryLine(report, "       value    : ", () => dimension.Value.HasValue
                    ? $"{dimension.Value.Value.FeetToMm():0.#} mm"
                    : string.Join(" | ", dimension.Segments
                        .Cast<DimensionSegment>()
                        .Select(s => s.Value.HasValue ? $"{s.Value.Value.FeetToMm():0.#}" : "?")) + " mm");

                TryLine(report, "       origin   : ", () => Mm(dimension.Origin));
                TryLine(report, "       curve    : ", () =>
                {
                    var curve = dimension.Curve;
                    return curve == null || !curve.IsBound
                        ? "(unbound)"
                        : $"{Mm(curve.GetEndPoint(0))} -> {Mm(curve.GetEndPoint(1))}";
                });

                var references = dimension.References;
                report.AppendLine($"       refs     : {references.Size}");
                foreach (Reference reference in references)
                {
                    var target = document.GetElement(reference.ElementId);
                    var mark = target?.GetString(BuiltInParameter.ALL_MODEL_MARK);
                    report.AppendLine(
                        $"         {reference.ElementReferenceType,-22} mark=\"{mark}\" -> {Describe(target)}");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"  DIM  (failed to read: {ex.Message})");
            }
        }

        // ------------------------------------------------------------------ helpers

        private static List<View> ViewsOf(Document document, AssemblyInstance assembly)
        {
            return document.OfClass<View>()
                .Where(v => !v.IsTemplate && v.AssociatedAssemblyInstanceId == assembly.Id)
                .OrderBy(v => v.ViewType.ToString())
                .ThenBy(v => v.Name)
                .ToList();
        }

        private static AssemblyInstance PickAssembly(UIDocument uiDocument)
        {
            var document = uiDocument.Document;

            foreach (var id in uiDocument.Selection.GetElementIds())
            {
                var element = document.GetElement(id);
                if (element is AssemblyInstance selected)
                {
                    return selected;
                }

                if (element != null && element.AssemblyInstanceId != ElementId.InvalidElementId)
                {
                    return document.GetElement(element.AssemblyInstanceId) as AssemblyInstance;
                }
            }

            return document.OfClass<AssemblyInstance>().OrderBy(a => a.Name).FirstOrDefault();
        }

        private static void TryLine(StringBuilder report, string label, Func<string> value)
        {
            try
            {
                report.AppendLine(label + value());
            }
            catch (Exception ex)
            {
                report.AppendLine(label + "(not available: " + ex.GetType().Name + ")");
            }
        }
    }
}
