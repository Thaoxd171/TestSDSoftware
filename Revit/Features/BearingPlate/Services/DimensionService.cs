using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Extensions;
using SDSoftware.RevitTest.Features.BearingPlate.Models;

namespace SDSoftware.RevitTest.Features.BearingPlate.Services
{
    /// <summary>
    /// Dimensions a plate drawing.
    ///
    /// The plan is built in rows stepping away from the plate, one row per kind of part. The first
    /// row belongs to the plate itself and measures how wide and how long it is; each row after that
    /// locates one kind of part - the holes, the studs - between the two edges of the plate. The tag
    /// clusters use the same rows and the same spacing, so every row ends in the tag that names it.
    /// </summary>
    public class DimensionService
    {
        /// <summary>Distance from the plate to the first row, and between rows after that.</summary>
        private const double RowStepMm = 40.0;

        /// <summary>How square a face has to be to the axis before it counts as facing that way.</summary>
        private const double NormalTolerance = 1e-3;

        private readonly Document _document;

        public DimensionService(Document document)
        {
            _document = document;
        }

        /// <summary>
        /// Plan is drawn looking down: the width rows run under the plate, the length rows up its
        /// left side.
        /// </summary>
        public AnnotationResult DimensionPlan(View view, Element plate, IList<PlateComponent> components, DimensionType type)
        {
            var result = new AnnotationResult();

            var box = plate.get_BoundingBox(null);
            var plateFaces = FacesOf(plate);
            if (box == null || plateFaces.Count == 0)
            {
                result.Note("the plate has no planar faces Revit will dimension to");
                return result;
            }

            // with no parts to locate there is still the plate itself to measure
            var rows = components.Count == 0 ? new List<PlateComponent> { null } : components.ToList();

            for (var row = 0; row < rows.Count; row++)
            {
                var component = rows[row];
                var offset = (RowStepMm * (row + 1)).MmToFeet();
                var label = component?.Name ?? "plate";

                // the plate's own row measures the outline, so it carries no intermediate markers
                var isOutline = component == null || component.IsOutline;

                Chain(result, view, type, plateFaces, XYZ.BasisX,
                    isOutline ? null : component.AlongX,
                    WidthLine(box, offset), label + " across");

                Chain(result, view, type, plateFaces, XYZ.BasisY,
                    isOutline ? null : component.AlongY,
                    LengthLine(box, offset), label + " along");
            }

            return result;
        }

        /// <summary>Front is drawn looking along -Y: width above the plate, height down its left side.</summary>
        public AnnotationResult DimensionFront(View view, Element plate, DimensionType type)
        {
            var result = new AnnotationResult();

            var box = plate.get_BoundingBox(null);
            var plateFaces = FacesOf(plate);
            if (box == null || plateFaces.Count == 0)
            {
                result.Note("the plate has no planar faces Revit will dimension to");
                return result;
            }

            var offset = RowStepMm.MmToFeet();

            Chain(result, view, type, plateFaces, XYZ.BasisX, null,
                Line.CreateBound(
                    new XYZ(box.Min.X, box.Max.Y, box.Max.Z + offset),
                    new XYZ(box.Max.X, box.Max.Y, box.Max.Z + offset)),
                "width");

            Chain(result, view, type, plateFaces, XYZ.BasisZ, null,
                Line.CreateBound(
                    new XYZ(box.Min.X - offset, box.Max.Y, box.Min.Z),
                    new XYZ(box.Min.X - offset, box.Max.Y, box.Max.Z)),
                "height");

            return result;
        }

