using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SDSoftware.RevitTest.Extensions;
using SDSoftware.RevitTest.Features.AdjustBeam.Models;

namespace SDSoftware.RevitTest.Features.AdjustBeam.Services
{
    /// <summary>
    /// Writes one planned move into the model. Must be called inside a transaction.
    ///
    /// The join is dropped and the end extension cleared before the axis is moved. Both matter: a
    /// joined beam is cut back by Revit at the face of whatever it runs into, and an extension pushes
    /// the solid past the axis, so with either of them left in place the beam would not stop where the
    /// clearance says it should.
    /// </summary>
    public static class BeamAdjuster
    {
        /// <summary>Revit refuses to build a line shorter than this.</summary>
        private const double ShortestLineMm = 1;

        public static void Apply(BeamGeometry beam, BeamEndPlan plan)
        {
            var element = beam.Beam;
            var end = plan.End;

            StructuralFramingUtils.DisallowJoinAtEnd(element, end);
            element.TrySet(end == 0 ? BuiltInParameter.START_EXTENSION : BuiltInParameter.END_EXTENSION, 0);

            var target = beam.PointAt(end) + beam.OutwardAt(end) * plan.MoveMm.MmToFeet();
            var axis = (LocationCurve)element.Location;
            var line = (Line)axis.Curve;

            var start = end == 0 ? target : line.GetEndPoint(0);
            var finish = end == 1 ? target : line.GetEndPoint(1);

            if (start.DistanceTo(finish) < ShortestLineMm.MmToFeet())
            {
                return;
            }

            axis.Curve = Line.CreateBound(start, finish);
        }
    }
}
