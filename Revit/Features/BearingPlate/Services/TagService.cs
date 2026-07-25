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

            // the tags down the left side carry names of different lengths; the reference drawings line
            // their right edges up rather than their left, so the long ones reach further out
            var column = PlaceCluster(result, view, tagType, components,
                start: new XYZ(box.Min.X - side, box.Min.Y - side, box.Max.Z),
                step: new XYZ(0, -step, 0),
                orientation: TagOrientation.Horizontal);

            RightAlign(column, view);

            PlaceLabel(result, view, labelType,
                new XYZ(box.Min.X, box.Min.Y - (LabelOffsetMm / 2).MmToFeet(), box.Max.Z),
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
            var end = EndOffsetMm.MmToFeet();

            // the two dimensions down the left side each get a tag above them: the stud on the
            // breakdown of plate thickness and stud length, the plate on the overall height. The
            // markers all sit flat on the plate, so height cannot tell them apart - the roles do.
            var stud = components.FirstOrDefault(c => c.IsStud);
            var outline = components.FirstOrDefault(c => c.IsOutline);
            var sideTags = new[] { stud, outline }.Where(c => c != null).ToList();

            // the markers are placed rotated 90 degrees and this tag family turns with its host, so the
            // orientations are the mirror of how they read: Vertical here comes out upright, which is
            // what the reference drawings use for the two tags above the side dimensions
            PlaceCluster(result, view, tagType, sideTags,
                start: new XYZ(box.Min.X - 2 * step, box.Max.Y, box.Max.Z + (StepMm / 4).MmToFeet()),
                step: new XYZ(step, 0, 0),
                orientation: TagOrientation.Vertical);

            // the rows read top to bottom Ø24, Ø11, stud, overall, so the tags climbing the right edge
            // must match: overall nearest the plate, then the parts in the reverse of the plan order
            var rowOrder = components.Where(c => c.IsOutline)
                .Concat(components.Where(c => !c.IsOutline).Reverse())
                .ToList();

            PlaceCluster(result, view, tagType, rowOrder,
                start: new XYZ(box.Max.X + end, box.Max.Y, box.Max.Z + step),
                step: new XYZ(0, 0, step),
                orientation: TagOrientation.Horizontal);

            PlaceLabel(result, view, labelType,
                new XYZ(box.Min.X, box.Min.Y, box.Min.Z - LabelOffsetMm.MmToFeet()),
                AssemblyViewBuilder.FrontName);

            return result;
        }

        private List<IndependentTag> PlaceCluster(
            AnnotationResult result,
            View view,
            FamilySymbol tagType,
            IList<PlateComponent> components,
            XYZ start,
            XYZ step,
            TagOrientation orientation)
        {
            var placed = new List<IndependentTag>();

            if (tagType == null)
            {
                result.Note("no tag type available");
                return placed;
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
                    placed.Add(IndependentTag.Create(
                        _document,
                        tagType.Id,
                        view.Id,
                        new Reference(target),
                        addLeader: false,
                        orientation,
                        start + step * index));
                    result.Success();
                }
                catch (Exception ex)
                {
                    result.Failure(component.Name, ex.Message);
                }
            }

            return placed;
        }

        /// <summary>
        /// Lines a column of tags up by their right edge instead of the point they were placed at, so
        /// longer names reach further out rather than crowding the drawing. Their real widths are read
        /// back from the view, so it works whatever the names turn out to be.
        /// </summary>
        private void RightAlign(IList<IndependentTag> tags, View view)
        {
            if (tags.Count < 2)
            {
                return;
            }

            _document.Regenerate();

            var edges = tags
                .Select(t => t.get_BoundingBox(view))
                .Where(b => b != null)
                .Select(b => b.Max.X)
                .ToList();

            if (edges.Count == 0)
            {
                return;
            }

            var rightEdge = edges.Min();

            foreach (var tag in tags)
            {
                var box = tag.get_BoundingBox(view);
                if (box == null)
                {
                    continue;
                }

                tag.TagHeadPosition += new XYZ(rightEdge - box.Max.X, 0, 0);
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
