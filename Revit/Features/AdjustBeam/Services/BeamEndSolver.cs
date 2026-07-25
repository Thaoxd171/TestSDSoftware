using System;
using System.Collections.Generic;
using System.Linq;
using SDSoftware.RevitTest.Features.AdjustBeam.Models;

namespace SDSoftware.RevitTest.Features.AdjustBeam.Services
{
    /// <summary>
    /// Decides where one beam end belongs. Everything it needs has already been reduced to distances
    /// in millimetres by <see cref="SupportProbe"/>, so this class holds the rules on their own,
    /// without a single call into the Revit API.
    ///
    /// The rules, in the order they win:
    ///   1. a beam meeting this one head on - both beams stop half the inline gap short of the point
    ///      they share: the centre of the pillar they meet over when there is one, otherwise the
    ///      midpoint between the two ends as they stand. The parting plane follows the pillar face, so
    ///      a beam arriving square to the pillar keeps a square end and only a skewed one is cut;
    ///   2. a beam crossing this one - stop the perpendicular gap short of its body. This is what the
    ///      two "extend to beam body" options ask for: the end runs past the pillar or wall it sits on
    ///      until it reaches the other beam;
    ///   3. a wall - stop the wall clearance short of its near face;
    ///   4. a pillar - run over it and stop the pillar clearance short of its far face.
    ///
    /// A pillar comes last because it stands under the beam, not in its way: the beam covers it and
    /// carries on to the wall behind. That is also why its clearance is measured from the far face,
    /// where a wall - which the beam runs into rather than over - is measured from the near one.
    /// Either way the pillar caps the answer: the end may not hang out past the far face of the
    /// pillar carrying it.
    /// </summary>
    public static class BeamEndSolver
    {
        /// <summary>A beam is never shortened below this length.</summary>
        public const double MinimumBeamLengthMm = 200;

        /// <summary>
        /// How thick the wedge left on a skewed end has to be before it is worth an opening.
        ///
        /// Measured along the beam, from the corner that reaches the face first to the one that
        /// reaches it last, so it is the width of the beam times the tangent of the skew - a hundredth
        /// of a turn across half a metre of beam is a couple of centimetres of wedge, and nobody cuts
        /// that off. Reading it as a thickness rather than an angle is what makes it a judgement
        /// anyone can make: an angle that matters on a wide beam does not on a narrow one.
        /// </summary>
        public const double NegligibleWedgeMm = 30;

        /// <summary>
        /// How much of its own thickness a wall has to be missing before it counts as opened up for
        /// the beam. Seven tenths leaves room for the ray reading a millimetre or two short of a solid
        /// wall without calling it a hole; the wall this was read off is missing well over half.
        /// </summary>
        private const double OpenedShare = 0.7;

        /// <summary>How far the corner of a face may miss the corner the beam reaches and still count.</summary>
        private const double ContactToleranceMm = 1;

        /// <summary>
        /// How much of the width a crossing beam has to stand across before this end is held clear of
        /// it. Below that it is clipping a corner, not barring the way.
        ///
        /// The measured ends leave a wide gap to put this in: the one crossing beam the reference model
        /// has an end resting against covers a sixteenth of the width, and the least any beam covers
        /// that does place an end is a little under a quarter.
        /// </summary>
        private const double SliverShare = 0.15;

        /// <summary>How near two limits have to be before they count as putting the end in one place.</summary>
        private const double TieToleranceMm = 1;

