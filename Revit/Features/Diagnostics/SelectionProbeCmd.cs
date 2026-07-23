using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using SDSoftware.RevitTest.Core;
using SDSoftware.RevitTest.Extensions;
using SDSoftware.RevitTest.Features.Diagnostics.Views;

namespace SDSoftware.RevitTest.Features.Diagnostics
{
    /// <summary>
    /// Reports everything about whatever is selected, and how the selected elements sit against each
    /// other. Select the pieces first - an opening, the beam it cuts and the column beside it, say -
    /// then run this. Read-only.
    /// Temporary: remove this command and its ribbon button before the final submission.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class SelectionProbeCmd : CommandBase
    {
        protected override string Title => "Selection Probe";

        protected override Result Run(CommandContext context)
        {
            var document = context.Document;
            if (document == null)
            {
                ShowInfo("No document is open.");
                return Result.Cancelled;
            }

            var elements = context.UiDocument.Selection.GetElementIds()
                .Select(document.GetElement)
                .Where(element => element != null)
                .ToList();

            if (elements.Count == 0)
            {
                ShowInfo("Nothing is selected.", "Select the elements to inspect, then run this again.");
                return Result.Cancelled;
            }

            var text = SelectionProbeReport.Build(document, elements);
            var window = new LogWindow(
                $"Selection Probe - {document.Title} ({elements.Count} elements)",
                text,
                "SelectionProbe_" + document.Title);

            RevitWindow.ShowDialog(window, context.UiApplication);
            return Result.Succeeded;
        }
    }
}
