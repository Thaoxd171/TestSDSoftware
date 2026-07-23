using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Core;
using SDSoftware.RevitTest.Extensions;
using SDSoftware.RevitTest.Features.AdjustBeam.Models;

namespace SDSoftware.RevitTest.Features.AdjustBeam.Services
{
    /// <summary>
    /// Runs the tool in four passes: measure everything, decide everything, write everything, then read
    /// the model back and check the beams really ended up where they were sent.
    ///
    /// Measuring and deciding are kept apart from writing on purpose. Two beams meeting head on are
    /// each measured against the other, so if the first one moved before the second was measured, the
    /// second would chase it and the gap would come out wrong.
    ///
    /// The whole thing is then repeated until a round changes nothing. One round cannot always finish
    /// the job: a beam stopping short of its neighbour is measured against the neighbour as it stands,
    /// and if that neighbour is itself about to be cut back, the answer was taken from a shape that no
    /// longer exists. Each round measures everything afresh, so the ends settle where they belong
    /// within two or three of them. Rounds converge rather than oscillate because no rule places an
    /// end relative to something that is chasing it: beams parting over a column both go to the centre
    /// of that column, which does not move.
    ///
    /// The check at the end exists because an assignment that raises no exception is not proof that
    /// anything moved - Revit can put an end straight back - and a tool reporting work it did not do
    /// is worse than one that fails loudly.
    /// </summary>
    public class AdjustBeamRunner
    {
        private const string ReadStep = "Read beam geometry";
        private const string SolveStep = "Find connection info";
        private const string AdjustStep = "Adjust structural beam";
        private const string CheckStep = "Check the result";

        /// <summary>How far off target an end may land before it is called a failure.</summary>
        private const double ToleranceMm = 0.5;

        /// <summary>How many times the whole job is redone before it is called settled anyway.</summary>
        private const int MaximumRounds = 4;

        private readonly Document _document;

        public AdjustBeamRunner(Document document)
        {
            _document = document;
        }

