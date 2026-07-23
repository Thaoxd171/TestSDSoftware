using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SDSoftware.RevitTest.Extensions;

namespace SDSoftware.RevitTest.Features.AdjustBeam.Services
{
    /// <summary>
    /// Takes the bearing block off the side of a beam where another beam has to come down past it.
    ///
    /// Where two precast beams land on the same column their widened ends want the same space, and the
    /// one in the way is cut back flush with the side of its web, leaving the other a clear path down.
    ///
    /// What has to come off is settled by intersecting the two solids rather than by comparing how far
    /// each reaches along the beam. A beam cut off at an angle still reaches a long way past the corner
    /// it presents, so reading extents alone finds beams clashing that are in fact a clean twenty
    /// millimetres apart.
    ///
    /// The opening itself is a plain vertical cut. It only has to be lined up with the web: above the
    /// block the beam is already narrower than the cut, so nothing up there is touched.
    ///
    /// Must be called inside a transaction, after the ends have been moved, squared off and the
    /// document regenerated - what is in the way depends on where the beams finally sit.
    /// </summary>
    public static class BeamNotcher
    {
        /// <summary>How far past the crossing beam the cut reaches, at each side.</summary>
        private const double SideMarginMm = 35;

        /// <summary>How far past the outside of the block the cut reaches.</summary>
        private const double DepthMarginMm = 150;

        /// <summary>How far around the beam other beams are looked for.</summary>
        private const double SearchMarginMm = 800;

        /// <summary>
        /// Below this the two solids are touching rather than clashing. In cubic feet, Revit's own
        /// unit for a volume - a thousand cubic millimetres, or a cube four millimetres on a side.
        /// </summary>
        private const double NegligibleVolume = 1000 / (304.8 * 304.8 * 304.8);

        /// <summary>
        /// Cuts this beam's bearing blocks back where they clash, one log entry per opening created.
        /// </summary>
        /// <param name="yieldedTo">
        /// The beams this one has already stopped short of. Two blocks wanting the same space is a
        /// clash only one of them can give way to, and it is not this one: an end that has already
        /// backed off to clear a neighbour does not hand over its bearing as well. The neighbour is
        /// notched for it instead, on its own turn through here.
        /// </param>
        public static IList<string> Trim(Document document, FamilyInstance beam, ICollection<long> yieldedTo)
        {
            var notes = new List<string>();
            var section = BeamSection.Read(beam);
            if (section == null || (!section.HasBlock(1) && !section.HasBlock(-1)))
            {
                return notes;
            }

            foreach (var neighbour in Neighbours(document, beam))
            {
                if (yieldedTo.Contains(neighbour.Id.ToLong()))
                {
                    continue;
                }

                var clash = Clash(section, neighbour);
                if (clash.Count == 0)
                {
                    continue;
                }

                foreach (var side in new[] { 1, -1 })
                {
                    if (!section.HasBlock(side))
                    {
                        continue;
                    }

                    var note = Cut(document, beam, section, side, clash);
                    if (note != null)
                    {
                        notes.Add(note + $" for id {neighbour.Id.ToLong()}");
                    }
                }
            }

            return notes;
        }

        /// <summary>Cuts one side back, or returns null when the clash is not on that side.</summary>
        private static string Cut(
            Document document,
            FamilyInstance beam,
            BeamSection section,
            int side,
            IList<XYZ> clash)
        {
            var web = section.Web(side).Value;
            var flange = section.Flange(side).Value;
            var step = BeamSection.StepMm.MmToFeet();

            var inTheBlock = clash
                .Where(point => section.Across(point, side) > web + step)
                .Select(section.Along)
                .ToList();

            if (inTheBlock.Count == 0)
            {
                return null;
            }

            var margin = SideMarginMm.MmToFeet();
            var low = inTheBlock.Min() - margin;
            var high = inTheBlock.Max() + margin;
            var depth = flange + DepthMarginMm.MmToFeet();

            var corners = new[]
            {
                section.PointAt(low, web, side),
                section.PointAt(high, web, side),
                section.PointAt(high, depth, side),
                section.PointAt(low, depth, side),
            };

            var profile = new CurveArray();
            for (var index = 0; index < corners.Length; index++)
            {
                profile.Append(Line.CreateBound(corners[index], corners[(index + 1) % corners.Length]));
            }

            document.Create.NewOpening(beam, profile, Autodesk.Revit.Creation.eRefFace.CenterZ);

            return $"took the bearing block off the {(side > 0 ? "left" : "right")} side, " +
                   $"{(flange - web).FeetToMm():0.#} mm of it over {(high - low).FeetToMm():0.#} mm";
        }

        /// <summary>Where the two beams actually share the same space, as points. Empty when they do not.</summary>
        private static IList<XYZ> Clash(BeamSection section, Element neighbour)
        {
            var others = neighbour.GetSolids().ToList();
            var points = new List<XYZ>();

            foreach (var mine in section.Solids)
            {
                foreach (var theirs in others)
                {
                    Solid shared;
                    try
                    {
                        shared = BooleanOperationsUtils.ExecuteBooleanOperation(
                            mine, theirs, BooleanOperationsType.Intersect);
                    }
                    catch
                    {
                        continue;
                    }

                    if (shared == null || shared.Volume < NegligibleVolume)
                    {
                        continue;
                    }

                    points.AddRange(shared.GetVertices());
                }
            }

            return points;
        }

        /// <summary>Beams whose bounding box comes near this one.</summary>
        private static IEnumerable<FamilyInstance> Neighbours(Document document, FamilyInstance beam)
        {
            var box = beam.get_BoundingBox(null);
            if (box == null)
            {
                return Enumerable.Empty<FamilyInstance>();
            }

            var margin = new XYZ(1, 1, 1) * SearchMarginMm.MmToFeet();
            var outline = new Outline(box.Min - margin, box.Max + margin);

            return new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .OfType<FamilyInstance>()
                .Where(other => other.Id != beam.Id)
                .Where(other => other.StructuralType == StructuralType.Beam)
                .ToList();
        }
    }
}
