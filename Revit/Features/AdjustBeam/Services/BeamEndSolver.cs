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

            var face = Cap(decision.TargetMm, supports);

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

        /// <summary>What the end is being placed against, where that puts it, and how it is cut.</summary>
        private static Decision Decide(
            IReadOnlyList<SupportCandidate> supports,
            AdjustBeamOptions options,
            double beamWidthMm)
        {
            var pillar = Nearest(supports, SupportKind.Pillar);

            var inline = Nearest(supports, SupportKind.InlineBeam);
            if (inline != null)
            {
                // Half the gap away from the point the two beams share. The pillar sets the parting
                // plane when there is one - it is the pillar face both beams are squared up to.
                var against = pillar ?? inline;
                var shared = pillar?.CentreAlongMm ?? inline.NearMm / 2;

                return new Decision
                {
                    Support = inline,
                    TargetMm = shared - AlongAxis(options.InlineGapMm / 2, against),
                    SkewDegrees = against.SkewDegrees,
                    CutAgainstId = against.Id,
                };
            }

            var wall = Nearest(supports, SupportKind.Wall);
            var crossing = Crossing(supports, options, beamWidthMm);

            // A crossing beam only wins when the end is allowed to run past whatever it sits on to
            // reach it. With the options cleared the pillar or the wall stops the beam first.
            var mayReachTheBeamBody = crossing != null
                                      && (pillar == null || options.ExtendToBeamBodyAtPillar)
                                      && (wall == null || options.ExtendToBeamBodyAtWall);

            if (mayReachTheBeamBody)
            {
                return crossing;
            }

            // The wall goes first even when the pillar is nearer: the beam bears on the pillar and
            // runs over it, so the pillar only decides the end when nothing else stands in front.
            if (wall != null)
            {
                return new Decision
                {
                    Support = wall,
                    TargetMm = wall.NearMm - AlongAxis(options.WallClearanceMm, wall),
                    SkewDegrees = wall.SkewDegrees,
                    CutAgainstId = wall.Id,
                };
            }

            if (pillar == null)
            {
                return null;
            }

            // The beam is bearing on the pillar, so it runs over it and stops short of the far edge -
            // not of the edge it arrives at.
            return new Decision
            {
                Support = pillar,
                TargetMm = pillar.FarMm - AlongAxis(options.PillarClearanceMm, pillar),
                SkewDegrees = pillar.SkewDegrees,
                CutAgainstId = pillar.Id,
            };
        }

        /// <summary>
        /// The crossing beam that holds this end back the most. Every one of them has to be cleared,
        /// so the one asking for the shortest beam is the one that decides, which is not always the
        /// one whose face plane is nearest.
        /// </summary>
        private static Decision Crossing(
            IReadOnlyList<SupportCandidate> supports,
            AdjustBeamOptions options,
            double beamWidthMm)
        {
            return supports
                .Where(support => support.Kind == SupportKind.CrossingBeam)
                .Select(support => CrossingAgainst(support, options, beamWidthMm))
                .OrderBy(decision => decision.TargetMm)
                .FirstOrDefault();
        }

        /// <summary>
        /// Where one crossing beam puts this end, and whether the end is cut to match its face.
        ///
        /// A face standing across the beam's whole width is met corner first, and the corner sits
        /// exactly half a width of skew nearer than the point where the axis crosses the face plane.
        /// When the beam touches sooner than that the face must stop somewhere inside the width - it
        /// is the end of another beam, cut off short - and then there is nothing to line up with: the
        /// end stays square and simply stops the gap short of the corner it would hit.
        /// </summary>
        private static Decision CrossingAgainst(
            SupportCandidate support,
            AdjustBeamOptions options,
            double beamWidthMm)
        {
            var gap = options.PerpendicularGapMm;
            var acrossTheWidth = support.NearMm - beamWidthMm / 2 * Math.Tan(Radians(support.SkewDegrees));
            var meetsAFace = !support.ClearMm.HasValue
                             || support.ClearMm.Value <= acrossTheWidth + ContactToleranceMm;

            return meetsAFace
                ? new Decision
                {
                    Support = support,
                    TargetMm = support.NearMm - AlongAxis(gap, support),
                    SkewDegrees = support.SkewDegrees,
                    CutAgainstId = support.Id,
                }
                : new Decision
                {
                    Support = support,
                    TargetMm = support.ClearMm.Value - gap,
                    SkewDegrees = 0,
                    CutAgainstId = support.Id,
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
        /// Holds the end back to the far face of the pillar it bears on. A precast beam is not allowed
        /// to overhang its bearing, so wherever the clearance rules would put it, it stops there at
        /// the latest.
        /// </summary>
        private static double Cap(double target, IReadOnlyList<SupportCandidate> supports)
        {
            var pillars = supports.Where(support => support.Kind == SupportKind.Pillar).ToList();
            return pillars.Count == 0 ? target : Math.Min(target, pillars.Max(pillar => pillar.FarMm));
        }

        private static SupportCandidate Nearest(IReadOnlyList<SupportCandidate> supports, SupportKind kind)
        {
            return supports
                .Where(support => support.Kind == kind)
                .OrderBy(support => support.NearMm)
                .FirstOrDefault();
        }

        /// <summary>One support, where it puts the end, and the face the end is squared up to.</summary>
        private class Decision
        {
            public SupportCandidate Support { get; set; }

            public double TargetMm { get; set; }

            public double SkewDegrees { get; set; }

            public long CutAgainstId { get; set; }
        }
    }
}
