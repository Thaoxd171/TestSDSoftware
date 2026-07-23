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
    /// A dry run of the Adjust Beam tool. It drives the real <see cref="SupportProbe"/> and
    /// <see cref="BeamEndSolver"/>, so what it prints is exactly what the tool would do - every
    /// support it found, every support it discarded and why, which one won, and where the end would
    /// land. Nothing is written to the model.
    /// Temporary: remove this command and its ribbon button before the final submission.
    /// </summary>
    internal static class AdjustBeamExplainReport
    {
        public static string Build(Document document, IList<FamilyInstance> beams, AdjustBeamOptions options)
        {
            var report = new StringBuilder();

            Section(report, "SD REVIT TEST - ADJUST BEAM, DRY RUN", () =>
            {
                report.AppendLine($"Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                report.AppendLine($"Document  : {document.Title}");
                report.AppendLine($"Beams     : {beams.Count}");
                report.AppendLine();
                report.AppendLine("Settings in force:");
                report.AppendLine($"  wall clearance         {options.WallClearanceMm:0.#} mm");
                report.AppendLine($"  pillar clearance       {options.PillarClearanceMm:0.#} mm");
                report.AppendLine($"  inline gap             {options.InlineGapMm:0.#} mm");
                report.AppendLine($"  perpendicular gap      {options.PerpendicularGapMm:0.#} mm");
                report.AppendLine($"  corner mode            {options.CornerMode}");
                report.AppendLine($"  extend to body, pillar {options.ExtendToBeamBodyAtPillar}");
                report.AppendLine($"  extend to body, wall   {options.ExtendToBeamBodyAtWall}");
                report.AppendLine();
                report.AppendLine("Distances are mm, measured outwards from the end of the beam:");
                report.AppendLine("  near +a / far +b  - the support starts a mm past the end and stops b mm past it.");
                report.AppendLine("Nothing here is written to the model.");
            });

            var probe = new SupportProbe(document);

            for (var index = 0; index < beams.Count; index++)
            {
                var beam = beams[index];
                Section(report, $"BEAM {index + 1} OF {beams.Count}  -  id {beam.Id.ToLong()}",
                    () => Explain(report, probe, beam, options));
            }

            return report.ToString();
        }

        private static void Explain(
            StringBuilder report,
            SupportProbe probe,
            FamilyInstance beam,
            AdjustBeamOptions options)
        {
            report.AppendLine($"element   : {Describe(beam)}");

            var geometry = BeamGeometry.Create(beam);
            if (geometry == null)
            {
                report.AppendLine("The tool would skip this one: no straight axis, or no readable geometry.");
                return;
            }

            report.AppendLine($"location  : {Mm(geometry.AxisStart)} -> {Mm(geometry.AxisFinish)}");
            report.AppendLine($"axis      : {Vec(geometry.Direction)}   solid length {geometry.LengthMm:0.#}");
            report.AppendLine($"extension : start {beam.GetLength(BuiltInParameter.START_EXTENSION).FeetToMm():0.#}" +
                              $"   end {beam.GetLength(BuiltInParameter.END_EXTENSION).FeetToMm():0.#}");
            report.AppendLine($"join      : allowed at start {JoinAllowed(beam, 0)}   at end {JoinAllowed(beam, 1)}");

            foreach (var end in BeamGeometry.Ends)
            {
                ExplainEnd(report, probe, geometry, end, options);
            }
        }

        private static void ExplainEnd(
            StringBuilder report,
            SupportProbe probe,
            BeamGeometry geometry,
            int end,
            AdjustBeamOptions options)
        {
            var point = geometry.PointAt(end);
            var outward = geometry.OutwardAt(end);

            report.AppendLine();
            report.AppendLine($"  END {end}   geometry stops at {Mm(point)}   outward {Vec(outward)}");
            report.AppendLine($"    probe ray starts at {Mm(geometry.ProbeOriginAt(end))}");

            var all = probe.ProbeAll(geometry, end);
            var accepted = all.Where(candidate => candidate.RejectionReason == null).ToList();

            report.AppendLine($"    candidates: {all.Count} seen, {accepted.Count} kept");

            foreach (var candidate in all.OrderBy(candidate => candidate.RejectionReason == null ? 0 : 1)
                         .ThenBy(candidate => candidate.NearMm))
            {
                Print(report, candidate);
            }

            var plan = BeamEndSolver.Solve(
                geometry.Beam.Id.ToLong(), end, accepted, options, geometry.LengthMm, geometry.WidthMm,
                geometry.AxisOffsetMm(end));

            report.AppendLine();
            report.AppendLine($"    governing : {plan.Support}" +
                              (string.IsNullOrEmpty(plan.SupportDescription)
                                  ? string.Empty
                                  : "  " + plan.SupportDescription));

            if (plan.IsSkipped)
            {
                report.AppendLine($"    decision  : nothing done - {plan.SkipReason}");
                return;
            }

            var target = point + outward * plan.MoveMm.MmToFeet();
            var verdict = plan.IsAlreadyCorrect ? "already correct, nothing done" : "the end would move";
            report.AppendLine($"    decision  : {plan.MoveMm:+0.##;-0.##;0} mm - {verdict}");
            report.AppendLine($"    target    : {Mm(target)}");

            if (plan.NeedsCut)
            {
                report.AppendLine($"    cut       : {plan.SkewDegrees:0.##} deg off square to id {plan.CutAgainstId}, " +
                                  $"so the axis runs out to cover the face and an opening trims back to " +
                                  $"{plan.CutPlaneMm:+0.##;-0.##;0} mm");
            }
            else
            {
                report.AppendLine("    cut       : none, the end stays square");
            }
        }

        private static void Print(StringBuilder report, SupportCandidate candidate)
        {
            var centre = candidate.CentreAlongMm.HasValue
                ? $"   centre {candidate.CentreAlongMm.Value:+0.#;-0.#;0}"
                : string.Empty;

            var skew = candidate.SkewDegrees > 0
                ? $"   skew {candidate.SkewDegrees:0.##} deg"
                : string.Empty;

            var clear = candidate.ClearMm.HasValue
                ? $"   touches at {candidate.ClearMm.Value:+0.#;-0.#;0}"
                : string.Empty;

            report.AppendLine($"      [{(candidate.RejectionReason == null ? "kept    " : "rejected")}] " +
                              $"{candidate.Kind,-16} near {candidate.NearMm,8:+0.#;-0.#;0}  " +
                              $"far {candidate.FarMm,8:+0.#;-0.#;0}{centre}{skew}{clear}   {candidate.Description}");
            report.AppendLine($"                 height: {candidate.BottomAboveBeamMm:+0.#;-0.#;0} to " +
                              $"{candidate.TopAboveBeamMm:+0.#;-0.#;0} measured from the top of the beam");

            if (candidate.RejectionReason != null)
            {
                report.AppendLine($"                 reason: {candidate.RejectionReason}");
            }
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
    }
}
