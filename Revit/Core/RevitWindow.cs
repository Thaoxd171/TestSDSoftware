using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.UI;

namespace SDSoftware.RevitTest.Core
{
    /// <summary>
    /// Shows a WPF window owned by the Revit main window, so it stays on top and Revit is
    /// correctly disabled while a modal dialog is open.
    /// </summary>
    public static class RevitWindow
    {
        public static bool? ShowDialog(Window window, UIApplication application)
        {
            new WindowInteropHelper(window) { Owner = application.MainWindowHandle };
            return window.ShowDialog();
        }
    }
}
