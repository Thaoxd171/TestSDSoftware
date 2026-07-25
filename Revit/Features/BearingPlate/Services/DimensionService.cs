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

            // row 0 is the plate itself: overall width and length, with no markers in between
            Chain(result, view, type, plateFaces, XYZ.BasisX, null,
                WidthLine(box, RowStepMm.MmToFeet()), "plate across");
            Chain(result, view, type, plateFaces, XYZ.BasisY, null,
                LengthLine(box, RowStepMm.MmToFeet()), "plate along");

            // one row per real part, locating it between the two edges of the plate; the plate's own
            // data device fills the outline and is already covered by the overall dimensions above
            var parts = components.Where(c => !c.IsOutline).ToList();

            for (var i = 0; i < parts.Count; i++)
            {
                var component = parts[i];
                var offset = (RowStepMm * (i + 2)).MmToFeet();

                Chain(result, view, type, plateFaces, XYZ.BasisX, component.AlongX,
                    WidthLine(box, offset), component.Name + " across");

                Chain(result, view, type, plateFaces, XYZ.BasisY, component.AlongY,
                    LengthLine(box, offset), component.Name + " along");
            }

            return result;
        }

        /// <summary>
        /// Front is drawn looking along -Y. The width and the part locations run in rows below the
        /// elevation; the plate thickness and any stud length run down the left side.
        ///
        /// Every face of the plate is seen edge-on in an elevation, and Revit will not keep a
        /// dimension anchored to an edge-on face, so - exactly as the reference drawings do - the
        /// ends are anchored to short detail lines drawn on the view. The markers still carry their
        /// own centre reference, which survives here just as it does on the plan.
        /// </summary>
        public AnnotationResult DimensionFront(View view, Element plate, IList<PlateComponent> components, DimensionType type)
        {
            var result = new AnnotationResult();

            var box = plate.get_BoundingBox(null);
            var plateFaces = FacesOf(plate);
            if (box == null || plateFaces.Count == 0)
            {
                result.Note("the plate has no planar faces Revit will dimension to");
                return result;
            }

            // everything drawn for the elevation lives on the view's cut plane
            var planeY = view.Origin.Y;
            var stub = 1.0.MmToFeet();

            var left = StubReference(view, new XYZ(box.Min.X, planeY, box.Max.Z), XYZ.BasisZ, stub);
            var right = StubReference(view, new XYZ(box.Max.X, planeY, box.Max.Z), XYZ.BasisZ, stub);

            // row 0: overall width; then one row per part, located across the plate
            HorizontalRow(result, view, type, planeY, box, left, right, null, RowStepMm.MmToFeet(), "plate across");

            // the elevation stacks its rows the opposite way round from the plan: the part nearest the
            // plate is the last one, the furthest is the first
            var parts = components.Where(c => !c.IsOutline).Reverse().ToList();
            for (var i = 0; i < parts.Count; i++)
            {
                var offset = (RowStepMm * (i + 2)).MmToFeet();
                HorizontalRow(result, view, type, planeY, box, left, right, parts[i].AlongX, offset, parts[i].Name + " across");
            }

            HeightChain(result, view, type, plateFaces, planeY, box);

            return result;
        }

        /// <summary>
        /// One horizontal dimension below the elevation: the plate edges as detail-line ends, with the
        /// markers of one part located between them. Passing no markers gives the overall width.
        /// </summary>
        private void HorizontalRow(
            AnnotationResult result,
            View view,
            DimensionType type,
            double planeY,
            BoundingBoxXYZ box,
            Reference left,
            Reference right,
            IReadOnlyList<Element> markers,
            double offset,
            string what)
        {
            var references = new ReferenceArray();
            references.Append(left);

            foreach (var marker in markers ?? new List<Element>())
            {
                var reference = CentreReference(marker, XYZ.BasisX);
                if (reference == null)
                {
                    result.Failure(what, "a marker has no centre reference square to the axis");
                    continue;
                }

                references.Append(reference);
            }

            references.Append(right);

            // rows stack above the elevation, clear of the studs that hang below it
            var z = box.Max.Z + offset;
            var line = Line.CreateBound(new XYZ(box.Min.X, planeY, z), new XYZ(box.Max.X, planeY, z));
            Place(result, view, line, references, type, what);
        }

        /// <summary>
        /// The vertical dimension down the left side: the plate thickness, and the stud length below it
        /// when the studs are modelled. Both come from the distinct heights of the plate's horizontal
        /// faces, anchored to detail lines because the faces are edge-on in the elevation.
        /// </summary>
        private void HeightChain(AnnotationResult result, View view, DimensionType type,
            IList<PlateFace> plateFaces, double planeY, BoundingBoxXYZ box)
        {
            var heights = plateFaces
                .Where(f => Math.Abs(f.Normal.Z) > 1 - NormalTolerance)
                .Select(f => f.Origin.Z)
                .OrderByDescending(z => z)
                .ToList();

            var distinct = new List<double>();
            foreach (var z in heights)
            {
                if (!distinct.Any(d => Math.Abs(d - z) < NormalTolerance))
                {
                    distinct.Add(z);
                }
            }

            if (distinct.Count < 2)
            {
                result.Failure("height", "the plate has fewer than two horizontal faces to dimension");
                return;
            }

            // keep the plate top, the plate underside and the stud tip; the little steps of a stud's
            // base foot in between are not called out
            var anchors = distinct.Count >= 3
                ? new List<double> { distinct[0], distinct[1], distinct[distinct.Count - 1] }
                : distinct;

            var stub = 1.0.MmToFeet();

            // both chains sit to the left of the elevation, one stub per height shared between them
            var stubs = anchors
                .Select(z => StubReference(view, new XYZ(box.Min.X, planeY, z), XYZ.BasisX, stub))
                .ToList();

            // both chains break the height down the same way - plate thickness over stud length - to
            // match the reference drawings: one nearer the plate, one further out, sharing the stubs
            var chainX = new[] { box.Min.X - RowStepMm.MmToFeet(), box.Min.X - (2 * RowStepMm).MmToFeet() };
            foreach (var x in chainX)
            {
                var breakdown = new ReferenceArray();
                foreach (var reference in stubs)
                {
                    breakdown.Append(reference);
                }

                Place(result, view,
                    Line.CreateBound(new XYZ(x, planeY, box.Max.Z), new XYZ(x, planeY, box.Min.Z)),
                    breakdown, type, "height");
            }
        }

        /// <summary>A short detail line drawn on the view, returned as something to dimension to.</summary>
        private Reference StubReference(View view, XYZ at, XYZ direction, double length)
        {
            var line = Line.CreateBound(at, at + direction.Normalize() * length);
            return _document.Create.NewDetailCurve(view, line).GeometryCurve.Reference;
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
                var reference = CentreReference(marker, axis);
                if (reference == null)
                {
                    result.Failure(what, "a marker has no centre reference square to the axis");
                    continue;
                }

                references.Append(reference);
            }

            references.Append(far.Reference);

            Place(result, view, line, references, type, what);
        }

        /// <summary>Creates one dimension, recording success or the reason it would not take.</summary>
        private void Place(AnnotationResult result, View view, Line line, ReferenceArray references, DimensionType type, string what)
        {
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
        /// Where a location dimension stops at a marker. The markers are annotation families with no
        /// solid to dimension to, but each one publishes a centre reference plane in either direction,
        /// which is exactly the line through the middle of the hole or stud. The plane square to the
        /// axis is the one that gives the distance along it: left-right for a width, front-back for a
        /// length.
        /// </summary>
        private static Reference CentreReference(Element marker, XYZ axis)
        {
            if (!(marker is FamilyInstance instance))
            {
                return null;
            }

            // the markers are placed rotated, so the world axis a centre plane is square to depends on
            // how the instance sits, not on a fixed mapping. The left-right plane is square to the
            // instance's hand, the front-back plane to its facing; pick whichever lines up with the
            // axis being dimensioned so the plane stays perpendicular to the dimension line.
            var hand = instance.HandOrientation;
            var facing = instance.FacingOrientation;

            var kind = Math.Abs(hand.DotProduct(axis)) >= Math.Abs(facing.DotProduct(axis))
                ? FamilyInstanceReferenceType.CenterLeftRight
                : FamilyInstanceReferenceType.CenterFrontBack;

            return instance.GetReferences(kind).FirstOrDefault();
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