        /// <summary>
        /// One dimension running edge to edge of the plate, stopping at each marker on the way.
        /// Passing no markers gives the plain overall dimension.
        /// </summary>
        private void Chain(
            AnnotationResult result,
            View view,
            DimensionType type,
            IList<PlateFace> plateFaces,
            XYZ axis,
            IReadOnlyList<Element> markers,
            Line line,
            string what)
        {
            var near = Outermost(plateFaces, axis.Negate());
            var far = Outermost(plateFaces, axis);

            if (near == null || far == null)
            {
                result.Failure(what, "no pair of plate faces square to the axis");
                return;
            }

            var references = new ReferenceArray();
            references.Append(near.Reference);

            foreach (var marker in markers ?? new List<Element>())
            {
                var face = FaceAtMarker(marker, axis);
                if (face == null)
                {
                    result.Failure(what, "a marker has no face square to the axis");
                    continue;
                }

                references.Append(face.Reference);
            }

            references.Append(far.Reference);

            try
            {
                if (type == null)
                {
                    _document.Create.NewDimension(view, line, references);
                }
                else
                {
                    _document.Create.NewDimension(view, line, references, type);
                }

                result.Success();
            }
            catch (Exception ex)
            {
                result.Failure(what, ex.Message);
            }
        }

        /// <summary>
        /// The face of a marker that the dimension should stop at. A marker is a one millimetre box,
        /// so its two faces square to the axis are a millimetre apart - on a fabrication drawing that
        /// is the difference between right and wrong. The one whose plane passes through the marker's
        /// own placement point is the one that means something.
        /// </summary>
        private PlateFace FaceAtMarker(Element marker, XYZ axis)
        {
            var point = (marker.Location as LocationPoint)?.Point;
            if (point == null)
            {
                return null;
            }

            var target = point.DotProduct(axis);

            return FacesOf(marker)
                .Where(f => Math.Abs(Math.Abs(f.Normal.DotProduct(axis)) - 1) < NormalTolerance)
                .OrderBy(f => Math.Abs(f.Origin.DotProduct(axis) - target))
                .FirstOrDefault();
        }

        /// <summary>The face pointing along <paramref name="direction"/> that sits furthest that way.</summary>
        private static PlateFace Outermost(IEnumerable<PlateFace> faces, XYZ direction)
        {
            return faces
                .Where(f => f.Normal.IsAlmostEqualTo(direction, NormalTolerance))
                .OrderByDescending(f => f.Origin.DotProduct(direction))
                .FirstOrDefault();
        }

        private static Line WidthLine(BoundingBoxXYZ box, double offset)
        {
            return Line.CreateBound(
                new XYZ(box.Min.X, box.Min.Y - offset, box.Max.Z),
                new XYZ(box.Max.X, box.Min.Y - offset, box.Max.Z));
        }

        private static Line LengthLine(BoundingBoxXYZ box, double offset)
        {
            return Line.CreateBound(
                new XYZ(box.Min.X - offset, box.Min.Y, box.Max.Z),
                new XYZ(box.Min.X - offset, box.Max.Y, box.Max.Z));
        }

        private List<PlateFace> FacesOf(Element element)
        {
            var options = new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine,
            };

            var faces = new List<PlateFace>();
            Collect(element.get_Geometry(options), Transform.Identity, faces);
            return faces;
        }

        /// <summary>
        /// Walks the geometry collecting planar faces together with a reference Revit will dimension
        /// to.
        ///
        /// A family instance keeps its geometry one level down. Reading it back through
        /// GetInstanceGeometry gives shapes already placed in the model but with no usable
        /// references, so the symbol geometry is read instead and the instance transform is carried
        /// along to work out where each face ended up.
        /// </summary>
        private static void Collect(GeometryElement geometry, Transform transform, List<PlateFace> faces)
        {
            if (geometry == null)
            {
                return;
            }

            foreach (var item in geometry)
            {
                if (item is GeometryInstance instance)
                {
                    Collect(instance.GetSymbolGeometry(), transform.Multiply(instance.Transform), faces);
                }
                else if (item is Solid solid)
                {
                    foreach (var face in solid.Faces.OfType<PlanarFace>().Where(f => f.Reference != null))
                    {
                        faces.Add(new PlateFace(
                            face.Reference,
                            transform.OfVector(face.FaceNormal),
                            transform.OfPoint(face.Origin)));
                    }
                }
            }
        }

        /// <summary>A face of the plate: where it is in the model, and how to point Revit at it.</summary>
        private class PlateFace
        {
            public PlateFace(Reference reference, XYZ normal, XYZ origin)
            {
                Reference = reference;
                Normal = normal;
                Origin = origin;
            }

            public Reference Reference { get; }

            public XYZ Normal { get; }

            public XYZ Origin { get; }
        }
    }
}
