using System;
using System.Collections.Generic;
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

        /// <summary>
        /// Looking straight down from above the assembly. HorizontalDetail would cut through it -
        /// Revit puts that cut a couple of millimetres below the top face - and everything above the
        /// cut is dropped, including the parts that have to be tagged.
        /// </summary>
        public View CreatePlan(ElementId assemblyId, ElementId templateId)
        {
            return CreateDetail(assemblyId, AssemblyDetailViewOrientation.ElevationTop, templateId, PlanName);
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
        /// Opens the section deep enough to reach past the whole assembly.
        ///
        /// The depth is measured from the view's own plane, not from the assembly: Revit can place
        /// that plane far away, and a far clip sized only to the assembly would then cut everything
        /// out of the drawing.
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

            // ViewDirection points towards the viewer, so depth into the drawing runs the other way
            var into = view.ViewDirection.Negate();
            var origin = view.Origin;
            var depth = Corners(box).Max(corner => (corner - origin).DotProduct(into));

            if (depth <= 0)
            {
                // the assembly sits behind the view plane; leave Revit's default alone
                return;
            }

            offset.Set(depth + DepthClearanceMm.MmToFeet());
        }

        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
        {
            foreach (var x in new[] { box.Min.X, box.Max.X })
            {
                foreach (var y in new[] { box.Min.Y, box.Max.Y })
                {
                    foreach (var z in new[] { box.Min.Z, box.Max.Z })
                    {
                        yield return new XYZ(x, y, z);
                    }
                }
            }
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
