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

        /// <summary>Distance to the centre, along the axis. Only pillars carry one.</summary>
        public double? CentreAlongMm { get; set; }

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
