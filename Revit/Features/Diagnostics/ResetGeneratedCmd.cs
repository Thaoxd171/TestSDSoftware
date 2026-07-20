using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SDSoftware.RevitTest.Core;
using SDSoftware.RevitTest.Extensions;

namespace SDSoftware.RevitTest.Features.Diagnostics
{
    /// <summary>
    /// Deletes everything the Bearing Plate generator produces - the assembly sheets and every view
    /// bound to an assembly - so the tool can be run again on the same model. The assemblies
    /// themselves, the view templates, the title blocks and the families are left untouched.
    /// Temporary development aid.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ResetGeneratedCmd : CommandBase
    {
        protected override string Title => "Reset Generated Drawings";

        protected override Result Run(CommandContext context)
        {
            var document = context.Document;
            if (document == null)
            {
                ShowInfo("No document is open.");
                return Result.Cancelled;
            }

            var sheets = document.OfClass<ViewSheet>()
                .Where(s => s.AssociatedAssemblyInstanceId != ElementId.InvalidElementId)
                .ToList();

            var views = document.OfClass<View>()
                .Where(v => !v.IsTemplate
                            && v.ViewType != ViewType.DrawingSheet
                            && v.AssociatedAssemblyInstanceId != ElementId.InvalidElementId)
                .ToList();

            if (sheets.Count == 0 && views.Count == 0)
            {
                ShowInfo("Nothing to reset.", "This model has no sheets or views bound to an assembly.");
                return Result.Succeeded;
            }

            if (!Confirm(document, sheets, views))
            {
                return Result.Cancelled;
            }

            // The active view cannot be deleted, so step away from it first.
            EscapeActiveView(context, sheets, views);

            var deleted = 0;
            TransactionHelper.Run(document, "Reset generated drawings", () =>
            {
                deleted += Delete(document, sheets.Select(s => s.Id));
                deleted += Delete(document, views.Select(v => v.Id));
            });

            ShowInfo($"Deleted {deleted} element(s).",
                $"{sheets.Count} sheet(s) and {views.Count} view(s) bound to an assembly were removed.");
            return Result.Succeeded;
        }

        private bool Confirm(Document document, List<ViewSheet> sheets, List<View> views)
        {
            var sheetList = string.Join(", ", sheets.Take(6).Select(s => s.SheetNumber));
            if (sheets.Count > 6)
            {
                sheetList += $", ... (+{sheets.Count - 6})";
            }

            var dialog = new TaskDialog(Title)
            {
                MainInstruction = $"Delete {sheets.Count} sheet(s) and {views.Count} view(s)?",
                MainContent = "Everything bound to an assembly will be removed from " + document.Title +
                              ". Assemblies, view templates, title blocks and families are kept.\n\n" +
                              "Sheets: " + sheetList,
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No,
            };

            return dialog.Show() == TaskDialogResult.Yes;
        }

        /// <summary>Activates a view that is not about to be deleted.</summary>
        private static void EscapeActiveView(CommandContext context, List<ViewSheet> sheets, List<View> views)
        {
            var doomed = sheets.Select(s => s.Id.ToLong())
                .Concat(views.Select(v => v.Id.ToLong()))
                .ToHashSet();

            if (!doomed.Contains(context.ActiveView.Id.ToLong()))
            {
                return;
            }

            var survivor = context.Document.OfClass<View>()
                .FirstOrDefault(v => !v.IsTemplate
                                     && v.ViewType != ViewType.DrawingSheet
                                     && !doomed.Contains(v.Id.ToLong()));

            if (survivor != null)
            {
                context.UiDocument.ActiveView = survivor;
            }
        }

        private static int Delete(Document document, IEnumerable<ElementId> ids)
        {
            var alive = ids.Where(id => document.GetElement(id) != null).ToList();
            return alive.Count == 0 ? 0 : document.Delete(alive).Count;
        }
    }
}