        /// <param name="axisOffsetMm">
        /// How far the location line already runs past where the solid stops at this end. Nought on a
        /// plain end; on one that has been cut it is what the axis has already travelled, and leaving
        /// it out would have the end asked to make the same journey over again.
        /// </param>
        /// <param name="acrossLeastMm">
        /// How far the beam reaches either side of its own axis, signed. Half the width each way when
        /// left out, which is right for any section centred on its line and wrong for a precast one
        /// that is not: the cut has to sweep past the material that is actually there.
        /// </param>
        /// <param name="forcedPartnerId">
        /// The beam this end was found to part from, read off the model before anything moved. Whether
        /// two beams are a pair is a fact about the design, not about where the run has pushed them to
        /// this moment; measured afresh each sweep it flickers as the partner comes and goes, and the
        /// end never settles. Nought when the end parts from nothing.
        /// </param>
        public static BeamEndPlan Solve(
            long beamId,
            int end,
            IReadOnlyList<SupportCandidate> supports,
            AdjustBeamOptions options,
            double beamLengthMm,
            double beamWidthMm,
            double axisOffsetMm = 0,
            double acrossLeastMm = 0,
            double acrossMostMm = 0,
            long forcedPartnerId = 0)
        {
            if (acrossMostMm <= acrossLeastMm)
            {
                acrossLeastMm = -beamWidthMm / 2;
                acrossMostMm = beamWidthMm / 2;
            }

            var plan = new BeamEndPlan { BeamId = beamId, End = end };

            var limits = Weigh(supports, options, beamWidthMm, forcedPartnerId);
            var decision = limits.FirstOrDefault(limit => limit.Governs);
            if (decision == null)
            {
                plan.Support = SupportKind.None;
                plan.SkipReason = "nothing found in front of this end";
                return plan;
            }

            plan.Support = decision.Support.Kind;
            plan.SupportDescription = decision.Support.Description;
            plan.SupportId = decision.Support.Id;
            plan.CutAgainstId = decision.CutAgainstId;
            plan.SkewDegrees = decision.SkewDegrees;

            var finish = Finish(limits, decision, acrossLeastMm, acrossMostMm);
            plan.MoveMm = finish.ReachMm;
            plan.Cuts = finish.Cuts;
            plan.CutPlaneMm = finish.Cuts.Count == 0 ? (double?)null : finish.Cuts[0].PlaneMm;

            plan.AxisTravelMm = plan.MoveMm - axisOffsetMm;

            if (beamLengthMm + plan.MoveMm < MinimumBeamLengthMm)
            {
                plan.SkipReason = $"the beam would be shorter than {MinimumBeamLengthMm:0} mm";
            }

            return plan;
        }

        private static double Radians(double degrees) => degrees * Math.PI / 180;

        /// <summary>How near two planes have to be before cutting to both would be cutting twice.</summary>
        private const double SamePlaneMm = 1;

        /// <summary>
        /// The least a plane has to take off to be given an opening of its own, on an end that is being
        /// shaped by more than one. Not the same judgement as <see cref="NegligibleWedgeMm"/>, which
        /// asks whether an end is worth cutting at all: here it already is, and what is left is only to
        /// keep a plane that grazes the corner by a hair from earning an opening.
        /// </summary>
        private const double SmallestBiteMm = 1;

        /// <summary>Below this a normal counts as having no sideways lean at all.</summary>
        private const double Flat = 1e-9;

        /// <summary>
        /// Where the end finishes up and which planes it is trimmed back to.
        ///
        /// A square cut through a skewed end reaches the face at one corner and falls short at the
        /// other, so the axis runs on until the last corner has cleared the plane as well and an
        /// opening takes the wedge back off. Run out that far and the finished end is the plane, whole.
        ///
        /// Where two planes share the end it need not run nearly so far, because between them they
        /// take off the whole square end sooner - and it must not, because running on would drive it
        /// through the second face. So the axis goes to the first place where nothing of the square
        /// end survives, which comes to the same thing when there is only one plane.
        /// </summary>
        private static (double ReachMm, IList<BeamEndCut> Cuts) Finish(
            IReadOnlyList<BeamEndLimit> limits,
            BeamEndLimit decision,
            double leastMm,
            double mostMm)
        {
            var planes = Planes(limits, decision).ToList();
            var reach = decision.TargetMm;

            // A plane biting too little to be worth an opening is dropped, and the end then has to run
            // further out to make up for what it is no longer taking off. Dropping one can only push
            // the end out, which can only make the rest bite deeper, so this settles rather than cycles.
            //
            // Only an end with a single plane is judged that way, though. A nearly square face on its
            // own means an end that should simply be moved rather than cut, and cutting it would be an
            // opening to no purpose. Once a second plane is in play the end is a shape rather than a
            // face, and the small plane is what finishes the corner the big one leaves: 1856700 END 1
            // meets a wall 1.08 degrees off square, worth a wedge of ten millimetres, and the reference
            // model cuts it - because without it the corner keeps that ten millimetres.
            while (planes.Count > 0)
            {
                reach = Reach(planes, leastMm, mostMm);
                var least = planes.Count > 1 ? SmallestBiteMm : NegligibleWedgeMm;

                var biting = planes
                    .Where(plane => Depth(plane, reach, leastMm, mostMm) >= least)
                    .ToList();

                if (biting.Count == planes.Count)
                {
                    break;
                }

                planes = biting;
            }

            if (planes.Count == 0)
            {
                return (decision.TargetMm, planes);
            }

            foreach (var plane in planes)
            {
                plane.DepthMm = Depth(plane, reach, leastMm, mostMm);
            }

            return (reach, planes);
        }

