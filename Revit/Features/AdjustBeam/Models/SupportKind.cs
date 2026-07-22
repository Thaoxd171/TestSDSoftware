namespace SDSoftware.RevitTest.Features.AdjustBeam.Models
{
    /// <summary>What a beam end runs into, which is what decides the clearance to apply.</summary>
    public enum SupportKind
    {
        /// <summary>Nothing was found in front of the end.</summary>
        None = 0,

        Wall,

        /// <summary>
        /// A column, called a pillar in the brief. It sits under the beam rather than in its way, so
        /// it plays three parts: its centre is the point two beams meeting head on share, its outer
        /// face is as far as a beam may hang out over it, and it only decides an end outright when
        /// there is nothing else in front.
        /// </summary>
        Pillar,

        /// <summary>A beam continuing along the same axis, so the two share a gap.</summary>
        InlineBeam,

        /// <summary>A beam running across this one, perpendicular or skew.</summary>
        CrossingBeam,
    }
}
