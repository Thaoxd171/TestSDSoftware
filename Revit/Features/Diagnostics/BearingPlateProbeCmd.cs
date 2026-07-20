using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using SDSoftware.RevitTest.Core;
using SDSoftware.RevitTest.Features.Diagnostics.Views;

namespace SDSoftware.RevitTest.Features.Diagnostics
{
    /// <summary>
    /// Reports one bearing plate assembly and the drawing produced for it in full detail.
    /// Select an assembly first to inspect that one; otherwise the first assembly is used.
    /// Temporary development aid.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class BearingPlateProbeCmd : CommandBase
    {
        protected override string Title => "Bearing Plate Probe";

        protected override Result Run(CommandContext context)
        {
            if (context.Document == null)
            {
                ShowInfo("No document is open.");
                return Result.Cancelled;
            }

            var text = BearingPlateProbeReport.Build(context.UiApplication);
            RevitWindow.ShowDialog(new LogWindow(Title + " - " + context.Document.Title, text), context.UiApplication);
            return Result.Succeeded;
        }
    }
}
