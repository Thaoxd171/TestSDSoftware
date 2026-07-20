using System;
using System.IO;
using Autodesk.Revit.UI;

namespace SDSoftware.RevitTest.Core
{
    /// <summary>
    /// Minimal error reporting: a readable dialog for the user and a rolling text log for diagnosis.
    /// </summary>
    internal static class AppLog
    {
        private static readonly string LogFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SDRevitTest",
            "SDRevitTest.log");

        public static void ShowError(string title, Exception ex)
        {
            Write(title, ex);

            var dialog = new TaskDialog(title)
            {
                MainIcon = TaskDialogIcon.TaskDialogIconError,
                MainInstruction = "The command could not be completed.",
                MainContent = ex.Message,
                ExpandedContent = ex.ToString(),
                FooterText = "Details were written to " + LogFile,
            };
            dialog.Show();
        }

        public static void Write(string title, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
                File.AppendAllText(
                    LogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{title}] {ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // logging must never break the command
            }
        }
    }
}
