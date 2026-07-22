using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SDSoftware.RevitTest.Core;
using SDSoftware.RevitTest.Extensions;
using SDSoftware.RevitTest.Features.Diagnostics.Views;

namespace SDSoftware.RevitTest.Features.Diagnostics
{
    /// <summary>
    /// Read-only survey of how the beams sit against their supports. Used to derive the clearance rules
    /// of the Adjust Beam tool from the reference model instead of guessing them.
    /// Temporary: remove this command and its ribbon button before the final submission.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class AdjustBeamProbeCmd : CommandBase
    {
        protected override string Title => "Adjust Beam Probe";

        protected override Result Run(CommandContext context)
        {
            var document = context.Document;
            if (document == null)
            {
                ShowInfo("No document is open.");
                return Result.Cancelled;
            }

            var beams = GetSelectedBeams(context, out var scope);
            if (beams.Count == 0)
            {
                beams = GetBeamsInActiveView(context, out scope);
            }

            if (beams.Count == 0)
            {
                // The inventory and the auto-scans are worth reading on their own.
                scope = "no beams picked - inventory and auto-scans only";
            }

            var text = AdjustBeamProbeReport.Build(document, beams, scope);
            var window = new LogWindow(
                $"Adjust Beam Probe - {document.Title} ({beams.Count} beams)",
                text,
                "AdjustBeamProbe_" + document.Title);

            RevitWindow.ShowDialog(window, context.UiApplication);
            return Result.Succeeded;
        }

        private static List<FamilyInstance> GetSelectedBeams(CommandContext context, out string scope)
        {
            scope = "current selection";
            var ids = context.UiDocument.Selection.GetElementIds();

            return ids
                .Select(id => context.Document.GetElement(id))
                .OfType<FamilyInstance>()
                .Where(IsStructuralFraming)
                .ToList();
        }

        private static List<FamilyInstance> GetBeamsInActiveView(CommandContext context, out string scope)
        {
            var view = context.ActiveView;
            scope = $"structural framing visible in view \"{view?.Name}\"";

            if (view == null)
            {
                return new List<FamilyInstance>();
            }

            return new FilteredElementCollector(context.Document, view.Id)
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .ToList();
        }

        private static bool IsStructuralFraming(Element element)
        {
            return element.Category?.Id.ToLong() == (long)BuiltInCategory.OST_StructuralFraming;
        }
    }
}
