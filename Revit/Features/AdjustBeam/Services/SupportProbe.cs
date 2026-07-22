using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SDSoftware.RevitTest.Extensions;
using SDSoftware.RevitTest.Features.AdjustBeam.Models;

namespace SDSoftware.RevitTest.Features.AdjustBeam.Services
{
    /// <summary>
    /// Looks at what stands in front of a beam end and turns it into plain numbers for
    /// <see cref="BeamEndSolver"/>. Walls and beams are found by shooting a ray along the beam axis
    /// and intersecting the solids it runs through; pillars are found in plan instead, because a
    /// precast beam sits on a corbel and the column itself starts above the beam, where no ray along
    /// the axis would ever reach it.
    /// </summary>
    public class SupportProbe
    {
        /// <summary>How far in front of the end a support is still taken into account.</summary>
        private const double ForwardReachMm = 1000;

        /// <summary>How far the end may already reach into something before it is ignored.</summary>
        private const double BackReachMm = 600;

        /// <summary>Sideways margin added to the search box around the ray.</summary>
        private const double SearchMarginMm = 600;

        /// <summary>Height searched above and below the beam, so columns on either side are found.</summary>
        private const double SearchHeightMm = 4000;

        /// <summary>Two beams count as continuing each other below this angle and this offset.</summary>
        private const double InlineAngleDegrees = 5;

        private const double InlineOffsetMm = 50;

        private static readonly IList<BuiltInCategory> SupportCategories = new List<BuiltInCategory>
        {
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_Columns,
            BuiltInCategory.OST_StructuralFraming,
        };

        private readonly Document _document;

        public SupportProbe(Document document)
        {
            _document = document;
        }

        /// <summary>The supports that count for this end.</summary>
        public IReadOnlyList<SupportCandidate> Probe(BeamGeometry beam, int end)
        {
            return ProbeAll(beam, end).Where(candidate => candidate.RejectionReason == null).ToList();
        }

        /// <summary>
        /// Everything the probe looked at, the rejected ones carrying the reason they were dropped.
        /// The tool itself only ever sees the accepted half; this is what the Explain command reads.
        /// </summary>
        public IReadOnlyList<SupportCandidate> ProbeAll(BeamGeometry beam, int end)
        {
            var origin = beam.ProbeOriginAt(end);
            var outward = beam.OutwardAt(end);
            var ray = Line.CreateBound(
                origin - outward * BackReachMm.MmToFeet(),
                origin + outward * ForwardReachMm.MmToFeet());

            var candidates = FindNeighbours(beam.Beam, ray)
                .Select(neighbour => Measure(beam, neighbour, origin, outward, ray))
                .Where(candidate => candidate != null)
                .ToList();

            foreach (var candidate in candidates)
            {
                candidate.RejectionReason = Reject(candidate);
            }

            return candidates;
        }

        /// <summary>
        /// Why a support cannot decide this end. It has to start close enough to matter, and it has to
        /// still stand in front of the end: something the beam has already run clear of - a wall it
        /// crosses in mid span, a column further back - is behind it and must be left alone, otherwise
        /// a continuous beam would be dragged back to the last thing it passed over.
        /// </summary>
        private static string Reject(SupportCandidate candidate)
        {
            if (candidate.RejectionReason != null)
            {
                return candidate.RejectionReason;
            }

            if (candidate.NearMm > ForwardReachMm)
            {
                return $"starts {candidate.NearMm:0.#} mm ahead, past the {ForwardReachMm:0} mm reach";
            }

            // A column stays in play even once the beam has passed over it: its centre is the point two
            // beams share and its outer face is as far as the beam may hang out. A wall or another beam
            // is done with as soon as the end is clear of it.
            if (candidate.Kind != SupportKind.Pillar && candidate.FarMm <= 0)
            {
                return $"ends {-candidate.FarMm:0.#} mm behind this end, the beam is already clear of it";
            }

            if (candidate.FarMm < -BackReachMm)
            {
                return $"ends {-candidate.FarMm:0.#} mm behind this end, past the {BackReachMm:0} mm reach";
            }

            return null;
        }

        /// <summary>Top and bottom of an element, measured up from the top of the beam.</summary>
        private static (double Top, double Bottom) HeightAboveBeam(Element neighbour, BeamGeometry beam)
        {
            var box = neighbour.get_BoundingBox(null);
            return box == null
                ? (0, 0)
                : ((box.Max.Z - beam.TopZ).FeetToMm(), (box.Min.Z - beam.TopZ).FeetToMm());
        }

        /// <summary>Everything of a supporting category whose bounding box meets the search box.</summary>
        private IEnumerable<Element> FindNeighbours(Element beam, Line ray)
        {
            var start = ray.GetEndPoint(0);
            var finish = ray.GetEndPoint(1);
            var margin = SearchMarginMm.MmToFeet();
            var height = SearchHeightMm.MmToFeet();

            var outline = new Outline(
                new XYZ(Math.Min(start.X, finish.X) - margin, Math.Min(start.Y, finish.Y) - margin, start.Z - height),
                new XYZ(Math.Max(start.X, finish.X) + margin, Math.Max(start.Y, finish.Y) + margin, start.Z + height));

            return new FilteredElementCollector(_document)
                .WherePasses(new ElementMulticategoryFilter(SupportCategories))
                .WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .Where(element => element.Id != beam.Id);
        }

