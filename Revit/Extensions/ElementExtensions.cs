using Autodesk.Revit.DB;

namespace SDSoftware.RevitTest.Extensions
{
    /// <summary>Parameter access shortcuts.</summary>
    public static class ElementExtensions
    {
        public static double GetLength(this Element element, BuiltInParameter parameter, double fallback = 0)
        {
            var p = element?.get_Parameter(parameter);
            return p != null && p.StorageType == StorageType.Double ? p.AsDouble() : fallback;
        }

        public static string GetString(this Element element, BuiltInParameter parameter)
        {
            return element?.get_Parameter(parameter)?.AsString();
        }

        /// <summary>Sets a writable parameter and reports whether the value was applied.</summary>
        public static bool TrySet(this Element element, BuiltInParameter parameter, double value)
        {
            var p = element?.get_Parameter(parameter);
            if (p == null || p.IsReadOnly)
            {
                return false;
            }

            return p.Set(value);
        }

        /// <summary>The element's type, cast to <typeparamref name="T"/>, or null.</summary>
        public static T GetTypeAs<T>(this Element element) where T : ElementType
        {
            return element?.Document.GetElement(element.GetTypeId()) as T;
        }

        /// <summary>Element id as a long, hiding the ElementId API difference between Revit versions.</summary>
        public static long ToLong(this ElementId id)
        {
#if Revit_2024 || Revit_2025
            return id?.Value ?? -1;
#else
            return id?.IntegerValue ?? -1;
#endif
        }
    }
}
