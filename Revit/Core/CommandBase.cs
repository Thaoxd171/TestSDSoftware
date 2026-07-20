using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SDSoftware.RevitTest.Core
{
    /// <summary>
    /// Base class for every command in this add-in. Centralises exception handling so that
    /// a user cancelling a dialog or a pick operation never surfaces as a Revit error.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public abstract class CommandBase : IExternalCommand
    {
        /// <summary>Title shown in dialogs raised by this command.</summary>
        protected abstract string Title { get; }

        protected abstract Result Run(CommandContext context);

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                return Run(new CommandContext(commandData));
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // user pressed Esc during a pick operation
                return Result.Cancelled;
            }
            catch (OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                AppLog.ShowError(Title, ex);
                return Result.Failed;
            }
        }

        /// <summary>Shows an information dialog using this command's title.</summary>
        protected void ShowInfo(string mainInstruction, string content = null)
        {
            var dialog = new TaskDialog(Title) { MainInstruction = mainInstruction };
            if (!string.IsNullOrEmpty(content))
            {
                dialog.MainContent = content;
            }

            dialog.Show();
        }
    }
}
