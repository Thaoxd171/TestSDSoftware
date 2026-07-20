using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Extensions;
using SDSoftware.RevitTest.Features.BearingPlate.Models;

namespace SDSoftware.RevitTest.Features.BearingPlate.Services
{
    /// <summary>
    /// Labels a plate drawing: a tag per kind of part, arranged in two evenly spaced clusters, plus
    /// the view name written under the drawing. Spacing and offsets are the ones measured on the
    /// reference sheets.
    /// </summary>
    public class TagService
    {
        /// <summary>Distance between two tags in a cluster.</summary>
        private const double StepMm = 40.0;

        /// <summary>How far a cluster sits beside the plate.</summary>
        private const double SideOffsetMm = 40.0;

        /// <summary>How far a cluster sits past the end of the plate.</summary>
        private const double EndOffsetMm = 25.0;

        /// <summary>Drop of the view label below the plate.</summary>
        private const double LabelOffsetMm = 17.5;

        private readonly Document _document;

        public TagService(Document document)
        {
            _document = document;
        }

        /// <summary>
        /// Plan is drawn looking down, so both clusters run in the XY plane: one above the plate
        /// stepping left, one below it stepping down.
        /// </summary>
        public AnnotationResult AnnotatePlan(View view, Element plate, IList<PlateComponent> components, FamilySymbol tagType, TextNoteType labelType)
        {
            var result = new AnnotationResult();

            var box = plate.get_BoundingBox(null);
            if (box == null)
            {
                result.Note("plate has no bounding box");
                return result;
            }

            var step = StepMm.MmToFeet();
            var side = SideOffsetMm.MmToFeet();
            var end = EndOffsetMm.MmToFeet();

            PlaceCluster(result, view, tagType, components,
                start: new XYZ(box.Min.X - side, box.Max.Y + end, box.Max.Z),
                step: new XYZ(-step, 0, 0),
                orientation: TagOrientation.Vertical);

            PlaceCluster(result, view, tagType, components,
                start: new XYZ(box.Min.X - side, box.Min.Y - side, box.Max.Z),
                step: new XYZ(0, -step, 0),
                orientation: TagOrientation.Horizontal);

            PlaceLabel(result, view, labelType,
                new XYZ(box.Min.X, box.Min.Y - LabelOffsetMm.MmToFeet(), box.Max.Z),
                AssemblyViewBuilder.PlanName);

            return result;
        }

        /// <summary>
        /// Front is drawn looking along -Y, so the clusters run in the XZ plane: one to the left of
        /// the plate stepping left, one to its right stepping up.
        /// </summary>
        public AnnotationResult AnnotateFront(View view, Element plate, IList<PlateComponent> components, FamilySymbol tagType, TextNoteType labelType)
        {
            var result = new AnnotationResult();

            var box = plate.get_BoundingBox(null);
            if (box == null)
            {
                result.Note("plate has no bounding box");
                return result;
            }

            var step = StepMm.MmToFeet();
            var side = SideOffsetMm.MmToFeet();
            var end = EndOffsetMm.MmToFeet();

            // down the side of an elevation only the parts that stand proud of the plate are worth
            // listing; a hole through it has no height of its own to call out here
            PlaceCluster(result, view, tagType, components.Where(c => c.HasHeight).ToList(),
                start: new XYZ(box.Min.X - side, box.Max.Y, box.Max.Z + end),
                step: new XYZ(-step, 0, 0),
                orientation: TagOrientation.Vertical);

            PlaceCluster(result, view, tagType, components,
                start: new XYZ(box.Max.X + end, box.Max.Y, box.Max.Z + step),
                step: new XYZ(0, 0, step),
                orientation: TagOrientation.Horizontal);

            PlaceLabel(result, view, labelType,
                new XYZ(box.Min.X, box.Min.Y, box.Min.Z - LabelOffsetMm.MmToFeet()),
                AssemblyViewBuilder.FrontName);

            return result;
        }

        private void PlaceCluster(
            AnnotationResult result,
            View view,
            FamilySymbol tagType,
            IList<PlateComponent> components,
            XYZ start,
            XYZ step,
            TagOrientation orientation)
        {
            if (tagType == null)
            {
                result.Note("no tag type available");
                return;
            }

            tagType.EnsureActive();
            var visible = VisibleElementIds(view);

            for (var index = 0; index < components.Count; index++)
            {
                var component = components[index];
                var target = component.Representative;

                if (!visible.Contains(target.Id.ToLong()))
                {
                    result.Failure(component.Name, "not visible in this view");
                    continue;
                }

                try
                {
                    IndependentTag.Create(
                        _document,
                        tagType.Id,
                        view.Id,
                        new Reference(target),
                        addLeader: false,
                        orientation,
                        start + step * index);
                    result.Success();
                }
                catch (Exception ex)
                {
                    result.Failure(component.Name, ex.Message);
                }
            }
        }

        /// <summary>
        /// What the view actually shows. A view template can hide a whole category, and Revit
        /// refuses to tag an element the view does not display.
        /// </summary>
        private HashSet<long> VisibleElementIds(View view)
        {
            return new HashSet<long>(
                new FilteredElementCollector(_document, view.Id)
                    .WhereElementIsNotElementType()
                    .ToElementIds()
                    .Select(id => id.ToLong()));
        }

        /// <summary>
        /// The label is the short drawing name - "Plan", "Front" - not the view's own name, which
        /// Revit may have had to qualify to keep unique.
        /// </summary>
        private void PlaceLabel(AnnotationResult result, View view, TextNoteType labelType, XYZ point, string label)
        {
            if (labelType == null)
            {
                result.Note("no text type available for the label");
                return;
            }

            try
            {
                TextNote.Create(_document, view.Id, point, label, labelType.Id);
            }
            catch (Exception ex)
            {
                result.Note("label failed: " + ex.Message);
            }
        }
    }
}
