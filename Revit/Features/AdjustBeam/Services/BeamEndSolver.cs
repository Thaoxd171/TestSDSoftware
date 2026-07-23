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

        /// <summary>Below this angle the end is treated as square and no opening is needed.</summary>
        public const double SquareEnoughDegrees = 0.5;

        /// <summary>How far the corner of a face may miss the corner the beam reaches and still count.</summary>
        private const double ContactToleranceMm = 1;

        /// <param name="axisOffsetMm">
        /// How far the location line already runs past where the solid stops at this end. Nought on a
        /// plain end; on one that has been cut it is what the axis has already travelled, and leaving
        /// it out would have the end asked to make the same journey over again.
        /// </param>
        public static BeamEndPlan Solve(
            long beamId,
            int end,
            IReadOnlyList<SupportCandidate> supports,
            AdjustBeamOptions options,
            double beamLengthMm,
            double beamWidthMm,
            double axisOffsetMm = 0)
        {
            var plan = new BeamEndPlan { BeamId = beamId, End = end };

            var decision = Decide(supports, options, beamWidthMm);
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

            var face = decision.TargetMm;

            if (decision.SkewDegrees > SquareEnoughDegrees)
            {
                // A square cut through a skewed end reaches the face plane at one corner and falls
                // short at the other, so the axis runs on until the near corner clears the plane too
                // and an opening takes the wedge back off.
                plan.CutPlaneMm = face;
                plan.MoveMm = face + beamWidthMm / 2 * Math.Tan(Radians(decision.SkewDegrees));
            }
            else
            {
                plan.MoveMm = face;
            }

            plan.AxisTravelMm = plan.MoveMm - axisOffsetMm;

            if (beamLengthMm + plan.MoveMm < MinimumBeamLengthMm)
            {
                plan.SkipReason = $"the beam would be shorter than {MinimumBeamLengthMm:0} mm";
            }

            return plan;
        }

        private static double Radians(double degrees) => degrees * Math.PI / 180;

        /// <summary>
        /// What the end is being placed against, where that puts it, and how it is cut.
        ///
        /// Every support the end has to satisfy names a place it may go no further than, and the end
        /// goes to the nearest of those. There is no order of precedence between the kinds: a beam
        /// pushing into a joint goes in as deep as it can, and how deep that is depends on whichever
        /// of the things around it runs out first.
        /// </summary>
        private static BeamEndLimit Decide(
            IReadOnlyList<SupportCandidate> supports,
            AdjustBeamOptions options,
            double beamWidthMm)
        {
            return Weigh(supports, options, beamWidthMm).FirstOrDefault(limit => limit.Governs);
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
            double beamWidthMm)
        {
            var found = Collect(supports, options, beamWidthMm).ToList();
            var governing = found.FirstOrDefault(limit => limit.Settles)
                            ?? found.Where(limit => !limit.Dropped)
                                .OrderBy(limit => limit.TargetMm)
                                .FirstOrDefault();

            foreach (var limit in found)
            {
                limit.Governs = ReferenceEquals(limit, governing);
            }

            return found;
        }

        private static IList<BeamEndLimit> Collect(
            IReadOnlyList<SupportCandidate> supports,
            AdjustBeamOptions options,
            double beamWidthMm)
        {
            var pillar = Bearing(supports);
            var limits = new List<BeamEndLimit>();

            var inline = FacingPartner(supports);
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
                limits.Add(new BeamEndLimit
                {
                    Support = wall,
                    TargetMm = wall.NearMm - AlongAxis(options.WallClearanceMm, wall),
                    SkewDegrees = wall.SkewDegrees,
                    CutAgainstId = wall.Id,
                    Dropped = options.ExtendToBeamBodyAtWall && crossings > 0,
                    Note = options.ExtendToBeamBodyAtWall && crossings > 0
                        ? "let go: the end may run over the wall to reach a beam"
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
                        CutAgainstId = pillar.Id,
                        Note = "stopping at its near face",
                    });
                }
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

            if (all.Any(limit => limit.MeetsAFace))
            {
                foreach (var tip in all.Where(limit => !limit.MeetsAFace))
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
        /// </summary>
        private static SupportCandidate FacingPartner(IReadOnlyList<SupportCandidate> supports)
        {
            var inline = Nearest(supports, SupportKind.InlineBeam);
            if (inline == null)
            {
                return null;
            }

            // Only something standing in front counts as being in the way. A beam this end already
            // reaches into is not between the two of them - it is a clash to be sorted out, and the
            // parting is how it gets sorted out.
            var between = supports
                .Where(support => support.Kind == SupportKind.CrossingBeam && support.NearMm > 0)
                .Any(support => support.NearMm < inline.NearMm - ContactToleranceMm);

            return between ? null : inline;
        }

        private static SupportCandidate Nearest(IReadOnlyList<SupportCandidate> supports, SupportKind kind)
        {
            return supports
                .Where(support => support.Kind == kind)
                .OrderBy(support => support.NearMm)
                .FirstOrDefault();
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

            public long CutAgainstId { get; set; }

            /// <summary>Set when the support stands across the beam's whole width, not on a tip.</summary>
            public bool MeetsAFace { get; set; }

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
