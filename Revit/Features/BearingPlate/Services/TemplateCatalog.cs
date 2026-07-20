using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Extensions;

namespace SDSoftware.RevitTest.Features.BearingPlate.Services
{
    /// <summary>
    /// The view templates, schedule templates and title blocks available in the model.
    /// The reference project ships them prefixed "REVGEN_CRH_", so those are preferred when
    /// nothing has been chosen yet.
    /// </summary>
    public class TemplateCatalog
    {
        public const string SchedulePrefix = "REVGEN_CRH_Schedule_BearingPlate";
        private const string ViewPrefix = "REVGEN_CRH_BearingPlate";

        private readonly Document _document;

        public TemplateCatalog(Document document)
        {
            _document = document;

            var templates = document.OfClass<View>().Where(v => v.IsTemplate).ToList();

            ScheduleTemplates = templates
                .OfType<ViewSchedule>()
                .Where(t => t.Name.StartsWith(SchedulePrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.Name)
                .ToList();

            DetailTemplates = templates
                .Where(v => v.ViewType == ViewType.Detail || v.ViewType == ViewType.Section)
                .OrderBy(v => v.Name)
                .ToList();

            ThreeDTemplates = templates
                .Where(v => v.ViewType == ViewType.ThreeD)
                .OrderBy(v => v.Name)
                .ToList();

            TitleBlocks = new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .Cast<FamilySymbol>()
                .OrderBy(t => t.Name)
                .ToList();
        }

        public List<ViewSchedule> ScheduleTemplates { get; }

        public List<View> DetailTemplates { get; }

        public List<View> ThreeDTemplates { get; }

        public List<FamilySymbol> TitleBlocks { get; }

        /// <summary>Preferred plan template: the REVGEN one, otherwise anything mentioning "plan".</summary>
        public View DefaultPlanTemplate => Prefer(DetailTemplates, ViewPrefix + " Plan", "plan");

        /// <summary>"Opstalt" is Danish for elevation - that is what the reference calls the front view.</summary>
        public View DefaultFrontTemplate => Prefer(DetailTemplates, ViewPrefix + " Opstalt", "opstalt", "front", "elevation");

        public View DefaultThreeDTemplate => Prefer(ThreeDTemplates, ViewPrefix + " 3D", "3d");

        /// <summary>A4 portrait title block, preferring the REVGEN family; null when none exists.</summary>
        public FamilySymbol A4TitleBlock =>
            TitleBlocks.FirstOrDefault(t => Contains(t.Name, "A4") && Contains(t.Name, "Portrait"))
            ?? TitleBlocks.FirstOrDefault(t => Contains(t.Name, "A4"));

        /// <summary>A3 landscape title block, preferring the REVGEN family; null when none exists.</summary>
        public FamilySymbol A3TitleBlock =>
            TitleBlocks.FirstOrDefault(t => Contains(t.Name, "A3") && Contains(t.Name, "Landscape"))
            ?? TitleBlocks.FirstOrDefault(t => Contains(t.Name, "A3"));

        /// <summary>Fallback used when a specific size cannot be found.</summary>
        public FamilySymbol DefaultTitleBlock => A3TitleBlock ?? A4TitleBlock ?? TitleBlocks.FirstOrDefault();

        public View FindDetailTemplate(string name) => FindByName(DetailTemplates, name) ?? DefaultPlanTemplate;

        public View FindThreeDTemplate(string name) => FindByName(ThreeDTemplates, name) ?? DefaultThreeDTemplate;

        public FamilySymbol FindTitleBlock(string name)
        {
            return TitleBlocks.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? DefaultTitleBlock;
        }

        private static T FindByName<T>(IEnumerable<T> views, string name) where T : View
        {
            return string.IsNullOrEmpty(name)
                ? null
                : views.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>First view whose name matches one of the hints, in order of preference.</summary>
        private static View Prefer(List<View> views, params string[] hints)
        {
            foreach (var hint in hints)
            {
                var match = views.FirstOrDefault(v => Contains(v.Name, hint));
                if (match != null)
                {
                    return match;
                }
            }

            return views.FirstOrDefault();
        }

        private static bool Contains(string value, string part)
        {
            return value != null && value.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
