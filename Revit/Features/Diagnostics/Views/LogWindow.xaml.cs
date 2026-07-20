using System;
using System.IO;
using System.Windows;

namespace SDSoftware.RevitTest.Features.Diagnostics.Views
{
    /// <summary>Read-only text viewer used to hand a report back to the developer.</summary>
    public partial class LogWindow : Window
    {
        private readonly string _text;

        public LogWindow(string title, string text)
        {
            InitializeComponent();
            Title = title;
            _text = text ?? string.Empty;
            LogBox.Text = _text;
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_text);
                StatusText.Text = "Copied.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Copy failed: " + ex.Message;
            }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "ModelProbe_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");

            try
            {
                File.WriteAllText(path, _text);
                StatusText.Text = "Saved to " + path;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Save failed: " + ex.Message;
            }
        }
    }
}
