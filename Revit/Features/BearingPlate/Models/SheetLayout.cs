using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Extensions;

namespace SDSoftware.RevitTest.Features.BearingPlate.Models
{
    public enum SheetFormat
    {
        A4Portrait,
        A3Landscape,
    }

    /// <summary>
    /// Where things sit on a bearing plate sheet, in millimetres from the lower-left corner of the
    /// title block. The numbers were measured on the fifteen reference drawings.
    ///
    /// Two things are constant per paper size: the schedule block, and the column the plan and front
    /// views are centred on. The viewport sizes are not - they grow with the amount of annotation -
    /// so the views are anchored by an edge and nudged into place after they are measured.
    /// </summary>
    public class SheetLayout
    {
        /// <summary>
        /// Longest plate on an A4 sheet in the reference set is PL-12 at 320 mm; the shortest plate
        /// on an A3 sheet is PL-08 at 375 mm. The cutoff sits in the middle of that 55 mm gap, so a
        /// few thousandths of a millimetre of feet-to-mm rounding error can never flip the result.
        /// </summary>
        private const double A4MaxLengthMm = 347.5;

        /// <summary>A3 landscape is A4 portrait with the same block shifted a half sheet to the right.</summary>
        private const double A3ScheduleOffsetMm = 210.0;

        /// <summary>Upper-left corner of each schedule on A4, keyed by the distinctive part of the template name.</summary>
        private static readonly Dictionary<string, (double X, double Y)> A4ScheduleCorners =
            new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase)
            {
                ["Base Component"] = (5.0, 76.2),
                ["Additional Components"] = (5.0, 72.1),
                ["Corrosion Category"] = (41.0, 62.0),
                ["Surface Treatment"] = (41.0, 57.6),
                ["Weight"] = (65.0, 81.5),
                ["Description"] = (110.8, 10.7),
            };

        private SheetLayout(SheetFormat format, double viewCentreX, double planBottomY, (double X, double Y) threeDTopRight)
        {
            Format = format;
            ViewCentreX = viewCentreX;
            PlanBottomY = planBottomY;
            ThreeDTopRight = threeDTopRight;
        }

        public SheetFormat Format { get; }

        /// <summary>The column the plan and front views are centred on.</summary>
        public double ViewCentreX { get; }

        /// <summary>
        /// Bottom edge of the plan view. On A4 the schedules occupy the lower left corner, so the
        /// views start above them; on A3 the schedules sit in the right half and the views can go low.
        /// </summary>
        public double PlanBottomY { get; }

        /// <summary>Upper-right corner of the 3D view.</summary>
        public (double X, double Y) ThreeDTopRight { get; }

        /// <summary>Gap between the top of the plan view and the bottom of the front view.</summary>
        public double FrontGapY => 15.0;

        /// <summary>Chooses the paper size from the plate's longest horizontal dimension.</summary>
        public static SheetFormat ChooseFormat(double plateLengthMm)
        {
            return plateLengthMm <= A4MaxLengthMm ? SheetFormat.A4Portrait : SheetFormat.A3Landscape;
        }

        public static SheetLayout For(SheetFormat format)
        {
            return format == SheetFormat.A4Portrait
                ? new SheetLayout(format, viewCentreX: 87.1, planBottomY: 90.0, threeDTopRight: (208.0, 295.5))
                : new SheetLayout(format, viewCentreX: 107.1, planBottomY: 7.0, threeDTopRight: (418.0, 296.0));
        }

        public (double X, double Y) ScheduleCornerFor(string templateName, int fallbackIndex)
        {
            var shift = Format == SheetFormat.A3Landscape ? A3ScheduleOffsetMm : 0.0;

            foreach (var pair in A4ScheduleCorners)
            {
                if (!string.IsNullOrEmpty(templateName) &&
                    templateName.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return (pair.Value.X + shift, pair.Value.Y);
                }
            }

            // unknown template: stack it under the block so nothing is lost
            return (5.0 + shift, 40.0 - fallbackIndex * 12.0);
        }

        /// <summary>Converts a paper position to sheet coordinates using the title block corner.</summary>
        public static XYZ ToSheetPoint(XYZ titleBlockCorner, (double X, double Y) paper)
        {
            return new XYZ(
                titleBlockCorner.X + paper.X.MmToFeet(),
                titleBlockCorner.Y + paper.Y.MmToFeet(),
                0);
        }
    }
}
