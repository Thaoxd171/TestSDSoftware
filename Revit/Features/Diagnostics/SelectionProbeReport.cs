using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SDSoftware.RevitTest.Extensions;
using static SDSoftware.RevitTest.Features.Diagnostics.ProbeFormat;

namespace SDSoftware.RevitTest.Features.Diagnostics
{
    /// <summary>
    /// Reports a selection by the part each element plays rather than as a flat list: the beams, the
    /// columns they sit on, and the openings cutting them. The last section is the one that answers the
    /// questions - where every beam end stops relative to the faces around it, and which face each
    /// opening cuts parallel to.
    ///
    /// Openings hosted by a selected beam are picked up on their own, so they need not be selected.
    /// Read-only.
    /// Temporary: remove this command and its ribbon button before the final submission.
    /// </summary>
    internal static class SelectionProbeReport
    {
        /// <summary>Planes are treated as parallel below this angle.</summary>
        private const double ParallelDegrees = 2;

        /// <summary>A face further away than this has nothing to do with the joint being looked at.</summary>
        private const double ReachMm = 2000;

        /// <summary>A face is upright when its normal is this close to horizontal.</summary>
        private const double UprightTolerance = 0.01;

        private const int MaxFaces = 12;

        private const int MaxPairLines = 20;

        public static string Build(Document document, IList<Element> elements)
        {
            var report = new StringBuilder();
            var parts = Collect(document, elements, out var added);

            var beams = parts.Where(part => part.Role == Role.Beam).ToList();
            var columns = parts.Where(part => part.Role == Role.Column).ToList();
            var openings = parts.Where(part => part.Role == Role.Opening).ToList();
            var others = parts.Where(part => part.Role == Role.Other).ToList();

            Section(report, "SD REVIT TEST - SELECTION PROBE", () =>
            {
                report.AppendLine($"Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                report.AppendLine($"Document  : {document.Title}");
                report.AppendLine($"Selected  : {elements.Count}");
                report.AppendLine($"Sorted as : {beams.Count} beam(s), {columns.Count} column(s), " +
                                  $"{openings.Count} opening(s), {others.Count} other");

                if (added > 0)
                {
                    report.AppendLine($"            {added} opening(s) hosted by a selected beam were added on their own.");
                }

                report.AppendLine();
                report.AppendLine("All lengths are mm, all angles degrees. A distance \"along\" a beam is measured");
                report.AppendLine("from where its solid stops, outwards: + means the face is past the end of the");
                report.AppendLine("beam, - means the beam already runs through it.");
            });

            foreach (var beam in beams)
            {
                Section(report, $"BEAM  -  id {beam.Element.Id.ToLong()}", () => DumpBeam(report, document, beam));
            }

            foreach (var column in columns)
            {
                Section(report, $"COLUMN  -  id {column.Element.Id.ToLong()}", () => DumpColumn(report, document, column));
            }

            foreach (var opening in openings)
            {
                Section(report, $"OPENING  -  id {opening.Element.Id.ToLong()}", () => DumpOpening(report, opening, beams));
            }

            foreach (var other in others)
            {
                Section(report, $"{Kind(other.Element)}  -  id {other.Element.Id.ToLong()}",
                    () => DumpOther(report, document, other));
            }

            if (beams.Count > 0 || columns.Count > 0)
            {
                Section(report, "HOW THE PIECES SIT", () => Joint(report, beams, columns, openings));
            }
            else
            {
                // Nothing recognisable, so fall back to comparing everything with everything.
                for (var first = 0; first < parts.Count; first++)
                {
                    for (var second = first + 1; second < parts.Count; second++)
                    {
                        var a = parts[first].Element;
                        var b = parts[second].Element;
                        Section(report, $"PAIR  {Kind(a)} {a.Id.ToLong()}  vs  {Kind(b)} {b.Id.ToLong()}",
                            () => Compare(report, a, b));
                    }
                }
            }

            return report.ToString();
        }

        /// <summary>
        /// Turns the selection into parts, and picks up any opening cutting a selected beam whether it
        /// was selected or not - they are small and easy to miss on screen.
        /// </summary>
        private static List<Part> Collect(Document document, IList<Element> elements, out int added)
        {
            var parts = elements.Select(Part.Create).Where(part => part != null).ToList();
            var beamIds = parts
                .Where(part => part.Role == Role.Beam)
                .Select(part => part.Element.Id)
                .ToList();

            var known = new HashSet<ElementId>(parts.Select(part => part.Element.Id));

            var hosted = new FilteredElementCollector(document)
                .OfClass(typeof(Opening))
                .Cast<Opening>()
                .Where(opening => opening.Host != null && beamIds.Contains(opening.Host.Id))
                .Where(opening => !known.Contains(opening.Id))
                .ToList();

            added = hosted.Count;
            parts.AddRange(hosted.Select(Part.Create).Where(part => part != null));
            return parts;
        }

        private static void DumpBeam(StringBuilder report, Document document, Part beam)
        {
            var instance = (FamilyInstance)beam.Element;

            report.AppendLine($"element   : {Describe(beam.Element)}");
            report.AppendLine($"level     : \"{LevelNameOf(document, beam.Element)}\"   " +
                              $"structural: {instance.StructuralType}");

            if (beam.Axis == null)
            {
                report.AppendLine("axis      : (not a straight location line)");
                return;
            }

            report.AppendLine($"axis      : {Mm(beam.AxisStart)} -> {Mm(beam.AxisFinish)}   {Vec(beam.Direction)}");
            report.AppendLine($"solid     : END 0 stops at {Mm(beam.PointAt(0))}   " +
                              $"END 1 stops at {Mm(beam.PointAt(1))}");
            report.AppendLine($"            length {beam.LengthMm:0.#}   width {beam.WidthMm:0.#}   " +
                              $"top {beam.TopZ.FeetToMm():0.#}   bottom {beam.BottomZ.FeetToMm():0.#}");
            report.AppendLine($"extension : start {Length(instance, BuiltInParameter.START_EXTENSION)}" +
                              $"   end {Length(instance, BuiltInParameter.END_EXTENSION)}");
            report.AppendLine($"join      : allowed at start {JoinAllowed(instance, 0)}   " +
                              $"at end {JoinAllowed(instance, 1)}");

            DumpUprights(report, beam);
        }

        private static void DumpColumn(StringBuilder report, Document document, Part column)
        {
            report.AppendLine($"element   : {Describe(column.Element)}");
            report.AppendLine($"level     : \"{LevelNameOf(document, column.Element)}\"");
            report.AppendLine($"location  : {LocationOf(column.Element)}");
            report.AppendLine($"centre    : {Mm(column.Centre)}   (middle of the solid)");
            report.AppendLine($"height    : {column.BottomZ.FeetToMm():0.#} to {column.TopZ.FeetToMm():0.#}");
            report.AppendLine($"orientation: {OrientationOf(column.Element)}");

            report.AppendLine();
            report.AppendLine("upright faces, and how far each one is from the centre of the column:");

            foreach (var face in column.Uprights)
            {
                var offset = (face.Point - column.Centre).DotProduct(face.Normal).FeetToMm();
                report.AppendLine($"  normal {Vec(face.Normal)}   at {Mm(face.Point)}   " +
                                  $"{offset:+0.#;-0.#;0} from the centre   area {face.AreaMm2:0} mm2");
            }
        }

        private static void DumpOpening(StringBuilder report, Part opening, IList<Part> beams)
        {
            var element = (Opening)opening.Element;

            report.AppendLine($"element   : {Describe(element)}");
            report.AppendLine($"host      : {Describe(element.Host)}");
            report.AppendLine($"shape     : rectangular={Safely(() => element.IsRectBoundary.ToString())}");

            var edges = Edges(element).ToList();
            if (edges.Count == 0)
            {
                report.AppendLine("boundary  : (not available)");
                return;
            }

            var centroid = Average(edges.Select(edge => edge.Middle));
            report.AppendLine($"centre    : {Mm(centroid)}");

            report.AppendLine();
            report.AppendLine("boundary:");
            foreach (var edge in edges)
            {
                report.AppendLine($"  {Mm(edge.Start)} -> {Mm(edge.Finish)}   length {edge.LengthMm:0.#}   " +
                                  $"direction {Vec(edge.Direction)}");
            }

            var host = beams.FirstOrDefault(part =>
                element.Host != null && part.Element.Id == element.Host.Id && part.Axis != null);

            if (host == null)
            {
                report.AppendLine();
                report.AppendLine("The host beam is not in the selection, so the cut cannot be placed along it.");
                return;
            }

            var end = host.NearestEnd(centroid);
            var outward = host.OutwardAt(end);

            report.AppendLine();
            report.AppendLine($"measured against its host, id {host.Element.Id.ToLong()}, END {end}:");
            report.AppendLine($"  the centre of the opening sits " +
                              $"{(centroid - host.PointAt(end)).DotProduct(outward).FeetToMm():+0.#;-0.#;0} " +
                              "along the beam from where its solid stops");

            report.AppendLine();
            report.AppendLine("the vertical planes its edges cut on:");

            foreach (var edge in edges)
            {
                var normal = XYZ.BasisZ.CrossProduct(edge.Direction);
                if (normal.IsZeroLength())
                {
                    continue;
                }

                normal = normal.Normalize();
                var denominator = outward.DotProduct(normal);
                var along = Math.Abs(denominator) < 1e-9
                    ? "(runs alongside the beam)"
                    : $"{((edge.Middle - host.PointAt(end)).DotProduct(normal) / denominator).FeetToMm():+0.#;-0.#;0} " +
                      "along the beam";

                report.AppendLine($"  normal {Vec(normal)}   {along}   " +
                                  $"{Off(host.Direction, normal):0.##} deg off square to the beam");
            }
        }

        private static void DumpOther(StringBuilder report, Document document, Part part)
        {
            report.AppendLine($"class     : {part.Element.GetType().Name}");
            report.AppendLine($"element   : {Describe(part.Element)}");
            report.AppendLine($"level     : \"{LevelNameOf(document, part.Element)}\"   host: {HostOf(part.Element)}");
            report.AppendLine($"location  : {LocationOf(part.Element)}");
            report.AppendLine($"bbox      : {BoxSize(part.Element.get_BoundingBox(null))}");

            DumpUprights(report, part);

            report.AppendLine();
            report.AppendLine("parameters:");
            DumpParameters(report, part.Element);
        }

        private static void DumpUprights(StringBuilder report, Part part)
        {
            report.AppendLine();
            report.AppendLine("upright faces:");

            if (part.Uprights.Count == 0)
            {
                report.AppendLine("  (none)");
                return;
            }

            foreach (var face in part.Uprights)
            {
                report.AppendLine($"  normal {Vec(face.Normal)}   at {Mm(face.Point)}   area {face.AreaMm2:0} mm2");
            }
        }

        /// <summary>
        /// The section the rest of the report exists for: every beam end measured against everything
        /// around it, and every opening measured against the faces it might have been cut parallel to.
        /// </summary>
        private static void Joint(StringBuilder report, IList<Part> beams, IList<Part> columns, IList<Part> openings)
        {
            foreach (var beam in beams.Where(beam => beam.Axis != null))
            {
                foreach (var end in new[] { 0, 1 })
                {
                    var origin = beam.ProbeOriginAt(end);
                    var outward = beam.OutwardAt(end);

                    report.AppendLine();
                    report.AppendLine($"BEAM {beam.Element.Id.ToLong()} END {end} - solid stops at " +
                                      $"{Mm(beam.PointAt(end))}, running out {Vec(outward)}");
                    report.AppendLine($"  measured at height {beam.ProbeZ.FeetToMm():0.#}");

                    foreach (var other in columns.Concat(beams).Concat(openings)
                                 .Where(other => other.Element.Id != beam.Element.Id))
                    {
                        AgainstFaces(report, origin, outward, beam.Direction, other);
                    }
                }
            }

            foreach (var opening in openings)
            {
                CutPlanes(report, opening, beams, columns);
            }
        }

        /// <summary>Where one end of a beam stands relative to the upright faces of another part.</summary>
        private static void AgainstFaces(StringBuilder report, XYZ origin, XYZ outward, XYZ axis, Part other)
        {
            var lines = new List<Tuple<double, string>>();

            foreach (var face in other.Uprights)
            {
                var denominator = outward.DotProduct(face.Normal);
                if (Math.Abs(denominator) < 1e-9)
                {
                    continue;
                }

                var along = ((face.Point - origin).DotProduct(face.Normal) / denominator).FeetToMm();
                if (Math.Abs(along) > ReachMm)
                {
                    continue;
                }

                lines.Add(Tuple.Create(along, $"    normal {Vec(face.Normal)}   {along:+0.#;-0.#;0} along the beam   " +
                                              $"{Off(axis, face.Normal):0.##} deg off square"));
            }

            if (lines.Count == 0)
            {
                return;
            }

            report.AppendLine($"  vs {Kind(other.Element)} {other.Element.Id.ToLong()}:");
            foreach (var line in lines.OrderBy(item => item.Item1).Take(MaxFaces))
            {
                report.AppendLine(line.Item2);
            }
        }

        /// <summary>Each vertical plane an opening cuts on, against the faces it could be parallel to.</summary>
        private static void CutPlanes(StringBuilder report, Part opening, IList<Part> beams, IList<Part> columns)
        {
            var element = (Opening)opening.Element;
            var edges = Edges(element).ToList();
            if (edges.Count == 0)
            {
                return;
            }

            var hostId = element.Host?.Id;

            foreach (var edge in edges)
            {
                var normal = XYZ.BasisZ.CrossProduct(edge.Direction);
                if (normal.IsZeroLength())
                {
                    continue;
                }

                normal = normal.Normalize();

                report.AppendLine();
                report.AppendLine($"OPENING {element.Id.ToLong()} cuts on the plane through {Mm(edge.Middle)} " +
                                  $"with normal {Vec(normal)}");

                foreach (var other in columns.Concat(beams).Where(part => part.Element.Id != hostId))
                {
                    PlaneAgainst(report, normal, edge.Middle, other);
                }
            }
        }

        private static void PlaneAgainst(StringBuilder report, XYZ normal, XYZ point, Part other)
        {
            var lines = new List<Tuple<double, string>>();

            foreach (var face in other.Uprights)
            {
                var angle = Off90(normal, face.Normal);
                var distance = (face.Point - point).DotProduct(normal).FeetToMm();

                if (Math.Abs(distance) > ReachMm)
                {
                    continue;
                }

                var note = angle <= ParallelDegrees ? "parallel" : $"{angle:0.##} deg apart";
                lines.Add(Tuple.Create(angle, $"    face normal {Vec(face.Normal)}   {note}   " +
                                              $"the face is {distance:+0.#;-0.#;0} along the cut normal"));
            }

            if (other.Centre != null)
            {
                var toCentre = (other.Centre - point).DotProduct(normal).FeetToMm();
                if (Math.Abs(toCentre) <= ReachMm)
                {
                    lines.Add(Tuple.Create(double.MaxValue,
                        $"    centre of the part is {toCentre:+0.#;-0.#;0} along the cut normal"));
                }
            }

            if (lines.Count == 0)
            {
                return;
            }

            report.AppendLine($"  vs {Kind(other.Element)} {other.Element.Id.ToLong()}:");
            foreach (var line in lines.OrderBy(item => item.Item1).Take(MaxFaces))
            {
                report.AppendLine(line.Item2);
            }
        }

        /// <summary>How two elements sit against each other. Only used when nothing was recognised.</summary>
        private static void Compare(StringBuilder report, Element a, Element b)
        {
            var axisA = a.GetLocationLine();
            var axisB = b.GetLocationLine();

            if (axisA != null && axisB != null)
            {
                report.AppendLine($"angle between the two axes: {Angle(axisA.Direction, axisB.Direction):0.##} deg");
            }

            var facesA = PlanarFaces(a).Take(MaxPairLines).ToList();
            var facesB = PlanarFaces(b).Take(MaxPairLines).ToList();

            report.AppendLine();
            report.AppendLine("parallel faces and the gap between their planes:");

            var lines = 0;
            foreach (var faceA in facesA)
            {
                foreach (var faceB in facesB)
                {
                    if (lines >= MaxPairLines)
                    {
                        break;
                    }

                    if (Off90(faceA.FaceNormal, faceB.FaceNormal) > ParallelDegrees)
                    {
                        continue;
                    }

                    var distance = (faceB.Origin - faceA.Origin).DotProduct(faceA.FaceNormal).FeetToMm();
                    if (Math.Abs(distance) > ReachMm)
                    {
                        continue;
                    }

                    lines++;
                    report.AppendLine($"  A normal {Vec(faceA.FaceNormal)} at {Mm(faceA.Origin)}");
                    report.AppendLine($"  B normal {Vec(faceB.FaceNormal)} at {Mm(faceB.Origin)}" +
                                      $"   ->  {distance:+0.#;-0.#;0} along A's normal");
                }
            }

            if (lines == 0)
            {
                report.AppendLine("  (none within " + ReachMm + " mm)");
            }
        }

        private static IEnumerable<PlanarFace> PlanarFaces(Element element)
        {
            return element
                .GetSolids()
                .SelectMany(solid => solid.Faces.Cast<Face>())
                .OfType<PlanarFace>()
                .OrderByDescending(face => face.Area);
        }

        private static IEnumerable<Edge2> Edges(Opening opening)
        {
            CurveArray boundary;
            try
            {
                boundary = opening.BoundaryCurves;
            }
            catch
            {
                yield break;
            }

            if (boundary == null)
            {
                yield break;
            }

            foreach (Curve curve in boundary)
            {
                var start = curve.GetEndPoint(0);
                var finish = curve.GetEndPoint(1);
                if (start.DistanceTo(finish) < GeometryExtensions.Tolerance)
                {
                    continue;
                }

                yield return new Edge2(start, finish);
            }
        }

        private static XYZ Average(IEnumerable<XYZ> points)
        {
            var list = points.ToList();
            if (list.Count == 0)
            {
                return null;
            }

            return list.Aggregate(XYZ.Zero, (sum, point) => sum + point) / list.Count;
        }

        /// <summary>Angle between two directions, 0 to 180.</summary>
        private static double Angle(XYZ first, XYZ second)
        {
            return first.AngleTo(second).RadiansToDegrees();
        }

        /// <summary>How far two directions are from being parallel, ignoring which way each points.</summary>
        private static double Off90(XYZ first, XYZ second)
        {
            return Math.Min(Angle(first, second), Angle(first, -second));
        }

        /// <summary>How far a face is from standing square across a direction.</summary>
        private static double Off(XYZ direction, XYZ faceNormal)
        {
            return Off90(direction, faceNormal);
        }

        private static string Length(Element element, BuiltInParameter parameter)
        {
            return $"{element.GetLength(parameter).FeetToMm():0.#}";
        }

        private static string JoinAllowed(FamilyInstance beam, int end)
        {
            return Safely(() => StructuralFramingUtils.IsJoinAllowedAtEnd(beam, end).ToString());
        }

        private static string Safely(Func<string> value)
        {
            try
            {
                return value();
            }
            catch (Exception ex)
            {
                return "(" + ex.GetType().Name + ")";
            }
        }

        private static string Kind(Element element)
        {
            return element is Opening ? "OPENING" : element.Category?.Name?.ToUpperInvariant() ?? "ELEMENT";
        }

        private enum Role
        {
            Beam,
            Column,
            Opening,
            Other,
        }

        /// <summary>One boundary edge of an opening, kept as plain points.</summary>
        private class Edge2
        {
            public Edge2(XYZ start, XYZ finish)
            {
                Start = start;
                Finish = finish;
                Direction = (finish - start).Normalize();
                Middle = (start + finish) / 2;
                LengthMm = start.DistanceTo(finish).FeetToMm();
            }

            public XYZ Start { get; }

            public XYZ Finish { get; }

            public XYZ Direction { get; }

            public XYZ Middle { get; }

            public double LengthMm { get; }
        }

        /// <summary>An upright face reduced to the plane it lies on.</summary>
        private class FacePlane
        {
            public XYZ Normal { get; set; }

            public XYZ Point { get; set; }

            public double AreaMm2 { get; set; }

            public double Offset => Point.DotProduct(Normal);
        }

        /// <summary>
        /// A selected element read once: the part it plays, the planes of its upright faces, and - for
        /// anything on a location line - where its solid really stops at each end.
        /// </summary>
        private class Part
        {
            public Element Element { get; private set; }

            public Role Role { get; private set; }

            public List<FacePlane> Uprights { get; private set; }

            public Line Axis { get; private set; }

            public XYZ AxisStart { get; private set; }

            public XYZ AxisFinish { get; private set; }

            public XYZ Direction { get; private set; }

            public XYZ Centre { get; private set; }

            public double TopZ { get; private set; }

            public double BottomZ { get; private set; }

            public double ProbeZ { get; private set; }

            public double WidthMm { get; private set; }

            public double LengthMm => (EndExtent - StartExtent).FeetToMm();

            private double StartExtent { get; set; }

            private double EndExtent { get; set; }

            public static Part Create(Element element)
            {
                if (element == null)
                {
                    return null;
                }

                var part = new Part { Element = element, Role = RoleOf(element) };
                var solids = element.GetSolids().ToList();

                part.Uprights = Planes(solids);

                var vertices = solids.SelectMany(solid => solid.GetVertices()).ToList();
                if (vertices.Count > 0)
                {
                    part.TopZ = vertices.Max(point => point.Z);
                    part.BottomZ = vertices.Min(point => point.Z);
                    part.Centre = new XYZ(
                        (vertices.Min(point => point.X) + vertices.Max(point => point.X)) / 2,
                        (vertices.Min(point => point.Y) + vertices.Max(point => point.Y)) / 2,
                        (part.TopZ + part.BottomZ) / 2);
                }

                // Faces are probed just below the top of the beam: a ray in the plane of a neighbour's
                // top face slides along it and reports nothing.
                part.ProbeZ = part.TopZ - Math.Min(50d.MmToFeet(), (part.TopZ - part.BottomZ) / 2);

                var axis = element.GetLocationLine();
                if (axis != null && solids.Count > 0)
                {
                    part.Axis = axis;
                    part.AxisStart = axis.GetEndPoint(0);
                    part.AxisFinish = axis.GetEndPoint(1);
                    part.Direction = axis.Direction;

                    var extent = solids.ExtentAlong(part.AxisStart, part.Direction);
                    part.StartExtent = extent.Min;
                    part.EndExtent = extent.Max;

                    var across = XYZ.BasisZ.CrossProduct(part.Direction);
                    part.WidthMm = across.IsZeroLength()
                        ? 0
                        : Spread(vertices.Select(point => point.DotProduct(across.Normalize()))).FeetToMm();
                }

                return part;
            }

            public XYZ PointAt(int end)
            {
                return AxisStart + Direction * (end == 0 ? StartExtent : EndExtent);
            }

            public XYZ OutwardAt(int end)
            {
                return end == 0 ? -Direction : Direction;
            }

            public XYZ ProbeOriginAt(int end)
            {
                var point = PointAt(end);
                return new XYZ(point.X, point.Y, ProbeZ);
            }

            public int NearestEnd(XYZ point)
            {
                return point.DistanceTo(PointAt(0)) <= point.DistanceTo(PointAt(1)) ? 0 : 1;
            }

            private static Role RoleOf(Element element)
            {
                if (element is Opening)
                {
                    return Role.Opening;
                }

                var category = (BuiltInCategory)(element.Category?.Id.ToLong() ?? 0);
                switch (category)
                {
                    case BuiltInCategory.OST_StructuralColumns:
                    case BuiltInCategory.OST_Columns:
                        return Role.Column;

                    case BuiltInCategory.OST_StructuralFraming:
                        return element is FamilyInstance ? Role.Beam : Role.Other;

                    default:
                        return Role.Other;
                }
            }

            /// <summary>
            /// The upright faces reduced to distinct planes. A solid repeats the same plane across
            /// several faces, and every repeat would be another line saying the same thing.
            /// </summary>
            private static List<FacePlane> Planes(IEnumerable<Solid> solids)
            {
                var planes = new List<FacePlane>();

                var faces = solids
                    .SelectMany(solid => solid.Faces.Cast<Face>())
                    .OfType<PlanarFace>()
                    .Where(face => Math.Abs(face.FaceNormal.Z) < UprightTolerance)
                    .OrderByDescending(face => face.Area);

                foreach (var face in faces)
                {
                    var plane = new FacePlane
                    {
                        Normal = face.FaceNormal,
                        Point = face.Origin,
                        AreaMm2 = face.Area.FeetToMm().FeetToMm(),
                    };

                    var duplicate = planes.Any(existing =>
                        Angle(existing.Normal, plane.Normal) < 1 &&
                        Math.Abs(existing.Offset - plane.Offset).FeetToMm() < 1);

                    if (!duplicate)
                    {
                        planes.Add(plane);
                    }
                }

                return planes;
            }

            private static double Spread(IEnumerable<double> values)
            {
                var list = values.ToList();
                return list.Count == 0 ? 0 : list.Max() - list.Min();
            }
        }
    }
}
