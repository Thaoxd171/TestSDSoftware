using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Core;
using SDSoftware.RevitTest.Extensions;
using SDSoftware.RevitTest.Features.BearingPlate.Models;

namespace SDSoftware.RevitTest.Features.BearingPlate.Services
{
    /// <summary>
    /// Turns bearing plates into drawings, in three passes: isolate each plate into an assembly,
    /// draw and annotate its views, then lay those views out on a sheet.
    ///
    /// The passes are separate because a viewport is sized by everything visible in its view, so the
    /// annotation has to be finished before a view can be positioned on paper.
    /// Every plate runs in its own transaction, so one that fails rolls back alone.
    /// </summary>
    public class BearingPlateGenerator
    {
        public const string AssemblyStep = "Create isolated assemblies";
        public const string DrawingStep = "Create bearing plate drawings";
        public const string SheetStep = "Place drawings on sheets";

        /// <summary>Gap kept above the tables and below the title-block edge when centring the views.</summary>
        private const double DrawingAreaMarginMm = 12.0;

        private readonly Document _document;
        private readonly TemplateCatalog _catalog;
        private readonly AssemblyBuilder _assemblies;
        private readonly AssemblyViewBuilder _views;
        private readonly AssemblyScheduleBuilder _schedules;
        private readonly SheetBuilder _sheets;
        private readonly ComponentCollector _components;
        private readonly TagService _tags;
        private readonly DimensionService _dimensions;

        public BearingPlateGenerator(Document document, TemplateCatalog catalog)
        {
            _document = document;
            _catalog = catalog;
            _assemblies = new AssemblyBuilder(document);
            _views = new AssemblyViewBuilder(document);
            _schedules = new AssemblyScheduleBuilder(document);
            _sheets = new SheetBuilder(document);
            _components = new ComponentCollector(document);
            _tags = new TagService(document);
            _dimensions = new DimensionService(document);
        }

        public List<GenerationResult> Run(IList<PlateItem> plates, BearingPlateOptions options, IProgressSink progress)
        {
            progress.Log($"{plates.Count} plate(s) selected.");
            progress.Log($"Title blocks: A4 {Name(_catalog.A4TitleBlock)} / A3 {Name(_catalog.A3TitleBlock)}");
            progress.Log($"Templates: {Name(_catalog.DefaultPlanTemplate)} / " +
                         $"{Name(_catalog.DefaultFrontTemplate)} / {Name(_catalog.DefaultThreeDTemplate)}");
            progress.Log($"Tag: {Name(_catalog.ComponentTagType)}   Label text: {Name(_catalog.LabelTextType)}");

            // everything runs in a single transaction so the whole generation is one undo step; each
            // plate is isolated inside its own SubTransaction, which rolls back alone if it fails
            using (var transaction = new Transaction(_document, "Generate bearing plate drawings"))
            {
                transaction.Start();

                if (!EnsureAssemblies(plates, progress))
                {
                    transaction.RollBack();
                    return new List<GenerationResult>();
                }

                var drawings = CreateDrawings(plates, options, progress);
                PlaceOnSheets(drawings, options, progress);

                transaction.Commit();
                return drawings.Select(d => d.Result).Where(r => r != null).ToList();
            }
        }

        // ------------------------------------------------------------------ pass 1

        /// <summary>Isolates every plate that is not in an assembly yet. False when the user stopped.</summary>
        private bool EnsureAssemblies(IList<PlateItem> plates, IProgressSink progress)
        {
            progress.Report(AssemblyStep, 0);

            var missing = plates.Where(p => !p.HasAssembly).ToList();
            if (missing.Count == 0)
            {
                progress.Log("All plates are already isolated into assemblies.");
                progress.Report(AssemblyStep, 1);
                return true;
            }

            // runs inside the shared transaction; a plate that cannot be isolated is logged and skipped
            for (var index = 0; index < missing.Count; index++)
            {
                if (progress.IsCancelled)
                {
                    return false;
                }

                var plate = missing[index];

                try
                {
                    _assemblies.EnsureAssembly(plate);
                    progress.Log($"{plate.Name}: assembly created");
                }
                catch (Exception ex)
                {
                    progress.Log($"{plate.Name}: could not create assembly - {ex.Message}");
                }

                progress.Report(AssemblyStep, (index + 1) / (double)missing.Count);
            }

            return true;
        }

