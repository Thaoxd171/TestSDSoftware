using Autodesk.Revit.DB;

namespace SDSoftware.RevitTest.Features.BearingPlate.Services
{
    /// <summary>
    /// Creates the drawing views that belong to an assembly. Revit names them itself - "Plan",
    /// "Front", "3D Ortho" - and scopes them under the assembly in the browser, which is why several
    /// assemblies can each own a view of the same name.
    /// </summary>
    public class AssemblyViewBuilder
    {
        private readonly Document _document;

        public AssemblyViewBuilder(Document document)
        {
            _document = document;
        }

        public View CreatePlan(ElementId assemblyId, ElementId templateId)
        {
            return CreateDetail(assemblyId, AssemblyDetailViewOrientation.HorizontalDetail, templateId);
        }

        public View CreateFront(ElementId assemblyId, ElementId templateId)
        {
            return CreateDetail(assemblyId, AssemblyDetailViewOrientation.ElevationFront, templateId);
        }

        public View CreateThreeD(ElementId assemblyId, ElementId templateId)
        {
            var view = AssemblyViewUtils.Create3DOrthographic(_document, assemblyId);
            ApplyTemplate(view, templateId);
            return view;
        }

        private View CreateDetail(ElementId assemblyId, AssemblyDetailViewOrientation orientation, ElementId templateId)
        {
            var view = AssemblyViewUtils.CreateDetailSection(_document, assemblyId, orientation);
            ApplyTemplate(view, templateId);
            return view;
        }

        /// <summary>
        /// The template carries scale, detail level and visibility. Applying it can fail when the
        /// template belongs to another view type, in which case the view keeps Revit's defaults.
        /// </summary>
        private static void ApplyTemplate(View view, ElementId templateId)
        {
            if (view == null || templateId == null || templateId == ElementId.InvalidElementId)
            {
                return;
            }

            try
            {
                view.ViewTemplateId = templateId;
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // template not valid for this view type - leave the view untemplated
            }
        }
    }
}
