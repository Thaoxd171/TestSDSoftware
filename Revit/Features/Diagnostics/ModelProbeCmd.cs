using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using SDSoftware.RevitTest.Core;
using SDSoftware.RevitTest.Features.Diagnostics.Views;

namespace SDSoftware.RevitTest.Features.Diagnostics
{
    /// <summary>
    /// Read-only survey of the open model, used while developing the Bearing Plate tool.
    /// Temporary: remove this command and its ribbon button before the final submission.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class ModelProbeCmd : CommandBase
    {
        protected override string Title => "Model Probe";

        protected override Result Run(CommandContext context)
        {
            if (context.Document == null)
            {
                ShowInfo("No document is open.");
                return Result.Cancelled;
            }

            var text = ModelProbeReport.Build(context.UiApplication);
            RevitWindow.ShowDialog(new LogWindow("Model Probe - " + context.Document.Title, text), context.UiApplication);
            return Result.Succeeded;
        }
    }
}
