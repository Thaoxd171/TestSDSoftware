using SDSoftware.RevitTest.Features.BearingPlate.Models;
using SDSoftware.RevitTest.Mvvm;

namespace SDSoftware.RevitTest.Features.BearingPlate.ViewModels
{
    /// <summary>One row of the plate table.</summary>
    public class PlateRow : ViewModelBase
    {
        private bool _isSelected;

        public PlateRow(PlateItem plate)
        {
            Plate = plate;
        }

        public PlateItem Plate { get; }

        public string Family => Plate.FamilyName;

        public string Type => Plate.TypeName;

        public bool HasAssembly => Plate.HasAssembly;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public bool Matches(string search)
        {
            if (string.IsNullOrWhiteSpace(search))
            {
                return true;
            }

            return Contains(Family, search) || Contains(Type, search);
        }

        private static bool Contains(string value, string part)
        {
            return value != null && value.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
