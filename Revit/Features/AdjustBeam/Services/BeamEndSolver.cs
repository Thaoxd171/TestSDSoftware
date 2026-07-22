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
    ///   1. a beam continuing along the same axis - both beams stop half the inline gap short of the
    ///      point they share: the centre of the pillar they meet over when there is one, otherwise the
    ///      midpoint between the two ends as they stand;
    ///   2. a beam crossing this one - stop the perpendicular gap short of its body. This is what the
    ///      two "extend to beam body" options ask for: the end runs past the pillar or wall it sits on
    ///      until it reaches the other beam;
    ///   3. a wall - stop the wall clearance short of its near face;
    ///   4. a pillar - stop the pillar clearance short of its near face.
    ///
    /// A pillar comes last because it stands under the beam, not in its way: the beam runs over it and
    /// carries on to the wall behind. It still caps the answer, though - the end may not hang out past
    /// the far face of the pillar carrying it.
    /// </summary>
    public static class BeamEndSolver
    {
        /// <summary>A beam is never shortened below this length.</summary>
        public const double MinimumBeamLengthMm = 200;

        public static BeamEndPlan Solve(
            long beamId,
            int end,
            IReadOnlyList<SupportCandidate> supports,
            AdjustBeamOptions options,
            double beamLengthMm)
        {
            var plan = new BeamEndPlan { BeamId = beamId, End = end };

            var governing = Choose(supports, options);
            if (governing == null)
            {
                plan.Support = SupportKind.None;
                plan.SkipReason = "nothing found in front of this end";
                return plan;
            }

            plan.Support = governing.Kind;
            plan.SupportDescription = governing.Description;
            plan.MoveMm = Cap(TargetFor(governing, supports, options), supports);

            if (beamLengthMm + plan.MoveMm < MinimumBeamLengthMm)
            {
                plan.SkipReason = $"the beam would be shorter than {MinimumBeamLengthMm:0} mm";
            }

            return plan;
        }

        /// <summary>The support that decides this end, or null when the end is free.</summary>
        private static SupportCandidate Choose(IReadOnlyList<SupportCandidate> supports, AdjustBeamOptions options)
        {
            var inline = Nearest(supports, SupportKind.InlineBeam);
            if (inline != null)
            {
                return inline;
            }

            var pillar = Nearest(supports, SupportKind.Pillar);
            var wall = Nearest(supports, SupportKind.Wall);
            var crossing = Nearest(supports, SupportKind.CrossingBeam);

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
            return wall ?? pillar;
        }

        /// <summary>How far the end must travel outwards to satisfy the governing support.</summary>
        private static double TargetFor(
            SupportCandidate governing,
            IReadOnlyList<SupportCandidate> supports,
            AdjustBeamOptions options)
        {
            switch (governing.Kind)
            {
                case SupportKind.InlineBeam:
                    // Half the gap away from the point the two beams share.
                    var shared = Nearest(supports, SupportKind.Pillar)?.CentreAlongMm ?? governing.NearMm / 2;
                    return shared - options.InlineGapMm / 2;

                case SupportKind.CrossingBeam:
                    return governing.NearMm - options.PerpendicularGapMm;

                case SupportKind.Pillar:
                    return governing.NearMm - options.PillarClearanceMm;

                case SupportKind.Wall:
                    return governing.NearMm - options.WallClearanceMm;

                default:
                    return 0;
            }
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

        private static SupportCandidate Closest(SupportCandidate first, SupportCandidate second)
        {
            if (first == null)
            {
                return second;
            }

            if (second == null)
            {
                return first;
            }

            return first.NearMm <= second.NearMm ? first : second;
        }
    }
}
