using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace SDSoftware.RevitTest.Extensions
{
    /// <summary>Geometry helpers shared by the tools. All values are in internal units (feet).</summary>
    public static class GeometryExtensions
    {
        /// <summary>Tolerance used for point/vector comparisons, matching Revit's short curve limit.</summary>
        public const double Tolerance = 1e-6;

        public static XYZ ToXY(this XYZ point) => new XYZ(point.X, point.Y, 0);

        public static bool IsAlmostEqualTo(this double a, double b, double tolerance = Tolerance)
        {
            return Math.Abs(a - b) < tolerance;
        }

        /// <summary>Point on a horizontal circle, measured counter-clockwise from the +X axis.</summary>
        public static XYZ PointOnCircle(XYZ centre, double radius, double angleRadians, double z = 0)
        {
            return new XYZ(
                centre.X + radius * Math.Cos(angleRadians),
                centre.Y + radius * Math.Sin(angleRadians),
                centre.Z + z);
        }

        /// <summary>Start and end point of a curve-driven element's location line.</summary>
        public static Line GetLocationLine(this Element element)
        {
            return (element?.Location as LocationCurve)?.Curve as Line;
        }

        public static XYZ GetLocationPoint(this Element element)
        {
            return (element?.Location as LocationPoint)?.Point;
        }

        /// <summary>Bounding box of several elements in a given view (null when none has geometry).</summary>
        public static BoundingBoxXYZ GetCombinedBoundingBox(this IEnumerable<Element> elements, View view = null)
        {
            var boxes = elements
                .Select(e => e.get_BoundingBox(view))
                .Where(b => b != null)
                .ToList();

            if (boxes.Count == 0)
            {
                return null;
            }

            var min = new XYZ(boxes.Min(b => b.Min.X), boxes.Min(b => b.Min.Y), boxes.Min(b => b.Min.Z));
            var max = new XYZ(boxes.Max(b => b.Max.X), boxes.Max(b => b.Max.Y), boxes.Max(b => b.Max.Z));
            return new BoundingBoxXYZ { Min = min, Max = max };
        }

        /// <summary>Grows a bounding box by an equal margin on every side.</summary>
        public static BoundingBoxXYZ Inflate(this BoundingBoxXYZ box, double margin)
        {
            var offset = new XYZ(margin, margin, margin);
            return new BoundingBoxXYZ { Min = box.Min - offset, Max = box.Max + offset };
        }

        /// <summary>
        /// Every solid of an element, including the ones nested inside geometry instances. Returns an
        /// empty sequence rather than throwing when the element has no readable geometry.
        /// </summary>
        public static IEnumerable<Solid> GetSolids(this Element element, ViewDetailLevel detail = ViewDetailLevel.Fine)
        {
            GeometryElement geometry;
            try
            {
                geometry = element?.get_Geometry(new Options { DetailLevel = detail, ComputeReferences = false });
            }
            catch
            {
                return Enumerable.Empty<Solid>();
            }

            return geometry == null ? Enumerable.Empty<Solid>() : Flatten(geometry);
        }

        /// <summary>Points along the edges of a solid - enough to bound it in any direction.</summary>
        public static IEnumerable<XYZ> GetVertices(this Solid solid)
        {
            foreach (Edge edge in solid.Edges)
            {
                foreach (var point in edge.Tessellate())
                {
                    yield return point;
                }
            }
        }

        /// <summary>How far a set of solids reaches along a direction, relative to an origin.</summary>
        public static (double Min, double Max) ExtentAlong(this IEnumerable<Solid> solids, XYZ origin, XYZ direction)
        {
            var distances = solids
                .SelectMany(GetVertices)
                .Select(point => point.Subtract(origin).DotProduct(direction))
                .ToList();

            return distances.Count == 0 ? (0, 0) : (distances.Min(), distances.Max());
        }

        private static IEnumerable<Solid> Flatten(GeometryElement geometry)
        {
            foreach (var item in geometry)
            {
                switch (item)
                {
                    case Solid solid when solid.Volume > 0:
                        yield return solid;
                        break;

                    case GeometryInstance instance:
                        foreach (var nested in Flatten(instance.GetInstanceGeometry()))
                        {
                            yield return nested;
                        }

                        break;
                }
            }
        }
    }
}
