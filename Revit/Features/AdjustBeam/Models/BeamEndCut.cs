namespace SDSoftware.RevitTest.Features.AdjustBeam.Models
{
    /// <summary>
    /// One plane a beam end is trimmed back to, written in the beam's own frame so that the cutter
    /// never has to go looking for the face again.
    ///
    /// An end usually has one of these: it arrives at a face at an angle, and the wedge left standing
    /// proud of that face is taken off. It has two where the beam runs into a corner - at 1855188 the
    /// end lands on a corbel pillar set flush into a wall, and it has to stand clear of the pillar's
    /// far face and of the wall's flank at once, so it comes out L-shaped with no square end left.
    /// </summary>
    public class BeamEndCut
    {
        /// <summary>How far out from where the solid stops the plane crosses the axis.</summary>
        public double PlaneMm { get; set; }

        /// <summary>
        /// The plane's normal, pointing the way the beam travels, split into the part along the axis
        /// and the part across it. Together they are a unit vector, so the along part is the cosine of
        /// the skew and the across part its sine, carrying the sign that says which way the face leans.
        /// </summary>
        public double AlongNormal { get; set; }

        public double AcrossNormal { get; set; }

        /// <summary>Angle between the beam axis and this plane, for the report.</summary>
        public double SkewDegrees { get; set; }

        /// <summary>Whose face it is.</summary>
        public long AgainstId { get; set; }

        /// <summary>
        /// How deep into the end this plane bites, measured along the axis from the corner it reaches
        /// first. On a single cut that is the whole wedge; where two planes share the end each takes
        /// its own share.
        /// </summary>
        public double DepthMm { get; set; }
    }
}
