using System;
using System.Collections.Generic;
using SDSoftware.RevitTest.Features.AdjustBeam.Models;
using SDSoftware.RevitTest.Mvvm;

namespace SDSoftware.RevitTest.Features.AdjustBeam.ViewModels
{
    /// <summary>
    /// Drives the Adjust Beam dialog. Holds the four clearances and the corner options, validates
    /// them, and hands back an <see cref="AdjustBeamOptions"/>. It knows nothing about Revit.
    /// </summary>
    public class AdjustBeamViewModel : ViewModelBase
    {
        private double _wallClearance;
        private double _pillarClearance;
        private double _inlineGap;
        private double _perpendicularGap;
        private BeamCornerMode _cornerMode;
        private bool _extendToBeamBodyAtPillar;
        private bool _extendToBeamBodyAtWall;

        public AdjustBeamViewModel(AdjustBeamOptions options)
        {
            _wallClearance = options.WallClearanceMm;
            _pillarClearance = options.PillarClearanceMm;
            _inlineGap = options.InlineGapMm;
            _perpendicularGap = options.PerpendicularGapMm;
            _cornerMode = options.CornerMode;
            _extendToBeamBodyAtPillar = options.ExtendToBeamBodyAtPillar;
            _extendToBeamBodyAtWall = options.ExtendToBeamBodyAtWall;

            Validate();
        }

        /// <summary>What happens next, shown under the title.</summary>
        public string Hint => "Set the clearances, then pick the beams in the model. " +
                              "A window selection is fine - anything that is not a beam is ignored.";

        public double WallClearance
        {
            get => _wallClearance;
            set => SetProperty(ref _wallClearance, value);
        }

        public double PillarClearance
        {
            get => _pillarClearance;
            set => SetProperty(ref _pillarClearance, value);
        }

        public double InlineGap
        {
            get => _inlineGap;
            set => SetProperty(ref _inlineGap, value);
        }

        public double PerpendicularGap
        {
            get => _perpendicularGap;
            set => SetProperty(ref _perpendicularGap, value);
        }

        public IReadOnlyList<BeamCornerMode> CornerModes { get; } =
            (BeamCornerMode[])Enum.GetValues(typeof(BeamCornerMode));

        public BeamCornerMode CornerMode
        {
            get => _cornerMode;
            set => SetProperty(ref _cornerMode, value);
        }

        public bool ExtendToBeamBodyAtPillar
        {
            get => _extendToBeamBodyAtPillar;
            set => SetProperty(ref _extendToBeamBodyAtPillar, value);
        }

        public bool ExtendToBeamBodyAtWall
        {
            get => _extendToBeamBodyAtWall;
            set => SetProperty(ref _extendToBeamBodyAtWall, value);
        }

        public AdjustBeamOptions ToOptions()
        {
            return new AdjustBeamOptions
            {
                WallClearanceMm = WallClearance,
                PillarClearanceMm = PillarClearance,
                InlineGapMm = InlineGap,
                PerpendicularGapMm = PerpendicularGap,
                CornerMode = CornerMode,
                ExtendToBeamBodyAtPillar = ExtendToBeamBodyAtPillar,
                ExtendToBeamBodyAtWall = ExtendToBeamBodyAtWall,
            };
        }

        protected override void Validate()
        {
            SetError(nameof(WallClearance), Range(WallClearance, "Beam to wall clearance"));
            SetError(nameof(PillarClearance), Range(PillarClearance, "Beam to pillar clearance"));
            SetError(nameof(InlineGap), Range(InlineGap, "Beam to beam inline gap"));
            SetError(nameof(PerpendicularGap), Range(PerpendicularGap, "Beam to beam perpendicular gap"));
        }

        private static string Range(double value, string displayName)
        {
            return ValidationRules.InRange(value, 0, AdjustBeamOptions.MaximumClearanceMm, displayName, "mm");
        }
    }
}
