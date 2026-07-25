using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Extensions;

namespace SDSoftware.RevitTest.Features.BearingPlate.Models
{
    /// <summary>
    /// One kind of part on a plate - a hole, a stud, the plate itself. A plate carries a data
    /// element per physical part, but several of them describe the same kind, so the drawing tags
    /// one representative per kind rather than every instance.
    /// </summary>
    public class PlateComponent
    {
        /// <summary>Instance parameter holding the part name, e.g. "Cirkulaert Hul 011".</summary>
        public const string NameParameter = "DATA-Navn";

        /// <summary>Height below which a part counts as flat, in feet.</summary>
        private const double FlatTolerance = 1e-6;

        /// <summary>Two markers closer together than this share a position on the drawing.</summary>
        private static readonly double SamePositionTolerance = 1.0.MmToFeet();

        public PlateComponent(string name, IReadOnlyList<Element> instances, BoundingBoxXYZ plateBox)
        {
            Name = name;
            Instances = instances;
            Representative = instances[0];
            Mark = Representative.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();

            var points = instances
                .Select(PointOf)
                .Where(p => p != null)
                .ToList();

            Height = points.Count == 0 ? 0 : points.Max(p => p.Z) - points.Min(p => p.Z);

            AlongX = DistinctAlong(instances, p => p.X);
            AlongY = DistinctAlong(instances, p => p.Y);

            IsOutline = IsPlateItself(name);
        }

        public string Name { get; }

        /// <summary>The element that gets tagged.</summary>
        public Element Representative { get; }

        public IReadOnlyList<Element> Instances { get; }

        public string Mark { get; }

        public int Count => Instances.Count;

        /// <summary>
        /// One marker per distinct position across the plate, left to right. Four studs in a line
        /// share one position and are dimensioned once.
        /// </summary>
        public IReadOnlyList<Element> AlongX { get; }

        /// <summary>One marker per distinct position along the plate, near to far.</summary>
        public IReadOnlyList<Element> AlongY { get; }

        /// <summary>
        /// How far the part reaches vertically, taken from the spread of its markers. The plate and
        /// anything welded to it are marked top and bottom; a hole through the plate is marked once.
        /// </summary>
        public double Height { get; }

        /// <summary>
        /// True when the part stands proud of the plate. Only these are worth listing down the side
        /// of an elevation - a hole has no height of its own to call out there.
        /// </summary>
        public bool HasHeight => Height > FlatTolerance;

        /// <summary>
        /// True for the data device that stands in for the plate itself rather than a part on it.
        /// Its marker is drawn the size of the whole plate, so the overall dimensions measure it and
        /// the location chains skip it.
        /// </summary>
        public bool IsOutline { get; }

        private static XYZ PointOf(Element element) => (element.Location as LocationPoint)?.Point;

        /// <summary>Keeps one marker per position, ordered along the axis.</summary>
        private static List<Element> DistinctAlong(IReadOnlyList<Element> instances, Func<XYZ, double> coordinate)
        {
            var kept = new List<Element>();
            var taken = new List<double>();

            var ordered = instances
                .Select(e => new { Element = e, Point = PointOf(e) })
                .Where(x => x.Point != null)
                .OrderBy(x => coordinate(x.Point));

            foreach (var item in ordered)
            {
                var value = coordinate(item.Point);
                if (taken.Any(t => Math.Abs(t - value) < SamePositionTolerance))
                {
                    continue;
                }

                taken.Add(value);
                kept.Add(item.Element);
            }

            return kept;
        }

        /// <summary>True for a hole through the plate ("Hul" is Danish for hole).</summary>
        public bool IsHole => Name != null && Name.IndexOf("Hul", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>True for a part standing proud of the plate, such as a welded stud.</summary>
        public bool IsStud => !IsOutline && !IsHole;

        /// <summary>
        /// True for the data device that stands in for the plate rather than a part on it. These are
        /// annotation families with no solid and no model bounding box to measure, so they cannot be
        /// told apart by geometry; the plate's own device is the one named after the plate ("Plade"
        /// is Danish for plate), which is the name this component library gives it.
        /// </summary>
        private static bool IsPlateItself(string name)
        {
            return name != null && name.TrimStart().StartsWith("Plade", StringComparison.OrdinalIgnoreCase);
        }
    }
}
