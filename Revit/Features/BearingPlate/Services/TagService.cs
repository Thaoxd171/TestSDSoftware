using System;
using System.Collections.Generic;
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
        public int AnnotatePlan(View view, Element plate, IList<PlateComponent> components, FamilySymbol tagType, TextNoteType labelType)
        {
            var box = plate.get_BoundingBox(null);
            if (box == null)
            {
                return 0;
            }

            var step = StepMm.MmToFeet();
            var side = SideOffsetMm.MmToFeet();
            var end = EndOffsetMm.MmToFeet();

            var placed = 0;
            placed += PlaceCluster(view, tagType, components,
                start: new XYZ(box.Min.X - side, box.Max.Y + end, box.Max.Z),
                step: new XYZ(-step, 0, 0),
                orientation: TagOrientation.Vertical);

            placed += PlaceCluster(view, tagType, components,
                start: new XYZ(box.Min.X - side, box.Min.Y - side, box.Max.Z),
                step: new XYZ(0, -step, 0),
                orientation: TagOrientation.Horizontal);

            PlaceLabel(view, labelType, new XYZ(box.Min.X, box.Min.Y - LabelOffsetMm.MmToFeet(), box.Max.Z));
            return placed;
        }

        /// <summary>
        /// Front is drawn looking along -Y, so the clusters run in the XZ plane: one to the left of
        /// the plate stepping left, one to its right stepping up.
        /// </summary>
        public int AnnotateFront(View view, Element plate, IList<PlateComponent> components, FamilySymbol tagType, TextNoteType labelType)
        {
            var box = plate.get_BoundingBox(null);
            if (box == null)
            {
                return 0;
            }

            var step = StepMm.MmToFeet();
            var side = SideOffsetMm.MmToFeet();
            var end = EndOffsetMm.MmToFeet();

            var placed = 0;
            placed += PlaceCluster(view, tagType, components,
                start: new XYZ(box.Min.X - side, box.Max.Y, box.Max.Z + end),
                step: new XYZ(-step, 0, 0),
                orientation: TagOrientation.Vertical);

            placed += PlaceCluster(view, tagType, components,
                start: new XYZ(box.Max.X + end, box.Max.Y, box.Max.Z + step),
                step: new XYZ(0, 0, step),
                orientation: TagOrientation.Horizontal);

            PlaceLabel(view, labelType, new XYZ(box.Min.X, box.Min.Y, box.Min.Z - LabelOffsetMm.MmToFeet()));
            return placed;
        }

        private int PlaceCluster(
            View view,
            FamilySymbol tagType,
            IList<PlateComponent> components,
            XYZ start,
            XYZ step,
            TagOrientation orientation)
        {
            if (tagType == null)
            {
                return 0;
            }

            tagType.EnsureActive();

            var placed = 0;
            for (var index = 0; index < components.Count; index++)
            {
                var point = start + step * index;

                try
                {
                    IndependentTag.Create(
                        _document,
                        tagType.Id,
                        view.Id,
                        new Reference(components[index].Representative),
                        addLeader: false,
                        orientation,
                        point);
                    placed++;
                }
                catch (Exception)
                {
                    // the part is not visible in this view - nothing to tag
                }
            }

            return placed;
        }

        private void PlaceLabel(View view, TextNoteType labelType, XYZ point)
        {
            if (labelType == null)
            {
                return;
            }

            try
            {
                TextNote.Create(_document, view.Id, point, view.Name, labelType.Id);
            }
            catch (Exception)
            {
                // a label is cosmetic; never fail the drawing over it
            }
        }
    }
}
