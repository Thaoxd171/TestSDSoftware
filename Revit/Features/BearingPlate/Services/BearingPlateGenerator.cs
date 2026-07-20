using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Core;
using SDSoftware.RevitTest.Features.BearingPlate.Models;

namespace SDSoftware.RevitTest.Features.BearingPlate.Services
{
    /// <summary>
    /// Turns bearing plates into drawings. Plates are isolated into assemblies first, then each
    /// assembly gets three views, six schedules and a sheet.
    /// Every plate runs in its own transaction inside one transaction group, so a plate that fails
    /// rolls back alone and the rest of the run still completes.
    /// </summary>
    public class BearingPlateGenerator
    {
        public const string AssemblyStep = "Create isolated assemblies";
        public const string DrawingStep = "Create bearing plate drawings";

        private readonly Document _document;
        private readonly TemplateCatalog _catalog;
        private readonly AssemblyBuilder _assemblies;
        private readonly AssemblyViewBuilder _views;
        private readonly AssemblyScheduleBuilder _schedules;
        private readonly SheetBuilder _sheets;

        public BearingPlateGenerator(Document document, TemplateCatalog catalog)
        {
            _document = document;
            _catalog = catalog;
            _assemblies = new AssemblyBuilder(document);
            _views = new AssemblyViewBuilder(document);
            _schedules = new AssemblyScheduleBuilder(document);
            _sheets = new SheetBuilder(document);
        }

        public List<GenerationResult> Run(IList<PlateItem> plates, BearingPlateOptions options, IProgressSink progress)
        {
            var results = new List<GenerationResult>();

            var planTemplate = _catalog.DefaultPlanTemplate;
            var frontTemplate = _catalog.DefaultFrontTemplate;
            var threeDTemplate = _catalog.DefaultThreeDTemplate;
            var titleBlock = _catalog.DefaultTitleBlock;

            progress.Log($"{plates.Count} plate(s) selected.");
            progress.Log($"Title block: {titleBlock?.Name ?? "(none found)"}");
            progress.Log($"Templates: {Name(planTemplate)} / {Name(frontTemplate)} / {Name(threeDTemplate)}");

            using (var group = new TransactionGroup(_document, "Generate bearing plate drawings"))
            {
                group.Start();

                if (!EnsureAssemblies(plates, progress))
                {
                    group.RollBack();
                    return results;
                }

                progress.Report(DrawingStep, 0);

                for (var index = 0; index < plates.Count; index++)
                {
                    if (progress.IsCancelled)
                    {
                        progress.Log("Stopped by user.");
                        break;
                    }

                    var plate = plates[index];

                    if (options.SkipExisting && plate.HasSheet)
                    {
                        results.Add(GenerationResult.Skipped(plate.Name, $"already on sheet {plate.ExistingSheet.SheetNumber}"));
                        progress.Log($"{plate.Name}: skipped, already on sheet {plate.ExistingSheet.SheetNumber}");
                    }
                    else
                    {
                        var result = Generate(plate, options, planTemplate, frontTemplate, threeDTemplate, titleBlock);
                        results.Add(result);
                        progress.Log(result.Status == GenerationStatus.Created
                            ? $"{plate.Name}: sheet {result.SheetNumber}, {result.Views} view(s), {result.Schedules} schedule(s)"
                            : $"{plate.Name}: FAILED - {result.Message}");
                    }

                    progress.Report(DrawingStep, (index + 1) / (double)plates.Count);
                }

                group.Assimilate();
            }

            return results;
        }

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

            using (var transaction = new Transaction(_document, "Create isolated assemblies"))
            {
                transaction.Start();

                for (var index = 0; index < missing.Count; index++)
                {
                    if (progress.IsCancelled)
                    {
                        transaction.RollBack();
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

                transaction.Commit();
            }

            return true;
        }

        private GenerationResult Generate(
            PlateItem plate,
            BearingPlateOptions options,
            View planTemplate,
            View frontTemplate,
            View threeDTemplate,
            FamilySymbol titleBlock)
        {
            if (!plate.HasAssembly)
            {
                return GenerationResult.Failed(plate.Name, "plate is not in an assembly");
            }

            using (var transaction = new Transaction(_document, "Bearing plate " + plate.Name))
            {
                transaction.Start();

                try
                {
                    var assemblyId = plate.Assembly.Id;

                    var plan = _views.CreatePlan(assemblyId, IdOf(planTemplate));
                    var front = _views.CreateFront(assemblyId, IdOf(frontTemplate));
                    var threeD = options.CreateThreeD ? _views.CreateThreeD(assemblyId, IdOf(threeDTemplate)) : null;

                    var schedules = options.CreateSchedules
                        ? _schedules.Create(assemblyId, _catalog.ScheduleTemplates, plate.Name)
                        : new List<ViewSchedule>();

                    var sheet = _sheets.Create(plate, IdOf(titleBlock), options);
                    var corner = _sheets.GetTitleBlockCorner(sheet);

                    _sheets.PlaceView(sheet, plan, SheetLayout.ToSheetPoint(corner, SheetLayout.PlanCentre));
                    _sheets.PlaceView(sheet, front, SheetLayout.ToSheetPoint(corner, SheetLayout.FrontCentre));
                    _sheets.PlaceView(sheet, threeD, SheetLayout.ToSheetPoint(corner, SheetLayout.ThreeDCentre));

                    PlaceSchedules(sheet, schedules, corner);

                    transaction.Commit();

                    return new GenerationResult
                    {
                        Assembly = plate.Name,
                        SheetNumber = sheet.SheetNumber,
                        Views = new[] { plan, front, threeD }.Count(v => v != null),
                        Schedules = schedules.Count,
                        Status = GenerationStatus.Created,
                    };
                }
                catch (Exception ex)
                {
                    if (transaction.HasStarted())
                    {
                        transaction.RollBack();
                    }

                    return GenerationResult.Failed(plate.Name, ex.Message);
                }
            }
        }

        private void PlaceSchedules(ViewSheet sheet, List<ViewSchedule> schedules, XYZ corner)
        {
            for (var index = 0; index < schedules.Count; index++)
            {
                var schedule = schedules[index];
                var templateName = _document.GetElement(schedule.ViewTemplateId)?.Name ?? schedule.Name;
                var paper = SheetLayout.ScheduleCornerFor(templateName, index);
                _sheets.PlaceSchedule(sheet, schedule, SheetLayout.ToSheetPoint(corner, paper));
            }
        }

        private static string Name(Element element) => element?.Name ?? "(none)";

        private static ElementId IdOf(Element element) => element?.Id ?? ElementId.InvalidElementId;
    }
}