        private SupportCandidate Measure(BeamGeometry beam, Element neighbour, XYZ origin, XYZ outward, Line ray)
        {
            var kind = Classify(beam, neighbour);
            if (kind == SupportKind.None)
            {
                return null;
            }

            var solids = neighbour.GetSolids().ToList();
            if (solids.Count == 0)
            {
                return null;
            }

            var isColumn = kind == SupportKind.Pillar;
            var height = HeightAboveBeam(neighbour, beam);

            var span = isColumn
                ? SpanInPlan(solids, origin, outward)
                : SpanAlongRay(solids, ray, origin, outward);

            var candidate = new SupportCandidate
            {
                Id = neighbour.Id.ToLong(),
                Kind = kind,
                Description = Describe(neighbour),
                TopAboveBeamMm = height.Top,
                BottomAboveBeamMm = height.Bottom,
            };

            if (span == null)
            {
                candidate.RejectionReason = isColumn
                    ? "the beam axis passes beside it in plan"
                    : "the beam axis does not run into it";
                return candidate;
            }

            candidate.NearMm = span.Value.Near.FeetToMm();
            candidate.FarMm = span.Value.Far.FeetToMm();
            candidate.CentreAlongMm = isColumn ? CentreAlong(neighbour, origin, outward, span.Value) : null;

            return candidate;
        }

        /// <summary>What kind of support this element is for that beam, or None when it is not one.</summary>
        private static SupportKind Classify(BeamGeometry beam, Element neighbour)
        {
            if (neighbour is Wall)
            {
                return SupportKind.Wall;
            }

            var category = neighbour.Category?.Id.ToLong();
            if (category == (long)BuiltInCategory.OST_StructuralColumns || category == (long)BuiltInCategory.OST_Columns)
            {
                return SupportKind.Pillar;
            }

            // Structural Framing also holds recesses and skirts; only real beams support anything.
            if (!(neighbour is FamilyInstance instance) || instance.StructuralType != StructuralType.Beam)
            {
                return SupportKind.None;
            }

            var otherAxis = neighbour.GetLocationLine();
            if (otherAxis == null)
            {
                return SupportKind.CrossingBeam;
            }

            var angle = AngleBetween(beam.Direction, otherAxis.Direction);
            var offset = DistanceFromAxis(beam.Axis, otherAxis.GetEndPoint(0));

            return angle <= InlineAngleDegrees && offset.FeetToMm() <= InlineOffsetMm
                ? SupportKind.InlineBeam
                : SupportKind.CrossingBeam;
        }

        /// <summary>Where the ray enters and leaves the solids, relative to the beam end.</summary>
        private static (double Near, double Far)? SpanAlongRay(
            IList<Solid> solids,
            Line ray,
            XYZ origin,
            XYZ outward)
        {
            var distances = new List<double>();

            foreach (var solid in solids)
            {
                SolidCurveIntersection intersection;
                try
                {
                    intersection = solid.IntersectWithCurve(ray, new SolidCurveIntersectionOptions());
                }
                catch
                {
                    continue;
                }

                for (var index = 0; index < (intersection?.SegmentCount ?? 0); index++)
                {
                    var segment = intersection.GetCurveSegment(index);
                    distances.Add(segment.GetEndPoint(0).Subtract(origin).DotProduct(outward));
                    distances.Add(segment.GetEndPoint(1).Subtract(origin).DotProduct(outward));
                }
            }

            return distances.Count == 0 ? ((double, double)?)null : (distances.Min(), distances.Max());
        }

        /// <summary>
        /// Where the axis enters and leaves the solids seen from above, ignoring height. Used for
        /// pillars, which sit above or below the beam rather than in its path.
        /// </summary>
        private static (double Near, double Far)? SpanInPlan(IList<Solid> solids, XYZ origin, XYZ outward)
        {
            var across = XYZ.BasisZ.CrossProduct(outward);
            if (across.IsZeroLength())
            {
                return null;
            }

            across = across.Normalize();

            var points = solids
                .SelectMany(solid => solid.GetVertices())
                .Select(point => point - origin)
                .ToList();

            if (points.Count == 0)
            {
                return null;
            }

            var sideways = points.Select(point => point.DotProduct(across)).ToList();
            if (sideways.Min() > 0 || sideways.Max() < 0)
            {
                // The beam axis passes beside the pillar, not over it.
                return null;
            }

            var along = points.Select(point => point.DotProduct(outward)).ToList();
            return (along.Min(), along.Max());
        }

        /// <summary>Distance from the beam end to the pillar centre, along the axis.</summary>
        private static double CentreAlong(Element pillar, XYZ origin, XYZ outward, (double Near, double Far) span)
        {
            var centre = pillar.GetLocationPoint();
            return centre != null
                ? centre.Subtract(origin).DotProduct(outward).FeetToMm()
                : ((span.Near + span.Far) / 2).FeetToMm();
        }

        /// <summary>Angle between two directions, folded into 0-90 degrees.</summary>
        private static double AngleBetween(XYZ first, XYZ second)
        {
            var angle = first.AngleTo(second).RadiansToDegrees();
            return angle > 90 ? 180 - angle : angle;
        }

        /// <summary>Distance from a point to a line, measured on the horizontal plane.</summary>
        private static double DistanceFromAxis(Line axis, XYZ point)
        {
            var origin = axis.GetEndPoint(0).ToXY();
            var direction = axis.Direction.ToXY();

            if (direction.IsZeroLength())
            {
                return 0;
            }

            direction = direction.Normalize();
            var offset = point.ToXY() - origin;
            return (offset - direction * offset.DotProduct(direction)).GetLength();
        }

        private static string Describe(Element element)
        {
            var type = element.Document.GetElement(element.GetTypeId())?.Name;
            return $"{element.Category?.Name} \"{type}\" (id {element.Id.ToLong()})";
        }
    }
}
