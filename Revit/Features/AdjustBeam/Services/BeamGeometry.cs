using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Extensions;

namespace SDSoftware.RevitTest.Features.AdjustBeam.Services
{
    /// <summary>
    /// A beam as it stands before anything is changed: its axis and the point where its solid really
    /// stops at each end. The two differ whenever the beam is joined to its neighbours or carries an
    /// end extension, and it is the solid that the clearances are measured from.
    /// Every beam is captured up front so that moving one never disturbs the measurement of the next.
    /// </summary>
    public class BeamGeometry
    {
        /// <summary>
        /// How far below the top of the beam the probe ray runs. Low enough not to graze the top face
        /// of a neighbouring beam - a ray in that plane slides along it and reports no hit - and high
        /// enough to stay above the bottom ledge of a precast section, which is wider than the body
        /// the beam is supposed to stop against.
        /// </summary>
        private const double ProbeInsetMm = 50;

        private BeamGeometry(FamilyInstance beam, Line axis, double startExtent, double endExtent, double topZ, double probeZ)
        {
            Beam = beam;
            Axis = axis;
            StartExtent = startExtent;
            EndExtent = endExtent;
            TopZ = topZ;
            ProbeZ = probeZ;
        }

        public FamilyInstance Beam { get; }

        /// <summary>The location line, as modelled.</summary>
        public Line Axis { get; }

        public XYZ Direction => Axis.Direction;

        /// <summary>Where the solid starts and stops, measured along the axis from its start point.</summary>
        private double StartExtent { get; }

        private double EndExtent { get; }

        /// <summary>Top of the beam. Tells a column carrying on upwards from a bracket it sits on.</summary>
        public double TopZ { get; }

        /// <summary>Height at which supports are probed.</summary>
        private double ProbeZ { get; }

        public double LengthMm => (EndExtent - StartExtent).FeetToMm();

        /// <summary>Null when the beam has no straight axis or no readable geometry.</summary>
        public static BeamGeometry Create(FamilyInstance beam)
        {
            var axis = beam.GetLocationLine();
            if (axis == null)
            {
                return null;
            }

            var solids = beam.GetSolids().ToList();
            if (solids.Count == 0)
            {
                return null;
            }

            var extent = solids.ExtentAlong(axis.GetEndPoint(0), axis.Direction);
            var vertices = solids.SelectMany(solid => solid.GetVertices()).ToList();
            var top = vertices.Max(point => point.Z);
            var bottom = vertices.Min(point => point.Z);

            // Shallow sections get half their depth instead of the fixed inset.
            var inset = System.Math.Min(ProbeInsetMm.MmToFeet(), (top - bottom) / 2);

            return new BeamGeometry(beam, axis, extent.Min, extent.Max, top, top - inset);
        }

        /// <summary>Where the solid stops, on the axis. End 0 is the start of the location line.</summary>
        public XYZ PointAt(int end)
        {
            return Axis.GetEndPoint(0) + Direction * (end == 0 ? StartExtent : EndExtent);
        }

        /// <summary>Direction pointing away from the beam at that end.</summary>
        public XYZ OutwardAt(int end)
        {
            return end == 0 ? -Direction : Direction;
        }

        /// <summary>The end point dropped to the probe height, where the ray is shot from.</summary>
        public XYZ ProbeOriginAt(int end)
        {
            var point = PointAt(end);
            return new XYZ(point.X, point.Y, ProbeZ);
        }

        /// <summary>Both ends, so callers can loop without repeating the indices.</summary>
        public static IEnumerable<int> Ends => new[] { 0, 1 };
    }
}
