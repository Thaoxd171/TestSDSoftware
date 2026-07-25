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
    /// Places every picked beam, one at a time, sweeping over them again and again until a whole sweep
    /// changes nothing.
    ///
    /// Each beam is put back to where the run found it before it is measured. That is the heart of it.
    /// A beam is placed by measuring what stands in front of its ends, so where two beams meet, the
    /// first to be dealt with is measured against a neighbour still standing where it does not belong.
    /// Measured again later, from the place that first reading sent it to, it reports itself correct -
    /// it is now resting against the neighbour, and nothing in the rules can tell that it should never
    /// have gone there. 2495769 was driven 155 mm out to reach a beam that was itself about to be cut
    /// back, and no number of further sweeps brought it home. Restoring it first is what lets a later
    /// sweep overrule an earlier one.
    ///
    /// Because each beam sees the ones before it as they now stand, the answer depends on the order the
    /// beams are picked in. That is the price of the thing working at all: an end has to be measured
    /// against something, and a neighbour where it belongs is a better something than a neighbour where
    /// it started. Beams whose ends are settled by a parting - which needs nothing but the column the
    /// two share - come out the same whenever they are reached, and they are what the rest hang off.
    ///
    /// The bearing blocks are cut last of all, once every end has stopped moving: what is in the way of
    /// what cannot be known until then.
    ///
    /// The check at the end exists because an assignment that raises no exception is not proof that
    /// anything moved - Revit can put an end straight back - and a tool reporting work it did not do
    /// is worse than one that fails loudly.
    /// </summary>
    public class AdjustBeamRunner
    {
        private const string SweepStep = "Adjust structural beam";
        private const string CheckStep = "Check the result";

        /// <summary>How far off target an end may land before it is called a failure.</summary>
        private const double ToleranceMm = 0.5;

        /// <summary>How many times the whole job is swept before it is called settled anyway.</summary>
        private const int MaximumSweeps = 5;

        private readonly Document _document;

        /// <summary>The openings this run has cut, per beam, so that they can be taken back off.</summary>
        private readonly Dictionary<long, List<ElementId>> _made = new Dictionary<long, List<ElementId>>();

        /// <summary>Where each beam's axis was left by the sweep before, to tell a sweep that changed nothing.</summary>
        private readonly Dictionary<long, XYZ[]> _where = new Dictionary<long, XYZ[]>();

        /// <summary>
        /// The beam each end was found to part from, read off the model before anything moved. Which
        /// two beams pair up is fixed by the design; measured afresh each sweep it flickers as the
        /// partner is nudged into place, so it is settled once here and then held.
        /// </summary>
        private readonly Dictionary<(long Beam, int End), long> _partners =
            new Dictionary<(long, int), long>();

        public AdjustBeamRunner(Document document)
        {
            _document = document;
        }

        public AdjustResult Run(IList<FamilyInstance> beams, AdjustBeamOptions options, IProgressSink progress)
        {
            var result = new AdjustResult { BeamsExamined = beams.Count };
            progress.Log($"{beams.Count} beam(s) picked.");

            // The openings this tool cuts reach past the beams they cut, so that the corners come out
            // clean; Revit says so every time, and it is not worth asking about.
            var expected = ExpectedWarnings.FromOpeningsThatOverreach();
            IList<BeamPlan> plans = null;

            using (var transaction = new Transaction(_document, "Adjust Beams"))
            {
                expected.TakeChargeOf(transaction);
                transaction.Start();

                var starts = Capture(beams, result, progress);
                DetectPartners(starts, options);

                for (var sweep = 1; sweep <= MaximumSweeps && !progress.IsCancelled; sweep++)
                {
                    if (sweep > 1)
                    {
                        progress.Log($"Sweep {sweep}: measuring again now that the beams have moved.");
                    }

                    // Every sweep rebuilds the openings from scratch, so the last one to run leaves the
                    // model complete however many came before it.
                    result.Reset();

                    bool changed;
                    plans = Sweep(starts, options, result, progress, out changed);

                    if (progress.IsCancelled || !changed)
                    {
                        break;
                    }

                    if (sweep == MaximumSweeps)
                    {
                        // Every case met so far settles in three. Running out means two ends are still
                        // moving each other, and saying so beats leaving it to be noticed.
                        result.DidNotSettle = true;
                        progress.Log($"Still moving after {MaximumSweeps} sweeps. The ends listed above " +
                                     "are where this sweep left them, not where the rules want them.");
                    }
                }

                if (!progress.IsCancelled)
                {
                    Notch(plans, result, progress);
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

        /// <summary>Remembers every beam as it stands before anything is written.</summary>
        private static List<BeamStart> Capture(
            IList<FamilyInstance> beams,
            AdjustResult result,
            IProgressSink progress)
        {
            var starts = new List<BeamStart>();

            foreach (var beam in beams)
            {
                var start = BeamStart.Capture(beam);
                if (start == null)
                {
                    result.EndsSkipped += 2;
                    progress.Log($"id {beam.Id.ToLong()}: skipped, no straight axis.");
                }
                else
                {
                    starts.Add(start);
                }
            }

            return starts;
        }

        /// <summary>One pass over every beam. Reports whether any of them ended up somewhere new.</summary>
        private IList<BeamPlan> Sweep(
            IList<BeamStart> starts,
            AdjustBeamOptions options,
            AdjustResult result,
            IProgressSink progress,
            out bool changed)
        {
            var probe = new SupportProbe(_document);
            var plans = new List<BeamPlan>();
            changed = false;

            progress.Report(SweepStep, 0);

            for (var index = 0; index < starts.Count; index++)
            {
                if (progress.IsCancelled)
                {
                    result.WasStopped = true;
                    progress.Log("Stopped. The beams adjusted so far are kept - undo the command to revert them.");
                    break;
                }

                var start = starts[index];

                Undo(start, progress);
                _document.Regenerate();

                var plan = Settle(start, probe, options, result, progress);
                if (plan != null)
                {
                    plans.Add(plan);
                }

                if (Moved(start.Beam))
                {
                    changed = true;
                }

                progress.Report(SweepStep, (index + 1) / (double)starts.Count);
            }

            return plans;
        }

        /// <summary>
        /// Puts one beam back the way the run found it. The openings come off first: an opening is
        /// sketched against its host, and while one is there Revit quietly refuses to move the beam
        /// under it.
        /// </summary>
        private void Undo(BeamStart start, IProgressSink progress)
        {
            var id = start.Beam.Id.ToLong();

            if (_made.TryGetValue(id, out var openings))
            {
                foreach (var opening in openings)
                {
                    try
                    {
                        _document.Delete(opening);
                    }
                    catch (Exception ex)
                    {
                        progress.Log($"id {id}: could not take back an opening - {ex.Message}");
                    }
                }

                openings.Clear();
            }

            start.Restore();
        }

        /// <summary>Measures one beam where it started, decides both ends, and writes them.</summary>
        private BeamPlan Settle(
            BeamStart start,
            SupportProbe probe,
            AdjustBeamOptions options,
            AdjustResult result,
            IProgressSink progress)
        {
            var geometry = BeamGeometry.Create(start.Beam);
            if (geometry == null)
            {
                result.EndsSkipped += 2;
                progress.Log($"id {start.Beam.Id.ToLong()}: skipped, no geometry to read.");
                return null;
            }

            var ends = Decide(geometry, probe, options);

            // Whatever the model already carried at the ends this run is about to work on comes off
            // now. Only those ends: an end the run leaves exactly as it is keeps its cut, because it
            // is not this tool's place to take one out of a model and put nothing back, and because
            // the end was measured with that cut in place and found right because of it.
            if (ClearOld(geometry, ends, progress) > 0)
            {
                _document.Regenerate();
                geometry = BeamGeometry.Create(start.Beam);
                if (geometry == null)
                {
                    result.EndsSkipped += 2;
                    return null;
                }

                ends = Decide(geometry, probe, options);
            }

            result.EndsSkipped += ends.Count(end => end.IsSkipped);
            result.EndsAlreadyCorrect += ends.Count(end => end.IsAlreadyCorrect);

            var plan = new BeamPlan(geometry, ends);

            if (plan.NeedsWork)
            {
                Move(plan, result, progress);
                _document.Regenerate();
                Confirm(plan, progress);

                foreach (var end in plan.Ends.Where(end => end.NeedsCut))
                {
                    Trim(plan, end, result, progress);
                }

                _document.Regenerate();
            }

            return plan;
        }

        private IList<BeamEndPlan> Decide(
            BeamGeometry geometry,
            SupportProbe probe,
            AdjustBeamOptions options)
        {
            var id = geometry.Beam.Id.ToLong();
            return BeamGeometry.Ends
                .Select(end => SolveEnd(
                    geometry, probe, options, end,
                    _partners.TryGetValue((id, end), out var partner) ? partner : 0))
                .ToList();
        }

        private static BeamEndPlan SolveEnd(
            BeamGeometry geometry,
            SupportProbe probe,
            AdjustBeamOptions options,
            int end,
            long forcedPartnerId)
        {
            return BeamEndSolver.Solve(
                geometry.Beam.Id.ToLong(),
                end,
                probe.Probe(geometry, end),
                options,
                geometry.LengthMm,
                geometry.WidthMm,
                geometry.AxisOffsetMm(end),
                geometry.AcrossAt(end).Least,
                geometry.AcrossAt(end).Most,
                forcedPartnerId);
        }

        /// <summary>
        /// Reads off which beams part from which, before anything has been touched. A parting is an
        /// agreement between two ends about a plane they share, and which end gives way was fixed when
        /// the model was drawn; recognising it depends on the two beams still sitting where they were
        /// drawn, which they are only at the outset. Held from here on, so that a beam nudged into place
        /// mid-run cannot make its partner forget the two are a pair.
        /// </summary>
        private void DetectPartners(IList<BeamStart> starts, AdjustBeamOptions options)
        {
            var probe = new SupportProbe(_document);

            foreach (var start in starts)
            {
                var geometry = BeamGeometry.Create(start.Beam);
                if (geometry == null)
                {
                    continue;
                }

                foreach (var end in BeamGeometry.Ends)
                {
                    var plan = SolveEnd(geometry, probe, options, end, 0);
                    if (!plan.IsSkipped && plan.Support == SupportKind.InlineBeam)
                    {
                        _partners[(geometry.Beam.Id.ToLong(), end)] = plan.SupportId;
                    }
                }
            }
        }

        /// <summary>Takes off whatever the model already had at the ends about to be worked on.</summary>
        private int ClearOld(BeamGeometry geometry, IEnumerable<BeamEndPlan> ends, IProgressSink progress)
        {
            var removed = 0;

            foreach (var end in ends.Where(end => end.WillMove || end.NeedsCut))
            {
                try
                {
                    var taken = BeamEndCutter.Clear(_document, geometry, end.End);
                    if (taken > 0)
                    {
                        removed += taken;
                        progress.Log($"id {end.BeamId} end {end.End}: took off {taken} old opening(s).");
                    }
                }
                catch (Exception ex)
                {
                    progress.Log($"id {end.BeamId} end {end.End}: could not clear the old opening - {ex.Message}");
                }
            }

            return removed;
        }

        /// <summary>Whether the beam's axis is anywhere other than the sweep before left it.</summary>
        private bool Moved(FamilyInstance beam)
        {
            var line = (beam.Location as LocationCurve)?.Curve as Line;
            if (line == null)
            {
                return false;
            }

            var now = new[] { line.GetEndPoint(0), line.GetEndPoint(1) };
            var id = beam.Id.ToLong();

            var changed = !_where.TryGetValue(id, out var was)
                          || was[0].DistanceTo(now[0]).FeetToMm() > ToleranceMm
                          || was[1].DistanceTo(now[1]).FeetToMm() > ToleranceMm;

            _where[id] = now;
            return changed;
        }

        /// <summary>Cuts the bearing blocks back where another beam has to come down past them.</summary>
        private void Notch(IEnumerable<BeamPlan> plans, AdjustResult result, IProgressSink progress)
        {
            foreach (var plan in plans ?? Enumerable.Empty<BeamPlan>())
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
        /// Checks the axis took its move, before any cutting starts. It separates a write Revit refused
        /// from a cut that pulled the beam back afterwards.
        /// </summary>
        private static void Confirm(BeamPlan plan, IProgressSink progress)
        {
            var line = (plan.Geometry.Beam.Location as LocationCurve)?.Curve as Line;
            if (line == null)
            {
                return;
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

        /// <summary>
        /// Squares off one skewed end with an opening for each plane it is trimmed back to. Nearly
        /// always one; an end running into a corner has two, and each is reported on its own so that a
        /// half-finished end cannot pass for a whole one.
        /// </summary>
        private void Trim(BeamPlan plan, BeamEndPlan end, AdjustResult result, IProgressSink progress)
        {
            foreach (var cut in end.Cuts)
            {
                try
                {
                    var opening = BeamEndCutter.Cut(_document, plan.Geometry, end, cut, out var refused);

                    if (refused == null)
                    {
                        result.CutsCreated++;
                        Remember(end.BeamId, opening);
                        progress.Log($"id {end.BeamId} end {end.End}: cut square to id {cut.AgainstId}, " +
                                     $"{cut.SkewDegrees:0.##} deg off, face at {cut.PlaneMm:+0.#;-0.#;0} mm, " +
                                     $"taking off {cut.DepthMm:0.#} mm");
                    }
                    else
                    {
                        result.CutsRefused++;
                        progress.Log($"id {end.BeamId} end {end.End}: NOT cut although it wanted a " +
                                     $"{cut.SkewDegrees:0.##} deg cut at {cut.PlaneMm:+0.#;-0.#;0} mm - {refused}");
                    }
                }
                catch (Exception ex)
                {
                    progress.Log($"id {end.BeamId} end {end.End}: the opening at " +
                                 $"{cut.PlaneMm:+0.#;-0.#;0} mm could not be made - {ex.Message}");
                }
            }
        }

        private void Remember(long beamId, Opening opening)
        {
            if (opening == null)
            {
                return;
            }

            if (!_made.TryGetValue(beamId, out var openings))
            {
                openings = new List<ElementId>();
                _made[beamId] = openings;
            }

            openings.Add(opening.Id);
        }

        /// <summary>
        /// Reads the beams back out of the model and compares where each end actually is with where it
        /// was sent. Anything that did not arrive is reported rather than quietly counted as done.
        /// </summary>
        private static void Check(IList<BeamPlan> plans, AdjustResult result, IProgressSink progress)
        {
            var moved = plans?.Where(plan => plan.HasMove).ToList();
            if (moved == null || moved.Count == 0)
            {
                // The last sweep found nothing left to do, which is the run settling rather than the
                // run failing - everything it wrote was checked by the sweep that wrote it.
                progress.Log("The last sweep moved nothing: every end is where the rules put it.");
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
