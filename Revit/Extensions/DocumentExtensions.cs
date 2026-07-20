using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace SDSoftware.RevitTest.Extensions
{
    /// <summary>Collector shortcuts used across the three tools.</summary>
    public static class DocumentExtensions
    {
        public static IEnumerable<T> OfClass<T>(this Document document) where T : Element
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(T))
                .Cast<T>();
        }

        public static IEnumerable<Element> OfCategory(this Document document, BuiltInCategory category, bool elementTypes = false)
        {
            var collector = new FilteredElementCollector(document).OfCategory(category);
            return elementTypes ? collector.WhereElementIsElementType() : collector.WhereElementIsNotElementType();
        }

        /// <summary>Finds a view family type, e.g. the type used to create drafting or section views.</summary>
        public static ViewFamilyType GetViewFamilyType(this Document document, ViewFamily family)
        {
            return document.OfClass<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == family);
        }

        /// <summary>Finds a level by name, or the lowest level when <paramref name="name"/> is null.</summary>
        public static Level GetLevel(this Document document, string name = null)
        {
            var levels = document.OfClass<Level>().ToList();
            return name == null
                ? levels.OrderBy(l => l.Elevation).FirstOrDefault()
                : levels.FirstOrDefault(l => l.Name == name);
        }

        /// <summary>Loaded family symbols of a category, ready to be activated before use.</summary>
        public static IEnumerable<FamilySymbol> GetFamilySymbols(this Document document, BuiltInCategory category)
        {
            return new FilteredElementCollector(document)
                .OfCategory(category)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>();
        }

        /// <summary>A family symbol must be active before it can be placed.</summary>
        public static void EnsureActive(this FamilySymbol symbol)
        {
            if (symbol != null && !symbol.IsActive)
            {
                symbol.Activate();
            }
        }
    }
}
