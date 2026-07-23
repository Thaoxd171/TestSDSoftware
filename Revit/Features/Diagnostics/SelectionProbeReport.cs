using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SDSoftware.RevitTest.Extensions;
using SDSoftware.RevitTest.Features.AdjustBeam.Models;
using SDSoftware.RevitTest.Features.AdjustBeam.Services;
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

        /// <summary>
        /// When the running assembly was built, and where it was loaded from. Revit holds an add-in
        /// for the whole session, so a report can easily be read as the answer of code that was
        /// rebuilt after the session began. This says outright which build answered.
        /// </summary>
        private static string BuildStamp()
        {
            try
            {
                var path = System.Reflection.Assembly.GetExecutingAssembly().Location;
                return string.IsNullOrEmpty(path)
                    ? "(unknown)"
                    : $"{System.IO.File.GetLastWriteTime(path):yyyy-MM-dd HH:mm:ss}   from {path}";
            }
            catch (Exception error)
            {
                return $"(unknown: {error.Message})";
            }
        }

        /// <summary>A face further away than this has nothing to do with the joint being looked at.</summary>
        private const double ReachMm = 2000;

        /// <summary>A face is upright when its normal is this close to horizontal.</summary>
        private const double UprightTolerance = 0.01;

        private const int MaxFaces = 12;

        private const int MaxPairLines = 20;

        /// <summary>
        /// What the tool asks of two ends before it treats them as parting from a shared point. Copied
        /// from the probe on purpose: the report is here to be compared against the tool's answer, and
        /// a reading taken under different terms could not be.
        /// </summary>
        private const double FacingAxisDegrees = 45;

        private const double FacingApartDegrees = 135;

        private const double FacingReachMm = 1000;

        /// <summary>How near the top of a column has to be to the underside of a beam to carry it.</summary>
        private const double BearingGapMm = 100;

        /// <summary>How far from an end an opening still counts as belonging to it.</summary>
        private const double OpeningReachMm = 1500;

        /// <summary>Below this the two solids are touching rather than clashing.</summary>
        private const double NegligibleVolume = 1000 / (304.8 * 304.8 * 304.8);

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
                report.AppendLine($"Built     : {BuildStamp()}");
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
                Section(report, "WHAT THE TOOL WOULD DECIDE", () => Decide(report, document, beams));
                Section(report, "WHAT EACH END BEARS ON", () => Bearing(report, beams, columns));
                Section(report, "END AGAINST END", () => EndPairs(report, beams, columns));
                Section(report, "WHERE THE SOLIDS CLASH", () => Clashes(report, beams));
                Section(report, "HOW THE PIECES SIT", () => Joint(report, beams, columns, openings, others));
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

            DumpFlanks(report, beam);
            DumpUprights(report, beam);
        }

        /// <summary>
        /// Where each flank of the beam steps out. A precast beam is widened at its ends into a bearing
        /// block, and the two numbers that follow decide most of what happens at a joint: clearances
        /// are taken from the web, and the block is what gets cut back.
        /// </summary>
        private static void DumpFlanks(StringBuilder report, Part beam)
        {
            report.AppendLine();
            report.AppendLine("section, measured out from the axis:");

            if (beam.Section == null)
            {
                report.AppendLine("  (not readable)");
                return;
            }

            foreach (var side in new[] { 1, -1 })
            {
                var web = beam.Section.Web(side);
                var flange = beam.Section.Flange(side);
                var name = side > 0 ? "left " : "right";

                if (!web.HasValue)
                {
                    report.AppendLine($"  {name}: (no face on this side)");
                    continue;
                }

                var step = (flange.Value - web.Value).FeetToMm();
                report.AppendLine($"  {name}: web at {web.Value.FeetToMm():0.#}   " +
                                  $"outermost at {flange.Value.FeetToMm():0.#}   " +
                                  (beam.Section.HasBlock(side)
                                      ? $"steps out {step:0.#}"
                                      : "runs straight"));

                var wide = beam.Widened(side).ToList();
                if (wide.Count == 0)
                {
                    continue;
                }

                var along = wide.Select(beam.Section.Along).ToList();
                report.AppendLine($"         widened along the beam from {along.Min().FeetToMm():0.#} " +
                                  $"to {along.Max().FeetToMm():0.#}, between heights " +
                                  $"{wide.Min(point => point.Z).FeetToMm():0.#} " +
                                  $"and {wide.Max(point => point.Z).FeetToMm():0.#}");
            }
        }

        /// <summary>
        /// What the tool itself would make of every end, run against the model as it stands.
        ///
        /// Every limit is listed with the place it says the end may go, not only the one that wins.
        /// Read against a model already built the right way, every end should come out at nought - so
        /// any other number is a rule that is wrong, and the list says which limit produced it. Working
        /// that out backwards from the one governing figure is what this section exists to stop.
        /// </summary>
        private static void Decide(StringBuilder report, Document document, IList<Part> beams)
        {
            var options = new AdjustBeamOptions();
            var probe = new SupportProbe(document);

            report.AppendLine($"Clearances: wall {options.WallClearanceMm:0.#}, " +
                              $"pillar {options.PillarClearanceMm:0.#}, inline {options.InlineGapMm:0.#}, " +
                              $"perpendicular {options.PerpendicularGapMm:0.#}");
            report.AppendLine("A target of 0 means the end is already where that limit wants it.");

            foreach (var part in beams)
            {
                var geometry = BeamGeometry.Create((FamilyInstance)part.Element);
                if (geometry == null)
                {
                    continue;
                }

                foreach (var end in BeamGeometry.Ends)
                {
                    var supports = probe.Probe(geometry, end);
                    var limits = BeamEndSolver.Weigh(supports, options, geometry.WidthMm);

                    report.AppendLine();
                    report.AppendLine($"BEAM {part.Element.Id.ToLong()} END {end}:");

                    if (limits.Count == 0)
                    {
                        report.AppendLine("  nothing limits this end");
                        continue;
                    }

                    foreach (var limit in limits)
                    {
                        var mark = limit.Governs ? " *** governs ***" : limit.Dropped ? "  (let go)" : string.Empty;
                        var note = string.IsNullOrEmpty(limit.Note) ? string.Empty : "   " + limit.Note;

                        var wedge = limit.WedgeMm < 0.05
                            ? string.Empty
                            : $"   wedge {limit.WedgeMm:0.#}" +
                              (limit.CutsTheEnd ? string.Empty : ", too small to cut");

                        report.AppendLine($"  {limit.Describe(),-34} -> {limit.TargetMm,9:+0.#;-0.#;0}" +
                                          $"{mark}{wedge}{note}");

                        if (!string.IsNullOrEmpty(limit.Support?.EntryNote))
                        {
                            report.AppendLine($"      skew {limit.SkewDegrees:0.##} {limit.Support.EntryNote}");
                        }
                    }

                    Verdict(report, supports, geometry, end, options);
                }
            }
        }

        /// <summary>How far the model and the tool disagree about this end, in one line.</summary>
        private static void Verdict(
            StringBuilder report,
            IReadOnlyList<SupportCandidate> supports,
            BeamGeometry geometry,
            int end,
            AdjustBeamOptions options)
        {
            var plan = BeamEndSolver.Solve(
                geometry.Beam.Id.ToLong(),
                end,
                supports,
                options,
                geometry.LengthMm,
                geometry.WidthMm,
                geometry.AxisOffsetMm(end));

            if (plan.IsSkipped)
            {
                report.AppendLine($"  decision: nothing done - {plan.SkipReason}");
                return;
            }

            var cut = plan.NeedsCut
                ? $", cut {plan.SkewDegrees:0.##} deg off square with the face at {plan.CutPlaneMm:+0.#;-0.#;0}"
                : ", no cut";

            var carried = geometry.AxisOffsetMm(end);

            report.AppendLine($"  decision: move the {(plan.NeedsCut ? "axis" : "end")} " +
                              $"{(plan.NeedsCut ? plan.AxisTravelMm : plan.MoveMm):+0.#;-0.#;0}{cut}" +
                              (Math.Abs(carried) < BeamEndPlan.NegligibleMoveMm
                                  ? string.Empty
                                  : $"   (the model's axis stands {carried:+0.#;-0.#;0} from where its " +
                                    "material stops)"));

            // An end left square is judged on the solid, because the tool always clears the end
            // extension and puts the axis where the material is to stop - so a model that shortened
            // the same end by setting an extension instead lands in the same place while its axis sits
            // somewhere else entirely.
            //
            // A cut end is not judged here at all. What shows on such an end is the face the opening
            // leaves, and where the axis runs on to behind that face is nobody's business: the
            // reference model puts it in a different place on every one of them, sometimes hard up
            // against the cut and sometimes ninety millimetres out. Comparing axes there raised three
            // complaints of a millimetre or six about ends whose faces matched to a tenth. The cut
            // face is compared instead, below.
            if (!plan.NeedsCut)
            {
                report.AppendLine(Math.Abs(plan.MoveMm) < BeamEndPlan.NegligibleMoveMm
                    ? "  ** agrees on the position **"
                    : $"  ** DISAGREES on the position: the model has this end where it is, the tool " +
                      $"would move it {plan.MoveMm:0.#} mm **");
            }

            Cuts(report, geometry, end, plan);
        }

        /// <summary>
        /// The openings the model already has at this end, against the one the tool would make.
        ///
        /// Judging an end on where it sits is only half of it. An end can be in exactly the right
        /// place and still be wrong, because the tool squared it off with an opening where the model
        /// simply made the beam shorter - and read on position alone that end passes.
        /// </summary>
        private static void Cuts(StringBuilder report, BeamGeometry beam, int end, BeamEndPlan plan)
        {
            var origin = beam.PointAt(end);
            var outward = beam.OutwardAt(end);
            var across = XYZ.BasisZ.CrossProduct(beam.Direction).Normalize();
            var reach = OpeningReachMm.MmToFeet();

            var found = new FilteredElementCollector(beam.Beam.Document)
                .OfClass(typeof(Opening))
                .Cast<Opening>()
                .Where(opening => opening.Host != null && opening.Host.Id == beam.Beam.Id)
                .Select(opening => new { opening, edges = Edges(opening).ToList() })
                .Where(item => item.edges.Any(edge => edge.Middle.DistanceTo(origin) < reach))
                .ToList();

            var squaresOff = false;
            var faces = new List<double>();

            foreach (var item in found)
            {
                var sideways = item.edges
                    .SelectMany(edge => new[] { edge.Start, edge.Finish })
                    .Select(point => (point - origin).DotProduct(across))
                    .ToList();

                var width = (sideways.Max() - sideways.Min()).FeetToMm();
                var whole = width >= beam.WidthMm - 1;
                squaresOff |= whole;

                var planes = item.edges
                    .Select(edge => Crossing(edge, origin, outward))
                    .Where(distance => distance.HasValue)
                    .Select(distance => distance.Value.FeetToMm())
                    .ToList();

                if (whole)
                {
                    faces.AddRange(planes);
                }

                report.AppendLine($"  model has opening {item.opening.Id.ToLong()}: " +
                                  $"{width:0.#} across the beam, " +
                                  (whole ? "squares the end off" : "a notch, not the width of the beam") +
                                  (planes.Count == 0
                                      ? string.Empty
                                      : ", faces at " + string.Join(
                                          " and ",
                                          planes.Select(plane => $"{plane:+0.#;-0.#;0}"))));
            }

            if (found.Count == 0)
            {
                report.AppendLine("  model has no opening at this end");
            }

            if (plan.NeedsCut && squaresOff)
            {
                // The face is what shows on a cut end, so it is the face that is compared. Which of
                // the opening's two faces is the finished one is not worth working out - the other is
                // a beam's width of skew away, far outside anything that would pass for agreement.
                var nearest = faces.OrderBy(face => Math.Abs(face - plan.CutPlaneMm.Value)).First();
                var apart = Math.Abs(nearest - plan.CutPlaneMm.Value);

                report.AppendLine(apart < BeamEndPlan.NegligibleMoveMm
                    ? $"  ** agrees on the cut face: {plan.CutPlaneMm:+0.#;-0.#;0} against " +
                      $"{nearest:+0.#;-0.#;0} **"
                    : $"  ** DISAGREES on the cut face: the tool would cut at " +
                      $"{plan.CutPlaneMm:+0.#;-0.#;0}, the model cuts at {nearest:+0.#;-0.#;0}, " +
                      $"{apart:0.#} mm apart **");
                return;
            }

            if (!plan.NeedsCut && !squaresOff)
            {
                report.AppendLine("  ** both leave this end square **");
                return;
            }

            report.AppendLine(plan.NeedsCut
                ? "  ** DISAGREES on the cut: the tool would square this end off, the model just " +
                  "made the beam shorter **"
                : "  ** DISAGREES on the cut: the model squares this end off, the tool would leave " +
                  "it square **");
        }

        /// <summary>Where the axis crosses the vertical plane an opening edge cuts on.</summary>
        private static double? Crossing(Edge2 edge, XYZ origin, XYZ outward)
        {
            var normal = XYZ.BasisZ.CrossProduct(edge.Direction);
            if (normal.IsZeroLength())
            {
                return null;
            }

            normal = normal.Normalize();
            var denominator = outward.DotProduct(normal);

            return Math.Abs(denominator) < 1e-9
                ? (double?)null
                : (edge.Middle - origin).DotProduct(normal) / denominator;
        }

        /// <summary>
        /// Which column carries each beam end. With more than one column in the joint this is the
        /// question everything else hangs on: the rule for two beams meeting head on parts them evenly
        /// over the centre of the column they share, and a pair landing on different columns has no
        /// shared centre to part over.
        /// </summary>
        private static void Bearing(StringBuilder report, IList<Part> beams, IList<Part> columns)
        {
            if (columns.Count == 0)
            {
                report.AppendLine("No column in the selection.");
                return;
            }

            foreach (var column in columns)
            {
                report.AppendLine($"COLUMN {column.Element.Id.ToLong()}: centre {Mm(column.Centre)}   " +
                                  $"top {column.TopZ.FeetToMm():0.#}");
            }

            for (var first = 0; first < columns.Count; first++)
            {
                for (var second = first + 1; second < columns.Count; second++)
                {
                    var apart = columns[first].Centre.ToXY().DistanceTo(columns[second].Centre.ToXY());
                    report.AppendLine($"centres of {columns[first].Element.Id.ToLong()} and " +
                                      $"{columns[second].Element.Id.ToLong()} are {apart.FeetToMm():0.#} apart");
                }
            }

            foreach (var beam in beams.Where(beam => beam.Axis != null))
            {
                foreach (var end in new[] { 0, 1 })
                {
                    report.AppendLine();
                    report.AppendLine($"BEAM {beam.Element.Id.ToLong()} END {end}:");

                    var carried = false;
                    foreach (var column in columns)
                    {
                        var line = BearsOn(beam, end, column);
                        if (line == null)
                        {
                            continue;
                        }

                        carried = true;
                        report.AppendLine("  " + line);
                    }

                    if (!carried)
                    {
                        report.AppendLine("  no column under this end");
                    }
                }
            }
        }

        /// <summary>How one end sits over one column, or null when it does not sit over it at all.</summary>
        private static string BearsOn(Part beam, int end, Part column)
        {
            var origin = beam.PointAt(end);
            var outward = beam.OutwardAt(end);
            var across = XYZ.BasisZ.CrossProduct(outward).Normalize();

            var offsets = column.Vertices.Select(point => (point - origin).DotProduct(across)).ToList();
            if (offsets.Count == 0 || offsets.Min() > 0 || offsets.Max() < 0)
            {
                // The axis of the beam runs beside the column rather than over it.
                return null;
            }

            var along = column.Vertices.Select(point => (point - origin).DotProduct(outward)).ToList();
            var centre = (column.Centre - origin).DotProduct(outward).FeetToMm();
            var seat = (column.TopZ - beam.BottomZ).FeetToMm();

            var note = Math.Abs(seat) <= BearingGapMm
                ? "carries it"
                : $"NOT carrying it, its top is {seat:+0.#;-0.#;0} from the underside";

            return $"column {column.Element.Id.ToLong()}: near {along.Min().FeetToMm():+0.#;-0.#;0}   " +
                   $"far {along.Max().FeetToMm():+0.#;-0.#;0}   centre {centre:+0.#;-0.#;0}   {note}";
        }

        /// <summary>
        /// Every pair of beam ends in the joint, against what the tool asks before it treats them as
        /// parting from a shared point. Printed for all pairs and not only the ones that pass, because
        /// which pairs fail is the half of the answer that is easy to guess wrong.
        /// </summary>
        private static void EndPairs(StringBuilder report, IList<Part> beams, IList<Part> columns)
        {
            report.AppendLine($"Two ends are treated as meeting head on when their axes are within " +
                              $"{FacingAxisDegrees:0} deg,");
            report.AppendLine($"they run out at each other by at least {FacingApartDegrees:0} deg, and " +
                              $"they are no more than {FacingReachMm:0} mm apart.");

            var ends = beams
                .Where(beam => beam.Axis != null)
                .SelectMany(beam => new[] { 0, 1 }.Select(end => new { beam, end }))
                .ToList();

            for (var first = 0; first < ends.Count; first++)
            {
                for (var second = first + 1; second < ends.Count; second++)
                {
                    var a = ends[first];
                    var b = ends[second];

                    if (a.beam.Element.Id == b.beam.Element.Id)
                    {
                        continue;
                    }

                    var axes = Off90(a.beam.Direction, b.beam.Direction);
                    var apart = a.beam.OutwardAt(a.end).AngleTo(b.beam.OutwardAt(b.end)).RadiansToDegrees();
                    var gap = a.beam.PointAt(a.end).DistanceTo(b.beam.PointAt(b.end)).FeetToMm();

                    var meets = axes <= FacingAxisDegrees
                                && apart >= FacingApartDegrees
                                && gap <= FacingReachMm;

                    report.AppendLine();
                    report.AppendLine($"{a.beam.Element.Id.ToLong()} END {a.end}  vs  " +
                                      $"{b.beam.Element.Id.ToLong()} END {b.end}: " +
                                      (meets ? "MEETING HEAD ON" : "crossing"));
                    report.AppendLine($"  axes {axes:0.##} deg apart   running out {apart:0.##} deg apart   " +
                                      $"ends {gap:0.#} apart");

                    if (meets)
                    {
                        Shared(report, a.beam, a.end, b.beam, b.end, columns);
                    }
                }
            }
        }

        /// <summary>Whether a pair meeting head on has a column in common to part over.</summary>
        private static void Shared(
            StringBuilder report,
            Part first,
            int firstEnd,
            Part second,
            int secondEnd,
            IList<Part> columns)
        {
            var mine = columns.Where(column => BearsOn(first, firstEnd, column) != null).ToList();
            var theirs = columns.Where(column => BearsOn(second, secondEnd, column) != null).ToList();
            var both = mine.Where(column => theirs.Any(other => other.Element.Id == column.Element.Id)).ToList();

            if (both.Count > 0)
            {
                foreach (var column in both)
                {
                    report.AppendLine($"  both sit over column {column.Element.Id.ToLong()}, centre " +
                                      $"{(column.Centre - first.PointAt(firstEnd)).DotProduct(first.OutwardAt(firstEnd)).FeetToMm():+0.#;-0.#;0} " +
                                      $"along the first and " +
                                      $"{(column.Centre - second.PointAt(secondEnd)).DotProduct(second.OutwardAt(secondEnd)).FeetToMm():+0.#;-0.#;0} " +
                                      "along the second");
                }

                return;
            }

            report.AppendLine("  ** no column in common **   " +
                              $"first sits over [{Ids(mine)}], second over [{Ids(theirs)}]");
            report.AppendLine("  There is no shared centre to part evenly over, so the rule as it stands");
            report.AppendLine("  has no answer for this pair.");
        }

        /// <summary>
        /// Where two beams actually share the same space. This is the reading the notcher works from -
        /// comparing how far each beam reaches instead finds beams clashing that are a clean gap apart,
        /// because a beam cut off at an angle reaches well past the corner it presents.
        /// </summary>
        private static void Clashes(StringBuilder report, IList<Part> beams)
        {
            var found = false;

            for (var first = 0; first < beams.Count; first++)
            {
                for (var second = first + 1; second < beams.Count; second++)
                {
                    var a = beams[first];
                    var b = beams[second];
                    var shared = Shared(a, b);

                    if (shared.Count == 0)
                    {
                        continue;
                    }

                    found = true;
                    var volume = shared.Sum(solid => solid.Volume);
                    var points = shared.SelectMany(solid => solid.GetVertices()).ToList();

                    report.AppendLine();
                    report.AppendLine($"{a.Element.Id.ToLong()} and {b.Element.Id.ToLong()} overlap by " +
                                      $"{volume * 304.8 * 304.8 * 304.8 / 1000:0} cm3");

                    Overlap(report, a, points);
                    Overlap(report, b, points);
                }
            }

            if (!found)
            {
                report.AppendLine("No two of the selected beams share any space.");
            }
        }

        /// <summary>Where an overlap falls in one beam's own terms, and whether it is in its block.</summary>
        private static void Overlap(StringBuilder report, Part beam, IList<XYZ> points)
        {
            if (beam.Section == null)
            {
                return;
            }

            var along = points.Select(beam.Section.Along).ToList();
            report.AppendLine($"  on {beam.Element.Id.ToLong()}: along the beam from " +
                              $"{along.Min().FeetToMm():0.#} to {along.Max().FeetToMm():0.#}");

            foreach (var side in new[] { 1, -1 })
            {
                var web = beam.Section.Web(side);
                if (!web.HasValue || !beam.Section.HasBlock(side))
                {
                    continue;
                }

                var beyond = points
                    .Where(point => beam.Section.Across(point, side) > web.Value + BeamSection.StepMm.MmToFeet())
                    .Select(beam.Section.Along)
                    .ToList();

                if (beyond.Count > 0)
                {
                    report.AppendLine($"    in its {(side > 0 ? "left" : "right")} bearing block, from " +
                                      $"{beyond.Min().FeetToMm():0.#} to {beyond.Max().FeetToMm():0.#}");
                }
            }
        }

        private static IList<Solid> Shared(Part first, Part second)
        {
            var result = new List<Solid>();

            foreach (var mine in first.Solids)
            {
                foreach (var theirs in second.Solids)
                {
                    Solid shared;
                    try
                    {
                        shared = BooleanOperationsUtils.ExecuteBooleanOperation(
                            mine, theirs, BooleanOperationsType.Intersect);
                    }
                    catch
                    {
                        continue;
                    }

                    if (shared != null && shared.Volume >= NegligibleVolume)
                    {
                        result.Add(shared);
                    }
                }
            }

            return result;
        }

        private static string Ids(IEnumerable<Part> parts)
        {
            var list = parts.Select(part => part.Element.Id.ToLong().ToString()).ToList();
            return list.Count == 0 ? "none" : string.Join(", ", list);
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
                                  $"{offset:+0.#;-0.#;0} from the centre   area {face.AreaMm2:0} mm2" +
                                  $"   {face.Height}");
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
                report.AppendLine($"  normal {Vec(face.Normal)}   at {Mm(face.Point)}" +
                                  $"   area {face.AreaMm2:0} mm2   {face.Height}");
            }
        }

        /// <summary>
        /// The section the rest of the report exists for: every beam end measured against everything
        /// around it, and every opening measured against the faces it might have been cut parallel to.
        /// </summary>
        private static void Joint(
            StringBuilder report,
            IList<Part> beams,
            IList<Part> columns,
            IList<Part> openings,
            IList<Part> others)
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

                    // Walls are in here with everything else. They were left out at first because a
                    // beam's neighbours are mostly columns and beams, and a wall's plane crossings are
                    // the one reading that says whether the model lets an end run into a wall or holds
                    // it off - which is exactly the question a wall raises.
                    foreach (var other in columns.Concat(beams).Concat(openings).Concat(others)
                                 .Where(other => other.Element.Id != beam.Element.Id))
                    {
                        AgainstFaces(report, origin, outward, beam.Direction, other);
                        Touches(report, beam, end, other);
                    }
                }
            }

            foreach (var opening in openings)
            {
                CutPlanes(report, opening, beams, columns);
            }
        }

        /// <summary>
        /// How far the end could still travel before it touched this part: the nearest its material
        /// comes, counting only what stands inside the width and height the beam sweeps. Printed twice,
        /// with and without the other beam's bearing block, because the block is cut back rather than
        /// stood clear of - the difference between the two says whether it is really in the way.
        /// </summary>
        private static void Touches(StringBuilder report, Part beam, int end, Part other)
        {
            var origin = beam.ProbeOriginAt(end);
            var outward = beam.OutwardAt(end);
            var across = XYZ.BasisZ.CrossProduct(outward);

            if (across.IsZeroLength() || other.Solids.Count == 0)
            {
                return;
            }

            var half = (beam.WidthMm / 2).MmToFeet();
            var inside = other.Solids
                .PointsInSlab(point => (point - origin).DotProduct(across.Normalize()), -half, half)
                .Where(point => point.Z >= beam.BottomZ && point.Z <= beam.TopZ)
                .ToList();

            if (inside.Count == 0)
            {
                return;
            }

            var all = inside.Min(point => (point - origin).DotProduct(outward)).FeetToMm();

            var web = other.Section == null
                ? inside
                : inside.Where(point => !other.Section.IsBeyondWeb(point)).ToList();

            var withoutBlock = web.Count == 0
                ? "(nothing but block)"
                : $"{web.Min(point => (point - origin).DotProduct(outward)).FeetToMm():+0.#;-0.#;0}";

            report.AppendLine($"    touches at {all:+0.#;-0.#;0} counting the bearing block, " +
                              $"{withoutBlock} without it");

            if (web.Count > 0)
            {
                Reach(report, beam, end, other, web.Min(point => (point - origin).DotProduct(outward)));
            }
        }

        /// <summary>
        /// Whether the support stands across the beam's whole width or only reaches in at a corner.
        ///
        /// This is what the solver goes by, so it is spelled out rather than left to be worked back
        /// from the numbers. A face standing right across is met corner first, half a width of skew
        /// ahead of where the axis crosses its plane; touching later than that means the face stops
        /// somewhere inside the width, and only a tip is really in the way.
        /// </summary>
        private static void Reach(StringBuilder report, Part beam, int end, Part other, double touches)
        {
            var origin = beam.ProbeOriginAt(end);
            var outward = beam.OutwardAt(end);

            // Chosen the way the probe chooses, so the report and the tool never disagree about which
            // face the end is arriving at.
            var entry = other.Uprights
                .Where(face => face.Normal.DotProduct(outward) < -0.05)
                .Where(face => Math.Abs(outward.DotProduct(face.Normal)) > 1e-9)
                .OrderBy(face => Math.Abs((face.Point - origin).DotProduct(face.Normal)
                                          / outward.DotProduct(face.Normal)))
                .FirstOrDefault();

            if (entry == null)
            {
                return;
            }

            var skew = Off90(outward, entry.Normal);
            var denominator = outward.DotProduct(entry.Normal);
            if (Math.Abs(denominator) < 1e-9)
            {
                return;
            }

            var crossing = ((entry.Point - origin).DotProduct(entry.Normal) / denominator).FeetToMm();
            var ifWhole = crossing - beam.WidthMm / 2 * Math.Tan(skew * Math.PI / 180);
            var whole = touches.FeetToMm() <= ifWhole + 1;

            report.AppendLine($"    {(whole ? "FACE" : "TIP ")}   {skew:0.##} deg off square   " +
                              $"the axis crosses its plane at {crossing:+0.#;-0.#;0}, " +
                              $"a whole face would be touched at {ifWhole:+0.#;-0.#;0}");
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

            /// <summary>
            /// How high the face itself stands. Its Point is a point on the plane and can sit well
            /// away from the face, so it says nothing about where the material is.
            /// </summary>
            public double? BottomMm { get; set; }

            public double? TopMm { get; set; }

            public string Height => BottomMm == null
                ? "height unknown"
                : $"z {BottomMm:0.#} to {TopMm:0.#}";

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

            public IList<Solid> Solids { get; private set; }

            public IList<XYZ> Vertices { get; private set; }

            /// <summary>Read for beams only; null for anything else.</summary>
            public BeamSection Section { get; private set; }

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
                part.Solids = solids;

                var vertices = solids.SelectMany(solid => solid.GetVertices()).ToList();
                part.Vertices = vertices;

                if (part.Role == Role.Beam)
                {
                    part.Section = BeamSection.Read(element);
                }

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

            /// <summary>
            /// The points where one flank really is wider than its web, so the caller can report how
            /// far the widening runs and how tall it is.
            ///
            /// The span is reported end to end and not split into stretches. A straight edge tessellates
            /// to its two endpoints and nothing in between, so a widening running the whole length comes
            /// back as two lone points - and splitting on the gap between them reads that as two
            /// separate widenings of no length at all, which is the opposite of the truth.
            /// </summary>
            public IEnumerable<XYZ> Widened(int side)
            {
                var web = Section?.Web(side);
                if (Section == null || !web.HasValue || !Section.HasBlock(side))
                {
                    return Enumerable.Empty<XYZ>();
                }

                var step = BeamSection.StepMm.MmToFeet();
                return Vertices.Where(point => Section.Across(point, side) > web.Value + step);
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
                    var range = face.ZRange();

                    var plane = new FacePlane
                    {
                        Normal = face.FaceNormal,
                        Point = face.Origin,
                        AreaMm2 = face.Area.FeetToMm().FeetToMm(),
                        BottomMm = range?.Bottom.FeetToMm(),
                        TopMm = range?.Top.FeetToMm(),
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
