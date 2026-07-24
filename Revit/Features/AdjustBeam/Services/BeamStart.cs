using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SDSoftware.RevitTest.Extensions;

namespace SDSoftware.RevitTest.Features.AdjustBeam.Services
{
    /// <summary>
    /// A beam as the run found it, kept so that it can be put back.
    ///
    /// The run works out where an end belongs by measuring from where the beam stands, so a beam that
    /// has already been moved is measured from the wrong place. Where two beams meet, the first one
    /// placed is placed against a neighbour that has not been dealt with yet, and once it has moved
    /// there is nothing left to tell it that the ground has shifted: measured again from its new
    /// position it is right where it is. Putting it back to where it started before measuring it again
    /// is what lets a later sweep overrule an earlier one.
    ///
    /// Everything the run writes to the axis is captured, not just the two points: an extension or a
    /// join left as the run set them would go on holding the solid somewhere other than the axis says.
    /// </summary>
    public class BeamStart
    {
        private readonly FamilyInstance _beam;
        private readonly XYZ _start;
        private readonly XYZ _finish;
        private readonly double _startExtension;
        private readonly double _endExtension;
        private readonly bool _joinAtStart;
        private readonly bool _joinAtEnd;

        private BeamStart(
            FamilyInstance beam,
            XYZ start,
            XYZ finish,
            double startExtension,
            double endExtension,
            bool joinAtStart,
            bool joinAtEnd)
        {
            _beam = beam;
            _start = start;
            _finish = finish;
            _startExtension = startExtension;
            _endExtension = endExtension;
            _joinAtStart = joinAtStart;
            _joinAtEnd = joinAtEnd;
        }

        public FamilyInstance Beam => _beam;

        /// <summary>Null when the beam has no straight axis to put back.</summary>
        public static BeamStart Capture(FamilyInstance beam)
        {
            var line = (beam.Location as LocationCurve)?.Curve as Line;
            if (line == null)
            {
                return null;
            }

            return new BeamStart(
                beam,
                line.GetEndPoint(0),
                line.GetEndPoint(1),
                Extension(beam, BuiltInParameter.START_EXTENSION),
                Extension(beam, BuiltInParameter.END_EXTENSION),
                Joined(beam, 0),
                Joined(beam, 1));
        }

        /// <summary>Puts the axis, the extensions and the joins back the way they were found.</summary>
        public void Restore()
        {
            var axis = _beam.Location as LocationCurve;
            if (axis == null)
            {
                return;
            }

            Join(0, _joinAtStart);
            Join(1, _joinAtEnd);

            _beam.TrySet(BuiltInParameter.START_EXTENSION, _startExtension);
            _beam.TrySet(BuiltInParameter.END_EXTENSION, _endExtension);

            axis.Curve = Line.CreateBound(_start, _finish);
        }

        private void Join(int end, bool allowed)
        {
            try
            {
                if (allowed)
                {
                    StructuralFramingUtils.AllowJoinAtEnd(_beam, end);
                }
                else
                {
                    StructuralFramingUtils.DisallowJoinAtEnd(_beam, end);
                }
            }
            catch
            {
                // Not every framing family takes a join at all, and one that does not is already
                // where this wanted to leave it.
            }
        }

        private static double Extension(Element beam, BuiltInParameter which)
        {
            return beam.get_Parameter(which)?.AsDouble() ?? 0;
        }

        private static bool Joined(FamilyInstance beam, int end)
        {
            try
            {
                return StructuralFramingUtils.IsJoinAllowedAtEnd(beam, end);
            }
            catch
            {
                return false;
            }
        }
    }
}
