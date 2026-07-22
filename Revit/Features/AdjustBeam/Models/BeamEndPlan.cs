using System;

namespace SDSoftware.RevitTest.Features.AdjustBeam.Models
{
    /// <summary>
    /// What the solver decided for one beam end: how far it has to travel and why. The plan is worked
    /// out for every end before anything is written, so no beam is measured against another beam that
    /// has already moved.
    /// </summary>
    public class BeamEndPlan
    {
        /// <summary>A move smaller than this is treated as "already correct".</summary>
        public const double NegligibleMoveMm = 0.5;

        public long BeamId { get; set; }

        /// <summary>0 for the start of the location line, 1 for its end.</summary>
        public int End { get; set; }

        public SupportKind Support { get; set; }

        /// <summary>How far the end travels outwards; negative shortens the beam.</summary>
        public double MoveMm { get; set; }

        /// <summary>Why nothing will be done. Null when the end is going to be adjusted.</summary>
        public string SkipReason { get; set; }

        /// <summary>What the end was measured against, for the report.</summary>
        public string SupportDescription { get; set; }

        public bool IsSkipped => SkipReason != null;

        public bool IsAlreadyCorrect => !IsSkipped && Math.Abs(MoveMm) < NegligibleMoveMm;

        public bool WillMove => !IsSkipped && !IsAlreadyCorrect;
    }
}
