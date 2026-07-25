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

        /// <summary>How far behind the end a support has to stop before the beam is done with it.</summary>
        private const double BehindToleranceMm = 1;

        /// <summary>
        /// How far a wall's top may miss the beam's soffit and still count as carrying it. A bearing is
        /// meant to be flush, so this is only there to keep a wall built exactly to the underside from
        /// being called an obstruction by a rounding error.
        /// </summary>
        private const double BearingToleranceMm = 5;

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

        /// <summary>
        /// How squarely a wall has to look back at the beam before its face is taken as the one the end
        /// arrives at, when no face passes the ordinary test. Three tenths is seventy-two degrees off
        /// square.
        ///
        /// It is there for a beam running into the corner where a wall ends: the wall's flank is then
        /// the second face the end has to clear, and at 1855188 it stands 65 degrees off square, past
        /// the ordinary limit. Left out, the wall reports no face at all and the end is squared off on
        /// one plane where the model wants two. The gap either side of three tenths is wide - the next
        /// thing down is a wall running alongside the beam at 87 degrees, which must stay out - so the
        /// figure is not finely balanced.
        /// </summary>
        private const double GlancingTolerance = 0.3;

        /// <summary>
        /// How much of the beam's width a face has to stand across before it counts as one the beam
        /// runs into. Precast walls carry rows of small recesses up their height, and one of those
        /// always lands at the depth a beam runs at. Its faces cross the beam axis nearer than the
        /// wall itself does, so left in they decide both the clearance and the angle the end is cut
        /// at. They are not what the beam arrives at: the beam arrives at the wall.
        ///
        /// A fifth is where the two kinds separate. The recess faces measure 14 per cent of the beam
        /// width at their widest; the narrowest face a beam has actually been found to meet is the
        /// end of a 180 mm wall met at an angle, at 25 per cent.
        /// </summary>
        private const double MinimumEntryShare = 0.20;

        /// <summary>How far a face has to reach into the width the beam sweeps to be in front of it.</summary>
        private const double InsideToleranceMm = 1;

        /// <summary>How far above a bearing block a face has to reach to count as standing over it.</summary>
        private const double BlockToleranceMm = 1;

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
            //
            // Clear of it, not resting against it. An end standing exactly on a wall's far face reads
            // as far = 0, and dropping that one leaves the end to be pushed out by whatever lies beyond
            // - 2975394 was driven 60 mm past where it belonged by the wall behind the one it was
            // already touching. A support has to be a whole millimetre behind before the beam has
            // really finished with it.
            if (candidate.Kind != SupportKind.Pillar
                && candidate.Kind != SupportKind.InlineBeam
                && candidate.FarMm < -BehindToleranceMm)
            {
                return $"ends {-candidate.FarMm:0.#} mm behind this end, the beam is already clear of it";
            }

            if (candidate.FarMm < -BackReachMm)
            {
                return $"ends {-candidate.FarMm:0.#} mm behind this end, past the {BackReachMm:0} mm reach";
            }

            return null;
        }

        /// <summary>
        /// Whether the beam runs over this wall rather than up against it. A precast beam lands on a
        /// wall built up to its bearing level and carries straight on over it; a wall carried up past
        /// the beam's web is something else entirely, an obstruction the end has to stop short of.
        /// The two are told apart by how high the wall reaches, because nothing else tells them apart:
        /// at this joint a wall the beam runs 168 mm into and a wall it is held 20 mm clear of give
        /// the same reading for where their faces are and where their material starts, and differ only
        /// in that one stops at the top of the bearing block and the other carries on to the top of
        /// the beam.
        ///
        /// The underside of the beam is the line, and it is the only one that can be defended: a beam
        /// rests on what it rests on, and anything standing higher than its soffit is in its way rather
        /// than under it. A precast section carries a bearing block from the soffit up - 300 mm of it
        /// on these beams - so a wall stopping part way up that block is not carrying anything, it is
        /// fouling the block.
        ///
        /// This was first drawn at half the depth, off three walls reading 470, 416 and 60 mm below the
        /// beam top. All three stood at one end - 1856700 END 1 - and the line was really only fitted
        /// to what the reference model did there, which was to run the beam over the two low ones. That
        /// model has since been changed to cut the beam against them instead, and nothing else measured
        /// anywhere asks for a wall to be passed over for being low.
        /// </summary>
        private static bool SitsOver(SupportCandidate wall, BeamGeometry beam)
        {
            var depth = (beam.TopZ - beam.BottomZ).FeetToMm();

            return wall.TopInTheWayMm.HasValue
                   && wall.TopInTheWayMm.Value < -depth + BearingToleranceMm;
        }

        /// <summary>Top and bottom of an element, measured up from the top of the beam.</summary>
        private static (double Top, double Bottom) HeightAboveBeam(Element neighbour, BeamGeometry beam)
        {
            var box = neighbour.get_BoundingBox(null);
            return box == null
                ? (0, 0)
                : ((box.Max.Z - beam.TopZ).FeetToMm(), (box.Min.Z - beam.TopZ).FeetToMm());
        }

        /// <summary>
        /// How high the support stands where it is actually in front of this end, measured up from the
        /// top of the beam. Null when none of it is.
        ///
        /// A wall is one element from end to end but is not one height: 1662890 is built to the top of
        /// the storey along most of its length and stops at the beam's soffit for the last half metre,
        /// which is a bearing nib with the beam running over it. Read off the whole wall's bounding box
        /// that reads as full height, and the beam is stopped dead at a wall it is meant to land on.
        /// </summary>
        private static double? TopInTheWay(IList<Solid> solids, BeamGeometry beam, XYZ origin, XYZ outward)
        {
            var across = XYZ.BasisZ.CrossProduct(outward);
            if (across.IsZeroLength())
            {
                return null;
            }

            var half = (beam.WidthMm / 2).MmToFeet();
            var reach = ForwardReachMm.MmToFeet();
            var back = BackReachMm.MmToFeet();

            var tops = solids
                .PointsInSlab(point => (point - origin).DotProduct(across.Normalize()), -half, half)
                .Where(point =>
                {
                    var along = (point - origin).DotProduct(outward);
                    return along >= -back && along <= reach;
                })
                .Select(point => point.Z)
                .ToList();

            return tops.Count == 0 ? (double?)null : (tops.Max() - beam.TopZ).FeetToMm();
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
                ThicknessMm = neighbour is Wall wall ? wall.Width.FeetToMm() : 0,
                Collinear = kind == SupportKind.InlineBeam && Collinear(beam, neighbour),
            };

            if (isColumn)
            {
                return MeasurePillar(candidate, neighbour, solids, origin, outward, beam.WidthMm);
            }

            candidate.TopInTheWayMm = TopInTheWay(solids, beam, origin, outward);

            if (kind == SupportKind.Wall && SitsOver(candidate, beam))
            {
                var depth = (beam.TopZ - beam.BottomZ).FeetToMm();
                candidate.RejectionReason =
                    $"where it stands in front of this end it stops {-candidate.TopInTheWayMm:0.#} mm " +
                    $"below the top of the beam, at or below the soffit {depth:0.#} down, so the beam " +
                    "lands on it and runs over it rather than up against it";
                return candidate;
            }

            var swept = Swept(solids, beam, neighbour, kind, origin, outward);
            candidate.ClearMm = swept.Count == 0 ? (double?)null : swept.Min().FeetToMm();

            var upright = AboveTheBlock(
                UprightFaces(neighbour.GetSolids(), beam.BottomZ, beam.TopZ), neighbour, kind);

            var entryFace = EntryFace(upright, origin, outward, beam.WidthMm);

            // A wall the beam meets at the corner where it ends shows the end nothing but its flank,
            // and a flank stands too far off square to pass the ordinary test. Rather than let such a
            // wall report no face at all, look again at a shallower angle - but only for a wall, and
            // only when nothing squarer was found, so no end that already has a face changes its mind.
            if (entryFace == null && kind == SupportKind.Wall)
            {
                entryFace = EntryFace(upright, origin, outward, beam.WidthMm, GlancingTolerance);
            }

            var entry = entryFace == null ? null : -entryFace.FaceNormal;
            candidate.SkewDegrees = entry == null ? 0 : AngleBetween(outward, entry);
            candidate.EntryAcross = entry == null ? 0 : AcrossShare(entry, outward);
            candidate.InsideMm = InsideBand(entryFace, origin, outward, beam.WidthMm);
            candidate.EntryFaceMm = entryFace == null
                ? (double?)null
                : Crossing(entryFace, origin, outward)?.FeetToMm();

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

            if (Collinear(beam, otherAxis))
            {
                return SupportKind.InlineBeam;
            }

            return FaceToFace(beam, end, neighbour, otherAxis, origin, outward)
                ? SupportKind.InlineBeam
                : SupportKind.CrossingBeam;
        }

        /// <summary>
        /// Whether the other beam runs on the same line as this one, meeting it end to end rather than
        /// crossing it. Nearly the same direction and barely any sideways offset - the two are one beam
        /// broken over the column between them.
        /// </summary>
        private static bool Collinear(BeamGeometry beam, Element neighbour)
        {
            var otherAxis = neighbour.GetLocationLine();
            return otherAxis != null && Collinear(beam, otherAxis);
        }

        private static bool Collinear(BeamGeometry beam, Line otherAxis)
        {
            var angle = AngleBetween(beam.Direction, otherAxis.Direction);
            var offset = DistanceFromAxis(beam.AxisStart, beam.Direction, otherAxis.GetEndPoint(0));
            return angle <= InlineAngleDegrees && offset.FeetToMm() <= InlineOffsetMm;
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
            var entry = EntryNormal(
                AboveTheBlock(
                    UprightFaces(neighbour.GetSolids(), beam.BottomZ, beam.TopZ),
                    neighbour,
                    SupportKind.CrossingBeam),
                origin,
                outward,
                beam.WidthMm);
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
            XYZ outward,
            double widthMm)
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
            var entryFace = EntryFace(faces, origin, outward, widthMm);
            var normal = entryFace == null ? null : -entryFace.FaceNormal;
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
            candidate.EntryAcross = AcrossShare(normal, outward);

            var centre = pillar.GetLocationPoint();
            candidate.CentreAlongMm = centre == null
                ? (candidate.NearMm + candidate.FarMm) / 2
                : ((centre - origin).DotProduct(normal) / outward.DotProduct(normal)).FeetToMm();

            return candidate;
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
        private static XYZ EntryNormal(IList<PlanarFace> faces, XYZ origin, XYZ outward, double widthMm)
        {
            var entry = EntryFace(faces, origin, outward, widthMm);
            return entry == null ? null : -entry.FaceNormal;
        }

        private static PlanarFace EntryFace(
            IList<PlanarFace> faces,
            XYZ origin,
            XYZ outward,
            double widthMm,
            double facing = FacingTolerance)
        {
            var across = XYZ.BasisZ.CrossProduct(outward);
            var half = (widthMm / 2).MmToFeet();
            var least = (widthMm * MinimumEntryShare).MmToFeet();
            var touching = InsideToleranceMm.MmToFeet();

            return faces
                .Where(face => face.FaceNormal.DotProduct(outward) < -facing)
                .Where(face => Wide(face, origin, across, half, least, touching))
                .Select(face => new { face, crossing = Crossing(face, origin, outward) })
                .Where(item => item.crossing.HasValue)
                .OrderBy(item => Math.Abs(item.crossing.Value))
                .Select(item => item.face)
                .FirstOrDefault();
        }

        /// <summary>
        /// How wide a face is across the beam, and how much of that width falls inside the band the
        /// beam sweeps. The two answer different questions and both are needed: the width tells a real
        /// face from a sliver, and it has to be the face's own width, because a beam clipping the
        /// corner of its neighbour still meets a full sized face. What falls inside the band tells
        /// whether the face is in front of the beam at all, rather than off to one side of it.
        /// </summary>
        /// <summary>Whether a face is broad enough, and near enough sideways, to be the one met.</summary>
        private static bool Wide(
            PlanarFace face,
            XYZ origin,
            XYZ across,
            double half,
            double least,
            double touching)
        {
            if (across.IsZeroLength())
            {
                return true;
            }

            var measure = AcrossBeam(face, origin, across.Normalize(), half);
            return measure.Wide >= least && measure.Inside > touching;
        }

        private static (double Wide, double Inside) AcrossBeam(
            PlanarFace face,
            XYZ origin,
            XYZ across,
            double half)
        {
            var least = double.MaxValue;
            var most = double.MinValue;

            foreach (var point in face.BoundaryPoints())
            {
                var value = (point - origin).DotProduct(across);
                least = Math.Min(least, value);
                most = Math.Max(most, value);
            }

            return least > most
                ? (0, 0)
                : (most - least, Math.Max(0, Math.Min(most, half) - Math.Max(least, -half)));
        }

        /// <summary>
        /// The faces of a neighbouring beam that stand above its bearing block.
        ///
        /// The block is the widened foot a precast beam lands on, and it is not what an end arrives at:
        /// it is cut back to let the other beam through, which is why the material of it was left out
        /// of the swept measurement in the first place. Its faces were not, and they are the nearest
        /// thing in front of an end often enough to win.
        ///
        /// Two of them decided 1856700. At END 0 the face chosen was 130 mm wide and 300 tall, sitting
        /// wholly below the web - the end of the neighbour's block, where its real end face is 372 wide
        /// and the full depth - and the parting the two beams share came out 261 mm adrift, because the
        /// column's centre had been projected onto the wrong plane. At END 1 it was the ten millimetre
        /// step between block and web, which runs the whole length of a beam and so is far too wide to
        /// be dropped for being a sliver.
        ///
        /// Only a beam is treated this way. A corbel pillar stands entirely below the beam by design,
        /// and the wall at 1856700 END 1 stops at the top of the block; judged by this they would both
        /// vanish.
        /// </summary>
        private static IList<PlanarFace> AboveTheBlock(
            IList<PlanarFace> faces,
            Element neighbour,
            SupportKind kind)
        {
            if (kind != SupportKind.CrossingBeam && kind != SupportKind.InlineBeam)
            {
                return faces;
            }

            var block = BeamSection.Read(neighbour)?.BlockTopZ;
            if (block == null)
            {
                return faces;
            }

            var line = block.Value + BlockToleranceMm.MmToFeet();

            var kept = faces
                .Where(face =>
                {
                    var range = face.ZRange();
                    return range == null || range.Value.Top > line;
                })
                .ToList();

            // A beam that is all block and no web has nothing else to offer, and no face at all is
            // worse than a low one.
            return kept.Count == 0 ? faces : kept;
        }

        private static IList<PlanarFace> UprightFaces(IEnumerable<Solid> solids)
        {
            return solids
                .SelectMany(solid => solid.Faces.Cast<Face>())
                .OfType<PlanarFace>()
                .Where(face => Math.Abs(face.FaceNormal.Z) < UprightTolerance)
                .ToList();
        }

        /// <summary>
        /// The upright faces standing at the height the beam occupies. A face lying wholly above or
        /// below the beam is one the beam can never touch, so it cannot be the face the beam arrives
        /// at - and its plane, extended, would otherwise be free to cross the axis nearer than the
        /// real face and win. Small chamfer strips along the foot of a wall do exactly that.
        /// </summary>
        private static IList<PlanarFace> UprightFaces(IEnumerable<Solid> solids, double bottomZ, double topZ)
        {
            return UprightFaces(solids)
                .Where(face =>
                {
                    var range = face.ZRange();
                    return range == null || (range.Value.Top >= bottomZ && range.Value.Bottom <= topZ);
                })
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

        /// <summary>How much of the beam's width a face stands across, in millimetres.</summary>
        private static double InsideBand(PlanarFace face, XYZ origin, XYZ outward, double widthMm)
        {
            var across = XYZ.BasisZ.CrossProduct(outward);
            if (face == null || across.IsZeroLength())
            {
                return 0;
            }

            return AcrossBeam(face, origin, across.Normalize(), (widthMm / 2).MmToFeet()).Inside.FeetToMm();
        }

        /// <summary>
        /// How much of a direction lies across the beam rather than along it, signed. Zero for a face
        /// met square; the sign says which side of the axis reaches it first.
        /// </summary>
        private static double AcrossShare(XYZ normal, XYZ outward)
        {
            var across = XYZ.BasisZ.CrossProduct(outward);
            return across.IsZeroLength() ? 0 : normal.DotProduct(across.Normalize());
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