        /// <summary>
        /// The planes the end has to be trimmed back to: the one it was placed against, and any wall
        /// its corners still run past once it is there.
        ///
        /// Only a wall joins the governing plane. A wall stands the whole depth of the beam and more,
        /// so every corner of the end has to clear it; a pillar is underneath, carrying the beam rather
        /// than barring it, and a beam alongside has already been allowed for by whatever parted the
        /// two. Nor a wall the beam passes through - one opened up to take the end is a bearing, and
        /// its face is not there to be cut to. Nor one asking the end in nearer than it has been put:
        /// that limit was overruled when the place was chosen, and cutting to it would take back what
        /// the decision gave.
        /// </summary>
        private static IEnumerable<BeamEndCut> Planes(
            IReadOnlyList<BeamEndLimit> limits,
            BeamEndLimit decision)
        {
            var governing = Plane(decision);
            yield return governing;

            foreach (var limit in limits)
            {
                if (ReferenceEquals(limit, decision)
                    || limit.Dropped
                    || limit.Opened
                    || limit.Support?.Kind != SupportKind.Wall
                    || limit.TargetMm < decision.TargetMm - TieToleranceMm)
                {
                    continue;
                }

                var plane = Plane(limit);
                if (Math.Abs(plane.AcrossNormal) > Flat && !SamePlane(plane, governing))
                {
                    yield return plane;
                }
            }
        }

        private static BeamEndCut Plane(BeamEndLimit limit)
        {
            return new BeamEndCut
            {
                PlaneMm = limit.TargetMm,
                AlongNormal = Math.Cos(Radians(limit.SkewDegrees)),
                AcrossNormal = limit.AcrossNormal,
                SkewDegrees = limit.SkewDegrees,
                AgainstId = limit.CutAgainstId,
            };
        }

        private static bool SamePlane(BeamEndCut plane, BeamEndCut other)
        {
            return Math.Abs(plane.PlaneMm - other.PlaneMm) < SamePlaneMm
                   && Math.Abs(plane.AlongNormal - other.AlongNormal) < 0.01
                   && Math.Abs(plane.AcrossNormal - other.AcrossNormal) < 0.01;
        }

        /// <summary>
        /// How deep a plane bites into the end, along the axis, measured at the corner it takes most
        /// from. With a single plane this is the whole wedge.
        /// </summary>
        private static double Depth(BeamEndCut plane, double reachMm, double leastMm, double mostMm)
        {
            return reachMm - plane.PlaneMm + Lean(plane, leastMm, mostMm);
        }

        /// <summary>
        /// How far past the axis the corner of the end that meets this plane first sticks out, measured
        /// along the beam. Zero on a face met square; on a skewed one it is the side the face leans
        /// away from, so an end reaching further one side of its axis than the other gets the right
        /// answer rather than an average of the two.
        /// </summary>
        private static double Lean(BeamEndCut plane, double leastMm, double mostMm)
        {
            var far = plane.AcrossNormal > 0 ? mostMm : leastMm;
            return plane.AcrossNormal / plane.AlongNormal * far;
        }