        // ------------------------------------------------------------------ pass 2

        private List<PlateDrawing> CreateDrawings(IList<PlateItem> plates, BearingPlateOptions options, IProgressSink progress)
        {
            progress.Report(DrawingStep, 0);

            var planTemplate = _catalog.DefaultPlanTemplate;
            var frontTemplate = _catalog.DefaultFrontTemplate;
            var threeDTemplate = _catalog.DefaultThreeDTemplate;

            var drawings = new List<PlateDrawing>();

            for (var index = 0; index < plates.Count; index++)
            {
                if (progress.IsCancelled)
                {
                    progress.Log("Stopped by user.");
                    break;
                }

                var plate = plates[index];
                var drawing = new PlateDrawing(plate);
                drawings.Add(drawing);

                if (options.SkipExisting && plate.HasSheet)
                {
                    drawing.Result = GenerationResult.Skipped(plate.Name, $"already on sheet {plate.ExistingSheet.SheetNumber}");
                    progress.Log($"{plate.Name}: skipped, already on sheet {plate.ExistingSheet.SheetNumber}");
                }
                else if (!plate.HasAssembly)
                {
                    drawing.Result = GenerationResult.Failed(plate.Name, "plate is not in an assembly");
                    progress.Log($"{plate.Name}: FAILED - plate is not in an assembly");
                }
                else
                {
                    DrawOne(drawing, options, planTemplate, frontTemplate, threeDTemplate, progress);
                }

                progress.Report(DrawingStep, (index + 1) / (double)plates.Count);
            }

            return drawings;
        }

        private void DrawOne(
            PlateDrawing drawing,
            BearingPlateOptions options,
            View planTemplate,
            View frontTemplate,
            View threeDTemplate,
            IProgressSink progress)
        {
            var plate = drawing.Plate;

            using (var sub = new SubTransaction(_document))
            {
                sub.Start();

                try
                {
                    var assemblyId = plate.Assembly.Id;

                    drawing.Plan = _views.CreatePlan(assemblyId, IdOf(planTemplate));
                    drawing.Front = _views.CreateFront(assemblyId, IdOf(frontTemplate));
                    drawing.ThreeD = options.CreateThreeD ? _views.CreateThreeD(assemblyId, IdOf(threeDTemplate)) : null;

                    var report = Annotate(drawing, options);

                    sub.Commit();
                    progress.Log($"{plate.Name}: {drawing.ViewCount} view(s){report}");
                }
                catch (Exception ex)
                {
                    if (sub.HasStarted())
                    {
                        sub.RollBack();
                    }

                    drawing.Reset();
                    drawing.Result = GenerationResult.Failed(plate.Name, ex.Message);
                    progress.Log($"{plate.Name}: FAILED - {ex.Message}");
                }
            }
        }

