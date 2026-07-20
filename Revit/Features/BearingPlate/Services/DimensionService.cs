using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Extensions;
using SDSoftware.RevitTest.Features.BearingPlate.Models;

namespace SDSoftware.RevitTest.Features.BearingPlate.Services
{
    /// <summary>
    /// Puts the overall dimensions on a plate drawing - how wide, how long, how thick. The dimension
    /// lines sit 40 mm off the plate, the same offset the tag clusters use, so a tag lands on the
    /// end of its dimension line the way it does on the reference sheets.
    /// </summary>
    public class DimensionService
    {
        /// <summary>How far a dimension line sits from the edge of the plate.</summary>
        private const double OffsetMm = 40.0;

        /// <summary>How square a face has to be to the axis before it counts as facing that way.</summary>
        private const double NormalTolerance = 1e-3;

        private readonly Document _document;

        public DimensionService(Document document)
        {
            _document = document;
        }

        /// <summary>Plan is drawn looking down: width across the bottom, length up the left side.</summary>
        public AnnotationResult DimensionPlan(View view, Element plate, DimensionType type)
        {
            var result = new AnnotationResult();

            var box = plate.get_BoundingBox(null);
            var faces = FacesOf(plate, result);
            if (box == null || faces.Count == 0)
            {
                return result;
            }

            var offset = OffsetMm.MmToFeet();

            Create(result, view, type, faces, XYZ.BasisX, "width",
                Line.CreateBound(
                    new XYZ(box.Min.X, box.Min.Y - offset, box.Max.Z),
                    new XYZ(box.Max.X, box.Min.Y - offset, box.Max.Z)));

            Create(result, view, type, faces, XYZ.BasisY, "length",
                Line.CreateBound(
                    new XYZ(box.Min.X - offset, box.Min.Y, box.Max.Z),
                    new XYZ(box.Min.X - offset, box.Max.Y, box.Max.Z)));

            return result;
        }

        /// <summary>Front is drawn looking along -Y: width above the plate, height down its left side.</summary>
        public AnnotationResult DimensionFront(View view, Element plate, DimensionType type)
        {
            var result = new AnnotationResult();

            var box = plate.get_BoundingBox(null);
            var faces = FacesOf(plate, result);
            if (box == null || faces.Count == 0)
            {
                return result;
            }

            var offset = OffsetMm.MmToFeet();

            Create(result, view, type, faces, XYZ.BasisX, "width",
                Line.CreateBound(
                    new XYZ(box.Min.X, box.Max.Y, box.Max.Z + offset),
                    new XYZ(box.Max.X, box.Max.Y, box.Max.Z + offset)));

            Create(result, view, type, faces, XYZ.BasisZ, "height",
                Line.CreateBound(
                    new XYZ(box.Min.X - offset, box.Max.Y, box.Min.Z),
                    new XYZ(box.Min.X - offset, box.Max.Y, box.Max.Z)));

            return result;
        }

        /// <summary>Dimensions between the two outermost faces square to <paramref name="axis"/>.</summary>
        private void Create(
            AnnotationResult result,
            View view,
            DimensionType type,
            IList<PlateFace> faces,
            XYZ axis,
            string what,
            Line line)
        {
            var near = Outermost(faces, axis.Negate());
            var far = Outermost(faces, axis);

            if (near == null || far == null)
            {
                result.Failure(what, "no pair of faces square to the axis");
                return;
            }

            var references = new ReferenceArray();
            references.Append(near.Reference);
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

        /// <summary>The face pointing along <paramref name="direction"/> that sits furthest that way.</summary>
        private static PlateFace Outermost(IEnumerable<PlateFace> faces, XYZ direction)
        {
            return faces
                .Where(f => f.Normal.IsAlmostEqualTo(direction, NormalTolerance))
                .OrderByDescending(f => f.Origin.DotProduct(direction))
                .FirstOrDefault();
        }

        private List<PlateFace> FacesOf(Element element, AnnotationResult result)
        {
            var options = new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine,
            };

            var faces = new List<PlateFace>();
            Collect(element.get_Geometry(options), Transform.Identity, faces);

            if (faces.Count == 0)
            {
                result.Note("the plate has no planar faces Revit will dimension to");
            }

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
