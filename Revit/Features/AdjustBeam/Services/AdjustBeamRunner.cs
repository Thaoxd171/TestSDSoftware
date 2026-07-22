using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Core;
using SDSoftware.RevitTest.Extensions;
using SDSoftware.RevitTest.Features.AdjustBeam.Models;

namespace SDSoftware.RevitTest.Features.AdjustBeam.Services
{
    /// <summary>
    /// Runs the tool in three passes: measure everything, decide everything, then write everything.
    ///
    /// The passes are kept apart on purpose. Two beams meeting head on are each measured against the
    /// other, so if the first one moved before the second was measured, the second would chase it and
    /// the gap would come out wrong. Measuring the whole set first removes that dependency, and it
    /// keeps the transaction down to the writes alone - stopping during either of the first two passes
    /// therefore leaves the model untouched.
    /// </summary>
    public class AdjustBeamRunner
    {
        private const string ReadStep = "Read beam geometry";
        private const string SolveStep = "Find connection info";
        private const string AdjustStep = "Adjust structural beam";

        private readonly Document _document;

        public AdjustBeamRunner(Document document)
        {
            _document = document;
        }

        public AdjustResult Run(IList<FamilyInstance> beams, AdjustBeamOptions options, IProgressSink progress)
        {
            var result = new AdjustResult { BeamsExamined = beams.Count };
            progress.Log($"{beams.Count} beam(s) picked.");

            var geometries = Read(beams, result, progress);
            if (progress.IsCancelled)
            {
                return Stopped(result, progress);
            }

            var work = Solve(geometries, options, progress);
            if (progress.IsCancelled)
            {
                return Stopped(result, progress);
            }

            using (var transaction = new Transaction(_document, "Adjust Beams"))
            {
                transaction.Start();
                Apply(work, result, progress);
                transaction.Commit();
            }

            return result;
        }

        /// <summary>Pass 1: capture every beam as it stands now.</summary>
        private static List<BeamGeometry> Read(
            IList<FamilyInstance> beams,
            AdjustResult result,
            IProgressSink progress)
        {
            var geometries = new List<BeamGeometry>();
            progress.Report(ReadStep, 0);

            for (var index = 0; index < beams.Count && !progress.IsCancelled; index++)
            {
                var beam = beams[index];
                var geometry = BeamGeometry.Create(beam);

                if (geometry == null)
                {
                    result.EndsSkipped += 2;
                    progress.Log($"id {beam.Id.ToLong()}: skipped, no straight axis or no geometry.");
                }
                else
                {
                    geometries.Add(geometry);
                }

                progress.Report(ReadStep, (index + 1) / (double)beams.Count);
            }

            return geometries;
        }

        /// <summary>Pass 2: work out where every end belongs. Still nothing is written.</summary>
        private List<KeyValuePair<BeamGeometry, BeamEndPlan>> Solve(
            IList<BeamGeometry> geometries,
            AdjustBeamOptions options,
            IProgressSink progress)
        {
            var probe = new SupportProbe(_document);
            var work = new List<KeyValuePair<BeamGeometry, BeamEndPlan>>();
            progress.Report(SolveStep, 0);

            for (var index = 0; index < geometries.Count && !progress.IsCancelled; index++)
            {
                var geometry = geometries[index];

                foreach (var end in BeamGeometry.Ends)
                {
                    var supports = probe.Probe(geometry, end);
                    var plan = BeamEndSolver.Solve(
                        geometry.Beam.Id.ToLong(), end, supports, options, geometry.LengthMm);

                    work.Add(new KeyValuePair<BeamGeometry, BeamEndPlan>(geometry, plan));
                }

                progress.Report(SolveStep, (index + 1) / (double)geometries.Count);
            }

            return work;
        }

        /// <summary>Pass 3: the only pass that touches the model.</summary>
        private static void Apply(
            IList<KeyValuePair<BeamGeometry, BeamEndPlan>> work,
            AdjustResult result,
            IProgressSink progress)
        {
            var changed = new HashSet<long>();
            progress.Report(AdjustStep, 0);

            for (var index = 0; index < work.Count; index++)
            {
                if (progress.IsCancelled)
                {
                    result.WasStopped = true;
                    progress.Log("Stopped. The ends adjusted so far are kept - undo the command to revert them.");
                    break;
                }

                var geometry = work[index].Key;
                var plan = work[index].Value;

                if (plan.IsSkipped)
                {
                    result.EndsSkipped++;
                }
                else if (plan.IsAlreadyCorrect)
                {
                    result.EndsAlreadyCorrect++;
                }
                else
                {
                    Move(geometry, plan, result, changed, progress);
                }

                progress.Report(AdjustStep, (index + 1) / (double)work.Count);
            }

            result.BeamsChanged = changed.Count;
        }

        private static void Move(
            BeamGeometry geometry,
            BeamEndPlan plan,
            AdjustResult result,
            ISet<long> changed,
            IProgressSink progress)
        {
            try
            {
                BeamAdjuster.Apply(geometry, plan);
                changed.Add(plan.BeamId);
                result.EndsMoved++;
                progress.Log($"id {plan.BeamId} end {plan.End}: {plan.MoveMm:+0.#;-0.#;0} mm against {Describe(plan)}");
            }
            catch (Exception ex)
            {
                result.EndsSkipped++;
                progress.Log($"id {plan.BeamId} end {plan.End}: failed - {ex.Message}");
            }
        }

        private static string Describe(BeamEndPlan plan)
        {
            return string.IsNullOrEmpty(plan.SupportDescription)
                ? plan.Support.ToString()
                : $"{plan.Support} {plan.SupportDescription}";
        }

        private static AdjustResult Stopped(AdjustResult result, IProgressSink progress)
        {
            result.WasStopped = true;
            progress.Log("Stopped before anything was changed.");
            return result;
        }
    }
}
