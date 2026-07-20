using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SDSoftware.RevitTest.Core
{
    /// <summary>
    /// The Revit objects a command normally needs, bundled so that services can be passed
    /// one argument instead of digging through ExternalCommandData.
    /// </summary>
    public class CommandContext
    {
        public CommandContext(ExternalCommandData commandData)
        {
            UiApplication = commandData.Application;
            UiDocument = UiApplication.ActiveUIDocument;
            Document = UiDocument?.Document;
        }

        public UIApplication UiApplication { get; }

        public UIDocument UiDocument { get; }

        public Document Document { get; }

        public View ActiveView => Document?.ActiveView;
    }
}
