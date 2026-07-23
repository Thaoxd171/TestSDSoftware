using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SDSoftware.RevitTest.Extensions;
using SDSoftware.RevitTest.Features.AdjustBeam.Models;

namespace SDSoftware.RevitTest.Features.AdjustBeam.Services
{
    /// <summary>
    /// Writes the planned moves for one beam into the model. Must be called inside a transaction.
    ///
    /// Both ends are written in a single assignment to the location curve. Writing them one at a time
    /// does not work: the second write has to read the curve back to keep the other end where it is,
    /// and until the document regenerates that read still returns the old curve - so the first move is
    /// quietly undone and the beam ends up short by only one of the two amounts.
    ///
    /// The join is dropped and the end extension cleared before the axis moves. Both matter: a joined
    /// beam is cut back by Revit at the face of whatever it runs into, and an extension pushes the
    /// solid past the axis, so with either left in place the beam would not stop where the clearance
    /// says it should.
    /// </summary>
    public static class BeamAdjuster
    {
        /// <summary>Revit refuses to build a line shorter than this.</summary>
        private const double ShortestLineMm = 1;

        /// <summary>Moves whichever of the two ends have somewhere to go. False when nothing was written.</summary>
        public static bool Apply(BeamGeometry beam, IEnumerable<BeamEndPlan> plans)
        {
            var element = beam.Beam;
            var axis = (LocationCurve)element.Location;
            var line = (Line)axis.Curve;

            // Read both ends up front: from here on the model is written, not read.
            var points = new[] { line.GetEndPoint(0), line.GetEndPoint(1) };
            var moved = false;

            foreach (var plan in plans.Where(plan => plan.WillMove))
            {
                StructuralFramingUtils.DisallowJoinAtEnd(element, plan.End);
                element.TrySet(
                    plan.End == 0 ? BuiltInParameter.START_EXTENSION : BuiltInParameter.END_EXTENSION, 0);

                points[plan.End] = beam.TargetAt(plan.End, plan.MoveMm);
                moved = true;
            }

            if (!moved || points[0].DistanceTo(points[1]) < ShortestLineMm.MmToFeet())
            {
                return false;
            }

            axis.Curve = Line.CreateBound(points[0], points[1]);
            return true;
        }
    }
}