        /// <summary>The same for the corner that meets it last. The two together are the whole wedge.</summary>
        private static double NearLean(BeamEndCut plane, double leastMm, double mostMm)
        {
            var near = plane.AcrossNormal > 0 ? leastMm : mostMm;
            return -plane.AcrossNormal / plane.AlongNormal * near;
        }

        /// <summary>
        /// How far out the axis has to go before no part of the square end survives the planes.
        ///
        /// Coverage only ever improves as the end runs out - every plane sweeps one way across the
        /// width - so the answer is found by halving the interval between a reach that clearly leaves
        /// something standing and one that clearly does not.
        /// </summary>
        private static double Reach(IList<BeamEndCut> planes, double leastMm, double mostMm)
        {
            // Short of every plane's first corner nothing is touched; each plane on its own has the
            // whole end clear by the time its last corner is past, so the nearest of those covers it.
            var low = planes.Select(plane => plane.PlaneMm - Lean(plane, leastMm, mostMm)).Min() - 1;
            var high = planes.Select(plane => plane.PlaneMm + NearLean(plane, leastMm, mostMm)).Min();

            for (var step = 0; step < 60 && high - low > 1e-9; step++)
            {
                var middle = (low + high) / 2;
                if (Covered(planes, middle, leastMm, mostMm))
                {
                    high = middle;
                }
                else
                {
                    low = middle;
                }
            }

            return high;
        }

        /// <summary>
        /// Whether the planes between them take off the whole of the square end sitting at that reach.
        ///
        /// Each plane cuts everything to one side of a line across the end, so what it removes is a
        /// half of the width. The end is gone when the halves overlap, or when one of them covers the
        /// width on its own.
        /// </summary>
        private static bool Covered(
            IEnumerable<BeamEndCut> planes,
            double reachMm,
            double leastMm,
            double mostMm)
        {
            var below = double.NegativeInfinity;
            var above = double.PositiveInfinity;

            foreach (var plane in planes)
            {
                var slack = plane.AlongNormal * (plane.PlaneMm - reachMm);

                if (Math.Abs(plane.AcrossNormal) < Flat)
                {
                    // Met square: nothing survives the moment the end is past it.
                    if (slack < 0)
                    {
                        return true;
                    }

                    continue;
                }

                var edge = slack / plane.AcrossNormal;
                if (plane.AcrossNormal > 0)
                {
                    above = Math.Min(above, edge);
                }
                else
                {
                    below = Math.Max(below, edge);
                }
            }

            return above < below || below > mostMm || above < leastMm;
        }

        /// <summary>
        /// Every limit on this end, the governing one marked, in the order they were worked out. The
        /// tool reads only the one that governs; the report reads all of them, so that an end landing
        /// somewhere unexpected shows which limit put it there instead of having to be worked back to
        /// from the answer.
        /// </summary>
        public static IReadOnlyList<BeamEndLimit> Weigh(
            IReadOnlyList<SupportCandidate> supports,
            AdjustBeamOptions options,
            double beamWidthMm,
            long forcedPartnerId = 0)
        {
            var found = Collect(supports, options, beamWidthMm, forcedPartnerId).ToList();
            var governing = found.FirstOrDefault(limit => limit.Settles) ?? Nearest(found);

            foreach (var limit in found)
            {
                limit.Governs = ReferenceEquals(limit, governing);
            }

            return found;
        }

