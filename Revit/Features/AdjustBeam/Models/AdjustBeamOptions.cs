using System.Runtime.Serialization;

namespace SDSoftware.RevitTest.Features.AdjustBeam.Models
{
    /// <summary>
    /// What the user typed in the dialog. Every clearance is a gap in millimetres between the beam end
    /// and a face of whatever supports it - the near face for a wall or a beam the end runs into, the
    /// far face for a pillar the beam runs over. The inline gap is the total gap between two beams
    /// meeting head on, so each of them stops half of it away from the shared centre.
    /// Serialised as-is by <see cref="Settings.SettingStore"/>.
    /// </summary>
    [DataContract]
    public class AdjustBeamOptions
    {
        /// <summary>Largest clearance the dialog accepts, and the range the solver trusts.</summary>
        public const double MaximumClearanceMm = 500;

        [DataMember]
        public double WallClearanceMm { get; set; } = 20;

        [DataMember]
        public double PillarClearanceMm { get; set; } = 20;

        [DataMember]
        public double InlineGapMm { get; set; } = 20;

        [DataMember]
        public double PerpendicularGapMm { get; set; } = 20;

        [DataMember]
        public BeamCornerMode CornerMode { get; set; } = BeamCornerMode.Default;

        /// <summary>
        /// At a corner or T-shape on a pillar, run the beam past the pillar until it is
        /// <see cref="PerpendicularGapMm"/> from the body of the beam it meets, instead of stopping
        /// at the pillar face.
        /// </summary>
        [DataMember]
        public bool ExtendToBeamBodyAtPillar { get; set; } = true;

        /// <summary>The same behaviour where the corner sits on a wall instead of a pillar.</summary>
        [DataMember]
        public bool ExtendToBeamBodyAtWall { get; set; } = true;
    }
}
