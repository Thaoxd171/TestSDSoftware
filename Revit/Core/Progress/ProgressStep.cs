using SDSoftware.RevitTest.Mvvm;

namespace SDSoftware.RevitTest.Core.Progress
{
    /// <summary>One labelled progress bar in the progress window.</summary>
    public class ProgressStep : ViewModelBase
    {
        private double _fraction;

        public ProgressStep(string name)
        {
            Name = name;
        }

        public string Name { get; }

        /// <summary>0..1.</summary>
        public double Fraction
        {
            get => _fraction;
            set
            {
                if (SetProperty(ref _fraction, value))
                {
                    OnPropertyChanged(nameof(Percent));
                    OnPropertyChanged(nameof(Caption));
                }
            }
        }

        public int Percent => (int)(_fraction * 100);

        public string Caption => $"{Name} - {Percent}%";
    }
}