        /// <summary>
        /// The limit that holds the end back the most, and where several hold it to the same place,
        /// the one that asks the least of it.
        ///
        /// Limits agreeing to within a millimetre are not disagreeing at all, and choosing between
        /// them on a tenth of one is choosing on noise - while what they ask for differs in kind, one
        /// leaving the end square and the other squaring it off with an opening. A beam bearing on a
        /// corbel that juts from a wall meets both at once, and the corbel it sits on is square to it
        /// where the wall behind is a few degrees off; letting the wall win by a tenth of a millimetre
        /// puts an angled cut on an end that wants none.
        /// </summary>
        /// <summary>
        /// Whether the wall has been cut away where this beam meets it.
        ///
        /// A beam running into a solid wall meets its whole thickness - 1751117 is 180 thick and met at
        /// 27 degrees, and the beam passes through 202 mm of it, which is 180 over the cosine to the
        /// millimetre. Meeting markedly less than that means the material is not all there: 1662890 is
        /// 220 thick and the beam passes through 93 mm of it, because its profile has been edited to
        /// let the beam in. Such a wall is a bearing rather than an obstruction, and the end stops
        /// short of its far side.
        ///
        /// Read off three walls. It holds for all three and rests on a measurement rather than a
        /// threshold picked to fit, but three is three.
        /// </summary>
        private static bool OpenedForTheBeam(SupportCandidate wall)
        {
            if (wall.ThicknessMm <= 0 || wall.SpanMm <= 0)
            {
                return false;
            }

            // Along the beam, not across it: a wall met at an angle is longer to pass through than it
            // is thick, and comparing the two without allowing for that calls every skewed wall solid.
            var expected = wall.ThicknessMm / Math.Cos(Radians(wall.SkewDegrees));
            return wall.SpanMm < expected * OpenedShare;
        }

        private static BeamEndLimit Nearest(IEnumerable<BeamEndLimit> found)
        {
            var live = found.Where(limit => !limit.Dropped).ToList();
            if (live.Count == 0)
            {
                return null;
            }

            var nearest = live.Min(limit => limit.TargetMm);

            return live
                .Where(limit => limit.TargetMm <= nearest + TieToleranceMm)
                .OrderBy(limit => limit.CutsTheEnd ? 1 : 0)
                .ThenBy(limit => limit.TargetMm)
                .First();
        }

        private static IList<BeamEndLimit> Collect(
            IReadOnlyList<SupportCandidate> supports,
            AdjustBeamOptions options,
            double beamWidthMm,
            long forcedPartnerId)
        {
            var pillar = Bearing(supports);
            var limits = new List<BeamEndLimit>();

            var inline = FacingPartner(supports, forcedPartnerId);
            if (inline != null)
            {
                // Half the gap away from the point the two beams share. The pillar sets the parting
                // plane when there is one - it is the pillar face both beams are squared up to.
                //
                // This settles the end on its own rather than joining the other limits. A parting is
                // an agreement between two ends about a plane they share, and which of the two gives
                // way was decided in making it. Every other beam at the joint is on the yielding side
                // of some such agreement, so letting one of them push back here would have each beam
                // driving the other and nothing ever settling.
                var against = pillar ?? inline;
                var shared = pillar?.CentreAlongMm ?? inline.NearMm / 2;

                limits.Add(new BeamEndLimit
                {
                    Support = inline,
                    TargetMm = shared - AlongAxis(options.InlineGapMm / 2, against),
                    SkewDegrees = against.SkewDegrees,
                    AcrossNormal = against.EntryAcross,
                    CutAgainstId = against.Id,
                    Settles = true,
                    Note = "parting over " + (pillar == null ? "the midpoint" : "pillar " + pillar.Id),
                });
            }

            limits.AddRange(Crossings(supports, options, beamWidthMm));
            var crossings = limits.Count(limit => limit.Support.Kind == SupportKind.CrossingBeam && !limit.Dropped);

            foreach (var wall in supports.Where(support => support.Kind == SupportKind.Wall))
            {
                // Cleared, the option stops the end at the wall rather than letting it run over the
                // wall to reach the beam beyond.
                //
                // Held off the face the end arrives at, not off the nearest material. A beam coming at
                // the end of a wall from the side is already past the plane of that end face while the
                // wall's body is still ahead, and it is the face it has to clear: the end is set
                // parallel to it and stood off it, exactly as it is at any other wall. The two readings
                // agree wherever a beam runs squarely into a wall, which is most of them.
                //
                // Unless the wall has been opened up to take the beam, in which case it is a bearing
                // and the end stops short of the far side, the way it does on a pillar. The entry face
                // is no use there: its plane still runs the whole length of the wall, unbroken, while
                // the material at that one spot has been cut away.
                var opened = OpenedForTheBeam(wall);

                limits.Add(new BeamEndLimit
                {
                    Support = wall,
                    TargetMm = (opened ? wall.FarMm : wall.EntryFaceMm ?? wall.NearMm)
                               - AlongAxis(options.WallClearanceMm, wall),
                    SkewDegrees = wall.SkewDegrees,
                    AcrossNormal = wall.EntryAcross,
                    CutAgainstId = wall.Id,
                    Opened = opened,
                    Dropped = options.ExtendToBeamBodyAtWall && crossings > 0,
                    Note = options.ExtendToBeamBodyAtWall && crossings > 0
                        ? "let go: the end may run over the wall to reach a beam"
                        : opened
                            ? "opened up for the beam, so measured off its far side"
                            : null,
                });
            }

            if (pillar != null)
            {
                // The beam bears on the pillar, so it runs over it and stops short of the far edge -
                // not of the edge it arrives at. This is a limit on how far the end may hang out over
                // its bearing, so it holds whatever else is going on.
                limits.Add(new BeamEndLimit
                {
                    Support = pillar,
                    TargetMm = pillar.FarMm - AlongAxis(options.PillarClearanceMm, pillar),
                    SkewDegrees = pillar.SkewDegrees,
                    AcrossNormal = pillar.EntryAcross,
                    CutAgainstId = pillar.Id,
                    Note = "clear of its far face",
                });

                if (!options.ExtendToBeamBodyAtPillar)
                {
                    limits.Add(new BeamEndLimit
                    {
                        Support = pillar,
                        TargetMm = pillar.NearMm - AlongAxis(options.PillarClearanceMm, pillar),
                        SkewDegrees = pillar.SkewDegrees,
                        AcrossNormal = pillar.EntryAcross,
                        CutAgainstId = pillar.Id,
                        Note = "stopping at its near face",
                    });
                }
            }

            foreach (var limit in limits)
            {
                limit.WedgeMm = beamWidthMm * Math.Tan(Radians(limit.SkewDegrees));
            }

            return limits;
        }

