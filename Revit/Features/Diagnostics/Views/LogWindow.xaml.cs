using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace SDSoftware.RevitTest.Features.Diagnostics.Views
{
    /// <summary>Read-only text viewer used to hand a report back to the developer.</summary>
    public partial class LogWindow : Window
    {
        private readonly string _text;
        private readonly string _fileName;

        public LogWindow(string title, string text, string fileName = "ModelProbe")
        {
            InitializeComponent();
            Title = title;
            _text = text ?? string.Empty;
            _fileName = Sanitise(fileName);
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
                _fileName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");

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

        /// <summary>Model titles reach the file name, so strip anything the file system rejects.</summary>
        private static string Sanitise(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "ModelProbe";
            }

            var cleaned = new string(fileName
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
                .ToArray());

            return cleaned.Length > 80 ? cleaned.Substring(0, 80) : cleaned;
        }
    }
}
