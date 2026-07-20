using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace SDSoftware.RevitTest.Settings
{
    /// <summary>
    /// Remembers the values a user last entered in a tool dialog. Stored as JSON per tool under
    /// %APPDATA%\SDRevitTest\. Reading or writing never throws: a corrupt file simply falls back
    /// to defaults.
    /// </summary>
    public static class SettingStore
    {
        private static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SDRevitTest");

        public static T Load<T>(string name) where T : class, new()
        {
            var path = GetPath(name);
            if (!File.Exists(path))
            {
                return new T();
            }

            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var serializer = new DataContractJsonSerializer(typeof(T));
                    return serializer.ReadObject(stream) as T ?? new T();
                }
            }
            catch
            {
                return new T();
            }
        }

        public static void Save<T>(string name, T value) where T : class
        {
            try
            {
                Directory.CreateDirectory(Folder);
                using (var stream = File.Create(GetPath(name)))
                {
                    new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                }
            }
            catch
            {
                // preferences are a convenience; failing to store them must not break the tool
            }
        }

        private static string GetPath(string name) => Path.Combine(Folder, name + ".json");
    }
}
