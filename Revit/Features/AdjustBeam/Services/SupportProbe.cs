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

        /// <summary>How squarely a face has to look back at the beam to count as the one it meets.</summary>
        private const double FacingTolerance = 0.05;

        /// <summary>How much of the largest facing face a face must have before it counts as one.</summary>
        private const double MinorFaceFraction = 0.2;

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

        private SupportCandidate Measure(
            BeamGeometry beam,
            Element neighbour,
            int end,
            XYZ origin,
            XYZ outward,
            Line ray)
        {
            var kind = Classify(beam, neighbour, end);
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

            var span = SpanAlongRay(solids, ray, origin, outward);
            if (span == null)
            {
                candidate.RejectionReason = "the beam axis does not run into it";
                return candidate;
            }

            candidate.NearMm = span.Value.Near.FeetToMm();
            candidate.FarMm = span.Value.Far.FeetToMm();
            candidate.ClearMm = Clearance(solids, beam, neighbour, kind, origin, outward)?.FeetToMm();

            var entry = EntryNormal(neighbour, origin, outward);
            candidate.SkewDegrees = entry == null ? 0 : AngleBetween(outward, entry);

            return candidate;
        }

        /// <summary>
        /// The closest the support's material comes to this end, measured along the beam axis and
        /// counting only what stands inside the width and the height the beam sweeps. The face
        /// crossing says where the plane of a face is; this says where the beam would actually touch.
        /// </summary>
        private static double? Clearance(
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
                return null;
            }

            across = across.Normalize();
            var half = (beam.WidthMm / 2).MmToFeet();

            // The bearing block on the end of another beam is not something to stand clear of - it is
            // cut back to let this beam through - so the clearance is taken from the web behind it.
            // Measure against the block instead and the two beams chase each other: back away from the
            // block and the cut made for it shrinks, putting the block back in the way.
            var section = kind == SupportKind.CrossingBeam || kind == SupportKind.InlineBeam
                ? BeamSection.Read(neighbour)
                : null;

            var distances = solids
                .PointsInSlab(point => (point - origin).DotProduct(across), -half, half)
                .Where(point => point.Z >= beam.BottomZ && point.Z <= beam.TopZ)
                .Where(point => section == null || !section.IsBeyondWeb(point))
                .Select(point => (point - origin).DotProduct(outward))
                .ToList();

            return distances.Count == 0 ? (double?)null : distances.Min();
        }

        /// <summary>What kind of support this element is for that beam, or None when it is not one.</summary>
        private static SupportKind Classify(BeamGeometry beam, Element neighbour, int end)
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

            return FaceToFace(beam, end, otherAxis) ? SupportKind.InlineBeam : SupportKind.CrossingBeam;
        }

        /// <summary>
        /// Whether this end and the near end of the other beam are running out at each other. Two ends
        /// that face each other are parting from a shared point, however far the beams are from being
        /// on one line; an end that meets the flank of another beam is not, however nearly parallel
        /// the two happen to run.
        /// </summary>
        private static bool FaceToFace(BeamGeometry beam, int end, Line otherAxis)
        {
            if (AngleBetween(beam.Direction, otherAxis.Direction) > FacingAxisDegrees)
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
            var normal = EntryNormal(faces, outward);
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
            return EntryNormal(UprightFaces(support.GetSolids()), outward);
        }

        /// <summary>
        /// The face a beam running this way meets most squarely, as a normal pointing along the beam.
        ///
        /// Slivers are dropped before choosing. A precast column is chamfered at every corner, and a
        /// 10 mm chamfer stands more squarely across a beam arriving at an angle than the face the beam
        /// is really landing on - so going by squareness alone would line the end up with the corner.
        /// </summary>
        private static XYZ EntryNormal(IList<PlanarFace> faces, XYZ outward)
        {
            var facing = faces
                .Where(face => face.FaceNormal.DotProduct(outward) < -FacingTolerance)
                .ToList();

            if (facing.Count == 0)
            {
                return null;
            }

            var biggest = facing.Max(face => face.Area);

            var entry = facing
                .Where(face => face.Area >= biggest * MinorFaceFraction)
                .OrderBy(face => face.FaceNormal.DotProduct(outward))
                .First();

            return -entry.FaceNormal;
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
