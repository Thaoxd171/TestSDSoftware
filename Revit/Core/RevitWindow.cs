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
            Own(window, application);
            return window.ShowDialog();
        }

        /// <summary>Shows a window without blocking - used for progress while the command runs.</summary>
        public static void Show(Window window, UIApplication application)
        {
            Own(window, application);
            window.Show();
        }

        private static void Own(Window window, UIApplication application)
        {
            new WindowInteropHelper(window) { Owner = application.MainWindowHandle };
        }
    }
}
