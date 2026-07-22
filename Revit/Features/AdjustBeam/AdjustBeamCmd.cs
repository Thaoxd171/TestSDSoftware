using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using SDSoftware.RevitTest.Core;
using SDSoftware.RevitTest.Core.Progress;
using SDSoftware.RevitTest.Features.AdjustBeam.Models;
using SDSoftware.RevitTest.Features.AdjustBeam.Services;
using SDSoftware.RevitTest.Features.AdjustBeam.ViewModels;
using SDSoftware.RevitTest.Features.AdjustBeam.Views;
using SDSoftware.RevitTest.Settings;

namespace SDSoftware.RevitTest.Features.AdjustBeam
{
    /// <summary>
    /// Trims and extends structural beams so that every end keeps the right clearance from the wall,
    /// pillar or beam it runs into. The clearances are set first, the beams are picked afterwards.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AdjustBeamCmd : CommandBase
    {
        private const string SettingName = "AdjustBeam";

        protected override string Title => "Adjust Beams";

        protected override Result Run(CommandContext context)
        {
            if (context.Document == null)
            {
                ShowInfo("No document is open.");
                return Result.Cancelled;
            }

            var viewModel = new AdjustBeamViewModel(SettingStore.Load<AdjustBeamOptions>(SettingName));
            if (RevitWindow.ShowDialog(new AdjustBeamWindow(viewModel), context.UiApplication) != true)
            {
                return Result.Cancelled;
            }

            var options = viewModel.ToOptions();
            SettingStore.Save(SettingName, options);

            // Esc during the pick raises a cancellation that CommandBase turns into Result.Cancelled,
            // so nothing is written when the user changes their mind.
            var beams = BeamCollector.Pick(context.UiDocument);
            if (beams.Count == 0)
            {
                ShowInfo("Nothing to adjust.", "No beam was found in that selection.");
                return Result.Cancelled;
            }

            var progress = new ProgressWindow(Title);
            RevitWindow.Show(progress, context.UiApplication);

            try
            {
                var result = new AdjustBeamRunner(context.Document).Run(beams, options, progress);
                progress.Finish(result.Summary);
            }
            catch
            {
                // Release the window before the error dialog goes up, or it refuses to close.
                progress.Finish("The run failed.");
                throw;
            }

            return Result.Succeeded;
        }
    }
}
