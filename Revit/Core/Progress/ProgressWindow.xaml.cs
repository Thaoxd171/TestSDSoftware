using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace SDSoftware.RevitTest.Core.Progress
{
    /// <summary>
    /// Shows one progress bar per step plus a log. Revit API calls have to stay on the main thread,
    /// so the work runs on that thread and this window pumps the dispatcher between items to stay
    /// responsive - that is what makes the Stop button clickable.
    /// </summary>
    public partial class ProgressWindow : Window, IProgressSink
    {
        private readonly ObservableCollection<ProgressStep> _steps = new ObservableCollection<ProgressStep>();
        private readonly StringBuilder _log = new StringBuilder();

        private bool _isRunning = true;

        public ProgressWindow(string title)
        {
            InitializeComponent();
            Title = title;
            StepList.ItemsSource = _steps;
        }

        public bool IsCancelled { get; private set; }

        public void Report(string step, double fraction)
        {
            var existing = _steps.FirstOrDefault(s => s.Name == step);
            if (existing == null)
            {
                existing = new ProgressStep(step);
                _steps.Add(existing);
            }

            existing.Fraction = Math.Max(0, Math.Min(1, fraction));
            Pump();
        }

        public void Log(string message)
        {
            _log.AppendLine(message);
            LogBox.Text = _log.ToString();
            LogBox.ScrollToEnd();
            Pump();
        }

        /// <summary>Call when the work is over: the window stays open so the log can be read.</summary>
        public void Finish(string summary)
        {
            _isRunning = false;
            StopButton.Content = "Close";
            Log(summary);
        }

        /// <summary>Lets the dispatcher render pending updates and process the Stop click.</summary>
        private void Pump()
        {
            Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
        }

        private void OnStopClick(object sender, RoutedEventArgs e)
        {
            if (!_isRunning)
            {
                Close();
                return;
            }

            IsCancelled = true;
            StopButton.IsEnabled = false;
            Log("Stopping as soon as the current item is finished...");
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_log.ToString());
            }
            catch (Exception ex)
            {
                Log("Copy failed: " + ex.Message);
            }
        }

        /// <summary>Closing while work is in flight asks the run to stop rather than tearing it down.</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (_isRunning)
            {
                e.Cancel = true;
                IsCancelled = true;
                Log("Stopping as soon as the current item is finished...");
                return;
            }

            base.OnClosing(e);
        }
    }
}
