using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Extensions;
using SDSoftware.RevitTest.Features.AdjustBeam.Models;

namespace SDSoftware.RevitTest.Features.AdjustBeam.Services
{
    /// <summary>
    /// Squares off a beam end against a support it meets at an angle.
    ///
    /// A beam is cut across its own axis, so an end that arrives skewed comes out at an angle to the
    /// face it is supposed to sit against - one corner short, the other proud. The axis is therefore
    /// run out until the whole end clears the face plane, and a structural opening takes the wedge
    /// back off, leaving an end face parallel to the support.
    ///
    /// Must be called inside a transaction, after the location line has been moved.
    /// </summary>
    public static class BeamEndCutter
    {
        /// <summary>How far past the wedge the opening reaches, so the cut comes out clean.</summary>
        private const double AlongMarginMm = 20;

        /// <summary>How far past each side of the beam the opening reaches.</summary>
        private const double SideMarginMm = 50;

        /// <summary>How far from the end an opening has to be to count as belonging to it.</summary>
        private const double BelongsToEndMm = 1500;

        /// <summary>
        /// Takes away the openings already sitting at one end. This has to happen before the axis is
        /// moved, not after: an opening is sketched against its host, and while one is there Revit
        /// quietly refuses to move the beam under it.
        /// </summary>
        public static int Clear(Document document, BeamGeometry beam, int end)
        {
            var openings = OpeningsAtEnd(document, beam, end).ToList();
            foreach (var opening in openings)
            {
                document.Delete(opening.Id);
            }

            return openings.Count;
        }

        /// <summary>
        /// Trims one end back to one plane, returning the opening it made.
        /// </summary>
        /// <param name="refused">
        /// Null when the opening was made, otherwise why it was not: a cut the solver asked for and the
        /// cutter dropped has to be said out loud, because the end then keeps the wedge the whole run
        /// was for and nothing else in the log would show it.
        /// </param>
        public static Opening Cut(
            Document document,
            BeamGeometry beam,
            BeamEndPlan plan,
            BeamEndCut cut,
            out string refused)
        {
            var outward = beam.OutwardAt(plan.End);
            var across = XYZ.BasisZ.CrossProduct(outward);

            if (across.IsZeroLength())
            {
                refused = "the beam stands upright, so there is no width to cut across";
                return null;
            }

            var profile = Profile(beam, plan, cut, outward, across.Normalize());
            if (profile == null)
            {
                refused = $"the plane at {cut.PlaneMm:0.#} mm takes nothing off this end";
                return null;
            }

            refused = null;
            return document.Create.NewOpening(beam.Beam, profile, Autodesk.Revit.Creation.eRefFace.CenterZ);
        }

