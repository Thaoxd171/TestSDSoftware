using System;
using System.Linq;
using System.Windows.Media.Imaging;

namespace SDSoftware.RevitTest.Core
{
    /// <summary>
    /// Loads ribbon icons that are embedded in this assembly, so the add-in stays a single file.
    /// </summary>
    internal static class ImageUtils
    {
        /// <summary>
        /// Returns the embedded PNG whose resource name ends with <paramref name="fileName"/>,
        /// or null when it is missing (a missing icon must not stop the ribbon from loading).
        /// </summary>
        public static BitmapImage LoadEmbeddedPng(string fileName)
        {
            try
            {
                var assembly = typeof(ImageUtils).Assembly;
                var resourceName = assembly
                    .GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));

                if (resourceName == null)
                {
                    return null;
                }

                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
