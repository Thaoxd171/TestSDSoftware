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
        /// Clearance kept in front of and behind the assembly along the view direction. Anything
        /// clipped away counts as not visible - it cannot be tagged and does not print.
        /// </summary>
        private const double DepthClearanceMm = 50.0;

        /// <summary>
        /// Margin kept around the assembly in the plane of the view, leaving room for the dimensions
        /// and tags that sit outside the plate. These two numbers reproduce the section boxes measured
        /// on the reference drawings exactly.
        /// </summary>
        private const double InPlaneMarginMm = 350.0;

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
            // every assembly owns a view called "Plan" and "Front", so the section-line "viewer"
            // element cannot be found by name once there is more than one plate. Snapshot the viewers
            // before and after creating this section; the new one belongs to this view.
            var before = ViewerIds();
            var view = AssemblyViewUtils.CreateDetailSection(_document, assemblyId, orientation);
            _document.Regenerate();
            var viewers = ViewerIds().Where(id => !before.Contains(id)).ToList();

            Rename(view, name);
            ApplyTemplate(view, templateId);
            FitCrop(view, viewers, assemblyId);
            return view;
        }

        private HashSet<ElementId> ViewerIds()
        {
            return new FilteredElementCollector(_document)
                .OfCategory(BuiltInCategory.OST_Viewers)
                .WhereElementIsNotElementType()
                .ToElementIds()
                .ToHashSet();
        }

        /// <summary>
        /// Shrinks the section down to the plate. Revit opens a new detail section on a huge default
        /// box whose cutting plane sits far from the plate, which throws the section line a long way
        /// off in the other views and cuts an enormous depth. This moves the cutting plane just in
        /// front of the plate, crops the drawing to it with a margin for the annotation, and sets the
        /// far clip to the plate's depth - the same section box the reference drawings use.
        ///
        /// The depth is driven by the far clip parameter, not the crop box: Revit ignores the crop
        /// box's own depth, so that has to be set on its own.
        /// </summary>
        private void FitCrop(View view, IList<ElementId> viewers, ElementId assemblyId)
        {
            var box = BoundsOf(assemblyId);
            if (view == null || box == null)
            {
                return;
            }

            var margin = InPlaneMarginMm.MmToFeet();
            var clearance = DepthClearanceMm.MmToFeet();
            var viewDir = view.ViewDirection;

            // Revit ignores a moved crop-box transform, so the whole section is slid along its view
            // direction until the cutting plane sits a clearance in front of the nearest face of the
            // plate. ViewDirection points towards the viewer, so the nearest face is the furthest one
            // along it. This is what puts the section line next to the plate in the other views.
            // The view has to be regenerated first, otherwise the move is silently dropped.
            _document.Regenerate();
            var target = Corners(box).Max(corner => corner.DotProduct(viewDir)) + clearance;
            var shift = (target - view.Origin.DotProduct(viewDir)) * viewDir;
            if (!shift.IsZeroLength())
            {
                // the section line is a separate "viewer" element; moving the view on its own leaves
                // that mark behind, so the view and its own viewers are moved together - the same as
                // selecting both by hand before dragging
                var ids = new List<ElementId> { view.Id };
                ids.AddRange(viewers ?? new List<ElementId>());

                try
                {
                    ElementTransformUtils.MoveElements(_document, ids, shift);
                    _document.Regenerate();
                }
                catch (Exception)
                {
                    // the section refused to move; the crop and far clip below still trim it
                }
            }

            // in-plane crop around the plate, measured from wherever the section ended up
            var crop = view.CropBox;
            var toLocal = crop.Transform.Inverse;
            var local = Corners(box).Select(toLocal.OfPoint).ToList();

            crop.Min = new XYZ(local.Min(p => p.X) - margin, local.Min(p => p.Y) - margin, local.Min(p => p.Z) - clearance);
            crop.Max = new XYZ(local.Max(p => p.X) + margin, local.Max(p => p.Y) + margin, local.Max(p => p.Z) + clearance);

            // the crop box is set but left inactive, exactly as the reference drawings have it: the
            // far clip trims the depth and the assembly scopes the view, so the viewport takes the
            // size of its content rather than the whole crop, which keeps a tall plan from pushing the
            // front off the sheet
            view.CropBox = crop;
            view.CropBoxActive = false;
            view.CropBoxVisible = false;

            // far clip reaches from the cutting plane to the back of the plate, plus a clearance
            var into = viewDir.Negate();
            var far = Corners(box).Max(corner => (corner - view.Origin).DotProduct(into)) + clearance;
            SetClip(view, BuiltInParameter.VIEWER_BOUND_ACTIVE_FAR, BuiltInParameter.VIEWER_BOUND_OFFSET_FAR, far);
        }

        private static void SetClip(View view, BuiltInParameter active, BuiltInParameter offset, double value)
        {
            var isActive = view.get_Parameter(active);
            if (isActive != null && !isActive.IsReadOnly)
            {
                isActive.Set(1);
            }

            var far = view.get_Parameter(offset);
            if (far != null && !far.IsReadOnly)
            {
                far.Set(value);
            }
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
