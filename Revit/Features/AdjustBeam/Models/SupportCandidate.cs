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
        /// Where the axis crosses the plane of the face the beam arrives at. This is not the same as
        /// <see cref="NearMm"/>, which says where the support's material first stands in the width the
        /// beam sweeps. The two agree whenever a beam runs squarely into something, and part company
        /// when it comes at the end of a wall from the side: the beam is then already past the plane of
        /// that end face while the wall's body is still ahead of it. Clearances are held off the face,
        /// because the face is what the end is set parallel to and what it has to stand clear of.
        /// Null when no face of the support looks back at this end.
        /// </summary>
        public double? EntryFaceMm { get; set; }

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

        /// <summary>
        /// How high the support reaches where it stands in front of this end, measured up from the top
        /// of the beam. Not the same as <see cref="TopAboveBeamMm"/>, which covers the whole element: a
        /// wall can be full height along its length and stop at the beam's soffit for the last stretch,
        /// where it is a nib the beam lands on. Null when none of it is in front of the end.
        /// </summary>
        public double? TopInTheWayMm { get; set; }

        /// <summary>Family and type, for the report.</summary>
        public string Description { get; set; }

        /// <summary>
        /// The face the skew was read off, written out for the report: which way it looks, where it
        /// crosses the axis and how high it stands. A skew is only ever as good as the face it came
        /// from, and nothing else in the report says which face that was.
        /// Temporary: remove with the diagnostic commands before the final submission.
        /// </summary>
        public string EntryNote { get; set; }

        /// <summary>
        /// Why the probe threw this one away, or null when it counts. The solver never sees a rejected
        /// candidate; the field exists so the Explain command can show what was discarded and why.
        /// </summary>
        public string RejectionReason { get; set; }
    }
}
