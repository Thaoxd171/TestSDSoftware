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

        /// <summary>Two beams on the same line count as continuing each other below this angle and offset.</summary>
        private const double InlineAngleDegrees = 5;

        private const double InlineOffsetMm = 50;

        /// <summary>
        /// Two beam ends count as meeting head on when their axes are no further apart than this and
        /// they run out at each other. Precast beams landing on opposite sides of the same column are
        /// rarely on one line - a third of a turn between them is normal - but they still share the
        /// column and still have to part evenly over its centre, so the test is how squarely the two
        /// ends face each other rather than how nearly parallel the beams are.
        /// </summary>
        private const double FacingAxisDegrees = 45;

        private const double FacingApartDegrees = 135;

        /// <summary>How near the other beam's end has to be before the two count as meeting at all.</summary>
        private const double FacingReachMm = 1000;

        /// <summary>A face counts as upright below this much tilt in its normal.</summary>
        private const double UprightTolerance = 0.01;

        /// <summary>Two normals count as the same axis this close to one.</summary>
        private const double ParallelTolerance = 0.02;

        /// <summary>
        /// How squarely a face has to look back at the beam to count as one it runs into. Half is
        /// sixty degrees off square: past that the face lies more alongside the beam than across it,
        /// and the beam slides along it rather than meeting it. Squaring an end up to such a face
        /// would take a wedge off it longer than the beam is wide.
        /// </summary>
        private const double FacingTolerance = 0.5;

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
                .Select(neighbour => Measure(beam, neighbour, end, origin, outward, ray))
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
            // beams share and its outer face is as far as the beam may hang out. So does the beam this
            // end is parting from - two ends that have run through each other are the very thing most
            // in need of setting right, and dropping the partner for being behind leaves the end with
            // nothing in front of it and no reason to come back. A wall, or a beam merely crossing this
            // one, is done with as soon as the end is clear of it.
            if (candidate.Kind != SupportKind.Pillar
                && candidate.Kind != SupportKind.InlineBeam
                && candidate.FarMm <= 0)
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

        private SupportCandidate Measure(
            BeamGeometry beam,
            Element neighbour,
            int end,
            XYZ origin,
            XYZ outward,
            Line ray)
        {
            var kind = Classify(beam, neighbour, end, origin, outward);
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

            var candidate = new SupportCandidate
            {
                Id = neighbour.Id.ToLong(),
                Kind = kind,
                Description = Describe(neighbour),
                TopAboveBeamMm = height.Top,
                BottomAboveBeamMm = height.Bottom,
            };

            if (isColumn)
            {
                return MeasurePillar(candidate, neighbour, solids, origin, outward);
            }

            var swept = Swept(solids, beam, neighbour, kind, origin, outward);
            candidate.ClearMm = swept.Count == 0 ? (double?)null : swept.Min().FeetToMm();

            var entry = EntryNormal(neighbour, origin, outward);
            candidate.SkewDegrees = entry == null ? 0 : AngleBetween(outward, entry);

            // The ray is one line down the middle of the beam, and a support the end merely slips past
            // the corner of is not on it. Worse, an end running out square to its neighbour travels
            // parallel to that neighbour's end face and never crosses it at all, however close it
            // passes. Where the ray finds nothing, the face the end is arriving at says where the
            // support starts and the width the beam sweeps says where it ends.
            //
            // The face, not the nearest speck of material: the two part company on a face that stops
            // inside the beam's width, and telling those apart is the whole point of having both.
            var span = SpanAlongRay(solids, ray, origin, outward);
            if (span == null && swept.Count > 0 && entry != null)
            {
                var denominator = outward.DotProduct(entry);
                if (Math.Abs(denominator) > GeometryExtensions.Tolerance)
                {
                    span = (Facing(solids, entry, origin, outward), swept.Max());
                }
            }

            if (span == null)
            {
                candidate.RejectionReason = "nothing of it stands in the width the beam sweeps";
                return candidate;
            }

            candidate.NearMm = span.Value.Item1.FeetToMm();
            candidate.FarMm = span.Value.Item2.FeetToMm();

            return candidate;
        }

        /// <summary>Where the axis crosses the plane of the face the end is arriving at.</summary>
        private static double Facing(IList<Solid> solids, XYZ entry, XYZ origin, XYZ outward)
        {
            var plane = solids
                .SelectMany(solid => solid.Faces.Cast<Face>())
                .OfType<PlanarFace>()
                .Where(face => Math.Abs(face.FaceNormal.DotProduct(entry) + 1) < ParallelTolerance)
                .Select(face => Crossing(face, origin, outward))
                .Where(crossing => crossing.HasValue)
                .Select(crossing => crossing.Value)
                .ToList();

            return plane.Count == 0 ? 0 : plane.OrderBy(Math.Abs).First();
        }

        /// <summary>
        /// Where the support's material sits along the beam axis, counting only what stands inside the
        /// width and the height the beam sweeps. The face crossing says where the plane of a face is;
        /// this says where the beam would actually touch.
        /// </summary>
        private static IList<double> Swept(
            IList<Solid> solids,
            BeamGeometry beam,
            Element neighbour,
            SupportKind kind,
            XYZ origin,
            XYZ outward)
        {
            var across = XYZ.BasisZ.CrossProduct(outward);
            if (across.IsZeroLength())
            {
                return new List<double>();
            }

            across = across.Normalize();
            var half = (beam.WidthMm / 2).MmToFeet();

            // The widened foot of another beam is not something to stand clear of - it is cut back to
            // let this beam through - so the clearance is taken from the web behind it. Measure against
            // the foot instead and the two beams chase each other: back away from it and the cut made
            // for it shrinks, putting it back in the way.
            var section = kind == SupportKind.CrossingBeam || kind == SupportKind.InlineBeam
                ? BeamSection.Read(neighbour)
                : null;

            return solids
                .PointsInSlab(point => (point - origin).DotProduct(across), -half, half)
                .Where(point => point.Z >= beam.BottomZ && point.Z <= beam.TopZ)
                .Where(point => section == null || !section.IsBeyondWeb(point))
                .Select(point => (point - origin).DotProduct(outward))
                .ToList();
        }

        /// <summary>What kind of support this element is for that beam, or None when it is not one.</summary>
        private static SupportKind Classify(
            BeamGeometry beam,
            Element neighbour,
            int end,
            XYZ origin,
            XYZ outward)
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
            var offset = DistanceFromAxis(beam.AxisStart, beam.Direction, otherAxis.GetEndPoint(0));

            if (angle <= InlineAngleDegrees && offset.FeetToMm() <= InlineOffsetMm)
            {
                return SupportKind.InlineBeam;
            }

            return FaceToFace(beam, end, neighbour, otherAxis, origin, outward)
                ? SupportKind.InlineBeam
                : SupportKind.CrossingBeam;
        }

        /// <summary>
        /// Whether this end and the near end of the other beam are running out at each other. Two ends
        /// that face each other are parting from a shared point, however far the beams are from being
        /// on one line; an end that meets the flank of another beam is not, however nearly parallel
        /// the two happen to run.
        /// </summary>
        private static bool FaceToFace(
            BeamGeometry beam,
            int end,
            Element neighbour,
            Line otherAxis,
            XYZ origin,
            XYZ outward)
        {
            if (AngleBetween(beam.Direction, otherAxis.Direction) > FacingAxisDegrees)
            {
                return false;
            }

            // The face this end arrives at has to be the other beam's end, not its flank. A beam
            // running up against the side of another is not parting from it however squarely the two
            // face each other - the one it is leaning on carries straight past, and there is no shared
            // point between them to divide.
            var entry = EntryNormal(UprightFaces(neighbour.GetSolids()), origin, outward);
            if (entry == null || AngleBetween(entry, otherAxis.Direction) > FacingAxisDegrees)
            {
                return false;
            }

            var here = beam.PointAt(end);
            var mine = beam.OutwardAt(end);

            var nearest = Math.Min(
                here.DistanceTo(otherAxis.GetEndPoint(0)),
                here.DistanceTo(otherAxis.GetEndPoint(1)));

            if (nearest.FeetToMm() > FacingReachMm)
            {
                return false;
            }

            var theirs = here.DistanceTo(otherAxis.GetEndPoint(0)) <= here.DistanceTo(otherAxis.GetEndPoint(1))
                ? -otherAxis.Direction
                : otherAxis.Direction;

            return mine.AngleTo(theirs).RadiansToDegrees() >= FacingApartDegrees;
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
        /// Measures a pillar off its own faces rather than off the ray. Two reasons: the pillar stands
        /// under the beam, so no ray along the axis would ever reach it; and when the beam arrives at
        /// an angle, the outline of the pillar seen along the axis is wider than the pillar itself, so
        /// the clearance has to be taken from the face plane the beam runs into, not from the outline.
        /// Distances stay measured along the beam axis - they say where the axis crosses each plane.
        /// </summary>
        private static SupportCandidate MeasurePillar(
            SupportCandidate candidate,
            Element pillar,
            IList<Solid> solids,
            XYZ origin,
            XYZ outward)
        {
            if (SpanInPlan(solids, origin, outward) == null)
            {
                candidate.RejectionReason = "the beam axis passes beside it in plan";
                return candidate;
            }

            var faces = UprightFaces(solids);
            if (faces.Count == 0)
            {
                candidate.RejectionReason = "the pillar has no upright faces to measure from";
                return candidate;
            }

            // The face the beam runs into, already turned to point the way the beam is travelling.
            var normal = EntryNormal(faces, origin, outward);
            if (normal == null)
            {
                candidate.RejectionReason = "no face of the pillar looks back at this end";
                return candidate;
            }

            var crossings = faces
                .Where(face => Math.Abs(Math.Abs(face.FaceNormal.DotProduct(normal)) - 1) < ParallelTolerance)
                .Select(face => Crossing(face, origin, outward))
                .Where(distance => distance.HasValue)
                .Select(distance => distance.Value)
                .ToList();

            if (crossings.Count == 0)
            {
                candidate.RejectionReason = "the beam axis does not cross the pillar faces";
                return candidate;
            }

            candidate.NearMm = crossings.Min().FeetToMm();
            candidate.FarMm = crossings.Max().FeetToMm();
            candidate.SkewDegrees = AngleBetween(outward, normal);

            var centre = pillar.GetLocationPoint();
            candidate.CentreAlongMm = centre == null
                ? (candidate.NearMm + candidate.FarMm) / 2
                : ((centre - origin).DotProduct(normal) / outward.DotProduct(normal)).FeetToMm();

            return candidate;
        }

        /// <summary>
        /// The upright face of a support that the beam runs into, returned as a normal pointing the
        /// way the beam travels. This is the plane a skewed end has to be cut parallel to, so the
        /// cutter asks for it again when it builds the opening.
        /// Null when the support has no upright face facing the beam.
        /// </summary>
        public static XYZ EntryNormal(Element support, XYZ origin, XYZ outward)
        {
            return EntryNormal(UprightFaces(support.GetSolids()), origin, outward);
        }

        /// <summary>
        /// The face a beam running this way meets most squarely, as a normal pointing along the beam.
        ///
        /// The nearest one wins. Squareness is no guide: a beam running past the flank of another meets
        /// its long side first and its end face second, and the end face - being the squarer of the two
        /// - would be chosen although the beam never comes near it.
        ///
        /// Nearness alone is enough. A chamfer on the corner of a column is cut back off the corner, so
        /// it always crosses the axis further out than the face it was cut from, and it loses without
        /// having to be sifted out by size. Sifting by size is worse than useless here: the flank of a
        /// beam dwarfs its own end face, and dropping small faces drops the very one the beam is
        /// arriving at.
        /// </summary>
        private static XYZ EntryNormal(IList<PlanarFace> faces, XYZ origin, XYZ outward)
        {
            var entry = faces
                .Where(face => face.FaceNormal.DotProduct(outward) < -FacingTolerance)
                .Select(face => new { face, crossing = Crossing(face, origin, outward) })
                .Where(item => item.crossing.HasValue)
                .OrderBy(item => Math.Abs(item.crossing.Value))
                .FirstOrDefault();

            return entry == null ? null : -entry.face.FaceNormal;
        }

        private static IList<PlanarFace> UprightFaces(IEnumerable<Solid> solids)
        {
            return solids
                .SelectMany(solid => solid.Faces.Cast<Face>())
                .OfType<PlanarFace>()
                .Where(face => Math.Abs(face.FaceNormal.Z) < UprightTolerance)
                .ToList();
        }

        /// <summary>How far along the axis, from the beam end, it crosses the plane of a face.</summary>
        private static double? Crossing(PlanarFace face, XYZ origin, XYZ outward)
        {
            var denominator = outward.DotProduct(face.FaceNormal);
            return Math.Abs(denominator) < GeometryExtensions.Tolerance
                ? (double?)null
                : (face.Origin - origin).DotProduct(face.FaceNormal) / denominator;
        }

        /// <summary>
        /// Where the axis enters and leaves the solids seen from above, ignoring height. Used to tell
        /// whether the beam passes over the pillar at all.
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

        /// <summary>Angle between two directions, folded into 0-90 degrees.</summary>
        private static double AngleBetween(XYZ first, XYZ second)
        {
            var angle = first.AngleTo(second).RadiansToDegrees();
            return angle > 90 ? 180 - angle : angle;
        }

        /// <summary>Distance from a point to a line, measured on the horizontal plane.</summary>
        private static double DistanceFromAxis(XYZ axisStart, XYZ axisDirection, XYZ point)
        {
            var origin = axisStart.ToXY();
            var direction = axisDirection.ToXY();

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
