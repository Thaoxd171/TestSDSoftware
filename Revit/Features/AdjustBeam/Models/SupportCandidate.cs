namespace SDSoftware.RevitTest.Features.AdjustBeam.Models
{
    /// <summary>
    /// One thing found in front of a beam end, already reduced to numbers: how far away it starts and
    /// ends along the beam axis. Distances are millimetres measured outwards from the beam end, so a
    /// negative value means the beam already reaches into it. Holding the measurement rather than the
    /// geometry is what keeps <see cref="Services.BeamEndSolver"/> free of the Revit API.
    /// </summary>
    public class SupportCandidate
    {
        public long Id { get; set; }

        public SupportKind Kind { get; set; }

        /// <summary>Distance to the face the beam meets first.</summary>
        public double NearMm { get; set; }

        /// <summary>Distance to the face on the far side.</summary>
        public double FarMm { get; set; }

        /// <summary>
        /// How far the end can still travel before it touches this support: the closest its material
        /// comes, counting only what stands inside the width and height the beam sweeps.
        ///
        /// Where a support faces the beam squarely across its whole width this is simply the distance
        /// to its face. It parts company when the face stops short - the cut end of another beam, say -
        /// because then the beam meets a corner, and the corner is a good deal nearer than the point
        /// where the axis would cross the face plane. Null when the support has no material in the way.
        /// </summary>
        public double? ClearMm { get; set; }

        /// <summary>Distance to the centre, along the axis. Only pillars carry one.</summary>
        public double? CentreAlongMm { get; set; }

        /// <summary>
        /// Angle between the beam axis and the face of the support it meets, where 0 means the beam
        /// arrives square. Clearances are measured across that face, so a skewed beam has to travel a
        /// little further along its own axis to keep the same gap - and its square-cut end no longer
        /// lands parallel to the face, which is what the opening cut is for.
        /// </summary>
        public double SkewDegrees { get; set; }

        /// <summary>Top of the support, measured up from the top of the beam.</summary>
        public double TopAboveBeamMm { get; set; }

        /// <summary>Bottom of the support, measured up from the top of the beam.</summary>
        public double BottomAboveBeamMm { get; set; }

        /// <summary>Family and type, for the report.</summary>
        public string Description { get; set; }

        /// <summary>
        /// Why the probe threw this one away, or null when it counts. The solver never sees a rejected
        /// candidate; the field exists so the Explain command can show what was discarded and why.
        /// </summary>
        public string RejectionReason { get; set; }
    }
}
