using System;
using System.Collections.Generic;

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

        /// <summary>
        /// How far outwards the location line end has to be put, measured from where the solid stops.
        /// Negative shortens the beam.
        /// </summary>
        public double MoveMm { get; set; }

        /// <summary>
        /// How far the location line end itself still has to travel to get there. It is the same as
        /// <see cref="MoveMm"/> on a plain end and differs on one that has been cut, where the axis
        /// already stands out past the solid: reading the journey off the solid on such an end asks
        /// for it again every time, and the run would never settle.
        /// </summary>
        public double AxisTravelMm { get; set; }

        /// <summary>
        /// Where the finished end face belongs, as a distance out from the end. Set only when the beam
        /// meets its support at an angle: a square cut then lands across the face instead of parallel
        /// to it, so the axis is run out far enough to cover this plane and an opening trims back to
        /// it. When the beam arrives square this is null and the axis move is the whole story.
        /// </summary>
        public double? CutPlaneMm { get; set; }

        /// <summary>Angle between the beam axis and the face it is being cut against.</summary>
        public double SkewDegrees { get; set; }

        /// <summary>The support the end was measured against, so the cutter can find its face again.</summary>
        public long SupportId { get; set; }

        /// <summary>
        /// Whose face the finished end is cut parallel to. Usually the governing support, but where two
        /// beams part over a pillar it is the pillar: the parting plane follows the pillar face, so the
        /// beam arriving square to it keeps a square end and only the skewed one is cut.
        /// </summary>
        public long CutAgainstId { get; set; }

        /// <summary>
        /// Every plane the end is trimmed back to, the governing one first. Empty on an end that comes
        /// out square. <see cref="CutPlaneMm"/> and <see cref="SkewDegrees"/> describe the first of
        /// them, which is the only one most ends have.
        /// </summary>
        public IList<BeamEndCut> Cuts { get; set; } = new List<BeamEndCut>();

        public bool NeedsCut => Cuts.Count > 0 && !IsSkipped;

        /// <summary>Why nothing will be done. Null when the end is going to be adjusted.</summary>
        public string SkipReason { get; set; }

        /// <summary>What the end was measured against, for the report.</summary>
        public string SupportDescription { get; set; }

        public bool IsSkipped => SkipReason != null;

        public bool IsAlreadyCorrect => !IsSkipped && Math.Abs(AxisTravelMm) < NegligibleMoveMm;

        public bool WillMove => !IsSkipped && !IsAlreadyCorrect;
    }
}
