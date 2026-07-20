using System;
using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Extensions;

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

        /// <summary>
        /// Clearance kept in front of and behind the assembly. Revit's default section depth is
        /// shallow enough to cut off the parts sitting under the plate, and anything clipped away
        /// counts as not visible - it cannot be tagged and does not print.
        /// </summary>
        private const double DepthClearanceMm = 50.0;

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
            FitFarClip(view, assemblyId);
            return view;
        }

        /// <summary>
        /// Opens the section deep enough to contain the whole assembly, measured along the view
        /// direction, with clearance on both sides.
        /// </summary>
        private void FitFarClip(View view, ElementId assemblyId)
        {
            var offset = view?.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
            if (offset == null || offset.IsReadOnly)
            {
                return;
            }

            var box = BoundsOf(assemblyId);
            if (box == null)
            {
                return;
            }

            // projected extent of an axis-aligned box onto the view direction
            var size = box.Max - box.Min;
            var direction = view.ViewDirection;
            var depth = Math.Abs(size.X * direction.X)
                        + Math.Abs(size.Y * direction.Y)
                        + Math.Abs(size.Z * direction.Z);

            offset.Set(depth + 2 * DepthClearanceMm.MmToFeet());
        }

        private BoundingBoxXYZ BoundsOf(ElementId assemblyId)
        {
            var assembly = _document.GetElement(assemblyId) as AssemblyInstance;
            if (assembly == null)
            {
                return null;
            }

            // an assembly reports no bounding box of its own, so combine its members
            return assembly.GetMemberIds()
                .Select(_document.GetElement)
                .Where(e => e != null)
                .GetCombinedBoundingBox();
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