        /// <summary>
        /// The pillar this end lands on.
        ///
        /// The one it is standing on comes first: the end is somewhere between that pillar's two faces.
        /// Failing that, one it has already run out over beats one it has not reached yet - an end
        /// hanging off the back of its bearing is exactly what the far face rule is there to pull back,
        /// while a pillar further along cannot be what the end is sitting on. Anything too far behind
        /// has already been dropped by the probe.
        /// </summary>
        private static SupportCandidate Bearing(IReadOnlyList<SupportCandidate> supports)
        {
            var pillars = supports.Where(support => support.Kind == SupportKind.Pillar).ToList();

            var standingOn = pillars.FirstOrDefault(pillar => pillar.NearMm <= 0 && pillar.FarMm >= 0);
            if (standingOn != null)
            {
                return standingOn;
            }

            var passed = pillars
                .Where(pillar => pillar.FarMm < 0)
                .OrderByDescending(pillar => pillar.FarMm)
                .FirstOrDefault();

            return passed ?? pillars.OrderBy(pillar => pillar.NearMm).FirstOrDefault();
        }

        /// <summary>
        /// What the crossing beams ask of this end.
        ///
        /// A beam standing squarely across the whole width is a face to seat against, and a beam cut
        /// off at an angle leaves only the sharp tip of the wedge. Where there is a face to go by, the
        /// tips are let go: holding a full clearance off every tip in a crowded joint would push the
        /// beams apart for the sake of slivers, and the tips are there because two other beams have
        /// already been parted. Where there is no face, the tip is all there is, and it is cleared.
        ///
        /// A beam barely reaching into the width is let go before any of that. It is the same thought -
        /// a sliver is not worth pushing a beam off - said by measuring the sliver rather than by
        /// waiting for some other beam to present a face. 2975257 stands across a sixteenth of 1856700
        /// and the reference model has the two resting against each other; every crossing beam that
        /// really places an end covers between a fifth and two thirds of it.
        /// </summary>
        private static IList<BeamEndLimit> Crossings(
            IReadOnlyList<SupportCandidate> supports,
            AdjustBeamOptions options,
            double beamWidthMm)
        {
            var all = supports
                .Where(support => support.Kind == SupportKind.CrossingBeam)
                .Select(support => CrossingAgainst(support, options, beamWidthMm))
                .ToList();

            foreach (var sliver in all.Where(limit => limit.Support.InsideMm < beamWidthMm * SliverShare))
            {
                sliver.Dropped = true;
                sliver.Note = $"let go: it stands across only {sliver.Support.InsideMm:0.#} of the " +
                              $"{beamWidthMm:0.#} this end sweeps";
            }

            var live = all.Where(limit => !limit.Dropped).ToList();
            if (live.Any(limit => limit.MeetsAFace))
            {
                foreach (var tip in live.Where(limit => !limit.MeetsAFace))
                {
                    tip.Dropped = true;
                    tip.Note = "let go: only a tip reaches in, and a whole face was found";
                }
            }

            return all;
        }

