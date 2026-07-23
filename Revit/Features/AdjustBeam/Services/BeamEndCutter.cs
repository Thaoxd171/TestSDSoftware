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

        /// <summary>Cuts the wedge off one end. Returns true when an opening was created.</summary>
        public static bool Cut(Document document, BeamGeometry beam, BeamEndPlan plan)
        {
            var outward = beam.OutwardAt(plan.End);
            var against = plan.CutAgainstId == 0 ? plan.SupportId : plan.CutAgainstId;
            var support = document.GetElement(new ElementId(against));
            var normal = support == null
                ? null
                : SupportProbe.EntryNormal(support, beam.ProbeOriginAt(plan.End), outward);

            if (normal == null)
            {
                return false;
            }

            // A point on the finished end face, sitting on the axis.
            var planePoint = beam.PointAt(plan.End) + outward * plan.CutPlaneMm.Value.MmToFeet();

            document.Create.NewOpening(
                beam.Beam,
                Profile(beam, plan, planePoint, normal),
                Autodesk.Revit.Creation.eRefFace.CenterZ);
            return true;
        }

        /// <summary>
        /// The rectangle to cut away: it starts on the finished end face and reaches past the far
        /// corner of the square cut, wide enough to clear both sides of the beam. Sketched half way
        /// down the beam because that is the reference face the opening is cut from.
        /// </summary>
        private static CurveArray Profile(BeamGeometry beam, BeamEndPlan plan, XYZ planePoint, XYZ normal)
        {
            var across = XYZ.BasisZ.CrossProduct(normal).Normalize();
            var skew = plan.SkewDegrees * Math.PI / 180;

            // The width is measured across the beam, but the opening is laid out along the cut plane,
            // and a plane meeting the beam at an angle sees a wider section than the beam really is.
            // Take the width as it stands and the cut falls short at both corners, leaving the two
            // spikes the wedge was supposed to take off - and the further from square the end is, the
            // further short it falls, so it is the very ends most in need of trimming that keep them.
            var half = (beam.WidthMm / 2 / Math.Cos(skew) + SideMarginMm).MmToFeet();
            var depth = (beam.WidthMm * Math.Tan(skew) + AlongMarginMm).MmToFeet();

            var origin = new XYZ(planePoint.X, planePoint.Y, beam.MiddleZ);
            var corners = new[]
            {
                origin + across * half,
                origin - across * half,
                origin - across * half + normal * depth,
                origin + across * half + normal * depth,
            };

            var profile = new CurveArray();
            for (var index = 0; index < corners.Length; index++)
            {
                profile.Append(Line.CreateBound(corners[index], corners[(index + 1) % corners.Length]));
            }

            return profile;
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
