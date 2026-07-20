using System.Linq;
using System.Windows;
using SDSoftware.RevitTest.Features.BearingPlate.ViewModels;

namespace SDSoftware.RevitTest.Features.BearingPlate.Views
{
    public partial class BearingPlateWindow : Window
    {
        public BearingPlateWindow(BearingPlateViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = viewModel;
        }

        public BearingPlateViewModel ViewModel { get; }

        private void OnGenerateClick(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.Plates.Any(p => p.IsSelected))
            {
                MessageBox.Show(this, "Check at least one plate.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
        }
    }
}