        /// <summary>
        /// Where one crossing beam puts this end, and whether the end is cut to match its face.
        ///
        /// A face standing across the beam's whole width is met corner first, and the corner sits
        /// exactly half a width of skew nearer than the point where the axis crosses the face plane.
        /// When the beam touches sooner than that the face must stop somewhere inside the width - it
        /// is the end of another beam, cut off short - and then there is nothing to line up with: the
        /// end stays square and simply stops the gap short of the corner it would hit.
        ///
        /// Meeting a face at an angle is what the opening is for: the end is run out past the plane
        /// and trimmed back parallel to it. A pillar is the exception - the beam sits on top of one
        /// rather than up against it, so nothing there has to be squared up to.
        /// </summary>
        private static BeamEndLimit CrossingAgainst(
            SupportCandidate support,
            AdjustBeamOptions options,
            double beamWidthMm)
        {
            var gap = options.PerpendicularGapMm;
            var acrossTheWidth = support.NearMm - beamWidthMm / 2 * Math.Tan(Radians(support.SkewDegrees));

            // A tip is something ahead that the end can be kept clear of. Material already lying
            // alongside means the end has driven past the face and into the flank of the other beam,
            // and then it is that flank the end has to be trimmed back to, however little of the
            // beam's width the flank happens to cover.
            var meetsAFace = !support.ClearMm.HasValue
                             || support.ClearMm.Value <= acrossTheWidth + ContactToleranceMm
                             || support.ClearMm.Value < 0;

            return meetsAFace
                ? new BeamEndLimit
                {
                    Support = support,
                    TargetMm = support.NearMm - AlongAxis(gap, support),
                    SkewDegrees = support.SkewDegrees,
                    AcrossNormal = support.EntryAcross,
                    CutAgainstId = support.Id,
                    MeetsAFace = true,
                }
                : new BeamEndLimit
                {
                    Support = support,
                    TargetMm = support.ClearMm.Value - gap,
                    SkewDegrees = 0,
                    CutAgainstId = support.Id,
                    Note = "only a tip reaches in, so the end stays square",
                };
        }

        /// <summary>
        /// Turns a clearance measured across the face of a support into the distance the end travels
        /// along its own axis. They are the same when the beam arrives square and grow apart as it
        /// skews: a beam meeting a face at an angle has to run further to keep the same gap.
        /// </summary>
        private static double AlongAxis(double clearanceMm, SupportCandidate support)
        {
            var skew = support?.SkewDegrees ?? 0;
            return skew <= 0 ? clearanceMm : clearanceMm / Math.Cos(Radians(skew));
        }