        /// <summary>Annotates both detail views and returns a line describing what happened.</summary>
        private string Annotate(PlateDrawing drawing, BearingPlateOptions options)
        {
            var plate = drawing.Plate.Plate;

            // annotation has to be finished before the views go on paper: a viewport is sized by
            // everything visible in its view, annotation included
            _document.Regenerate();

            // one list drives both: row i of the dimensions is named by tag i
            var components = _components.Collect(drawing.Plate.Assembly, plate.get_BoundingBox(null));
            var report = string.Empty;

            if (options.CreateTags)
            {
                var tagPlan = new AnnotationResult();
                var tagFront = new AnnotationResult();

                if (components.Count == 0)
                {
                    tagPlan.Note("no parts to tag");
                }
                else
                {
                    var tagType = _catalog.ComponentTagType;
                    var labelType = _catalog.LabelTextType;
                    tagPlan.Add(_tags.AnnotatePlan(drawing.Plan, plate, components, tagType, labelType));
                    tagFront.Add(_tags.AnnotateFront(drawing.Front, plate, components, tagType, labelType));
                }

                drawing.Tags = tagPlan.Placed + tagFront.Placed;
                report += $", tags {tagPlan.Describe(AssemblyViewBuilder.PlanName)} " +
                          $"{tagFront.Describe(AssemblyViewBuilder.FrontName)}";
            }

            if (options.CreateDimensions)
            {
                var type = _catalog.LinearDimensionType;
                var dimPlan = _dimensions.DimensionPlan(drawing.Plan, plate, components, type);
                var dimFront = _dimensions.DimensionFront(drawing.Front, plate, components, type);

                drawing.Dimensions = dimPlan.Placed + dimFront.Placed;
                report += $", dims {dimPlan.Describe(AssemblyViewBuilder.PlanName)} " +
                          $"{dimFront.Describe(AssemblyViewBuilder.FrontName)}";
            }

            return report;
        }

        // ------------------------------------------------------------------ pass 3

        private void PlaceOnSheets(List<PlateDrawing> drawings, BearingPlateOptions options, IProgressSink progress)
        {
            var pending = drawings.Where(d => d.Result == null).ToList();
            progress.Report(SheetStep, 0);

            for (var index = 0; index < pending.Count; index++)
            {
                if (progress.IsCancelled)
                {
                    progress.Log("Stopped by user.");
                    break;
                }

                SheetOne(pending[index], options, progress);
                progress.Report(SheetStep, (index + 1) / (double)pending.Count);
            }
        }

