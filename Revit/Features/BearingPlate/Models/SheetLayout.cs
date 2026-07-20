using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Extensions;

namespace SDSoftware.RevitTest.Features.BearingPlate.Models
{
    /// <summary>
    /// Where things sit on the sheet, in millimetres measured from the lower-left corner of the
    /// title block. The values are the ones measured on the reference drawing (A3 landscape).
    /// A sheet has no fixed origin of its own, so every position is resolved against the title
    /// block bounding box at generation time.
    /// </summary>
    public static class SheetLayout
    {
        /// <summary>Centre of each viewport.</summary>
        public static readonly (double X, double Y) PlanCentre = (107.1, 74.5);

        public static readonly (double X, double Y) FrontCentre = (123.7, 189.3);

        public static readonly (double X, double Y) ThreeDCentre = (397.1, 277.7);

        /// <summary>
        /// Upper-left corner of each schedule, keyed by the distinctive part of its template name.
        /// Matching is done on the template name so renamed templates still line up.
        /// </summary>
        private static readonly Dictionary<string, (double X, double Y)> ScheduleCorners =
            new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase)
            {
                ["Base Component"] = (215.0, 76.2),
                ["Additional Components"] = (215.0, 72.1),
                ["Weight"] = (275.0, 81.5),
                ["Corrosion Category"] = (251.0, 62.0),
                ["Surface Treatment"] = (251.0, 57.6),
                ["Description"] = (320.8, 10.7),
            };

        /// <summary>Fallback column for a schedule whose template name is not recognised.</summary>
        private static readonly (double X, double Y) UnknownScheduleCorner = (215.0, 40.0);

        private const double UnknownScheduleStep = 12.0;

        public static (double X, double Y) ScheduleCornerFor(string templateName, int fallbackIndex)
        {
            foreach (var pair in ScheduleCorners)
            {
                if (!string.IsNullOrEmpty(templateName) &&
                    templateName.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return pair.Value;
                }
            }

            return (UnknownScheduleCorner.X, UnknownScheduleCorner.Y - fallbackIndex * UnknownScheduleStep);
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
