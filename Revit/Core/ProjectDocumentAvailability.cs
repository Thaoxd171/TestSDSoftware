using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SDSoftware.RevitTest.Core
{
    /// <summary>
    /// Buttons using this availability class are greyed out unless a project document
    /// (not a family document) is open.
    /// </summary>
    public class ProjectDocumentAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
        {
            var doc = applicationData?.ActiveUIDocument?.Document;
            return doc != null && !doc.IsFamilyDocument;
        }
    }
}