        /// <summary>
        /// What to take off the end: the part of it lying past the cut plane.
        ///
        /// Laid out on the beam's own axes rather than on the plane's, which is what lets two planes
        /// share one end. A rectangle covering the whole end, margins and all, is clipped by the plane
        /// and whatever is left over is the piece to remove - a wedge where one plane does the work, a
        /// three-cornered sliver where a second one has already taken the rest.
        ///
        /// Sizing it by clipping rather than by the wedge also keeps a glancing plane in bounds: at 65
        /// degrees off square the wedge is over a metre, and a rectangle that long would reach back
        /// into the middle of the beam and cut away a good deal that was never in question.
        ///
        /// Sketched half way down the beam because that is the reference face the opening is cut from.
        /// </summary>
        private static CurveArray Profile(
            BeamGeometry beam,
            BeamEndPlan plan,
            BeamEndCut cut,
            XYZ outward,
            XYZ across)
        {
            // The beam's own reach either side of its axis, not half its width each way: a precast
            // section need not be centred on its line, and laying the opening out symmetrically leaves
            // the far edge of a bearing block standing where the end was meant to be cut clean off.
            var span = beam.AcrossAt(plan.End);
            var least = span.Least - SideMarginMm;
            var most = span.Most + SideMarginMm;

            // Where the plane crosses each side of that band. Far enough back that it has swept the
            // whole width before the rectangle runs out, and far enough forward to clear the end of
            // the beam wherever the axis was left.
            var slope = cut.AcrossNormal / cut.AlongNormal;
            var atLeast = cut.PlaneMm - slope * least;
            var atMost = cut.PlaneMm - slope * most;

            var back = Math.Min(atLeast, atMost) - AlongMarginMm;
            var front = Math.Max(plan.MoveMm, Math.Max(atLeast, atMost)) + AlongMarginMm;

            var corners = new List<XYZ>
            {
                Local(beam, plan, outward, across, back, least),
                Local(beam, plan, outward, across, front, least),
                Local(beam, plan, outward, across, front, most),
                Local(beam, plan, outward, across, back, most),
            };

            var removed = Beyond(corners, beam, plan, cut, outward, across);
            if (removed.Count < 3)
            {
                return null;
            }

            var profile = new CurveArray();
            for (var index = 0; index < removed.Count; index++)
            {
                var from = removed[index];
                var to = removed[(index + 1) % removed.Count];

                if (from.DistanceTo(to) > GeometryExtensions.Tolerance)
                {
                    profile.Append(Line.CreateBound(from, to));
                }
            }

            return profile.Size < 3 ? null : profile;
        }

        /// <summary>A point so many millimetres out from the end and across from the axis.</summary>
        private static XYZ Local(
            BeamGeometry beam,
            BeamEndPlan plan,
            XYZ outward,
            XYZ across,
            double alongMm,
            double acrossMm)
        {
            var point = beam.PointAt(plan.End) + outward * alongMm.MmToFeet() + across * acrossMm.MmToFeet();
            return new XYZ(point.X, point.Y, beam.MiddleZ);
        }

        /// <summary>
        /// The part of a polygon lying past the cut plane, found by walking its edges and keeping
        /// whatever is on the far side, with the crossing points put in where an edge steps over.
        /// </summary>
        private static IList<XYZ> Beyond(
            IList<XYZ> corners,
            BeamGeometry beam,
            BeamEndPlan plan,
            BeamEndCut cut,
            XYZ outward,
            XYZ across)
        {
            var origin = Local(beam, plan, outward, across, cut.PlaneMm, 0);
            var normal = (outward * cut.AlongNormal + across * cut.AcrossNormal).Normalize();

            var kept = new List<XYZ>();

            for (var index = 0; index < corners.Count; index++)
            {
                var from = corners[index];
                var to = corners[(index + 1) % corners.Count];

                var here = (from - origin).DotProduct(normal);
                var there = (to - origin).DotProduct(normal);

                if (here >= 0)
                {
                    kept.Add(from);
                }

                if (here > 0 != there > 0 && Math.Abs(here - there) > GeometryExtensions.Tolerance)
                {
                    kept.Add(from + (to - from) * (here / (here - there)));
                }
            }

            return kept;
        }

        /// <summary>Openings this tool would own: hosted by the beam and sitting near that end.</summary>
        private static IEnumerable<Opening> OpeningsAtEnd(Document document, BeamGeometry beam, int end)
        {
            var endPoint = beam.PointAt(end);
            var reach = BelongsToEndMm.MmToFeet();

            return new FilteredElementCollector(document)
                .OfClass(typeof(Opening))
                .Cast<Opening>()
                .Where(opening => opening.Host != null && opening.Host.Id == beam.Beam.Id)
                .Where(opening => Points(opening).Any(point => point.DistanceTo(endPoint) < reach));
        }

        private static IEnumerable<XYZ> Points(Opening opening)
        {
            CurveArray boundary;
            try
            {
                boundary = opening.BoundaryCurves;
            }
            catch
            {
                yield break;
            }

            if (boundary == null)
            {
                yield break;
            }

            foreach (Curve curve in boundary)
            {
                yield return curve.GetEndPoint(0);
            }
        }
    }
}
