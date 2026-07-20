using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace SDSoftware.RevitTest.Features.BearingPlate.Services
{
    /// <summary>
    /// Creates the schedules that go on a bearing plate sheet. Which kind of schedule to create is
    /// read from the template itself: a template with no category is a material takeoff, otherwise
    /// it is a single category schedule over that category.
    /// </summary>
    public class AssemblyScheduleBuilder
    {
        private const string NameSuffix = " - Assembly";

        private readonly Document _document;

        public AssemblyScheduleBuilder(Document document)
        {
            _document = document;
        }

        public List<ViewSchedule> Create(ElementId assemblyId, IEnumerable<ViewSchedule> templates, string assemblyName)
        {
            var schedules = new List<ViewSchedule>();

            foreach (var template in templates)
            {
                var schedule = CreateFrom(assemblyId, template);
                if (schedule == null)
                {
                    continue;
                }

                schedule.ViewTemplateId = template.Id;
                Rename(schedule, template.Name + NameSuffix, assemblyName);
                schedules.Add(schedule);
            }

            return schedules;
        }

        private ViewSchedule CreateFrom(ElementId assemblyId, ViewSchedule template)
        {
            var categoryId = template.Definition?.CategoryId;

            try
            {
                return categoryId == null || categoryId == ElementId.InvalidElementId
                    ? AssemblyViewUtils.CreateMaterialTakeoff(_document, assemblyId)
                    : AssemblyViewUtils.CreateSingleCategorySchedule(_document, assemblyId, categoryId);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // the category is not present in this assembly - nothing to schedule
                return null;
            }
        }

        /// <summary>
        /// Assembly views may share a name across assemblies, but the model can still refuse a
        /// duplicate; fall back to a name qualified with the assembly.
        /// </summary>
        private static void Rename(View schedule, string name, string assemblyName)
        {
            try
            {
                schedule.Name = name;
            }
            catch (Exception)
            {
                try
                {
                    schedule.Name = $"{name} {assemblyName}";
                }
                catch (Exception)
                {
                    // keep whatever name Revit generated
                }
            }
        }
    }
}
