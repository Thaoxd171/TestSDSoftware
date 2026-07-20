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
            var faces = PlanarFacesOf(plate, result);
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
            var faces = PlanarFacesOf(plate, result);
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
            IList<PlanarFace> faces,
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
        private static PlanarFace Outermost(IEnumerable<PlanarFace> faces, XYZ direction)
        {
            return faces
                .Where(f => f.FaceNormal.IsAlmostEqualTo(direction, NormalTolerance))
                .OrderByDescending(f => f.Origin.DotProduct(direction))
                .FirstOrDefault();
        }

        /// <summary>
        /// Planar faces of the element that Revit will dimension to. References only come back when
        /// the geometry is read with ComputeReferences, and a family instance keeps its geometry one
        /// level down, inside a GeometryInstance.
        /// </summary>
        private List<PlanarFace> PlanarFacesOf(Element element, AnnotationResult result)
        {
            var options = new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine,
            };

            var faces = Flatten(element.get_Geometry(options))
                .OfType<Solid>()
                .SelectMany(solid => solid.Faces.OfType<PlanarFace>())
                .Where(face => face.Reference != null)
                .ToList();

            if (faces.Count == 0)
            {
                result.Note("the plate has no planar faces to dimension to");
            }

            return faces;
        }

        private static IEnumerable<GeometryObject> Flatten(GeometryElement geometry)
        {
            if (geometry == null)
            {
                yield break;
            }

            foreach (var item in geometry)
            {
                if (item is GeometryInstance instance)
                {
                    foreach (var nested in Flatten(instance.GetInstanceGeometry()))
                    {
                        yield return nested;
                    }
                }
                else
                {
                    yield return item;
                }
            }
        }
    }
}
