using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Extensions;

namespace SDSoftware.RevitTest.Features.AdjustBeam.Services
{
    /// <summary>
    /// A beam read in its own frame - distance along the axis, sideways offset, height - together with
    /// where each flank steps out.
    ///
    /// A precast beam is widened at its ends into a bearing block that spreads the load onto the column
    /// it lands on. The block matters twice over, in opposite directions. It is not an obstruction: a
    /// beam arriving beside it is measured against the web behind it, because the block is going to be
    /// cut back rather than stood clear of. But it is in the way: whoever owns it has to have it cut.
    /// Both readings need the same answer to "where does the web end and the block begin", which is
    /// what this class exists to give.
    /// </summary>
    public class BeamSection
    {
        /// <summary>A flank counts as stepped out when it widens at least this far past the web.</summary>
        public const double StepMm = 5;

        /// <summary>A face counts as upright below this much tilt in its normal.</summary>
        private const double UprightTolerance = 0.01;

        /// <summary>How squarely a face has to look sideways to be one of the beam's own flanks.</summary>
        private const double FlankTolerance = 0.9;

        private readonly Dictionary<int, Flank> _flanks = new Dictionary<int, Flank>();

        private XYZ _origin;
        private XYZ _direction;
        private XYZ _across;

        public IList<Solid> Solids { get; private set; }

        public double TopZ { get; private set; }

        public double BottomZ { get; private set; }

        public double MiddleZ => (TopZ + BottomZ) / 2;

        /// <summary>Null when the element has no straight location line or no readable geometry.</summary>
        public static BeamSection Read(Element beam)
        {
            var axis = beam?.GetLocationLine();
            if (axis == null)
            {
                return null;
            }

            var solids = beam.GetSolids().ToList();
            if (solids.Count == 0)
            {
                return null;
            }

            var across = XYZ.BasisZ.CrossProduct(axis.Direction);
            if (across.IsZeroLength())
            {
                return null;
            }

            var vertices = solids.SelectMany(solid => solid.GetVertices()).ToList();
            if (vertices.Count == 0)
            {
                return null;
            }

            var section = new BeamSection
            {
                _origin = axis.GetEndPoint(0),
                _direction = axis.Direction,
                _across = across.Normalize(),
                Solids = solids,
                TopZ = vertices.Max(point => point.Z),
                BottomZ = vertices.Min(point => point.Z),
            };

            section.ReadFlanks();
            return section;
        }

        public double Along(XYZ point) => (point - _origin).DotProduct(_direction);

        /// <summary>Sideways offset, counted positive outwards on the side asked about.</summary>
        public double Across(XYZ point, int side) => (point - _origin).DotProduct(_across) * side;

        public XYZ PointAt(double along, double across, int side)
        {
            var point = _origin + _direction * along + _across * (across * side);
            return new XYZ(point.X, point.Y, MiddleZ);
        }

        /// <summary>Offset of the web face on one side, or null when that flank has no face.</summary>
        public double? Web(int side) => FlankOn(side)?.Web;

        /// <summary>Offset of the outermost face on one side.</summary>
        public double? Flange(int side) => FlankOn(side)?.Flange;

        /// <summary>Whether that side widens out into a bearing block at all.</summary>
        public bool HasBlock(int side)
        {
            var flank = FlankOn(side);
            return flank != null && (flank.Flange - flank.Web).FeetToMm() >= StepMm;
        }

        /// <summary>
        /// Whether a point sits outside the web, in the widened part of the section. Material out
        /// there is a bearing block, and a bearing block is cut back rather than stood clear of.
        /// </summary>
        public bool IsBeyondWeb(XYZ point)
        {
            var step = StepMm.MmToFeet();

            return new[] { 1, -1 }
                .Select(FlankOn)
                .Where(flank => flank != null)
                .Any(flank => Across(point, flank.Side) > flank.Web + step);
        }

        private Flank FlankOn(int side)
        {
            return _flanks.TryGetValue(side, out var flank) ? flank : null;
        }

        private void ReadFlanks()
        {
            var faces = Solids
                .SelectMany(solid => solid.Faces.Cast<Face>())
                .OfType<PlanarFace>()
                .Where(face => Math.Abs(face.FaceNormal.Z) < UprightTolerance)
                .ToList();

            foreach (var side in new[] { 1, -1 })
            {
                var offsets = faces
                    .Where(face => face.FaceNormal.DotProduct(_across) * side > FlankTolerance)
                    .Select(face => Across(face.Origin, side))
                    .ToList();

                if (offsets.Count > 0)
                {
                    _flanks[side] = new Flank { Side = side, Web = offsets.Min(), Flange = offsets.Max() };
                }
            }
        }

        private class Flank
        {
            public int Side { get; set; }

            public double Web { get; set; }

            public double Flange { get; set; }
        }
    }
}
