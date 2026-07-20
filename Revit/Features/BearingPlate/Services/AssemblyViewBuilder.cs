using System;
using Autodesk.Revit.DB;

namespace SDSoftware.RevitTest.Features.BearingPlate.Services
{
    /// <summary>
    /// Creates the drawing views that belong to an assembly. Revit generates its own names, so each
    /// view is renamed to the short name the reference drawings use. Assembly views are scoped under
    /// their assembly in the browser, which is why several assemblies can each own a view called
    /// "Plan".
    /// </summary>
    public class AssemblyViewBuilder
    {
        public const string PlanName = "Plan";
        public const string FrontName = "Front";
        public const string ThreeDName = "3D Ortho";

        private readonly Document _document;

        public AssemblyViewBuilder(Document document)
        {
            _document = document;
        }

        public View CreatePlan(ElementId assemblyId, ElementId templateId)
        {
            return CreateDetail(assemblyId, AssemblyDetailViewOrientation.HorizontalDetail, templateId, PlanName);
        }

        public View CreateFront(ElementId assemblyId, ElementId templateId)
        {
            return CreateDetail(assemblyId, AssemblyDetailViewOrientation.ElevationFront, templateId, FrontName);
        }

        public View CreateThreeD(ElementId assemblyId, ElementId templateId)
        {
            var view = AssemblyViewUtils.Create3DOrthographic(_document, assemblyId);
            Rename(view, ThreeDName);
            ApplyTemplate(view, templateId);
            return view;
        }

        private View CreateDetail(ElementId assemblyId, AssemblyDetailViewOrientation orientation, ElementId templateId, string name)
        {
            var view = AssemblyViewUtils.CreateDetailSection(_document, assemblyId, orientation);
            Rename(view, name);
            ApplyTemplate(view, templateId);
            return view;
        }

        /// <summary>Keeps Revit's generated name when the wanted one is refused.</summary>
        private static void Rename(View view, string name)
        {
            if (view == null)
            {
                return;
            }

            try
            {
                view.Name = name;
            }
            catch (Exception)
            {
                // name already in use in a way this model does not allow
            }
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
