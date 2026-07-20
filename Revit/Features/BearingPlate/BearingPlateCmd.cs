using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using SDSoftware.RevitTest.Core;
using SDSoftware.RevitTest.Core.Progress;
using SDSoftware.RevitTest.Features.BearingPlate.Models;
using SDSoftware.RevitTest.Features.BearingPlate.Services;
using SDSoftware.RevitTest.Features.BearingPlate.ViewModels;
using SDSoftware.RevitTest.Features.BearingPlate.Views;

namespace SDSoftware.RevitTest.Features.BearingPlate
{
    /// <summary>
    /// Creates a drawing for each selected bearing plate: the plate is isolated into an assembly,
    /// then a plan, a front elevation, a 3D view and the component schedules are placed on a sheet.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class BearingPlateCmd : CommandBase
    {
        protected override string Title => "Generate Bearing Plate Drawings";

        protected override Result Run(CommandContext context)
        {
            var document = context.Document;
            if (document == null)
            {
                ShowInfo("No document is open.");
                return Result.Cancelled;
            }

            var plates = new PlateCollector(document).Collect();
            if (plates.Count == 0)
            {
                ShowInfo("No bearing plates found.",
                    "This tool works on generic model families; none were found in the model.");
                return Result.Cancelled;
            }

            var viewModel = new BearingPlateViewModel(plates);
            var dialog = new BearingPlateWindow(viewModel);
            if (RevitWindow.ShowDialog(dialog, context.UiApplication) != true)
            {
                return Result.Cancelled;
            }

            var selected = viewModel.SelectedPlates;
            var progress = new ProgressWindow(Title);
            RevitWindow.Show(progress, context.UiApplication);

            var generator = new BearingPlateGenerator(document, new TemplateCatalog(document));
            var results = generator.Run(selected, new BearingPlateOptions(), progress);

            var created = results.Count(r => r.Status == GenerationStatus.Created);
            var skipped = results.Count(r => r.Status == GenerationStatus.Skipped);
            var failed = results.Count(r => r.Status == GenerationStatus.Failed);

            progress.Finish($"Done. {created} sheet(s) created, {skipped} skipped, {failed} failed.");
            return Result.Succeeded;
        }
    }
}
