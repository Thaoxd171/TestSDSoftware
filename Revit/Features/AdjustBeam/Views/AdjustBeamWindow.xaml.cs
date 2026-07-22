using System.Windows;
using SDSoftware.RevitTest.Features.AdjustBeam.ViewModels;

namespace SDSoftware.RevitTest.Features.AdjustBeam.Views
{
    public partial class AdjustBeamWindow : Window
    {
        public AdjustBeamWindow(AdjustBeamViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = viewModel;
        }

        public AdjustBeamViewModel ViewModel { get; }

        private void OnAdjustClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.IsValid)
            {
                DialogResult = true;
            }
        }
    }
}
