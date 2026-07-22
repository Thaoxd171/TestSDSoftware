using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using SDSoftware.RevitTest.Core;
using SDSoftware.RevitTest.Features.AdjustBeam.Models;
using SDSoftware.RevitTest.Features.AdjustBeam.Services;
using SDSoftware.RevitTest.Features.Diagnostics.Views;
using SDSoftware.RevitTest.Settings;

namespace SDSoftware.RevitTest.Features.Diagnostics
{
    /// <summary>
    /// Runs the Adjust Beam tool without writing anything and reports every decision it would take.
    /// Pick the beams exactly as in the real command; the saved clearances are the ones used.
    /// Temporary: remove this command and its ribbon button before the final submission.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class AdjustBeamExplainCmd : CommandBase
    {
        private const string SettingName = "AdjustBeam";

        protected override string Title => "Adjust Beam Explain";

        protected override Result Run(CommandContext context)
        {
            var document = context.Document;
            if (document == null)
            {
                ShowInfo("No document is open.");
                return Result.Cancelled;
            }

            var beams = BeamCollector.Pick(context.UiDocument);
            if (beams.Count == 0)
            {
                ShowInfo("Nothing to explain.", "No beam was found in that selection.");
                return Result.Cancelled;
            }

            var options = SettingStore.Load<AdjustBeamOptions>(SettingName);
            var text = AdjustBeamExplainReport.Build(document, beams, options);

            var window = new LogWindow(
                $"Adjust Beam, dry run - {document.Title} ({beams.Count} beams)",
                text,
                "AdjustBeamExplain_" + document.Title);

            RevitWindow.ShowDialog(window, context.UiApplication);
            return Result.Succeeded;
        }
    }
}
