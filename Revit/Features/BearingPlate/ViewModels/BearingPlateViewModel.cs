using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using SDSoftware.RevitTest.Features.BearingPlate.Models;
using SDSoftware.RevitTest.Mvvm;

namespace SDSoftware.RevitTest.Features.BearingPlate.ViewModels
{
    /// <summary>Drives the plate picker. Knows nothing about Revit windows or transactions.</summary>
    public class BearingPlateViewModel : ViewModelBase
    {
        private readonly ICollectionView _view;
        private string _search;

        public BearingPlateViewModel(IEnumerable<PlateItem> plates)
        {
            Plates = new ObservableCollection<PlateRow>();

            foreach (var plate in plates)
            {
                var row = new PlateRow(plate);
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PlateRow.IsSelected))
                    {
                        OnPropertyChanged(nameof(CheckedText));
                    }
                };
                Plates.Add(row);
            }

            _view = CollectionViewSource.GetDefaultView(Plates);
            _view.Filter = item => ((PlateRow)item).Matches(_search);
        }

        public ObservableCollection<PlateRow> Plates { get; }

        public string Search
        {
            get => _search;
            set
            {
                if (SetProperty(ref _search, value))
                {
                    _view.Refresh();
                }
            }
        }

        public string CheckedText => $"({Plates.Count(p => p.IsSelected)} checked)";

        public List<PlateItem> SelectedPlates =>
            Plates.Where(p => p.IsSelected).Select(p => p.Plate).ToList();
    }
}