        public AdjustResult Run(IList<FamilyInstance> beams, AdjustBeamOptions options, IProgressSink progress)
        {
            var result = new AdjustResult { BeamsExamined = beams.Count };
            progress.Log($"{beams.Count} beam(s) picked.");

            IList<BeamPlan> plans = null;

            // The openings this tool cuts reach past the beams they cut, so that the corners come out
            // clean; Revit says so every time, and it is not worth asking about.
            var expected = ExpectedWarnings.FromOpeningsThatOverreach();

            using (var transaction = new Transaction(_document, "Adjust Beams"))
            {
                expected.TakeChargeOf(transaction);
                transaction.Start();

                for (var round = 1; round <= MaximumRounds && !progress.IsCancelled; round++)
                {
                    if (round > 1)
                    {
                        progress.Log($"Round {round}: measuring again now that the beams have moved.");
                    }

                    var geometries = Read(beams, result, progress);
                    if (progress.IsCancelled)
                    {
                        break;
                    }

                    plans = Solve(geometries, options, progress);
                    if (progress.IsCancelled)
                    {
                        break;
                    }

                    // Every round rebuilds the openings from scratch, so the last one to run leaves the
                    // model complete however many came before it.
                    result.Reset();
                    Apply(plans, result, progress);

                    if (!plans.Any(plan => plan.HasMove))
                    {
                        break;
                    }

                    if (round == MaximumRounds)
                    {
                        // Every case met so far settles in three. Running out of rounds means two ends
                        // are still moving each other, and saying so beats leaving it to be noticed.
                        result.DidNotSettle = true;
                        progress.Log($"Still moving after {MaximumRounds} rounds. The ends listed above " +
                                     "are where this round left them, not where the rules want them.");
                    }

                    _document.Regenerate();
                }

                transaction.Commit();
            }

            if (expected.Count > 0)
            {
                progress.Log($"{expected.Count} \"opening partially cuts its host\" warning(s) let past: " +
                             "the cuts are meant to reach beyond the beams they trim.");
            }

            if (progress.IsCancelled)
            {
                return Stopped(result, progress);
            }

            Check(plans, result, progress);
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
        private List<BeamPlan> Solve(
            IList<BeamGeometry> geometries,
            AdjustBeamOptions options,
            IProgressSink progress)
        {
            var probe = new SupportProbe(_document);
            var plans = new List<BeamPlan>();
            progress.Report(SolveStep, 0);

            for (var index = 0; index < geometries.Count && !progress.IsCancelled; index++)
            {
                var geometry = geometries[index];

                var ends = BeamGeometry.Ends
                    .Select(end => BeamEndSolver.Solve(
                        geometry.Beam.Id.ToLong(),
                        end,
                        probe.Probe(geometry, end),
                        options,
                        geometry.LengthMm,
                        geometry.WidthMm,
                        geometry.AxisOffsetMm(end)))
                    .ToList();

                plans.Add(new BeamPlan(geometry, ends));
                progress.Report(SolveStep, (index + 1) / (double)geometries.Count);
            }

            return plans;
        }

        /// <summary>Pass 3: the only pass that touches the model. One write per beam.</summary>
        private void Apply(IList<BeamPlan> plans, AdjustResult result, IProgressSink progress)
        {
            progress.Report(AdjustStep, 0);

            // An opening pins the beam it is cut from, so anything already at an end this run is going
            // to touch comes off first, before the axis moves.
            Clear(plans, progress);
            _document.Regenerate();

            for (var index = 0; index < plans.Count; index++)
            {
                if (progress.IsCancelled)
                {
                    result.WasStopped = true;
                    progress.Log("Stopped. The beams adjusted so far are kept - undo the command to revert them.");
                    break;
                }

                var plan = plans[index];
                result.EndsSkipped += plan.Ends.Count(end => end.IsSkipped);
                result.EndsAlreadyCorrect += plan.Ends.Count(end => end.IsAlreadyCorrect);

                if (plan.NeedsWork)
                {
                    Move(plan, result, progress);
                }

                progress.Report(AdjustStep, (index + 1) / (double)plans.Count);
            }

            // Revit has to catch up with the new axes before anything is sketched against them.
            _document.Regenerate();
            Confirm(plans, progress);

            foreach (var plan in plans)
            {
                foreach (var end in plan.Ends.Where(end => end.NeedsCut))
                {
                    Trim(plan, end, result, progress);
                }
            }

            // The blocks are cut last: what stands in the way of what is only settled once every end
            // has been placed and every skewed one squared off.
            _document.Regenerate();
            Notch(plans, result, progress);
        }

        /// <summary>
        /// Removes the openings this tool owns from both ends of every beam in the run, whether that
        /// end is going to move or not: a block cut for a neighbour that has since moved has to go
        /// even when this beam itself stays put. Only the ends are swept, so a service hole in the
        /// middle of a span is left alone.
        /// </summary>
        private void Clear(IEnumerable<BeamPlan> plans, IProgressSink progress)
        {
            foreach (var plan in plans)
            {
                foreach (var end in plan.Ends)
                {
                    try
                    {
                        var removed = BeamEndCutter.Clear(_document, plan.Geometry, end.End);
                        if (removed > 0)
                        {
                            progress.Log($"id {end.BeamId} end {end.End}: took off {removed} old opening(s).");
                        }
                    }
                    catch (Exception ex)
                    {
                        progress.Log($"id {end.BeamId} end {end.End}: could not clear the old opening - {ex.Message}");
                    }
                }
            }
        }

        /// <summary>Cuts the bearing blocks back where another beam has to come down past them.</summary>
        private void Notch(IEnumerable<BeamPlan> plans, AdjustResult result, IProgressSink progress)
        {
            foreach (var plan in plans)
            {
                try
                {
                    var yieldedTo = plan.Ends
                        .Where(end => !end.IsSkipped)
                        .Select(end => end.SupportId)
                        .ToList();

                    foreach (var note in BeamNotcher.Trim(_document, plan.Geometry.Beam, yieldedTo))
                    {
                        result.NotchesCreated++;
                        progress.Log($"id {plan.Geometry.Beam.Id.ToLong()}: {note}");
                    }

                    // Each beam has to see the blocks already taken off the ones before it, or it goes
                    // on finding a clash with material that is no longer there.
                    _document.Regenerate();
                }
                catch (Exception ex)
                {
                    progress.Log($"id {plan.Geometry.Beam.Id.ToLong()}: the bearing block could not be cut - " +
                                 ex.Message);
                }
            }
        }

        /// <summary>
        /// Checks the axes took their move, while the transaction is still open and before any cutting
        /// starts. It separates a write Revit refused from a cut that pulled the beam back afterwards.
        /// </summary>
        private static void Confirm(IEnumerable<BeamPlan> plans, IProgressSink progress)
        {
            foreach (var plan in plans)
            {
                var line = (plan.Geometry.Beam.Location as LocationCurve)?.Curve as Line;
                if (line == null)
                {
                    continue;
                }

                foreach (var end in plan.Ends.Where(end => end.WillMove))
                {
                    var target = plan.Geometry.TargetAt(end.End, end.MoveMm);
                    var held = line.GetEndPoint(end.End);

                    if (held.DistanceTo(target).FeetToMm() > ToleranceMm)
                    {
                        progress.Log($"id {end.BeamId} end {end.End}: the axis did not take the move - " +
                                     $"wrote {Point(target)}, holds {Point(held)}");
                    }
                }
            }
        }

        private void Move(BeamPlan plan, AdjustResult result, IProgressSink progress)
        {
            try
            {
                BeamAdjuster.Apply(plan.Geometry, plan.Ends);

                foreach (var end in plan.Ends.Where(end => end.WillMove))
                {
                    result.RecordMove(end.BeamId, end.End);
                    progress.Log($"id {end.BeamId} end {end.End}: {end.MoveMm:+0.#;-0.#;0} mm " +
                                 $"against {Describe(end)}");
                }
            }
            catch (Exception ex)
            {
                result.EndsSkipped += plan.Ends.Count(end => end.WillMove);
                progress.Log($"id {plan.Geometry.Beam.Id.ToLong()}: failed - {ex.Message}");
            }
        }

        /// <summary>Squares off one skewed end with an opening.</summary>
        private void Trim(BeamPlan plan, BeamEndPlan end, AdjustResult result, IProgressSink progress)
        {
            try
            {
                if (BeamEndCutter.Cut(_document, plan.Geometry, end))
                {
                    result.CutsCreated++;
                    progress.Log($"id {end.BeamId} end {end.End}: cut square to {Describe(end)}, " +
                                 $"{end.SkewDegrees:0.##} deg off, face at {end.CutPlaneMm:+0.#;-0.#;0} mm");
                }
            }
            catch (Exception ex)
            {
                progress.Log($"id {end.BeamId} end {end.End}: the opening could not be made - {ex.Message}");
            }
        }

        /// <summary>
        /// Pass 4: read the beams back out of the model and compare where each end actually is with
        /// where it was sent. Anything that did not arrive is reported rather than quietly counted as
        /// done.
        /// </summary>
        private static void Check(IList<BeamPlan> plans, AdjustResult result, IProgressSink progress)
        {
            var moved = plans?.Where(plan => plan.HasMove).ToList();
            if (moved == null || moved.Count == 0)
            {
                // The last round found nothing left to do, which is the run settling rather than the
                // run failing - everything it wrote was checked by the round that wrote it.
                progress.Log("The last round moved nothing: every end is where the rules put it.");
                return;
            }

            progress.Report(CheckStep, 0);

            for (var index = 0; index < moved.Count; index++)
            {
                var plan = moved[index];
                var actual = BeamGeometry.Create(plan.Geometry.Beam);

                foreach (var end in plan.Ends.Where(end => end.WillMove))
                {
                    var target = plan.Geometry.TargetAt(end.End, end.MoveMm);
                    var axis = actual?.AxisPointAt(end.End);
                    var solid = actual?.PointAt(end.End);

                    // A cut end is measured on its axis. The axis is what was written, and the solid
                    // behind it deliberately stops short - the opening has taken the wedge off - so
                    // reading the solid back would report every squared-off end as having gone astray.
                    var reached = end.NeedsCut ? axis : solid;
                    var delta = reached == null ? double.NaN : reached.DistanceTo(target).FeetToMm();

                    if (double.IsNaN(delta) || delta > ToleranceMm)
                    {
                        // The axis is reported alongside the solid: an axis that stayed put means the
                        // write did not take, while an axis on target with the solid short of it means
                        // something is cutting the beam back.
                        var axisDelta = axis == null ? double.NaN : axis.DistanceTo(target).FeetToMm();
                        var solidDelta = solid == null ? double.NaN : solid.DistanceTo(target).FeetToMm();

                        result.EndsOffTarget++;
                        progress.Log($"id {end.BeamId} end {end.End}: did not arrive - target " +
                                     $"{Point(target)}; axis at {Point(axis)} (off {axisDelta:0.#}); " +
                                     $"solid at {Point(solid)} (off {solidDelta:0.#})");
                    }
                }

                progress.Report(CheckStep, (index + 1) / (double)moved.Count);
            }

            if (result.EndsOffTarget == 0)
            {
                progress.Log("Every end checked out at the position it was sent to.");
            }
        }

        private static string Point(XYZ point)
        {
            return point == null
                ? "(gone)"
                : $"({point.X.FeetToMm():0.#}, {point.Y.FeetToMm():0.#}, {point.Z.FeetToMm():0.#})";
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
            progress.Log(result.EndsMoved == 0
                ? "Stopped before anything was changed."
                : "Stopped. What was adjusted is kept - undo the command to revert it.");
            return result;
        }
    }
}
