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
    /// Measures how beam ends sit against the things that support them. Run on the model that was
    /// already adjusted by the reference tool: the numbers it prints - distance from the beam end to
    /// the near face, the far face and the centre of everything around it - are what the four clearance
    /// rules of the Adjust Beam tool have to reproduce.
    /// Beyond the beams the user picked, the report auto-scans the whole document for the two junctions
    /// that are hard to find by hand: a beam meeting a column, and a beam meeting a perpendicular beam.
    /// Nothing is filtered by category: a support only has to be solid geometry the beam axis runs into,
    /// so a column modelled in an unexpected category still shows up.
    /// Read-only.
    /// </summary>
    internal static class AdjustBeamProbeReport
    {
        /// <summary>How far around a beam end supports are looked for.</summary>
        private const double SearchRadiusMm = 1500;

        /// <summary>Half length of the ray shot along the beam axis when intersecting a support.</summary>
        private const double RayLengthMm = 2000;

        /// <summary>Neighbours the axis misses are only reported when they are this close to the end.</summary>
        private const double NearMissMm = 500;

        /// <summary>Two axes count as parallel or perpendicular within this angle.</summary>
        private const double AngleToleranceDegrees = 2;

        /// <summary>Parallel axes count as collinear below this distance between them.</summary>
        private const double CollinearOffsetMm = 5;

        /// <summary>Neighbours listed per beam end, nearest first.</summary>
        private const int MaxNeighbours = 8;

        /// <summary>Elements whose solid is read per beam end. Keeps the probe responsive.</summary>
        private const int MaxCandidates = 60;

        /// <summary>Junctions reported by each auto-scan.</summary>
        private const int MaxJunctions = 12;

        /// <summary>A beam end this close to a column counts as sitting on it.</summary>
        private const double ColumnSearchMm = 1500;

        /// <summary>A beam end this close to another beam's axis counts as a junction.</summary>
        private const double JunctionSearchMm = 800;

        /// <summary>Angle window that counts as a perpendicular junction.</summary>
        private const double PerpendicularWindowDegrees = 5;

        /// <summary>Categories that are numerous and can never support a beam.</summary>
        private static readonly HashSet<long> IgnoredCategories = new HashSet<long>
        {
            (long)BuiltInCategory.OST_Rebar,
            (long)BuiltInCategory.OST_AreaRein,
            (long)BuiltInCategory.OST_PathRein,
            (long)BuiltInCategory.OST_FabricAreas,
            (long)BuiltInCategory.OST_FabricReinforcement,
            (long)BuiltInCategory.OST_Topography,
        };

        public static string Build(Document document, IList<FamilyInstance> beams, string scope)
        {
            var report = new StringBuilder();

            Section(report, "SD REVIT TEST - ADJUST BEAM PROBE", () =>
            {
                report.AppendLine($"Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                report.AppendLine($"Document  : {document.Title}");
                report.AppendLine($"Scope     : {scope}");
                report.AppendLine($"Beams     : {beams.Count}");
                report.AppendLine();
                report.AppendLine("All lengths are mm. For each beam end the axis is shot outwards through the");
                report.AppendLine("neighbouring geometry, so a neighbour reads as:");
                report.AppendLine("  near +a / far +b   - it starts a mm past the beam end and ends b mm past it;");
                report.AppendLine("                       a negative value means the beam already reaches that far.");
                report.AppendLine("A beam left with an air gap therefore shows near = +clearance, while a beam");
                report.AppendLine("trimmed inside its support shows far = +clearance and a negative near.");
            });

            Section(report, "MODEL INVENTORY", () => DumpInventory(report, document));
            Section(report, "COLUMN JUNCTIONS (auto-scan)", () => DumpColumnJunctions(report, document));
            Section(report, "PERPENDICULAR JUNCTIONS (auto-scan)", () => DumpPerpendicularJunctions(report, document));

            for (var index = 0; index < beams.Count; index++)
            {
                var beam = beams[index];
                Section(report, $"BEAM {index + 1} OF {beams.Count}  -  id {beam.Id.ToLong()}",
                    () => DumpBeam(report, document, beam));
            }

            return report.ToString();
        }

        /// <summary>
        /// What the model is actually made of. Structural Framing holds far more than beams - recesses
        /// and skirts live there too - so the structural type of every family is listed as well: that is
        /// what the real tool will filter on.
        /// </summary>
        private static void DumpInventory(StringBuilder report, Document document)
        {
            var instances = new FilteredElementCollector(document)
                .WhereElementIsNotElementType()
                .Where(element => element.Category != null && element.Category.CategoryType == CategoryType.Model)
                .ToList();

            report.AppendLine("Model instances per category:");
            foreach (var group in instances
                .GroupBy(element => element.Category.Name)
                .OrderByDescending(group => group.Count())
                .Take(40))
            {
                report.AppendLine($"  {Trim(group.Key, 44),-44} {group.Count(),6}");
            }

            report.AppendLine();
            report.AppendLine("Family, type and structural type of everything that could carry a beam:");

            var carriers = new[]
            {
                BuiltInCategory.OST_Walls,
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_Columns,
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_StructuralFoundation,
                BuiltInCategory.OST_GenericModel,
            }.Select(category => (long)category).ToList();

            foreach (var group in instances
                .Where(element => carriers.Contains(element.Category.Id.ToLong()))
                .GroupBy(element => $"{element.Category.Name} | {FamilyNameOf(element)} | " +
                                    $"{element.Document.GetElement(element.GetTypeId())?.Name} | " +
                                    $"{StructuralTypeOf(element)}")
                .OrderBy(group => group.Key))
            {
                report.AppendLine($"  {Trim(group.Key, 96),-96} {group.Count(),6}");
            }
        }

        /// <summary>
        /// Finds every column that has a beam end near it and reports those ends. This is what settles
        /// the "beam to pillar" clearance and the inline gap taken from the column centre.
        /// </summary>
        private static void DumpColumnJunctions(StringBuilder report, Document document)
        {
            var beams = BeamIndex(document);
            var columns = new FilteredElementCollector(document)
                .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory>
                {
                    BuiltInCategory.OST_StructuralColumns,
                    BuiltInCategory.OST_Columns,
                }))
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .ToList();

            report.AppendLine($"Columns in the model: {columns.Count}   beams with a straight axis: {beams.Count}");
            report.AppendLine($"Listing the first {MaxJunctions} columns that have a beam end within {ColumnSearchMm} mm.");

            var radius = ColumnSearchMm.MmToFeet();
            var shown = 0;

            foreach (var column in columns)
            {
                if (shown >= MaxJunctions)
                {
                    break;
                }

                var centre = column.GetLocationPoint();
                if (centre == null)
                {
                    continue;
                }

                var nearby = beams
                    .Where(beam => beam.EndPoints.Any(point => point.ToXY().DistanceTo(centre.ToXY()) < radius))
                    .ToList();

                if (nearby.Count == 0)
                {
                    continue;
                }

                shown++;
                report.AppendLine();
                report.AppendLine($"--- COLUMN {shown}: {DescribeElement(column)}");
                report.AppendLine($"    centre  : {Mm(centre)}   bbox {BoxSize(column.get_BoundingBox(null))}");
                report.AppendLine($"    beams   : {nearby.Count}");

                foreach (var beam in nearby)
                {
                    foreach (var end in EndsOf(beam.Element)
                        .Where(end => end.Point.ToXY().DistanceTo(centre.ToXY()) < radius))
                    {
                        report.AppendLine();
                        report.AppendLine($"    beam {DescribeElement(beam.Element)}");
                        report.AppendLine($"        column centre is {CentreDistances(end, centre)}");
                        DumpEnd(report, document, beam.Element, end);
                    }
                }
            }

            if (shown == 0)
            {
                report.AppendLine();
                report.AppendLine("No column in this model has a beam end near it.");
            }
        }

        /// <summary>Finds beam ends that stop against a beam running at right angles to them.</summary>
        private static void DumpPerpendicularJunctions(StringBuilder report, Document document)
        {
            var beams = BeamIndex(document);
            var reach = JunctionSearchMm.MmToFeet();
            var shown = 0;

            report.AppendLine($"Listing the first {MaxJunctions} beam ends that stop within {JunctionSearchMm} mm " +
                              $"of a beam at 90 +/- {PerpendicularWindowDegrees} deg.");

            foreach (var beam in beams)
            {
                if (shown >= MaxJunctions)
                {
                    break;
                }

                for (var index = 0; index < beam.EndPoints.Count && shown < MaxJunctions; index++)
                {
                    var endPoint = beam.EndPoints[index];

                    var crossing = beams
                        .Where(other => other.Element.Id != beam.Element.Id)
                        .Where(other => Math.Abs(AngleBetween(beam.Line, other.Line) - 90) < PerpendicularWindowDegrees)
                        .Where(other => DistanceToSegment(endPoint, other.Line) < reach)
                        .ToList();

                    if (crossing.Count == 0)
                    {
                        continue;
                    }

                    var ends = EndsOf(beam.Element);
                    if (index >= ends.Count)
                    {
                        continue;
                    }

                    shown++;
                    report.AppendLine();
                    report.AppendLine($"--- JUNCTION {shown}: {DescribeElement(beam.Element)}");
                    foreach (var other in crossing)
                    {
                        report.AppendLine($"    crosses {DescribeElement(other.Element)}   " +
                                          $"axis distance {DistanceToSegment(endPoint, other.Line).FeetToMm():0.#}");
                    }

                    DumpEnd(report, document, beam.Element, ends[index]);
                }
            }

            if (shown == 0)
            {
                report.AppendLine();
                report.AppendLine("No beam end stops near a perpendicular beam in this model.");
            }
        }

        private static void DumpBeam(StringBuilder report, Document document, FamilyInstance beam)
        {
            report.AppendLine($"element   : {DescribeElement(beam)}");
            report.AppendLine($"level     : \"{LevelNameOf(document, beam)}\"   mark: \"{beam.GetString(BuiltInParameter.ALL_MODEL_MARK)}\"");

            var line = beam.GetLocationLine();
            if (line == null)
            {
                report.AppendLine("location  : (not a straight location line - skipped)");
                return;
            }

            var direction = line.Direction;
            report.AppendLine($"location  : {Mm(line.GetEndPoint(0))} -> {Mm(line.GetEndPoint(1))}");
            report.AppendLine($"axis      : {Vec(direction)}   bearing {Bearing(direction):0.##} deg   length {line.Length.FeetToMm():0.#}");
            report.AppendLine($"extension : start {beam.GetLength(BuiltInParameter.START_EXTENSION).FeetToMm():0.#}" +
                              $"   end {beam.GetLength(BuiltInParameter.END_EXTENSION).FeetToMm():0.#}");
            report.AppendLine($"join      : allowed at start {JoinAllowed(beam, 0)}   at end {JoinAllowed(beam, 1)}");

            var solids = GetSolids(beam).ToList();
            if (solids.Count == 0)
            {
                report.AppendLine("geometry  : (no solid)");
                return;
            }

            var extent = ExtentAlong(solids, line.GetEndPoint(0), direction);
            var section = CrossSection(solids, direction);
            report.AppendLine($"geometry  : along the axis from {extent.Min.FeetToMm():0.#} to {extent.Max.FeetToMm():0.#}, " +
                              "measured from the location start point");
            report.AppendLine($"section   : width {section.Width.FeetToMm():0.#}   depth {section.Depth.FeetToMm():0.#}");

            foreach (var end in EndsOf(beam, line, solids))
            {
                DumpEnd(report, document, beam, end);
            }
        }

        /// <summary>
        /// Reports every neighbour around one beam end. The end point is where the beam geometry
        /// actually stops and the outward direction points away from the beam, so all distances read
        /// as "this far past the end of the beam".
        /// </summary>
        private static void DumpEnd(StringBuilder report, Document document, FamilyInstance beam, BeamEnd end)
        {
            report.AppendLine();
            report.AppendLine($"  END {end.Index}   geometry stops at {Mm(end.Point)}   outward {Vec(end.Outward)}");

            var ray = Ray(end.Point, end.Outward);
            var neighbours = FindNeighbours(document, beam, end.Point)
                .Select(neighbour => Measure(beam, neighbour, end.Point, end.Outward, ray))
                .Where(measured => measured != null)
                .OrderBy(measured => measured.Hit ? Math.Abs(measured.Near) : NearMissMm + measured.ClearDistance)
                .Take(MaxNeighbours)
                .ToList();

            if (neighbours.Count == 0)
            {
                report.AppendLine($"    (nothing found within {SearchRadiusMm} mm)");
                return;
            }

            foreach (var neighbour in neighbours)
            {
                report.AppendLine($"    [{neighbour.Kind}] {DescribeElement(neighbour.Element)}");

                if (neighbour.Hit)
                {
                    report.AppendLine($"        axis hit  : near {neighbour.Near:+0.#;-0.#;0}   far {neighbour.Far:+0.#;-0.#;0}" +
                                      $"   thickness {neighbour.Far - neighbour.Near:0.#}");
                }
                else
                {
                    report.AppendLine($"        axis miss : nearest solid is {neighbour.ClearDistance:0.#} away from the beam end");
                }

                if (neighbour.AngleDegrees.HasValue)
                {
                    report.AppendLine($"        angle     : {neighbour.AngleDegrees.Value:0.##} deg between the two axes");
                }

                if (neighbour.CentreAlongAxis.HasValue)
                {
                    report.AppendLine($"        centre    : {neighbour.CentreAlongAxis.Value:+0.#;-0.#;0} along the axis" +
                                      $"   {neighbour.CentreAcrossAxis.Value:+0.#;-0.#;0} across it");
                }

                if (neighbour.SideOffset.HasValue)
                {
                    report.AppendLine($"        side gap  : {neighbour.SideOffset.Value:0.#} between the two axes " +
                                      "(perpendicular to this beam)");
                }

                if (neighbour.EndGap.HasValue)
                {
                    report.AppendLine($"        end gap   : {neighbour.EndGap.Value:0.#} between the two geometries " +
                                      "measured along this axis");
                }
            }
        }

        /// <summary>Beams with a straight axis, indexed once so the auto-scans stay cheap.</summary>
        private static List<BeamRecord> BeamIndex(Document document)
        {
            return new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(beam => beam.StructuralType == StructuralType.Beam)
                .Select(beam => new { beam, line = beam.GetLocationLine() })
                .Where(item => item.line != null)
                .Select(item => new BeamRecord(item.beam, item.line))
                .ToList();
        }

        /// <summary>The two ends of a beam, at the point where its geometry really stops.</summary>
        private static IList<BeamEnd> EndsOf(FamilyInstance beam)
        {
            var line = beam.GetLocationLine();
            if (line == null)
            {
                return new List<BeamEnd>();
            }

            var solids = GetSolids(beam).ToList();
            return solids.Count == 0 ? new List<BeamEnd>() : EndsOf(beam, line, solids);
        }

        private static IList<BeamEnd> EndsOf(FamilyInstance beam, Line line, IList<Solid> solids)
        {
            var direction = line.Direction;
            var origin = line.GetEndPoint(0);
            var extent = ExtentAlong(solids, origin, direction);

            // End 0 points backwards along the axis, end 1 forwards.
            return new List<BeamEnd>
            {
                new BeamEnd(0, origin + direction * extent.Min, -direction),
                new BeamEnd(1, origin + direction * extent.Max, direction),
            };
        }

        /// <summary>Where a point sits relative to a beam end, along the axis and across it.</summary>
        private static string CentreDistances(BeamEnd end, XYZ point)
        {
            var offset = point - end.Point;
            var across = XYZ.BasisZ.CrossProduct(end.Outward);
            var sideways = across.IsZeroLength() ? 0 : offset.DotProduct(across.Normalize()).FeetToMm();
            return $"{offset.DotProduct(end.Outward).FeetToMm():+0.#;-0.#;0} along the axis, {sideways:+0.#;-0.#;0} across it";
        }

        /// <summary>
        /// Model elements whose bounding box is near the beam end, whatever their category. Reinforcement
        /// is dropped and the list is capped: reading the solid of every rebar around a beam end would
        /// cost minutes and none of it can support anything.
        /// </summary>
        private static IEnumerable<Element> FindNeighbours(Document document, FamilyInstance beam, XYZ endPoint)
        {
            var radius = SearchRadiusMm.MmToFeet();
            var offset = new XYZ(radius, radius, radius);
            var outline = new Outline(endPoint - offset, endPoint + offset);

            return new FilteredElementCollector(document)
                .WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .Where(element => element.Id != beam.Id)
                .Where(element => element.Category != null && element.Category.CategoryType == CategoryType.Model)
                .Where(element => !IgnoredCategories.Contains(element.Category.Id.ToLong()))
                .OrderBy(element => DistanceToBoundingBox(element, endPoint))
                .Take(MaxCandidates);
        }

        /// <summary>Rough distance from a point to an element, used only to rank candidates.</summary>
        private static double DistanceToBoundingBox(Element element, XYZ point)
        {
            var box = element.get_BoundingBox(null);
            return box == null ? double.MaxValue : ((box.Min + box.Max) / 2 - point).GetLength();
        }

        /// <summary>
        /// Where a single neighbour sits relative to the beam end. Returns null when the beam axis
        /// misses it and it is too far away to matter.
        /// </summary>
        private static SupportMeasurement Measure(
            FamilyInstance beam,
            Element neighbour,
            XYZ endPoint,
            XYZ outward,
            Line ray)
        {
            var solids = GetSolids(neighbour).ToList();
            if (solids.Count == 0)
            {
                return null;
            }

            var hits = solids.SelectMany(solid => Intersect(solid, ray, endPoint, outward)).ToList();
            var clearDistance = hits.Count > 0 ? 0 : DistanceToSolids(solids, endPoint);

            if (hits.Count == 0 && (!clearDistance.HasValue || clearDistance.Value > NearMissMm))
            {
                return null;
            }

            var beamAxis = beam.GetLocationLine();
            var neighbourAxis = neighbour.GetLocationLine();

            var measurement = new SupportMeasurement
            {
                Element = neighbour,
                Hit = hits.Count > 0,
                Near = hits.Count == 0 ? 0 : hits.Min(hit => hit.Near).FeetToMm(),
                Far = hits.Count == 0 ? 0 : hits.Max(hit => hit.Far).FeetToMm(),
                ClearDistance = clearDistance ?? 0,
                Kind = Classify(neighbour, beamAxis, neighbourAxis),
            };

            if (beamAxis != null && neighbourAxis != null)
            {
                measurement.AngleDegrees = AngleBetween(beamAxis, neighbourAxis);
                measurement.SideOffset = PerpendicularDistance(beamAxis, neighbourAxis).FeetToMm();
            }

            var centre = neighbour.GetLocationPoint();
            if (centre != null)
            {
                var offset = centre - endPoint;
                var across = XYZ.BasisZ.CrossProduct(outward);
                measurement.CentreAlongAxis = offset.DotProduct(outward).FeetToMm();
                measurement.CentreAcrossAxis = across.IsZeroLength()
                    ? 0
                    : offset.DotProduct(across.Normalize()).FeetToMm();
            }

            measurement.EndGap = GapToNearestGeometry(solids, endPoint, outward);
            return measurement;
        }

        /// <summary>What the neighbour is, and how its axis relates to the beam's.</summary>
        private static string Classify(Element neighbour, Line beamAxis, Line neighbourAxis)
        {
            if (neighbour is Wall)
            {
                return "WALL";
            }

            var category = neighbour.Category.Id.ToLong();
            if (category == (long)BuiltInCategory.OST_StructuralColumns || category == (long)BuiltInCategory.OST_Columns)
            {
                return "COLUMN";
            }

            if (category != (long)BuiltInCategory.OST_StructuralFraming)
            {
                return Trim(neighbour.Category.Name.ToUpperInvariant(), 24);
            }

            if (beamAxis == null || neighbourAxis == null)
            {
                return "BEAM";
            }

            var angle = AngleBetween(beamAxis, neighbourAxis);

            if (angle < AngleToleranceDegrees)
            {
                return PerpendicularDistance(beamAxis, neighbourAxis).FeetToMm() < CollinearOffsetMm
                    ? "BEAM inline"
                    : "BEAM parallel";
            }

            return Math.Abs(angle - 90) < AngleToleranceDegrees ? "BEAM perpendicular" : "BEAM skew";
        }

        /// <summary>Angle between two axes, folded into 0-90 degrees so direction does not matter.</summary>
        private static double AngleBetween(Line first, Line second)
        {
            var angle = first.Direction.AngleTo(second.Direction).RadiansToDegrees();
            return angle > 90 ? 180 - angle : angle;
        }

        /// <summary>Distance from a point to a line segment, measured on the horizontal plane.</summary>
        private static double DistanceToSegment(XYZ point, Line line)
        {
            var start = line.GetEndPoint(0).ToXY();
            var span = line.GetEndPoint(1).ToXY() - start;
            var lengthSquared = span.DotProduct(span);

            if (lengthSquared < GeometryExtensions.Tolerance)
            {
                return point.ToXY().DistanceTo(start);
            }

            var position = Math.Max(0, Math.Min(1, (point.ToXY() - start).DotProduct(span) / lengthSquared));
            return point.ToXY().DistanceTo(start + span * position);
        }

        /// <summary>Distance from the beam end to the closest point of the neighbour, along the axis.</summary>
        private static double? GapToNearestGeometry(IEnumerable<Solid> solids, XYZ endPoint, XYZ outward)
        {
            var distances = solids
                .SelectMany(Vertices)
                .Select(point => point.Subtract(endPoint).DotProduct(outward))
                .Where(distance => distance > 0)
                .ToList();

            return distances.Count == 0 ? (double?)null : distances.Min().FeetToMm();
        }

        /// <summary>
        /// Shortest distance from a point to the solids, in mm. Face.Project only answers when the
        /// point falls within the face boundary, so the tessellated edges are measured as well - without
        /// them a beam end just past the corner of its neighbour reads as "nothing there".
        /// </summary>
        private static double? DistanceToSolids(IEnumerable<Solid> solids, XYZ point)
        {
            var best = double.MaxValue;

            foreach (var solid in solids)
            {
                foreach (var face in solid.Faces.Cast<Face>())
                {
                    try
                    {
                        var projection = face.Project(point);
                        if (projection != null)
                        {
                            best = Math.Min(best, projection.Distance);
                        }
                    }
                    catch
                    {
                        // a face that cannot be projected onto simply does not contribute
                    }
                }

                foreach (var vertex in Vertices(solid))
                {
                    best = Math.Min(best, vertex.DistanceTo(point));
                }
            }

            return best == double.MaxValue ? (double?)null : best.FeetToMm();
        }

        /// <summary>Shortest distance between two infinite lines, projected onto the horizontal plane.</summary>
        private static double PerpendicularDistance(Line first, Line second)
        {
            var origin = first.GetEndPoint(0).ToXY();
            var direction = first.Direction.ToXY();

            if (direction.IsZeroLength())
            {
                return 0;
            }

            direction = direction.Normalize();
            var offset = second.GetEndPoint(0).ToXY() - origin;
            return (offset - direction * offset.DotProduct(direction)).GetLength();
        }

        /// <summary>Segments of the ray that run inside the solid, as distances from the beam end.</summary>
        private static IEnumerable<(double Near, double Far)> Intersect(Solid solid, Line ray, XYZ endPoint, XYZ outward)
        {
            SolidCurveIntersection intersection;
            try
            {
                intersection = solid.IntersectWithCurve(ray, new SolidCurveIntersectionOptions());
            }
            catch
            {
                yield break;
            }

            if (intersection == null)
            {
                yield break;
            }

            for (var index = 0; index < intersection.SegmentCount; index++)
            {
                var segment = intersection.GetCurveSegment(index);
                var start = segment.GetEndPoint(0).Subtract(endPoint).DotProduct(outward);
                var finish = segment.GetEndPoint(1).Subtract(endPoint).DotProduct(outward);
                yield return (Math.Min(start, finish), Math.Max(start, finish));
            }
        }

        private static Line Ray(XYZ endPoint, XYZ outward)
        {
            var half = RayLengthMm.MmToFeet();
            return Line.CreateBound(endPoint - outward * half, endPoint + outward * half);
        }

        /// <summary>Compass bearing of a direction, so parallel beams are easy to spot in the log.</summary>
        private static double Bearing(XYZ direction)
        {
            return Math.Atan2(direction.Y, direction.X).RadiansToDegrees();
        }

        /// <summary>How far the geometry reaches along a direction, relative to an origin point.</summary>
        private static (double Min, double Max) ExtentAlong(IEnumerable<Solid> solids, XYZ origin, XYZ direction)
        {
            var distances = solids
                .SelectMany(Vertices)
                .Select(point => point.Subtract(origin).DotProduct(direction))
                .ToList();

            return distances.Count == 0 ? (0, 0) : (distances.Min(), distances.Max());
        }

        /// <summary>Cross section of the beam: width across the axis horizontally, depth vertically.</summary>
        private static (double Width, double Depth) CrossSection(IEnumerable<Solid> solids, XYZ direction)
        {
            var across = XYZ.BasisZ.CrossProduct(direction);
            if (across.IsZeroLength())
            {
                return (0, 0);
            }

            across = across.Normalize();
            var points = solids.SelectMany(Vertices).ToList();
            if (points.Count == 0)
            {
                return (0, 0);
            }

            var sideways = points.Select(point => point.DotProduct(across)).ToList();
            var vertical = points.Select(point => point.Z).ToList();
            return (sideways.Max() - sideways.Min(), vertical.Max() - vertical.Min());
        }

        private static IEnumerable<XYZ> Vertices(Solid solid)
        {
            foreach (Edge edge in solid.Edges)
            {
                foreach (var point in edge.Tessellate())
                {
                    yield return point;
                }
            }
        }

        /// <summary>Every solid of an element, including the ones nested in geometry instances.</summary>
        private static IEnumerable<Solid> GetSolids(Element element)
        {
            GeometryElement geometry;
            try
            {
                geometry = element.get_Geometry(new Options
                {
                    DetailLevel = ViewDetailLevel.Fine,
                    ComputeReferences = false,
                });
            }
            catch
            {
                return Enumerable.Empty<Solid>();
            }

            return geometry == null ? Enumerable.Empty<Solid>() : Flatten(geometry);
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

        /// <summary>Describe() plus the structural type, which is how beams are told from recesses.</summary>
        private static string DescribeElement(Element element)
        {
            return $"{Describe(element)} struct={StructuralTypeOf(element)}";
        }

        private static string StructuralTypeOf(Element element)
        {
            return element is FamilyInstance instance ? instance.StructuralType.ToString() : "-";
        }

        private static string JoinAllowed(FamilyInstance beam, int end)
        {
            try
            {
                return StructuralFramingUtils.IsJoinAllowedAtEnd(beam, end).ToString();
            }
            catch (Exception ex)
            {
                return "(" + ex.GetType().Name + ")";
            }
        }

        /// <summary>A beam and its axis, kept together so the auto-scans do not re-read the location.</summary>
        private class BeamRecord
        {
            public BeamRecord(FamilyInstance element, Line line)
            {
                Element = element;
                Line = line;
                EndPoints = new List<XYZ> { line.GetEndPoint(0), line.GetEndPoint(1) };
            }

            public FamilyInstance Element { get; }

            public Line Line { get; }

            public IList<XYZ> EndPoints { get; }
        }

        /// <summary>One end of a beam: where the geometry stops and which way is "away from the beam".</summary>
        private class BeamEnd
        {
            public BeamEnd(int index, XYZ point, XYZ outward)
            {
                Index = index;
                Point = point;
                Outward = outward;
            }

            public int Index { get; }

            public XYZ Point { get; }

            public XYZ Outward { get; }
        }

        private class SupportMeasurement
        {
            public Element Element { get; set; }

            public string Kind { get; set; }

            /// <summary>True when the beam axis actually runs into this neighbour.</summary>
            public bool Hit { get; set; }

            public double Near { get; set; }

            public double Far { get; set; }

            public double ClearDistance { get; set; }

            public double? AngleDegrees { get; set; }

            public double? CentreAlongAxis { get; set; }

            public double? CentreAcrossAxis { get; set; }

            public double? SideOffset { get; set; }

            public double? EndGap { get; set; }
        }
    }
}
