using System;
using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Features.BearingPlate.Models;

namespace SDSoftware.RevitTest.Features.BearingPlate.Services
{
    /// <summary>Creates the assembly sheet and places the views and schedules on it.</summary>
    public class SheetBuilder
    {
        private readonly Document _document;

        public SheetBuilder(Document document)
        {
            _document = document;
        }

        public ViewSheet Create(PlateItem plate, ElementId titleBlockTypeId, BearingPlateOptions options)
        {
            var sheet = AssemblyViewUtils.CreateSheet(_document, plate.Assembly.Id, titleBlockTypeId);
            SetNumber(sheet, options.SheetNumberFor(plate.Name));
            SetName(sheet, options.SheetNameFor(plate.Name));
            return sheet;
        }

        /// <summary>
        /// Lower-left corner of the title block, in sheet coordinates. A sheet has no fixed origin -
        /// on the reference drawing it sits at the middle of the bottom edge - so paper positions
        /// are always measured from this corner.
        /// </summary>
        public XYZ GetTitleBlockCorner(ViewSheet sheet)
        {
            _document.Regenerate();

            var titleBlock = new FilteredElementCollector(_document, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstOrDefault();

            return titleBlock?.get_BoundingBox(sheet)?.Min ?? XYZ.Zero;
        }

        public Viewport PlaceView(ViewSheet sheet, View view, XYZ centre)
        {
            if (view == null || !Viewport.CanAddViewToSheet(_document, sheet.Id, view.Id))
            {
                return null;
            }

            return Viewport.Create(_document, sheet.Id, view.Id, centre);
        }

        public ScheduleSheetInstance PlaceSchedule(ViewSheet sheet, ViewSchedule schedule, XYZ upperLeft)
        {
            return schedule == null
                ? null
                : ScheduleSheetInstance.Create(_document, sheet.Id, schedule.Id, upperLeft);
        }

        /// <summary>Sheet numbers must be unique in the model, so clashes get a numeric suffix.</summary>
        private void SetNumber(ViewSheet sheet, string number)
        {
            if (string.IsNullOrWhiteSpace(number))
            {
                return;
            }

            for (var attempt = 0; attempt < 100; attempt++)
            {
                var candidate = attempt == 0 ? number : $"{number}-{attempt + 1}";

                try
                {
                    sheet.SheetNumber = candidate;
                    return;
                }
                catch (Exception)
                {
                    // number already taken - try the next suffix
                }
            }
        }

        private static void SetName(ViewSheet sheet, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            try
            {
                sheet.Name = name;
            }
            catch (Exception)
            {
                // keep the name Revit generated
            }
        }
    }
}
