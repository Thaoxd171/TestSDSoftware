namespace SDSoftware.RevitTest.Features.AdjustBeam.Models
{
    /// <summary>
    /// How the ends are resolved where two beams meet at a pillar corner. Only <see cref="Default"/>
    /// is in scope for this test; the enum exists so the other modes can be added without changing
    /// the dialog or the solver signature.
    /// </summary>
    public enum BeamCornerMode
    {
        /// <summary>Every end follows the clearance rules, whichever support governs it.</summary>
        Default = 0,
    }
}