        /// <summary>
        /// The beam this end is really parting from, or null when it is not parting from one at all.
        ///
        /// Facing each other is not enough on its own. Two beams can run out at each other across a
        /// joint with two more beams standing between them - near enough, and within the angles - and
        /// they are then no more a pair than any two strangers either side of a crowd. So the partner
        /// also has to be the first thing this end meets: anything nearer is what the end is actually
        /// stopping against, and the beam beyond it is somebody else's business.
        ///
        /// That last test cannot be trusted once the run is under way. Whether the crossing beam stands
        /// in front of the partner is read from where the partner sits, and as the run places it the
        /// partner retreats to the parting plane, so a crossing beam that was behind it comes to be in
        /// front and the pair breaks. Which two beams part is a fact about the design, settled before
        /// anything moved: when it has been, the caller names the partner and the test is not asked
        /// again.
        /// </summary>
        private static SupportCandidate FacingPartner(
            IReadOnlyList<SupportCandidate> supports,
            long forcedPartnerId)
        {
            if (forcedPartnerId != 0)
            {
                return supports.FirstOrDefault(
                    support => support.Kind == SupportKind.InlineBeam && support.Id == forcedPartnerId);
            }

            // Only one beam can be the partner, so the choice between two facing this end matters.
            // A beam on the same line as this one wins it: two beams broken over the column between
            // them are as plain a pair as the design ever states, where one arriving at an angle is
            // only inferred from where it happens to point.
            //
            // Nearness cannot settle it, because the beams most in need of parting are the ones that
            // have run into each other, and a beam this end is already buried in reads as nearer than
            // one resting exactly against it. 1856006 met its own continuation 1856062 at nought and a
            // beam crossing the joint at minus five hundred, and picked the crossing beam.
            var inline = supports
                .Where(support => support.Kind == SupportKind.InlineBeam)
                .OrderBy(support => support.Collinear ? 0 : 1)
                .ThenBy(support => support.NearMm)
                .FirstOrDefault();

            if (inline == null)
            {
                return null;
            }

            // Two beams on one line meet end to end over the column between them; nothing can stand in
            // that gap, so the test below - which is for beams that face each other across open space -
            // is not asked.
            if (inline.Collinear)
            {
                return inline;
            }

            // Only something standing in front counts as being in the way. A beam this end already
            // reaches into is not between the two of them - it is a clash to be sorted out, and the
            // parting is how it gets sorted out.
            var between = supports
                .Where(support => support.Kind == SupportKind.CrossingBeam && support.NearMm > 0)
                .Any(support => support.NearMm < inline.NearMm - ContactToleranceMm);

            return between ? null : inline;
        }

        /// <summary>
        /// One limit on where an end may go: a support, the place it says the end may go no further
        /// than, and how the end is squared up to it.
        /// </summary>
        public class BeamEndLimit
        {
            public SupportCandidate Support { get; set; }

            public double TargetMm { get; set; }

            public double SkewDegrees { get; set; }

            /// <summary>
            /// Which way the face leans across the beam, signed, taken from the support the end is
            /// squared up to. The skew says how far off square the face is; this says which corner of
            /// the end meets it first, which is what decides whether two planes take opposite halves
            /// of the end or the same one.
            /// </summary>
            public double AcrossNormal { get; set; }

            /// <summary>Set on a wall the beam passes through rather than stops against.</summary>
            public bool Opened { get; set; }

            public long CutAgainstId { get; set; }

            /// <summary>Set when the support stands across the beam's whole width, not on a tip.</summary>
            public bool MeetsAFace { get; set; }

            /// <summary>
            /// How much of the end a square cut would leave standing proud of the face, measured
            /// along the beam from the first corner to reach it to the last.
            /// </summary>
            public double WedgeMm { get; set; }

            /// <summary>Whether that wedge is worth taking off with an opening.</summary>
            public bool CutsTheEnd => WedgeMm >= NegligibleWedgeMm;

            /// <summary>Set on a limit that answers the end on its own, without being weighed.</summary>
            public bool Settles { get; set; }

            /// <summary>Set on a limit that was worked out and then let go.</summary>
            public bool Dropped { get; set; }

            /// <summary>Set on the one the end was placed by.</summary>
            public bool Governs { get; set; }

            /// <summary>Why it settles, was let go, or otherwise reads the way it does.</summary>
            public string Note { get; set; }

            public string Describe()
            {
                return Support == null
                    ? "nothing"
                    : $"{Support.Kind} {Support.Id}";
            }
        }
    }
}