        private void SheetOne(PlateDrawing drawing, BearingPlateOptions options, IProgressSink progress)
        {
            var plate = drawing.Plate;

            using (var sub = new SubTransaction(_document))
            {
                sub.Start();

                try
                {
                    var layout = SheetLayout.For(SheetLayout.ChooseFormat(LongestSideMm(plate.Plate)));
                    var titleBlock = layout.Format == SheetFormat.A4Portrait
                        ? _catalog.A4TitleBlock ?? _catalog.DefaultTitleBlock
                        : _catalog.A3TitleBlock ?? _catalog.DefaultTitleBlock;

                    var schedules = options.CreateSchedules
                        ? _schedules.Create(plate.Assembly.Id, _catalog.ScheduleTemplates, plate.Name)
                        : new List<ViewSchedule>();

                    var sheet = _sheets.Create(plate, IdOf(titleBlock), options);
                    var corner = _sheets.GetTitleBlockCorner(sheet);

                    // plan sits on its bottom anchor, front stacks above it, 3D hangs in the top-right
                    var planViewport = _sheets.PlaceViewAnchoredBottom(sheet, drawing.Plan, corner, layout.ViewCentreX, layout.PlanBottomY);
                    var frontBottom = _sheets.GetTopEdgeMm(planViewport, corner) + layout.FrontGapY;
                    var frontViewport = _sheets.PlaceViewAnchoredBottom(sheet, drawing.Front, corner, layout.ViewCentreX, frontBottom);
                    var threeDViewport = _sheets.PlaceViewAnchoredTopRight(sheet, drawing.ThreeD, corner, layout.ThreeDTopRight.X, layout.ThreeDTopRight.Y);

                    // the schedules go on first so the free drawing area can be measured from what is
                    // actually on the sheet
                    PlaceSchedules(sheet, schedules, corner, layout);

                    // lay the plan and front out inside the free part of the sheet: on A4 that is the
                    // gap above the material tables (full width); on A3 it is the left of the tables,
                    // which sit in the right half. The rectangle is read back from what is actually on
                    // the sheet, then the views are measured and fitted to it, clear of the 3D.
                    try
                    {
                        LayoutViews(sheet, corner, layout, planViewport, frontViewport, threeDViewport, plate.Name, progress);
                    }
                    catch (Exception ex)
                    {
                        // layout is a nicety; never let it lose the whole sheet
                        progress.Log($"{plate.Name}: layout skipped - {ex.Message}");
                    }

                    sub.Commit();

                    drawing.Result = new GenerationResult
                    {
                        Assembly = plate.Name,
                        SheetNumber = sheet.SheetNumber,
                        Views = drawing.ViewCount,
                        Schedules = schedules.Count,
                        Tags = drawing.Tags,
                        Status = GenerationStatus.Created,
                    };

                    progress.Log($"{plate.Name}: sheet {sheet.SheetNumber} ({layout.Format}), {schedules.Count} schedule(s)");
                }
                catch (Exception ex)
                {
                    if (sub.HasStarted())
                    {
                        sub.RollBack();
                    }

                    drawing.Result = GenerationResult.Failed(plate.Name, ex.Message);
                    progress.Log($"{plate.Name}: FAILED - {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Builds the drawing-area rectangle for this paper size and hands it to the layout solver.
        /// A4: the views share the full width above the tables. A3: they take the width to the left of
        /// the tables, over the full height. Either way the 3D stays where it was anchored and the
        /// plan and front are fitted around it.
        /// </summary>
        private void LayoutViews(
            ViewSheet sheet, XYZ corner, SheetLayout layout,
            Viewport plan, Viewport front, Viewport threeD, string plateName, IProgressSink progress)
        {
            var top = _sheets.FrameTopMm(sheet, corner) - DrawingAreaMarginMm;

            double left, right, bottom;
            if (layout.Format == SheetFormat.A4Portrait)
            {
                left = 0;
                right = _sheets.FrameWidthMm(sheet, corner);
                bottom = _sheets.TopOfContentMm(sheet, corner) + DrawingAreaMarginMm;
            }
            else
            {
                left = 0;
                right = _sheets.LeftOfContentMm(sheet, corner) - DrawingAreaMarginMm;
                bottom = layout.PlanBottomY;
            }

            var outcome = _sheets.LayoutPlanAndFront(plan, front, threeD, corner, left, right, bottom, top, layout.ViewCentreX);
            if (!outcome.Fits)
            {
                progress.Log($"{plateName}: layout tight - short by {outcome.DeficitMm:0.#} mm");
            }
        }

        private void PlaceSchedules(ViewSheet sheet, List<ViewSchedule> schedules, XYZ corner, SheetLayout layout)
        {
            for (var index = 0; index < schedules.Count; index++)
            {
                var schedule = schedules[index];
                var templateName = _document.GetElement(schedule.ViewTemplateId)?.Name ?? schedule.Name;
                var paper = layout.ScheduleCornerFor(templateName, index);
                _sheets.PlaceSchedule(sheet, schedule, SheetLayout.ToSheetPoint(corner, paper));
            }
        }

        // ------------------------------------------------------------------ helpers

        private static double LongestSideMm(Element plate)
        {
            var box = plate.get_BoundingBox(null);
            return box == null
                ? 0
                : Math.Max((box.Max.X - box.Min.X).FeetToMm(), (box.Max.Y - box.Min.Y).FeetToMm());
        }

        private static string Name(Element element) => element?.Name ?? "(none)";

        private static ElementId IdOf(Element element) => element?.Id ?? ElementId.InvalidElementId;

        /// <summary>Carries one plate through the passes.</summary>
        private class PlateDrawing
        {
            public PlateDrawing(PlateItem plate)
            {
                Plate = plate;
            }

            public PlateItem Plate { get; }

            public View Plan { get; set; }

            public View Front { get; set; }

            public View ThreeD { get; set; }

            public int Tags { get; set; }

            public int Dimensions { get; set; }

            /// <summary>Null while the plate is still being worked on.</summary>
            public GenerationResult Result { get; set; }

            public int ViewCount => new[] { Plan, Front, ThreeD }.Count(v => v != null);

            public void Reset()
            {
                Plan = null;
                Front = null;
                ThreeD = null;
                Tags = 0;
                Dimensions = 0;
            }
        }
    }
}
