using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Extensions;
using SDSoftware.RevitTest.Features.BearingPlate.Models;

namespace SDSoftware.RevitTest.Features.BearingPlate.Services
{
    /// <summary>Creates the assembly sheet and places the views and schedules on it.</summary>
    public class SheetBuilder
    {
        /// <summary>Gap left between the plan and a front view placed beside it.</summary>
        private const double SideGapMm = 10.0;

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

        /// <summary>
        /// Places a view so that its box is centred on <paramref name="centreX"/> with its bottom
        /// edge at <paramref name="bottomY"/> (paper mm from the title block corner). The box size
        /// depends on the annotation inside the view, so it is measured after a provisional
        /// placement and the viewport is then moved into position.
        /// </summary>
        public Viewport PlaceViewAnchoredBottom(ViewSheet sheet, View view, XYZ corner, double centreX, double bottomY)
        {
            var viewport = PlaceView(sheet, view, corner);
            if (viewport == null)
            {
                return null;
            }

            _document.Regenerate();
            var outline = viewport.GetBoxOutline();
            var height = outline.MaximumPoint.Y - outline.MinimumPoint.Y;

            viewport.SetBoxCenter(new XYZ(
                corner.X + centreX.MmToFeet(),
                corner.Y + bottomY.MmToFeet() + height / 2,
                0));

            return viewport;
        }

        /// <summary>
        /// Places a view with its upper-right corner at the given paper position - used for the 3D
        /// view sitting in the top-right corner of the sheet.
        /// </summary>
        public Viewport PlaceViewAnchoredTopRight(ViewSheet sheet, View view, XYZ corner, double rightX, double topY)
        {
            var viewport = PlaceView(sheet, view, corner);
            if (viewport == null)
            {
                return null;
            }

            _document.Regenerate();
            var outline = viewport.GetBoxOutline();
            var width = outline.MaximumPoint.X - outline.MinimumPoint.X;
            var height = outline.MaximumPoint.Y - outline.MinimumPoint.Y;

            viewport.SetBoxCenter(new XYZ(
                corner.X + rightX.MmToFeet() - width / 2,
                corner.Y + topY.MmToFeet() - height / 2,
                0));

            return viewport;
        }

        /// <summary>Top edge of a placed viewport in paper mm, used to stack the next view above it.</summary>
        public double GetTopEdgeMm(Viewport viewport, XYZ corner)
        {
            return viewport == null ? 0 : (viewport.GetBoxOutline().MaximumPoint.Y - corner.Y).FeetToMm();
        }

        /// <summary>
        /// Paper height of the tallest thing already placed on the sheet that is not a view - the
        /// schedules and their title-block tables - measured from the title block corner. The views
        /// are centred in the space above this.
        /// </summary>
        public double TopOfContentMm(ViewSheet sheet, XYZ corner)
        {
            _document.Regenerate();

            var tops = new FilteredElementCollector(_document, sheet.Id)
                .OfClass(typeof(ScheduleSheetInstance))
                .Select(s => s.get_BoundingBox(sheet))
                .Where(b => b != null)
                .Select(b => (b.Max.Y - corner.Y).FeetToMm())
                .ToList();

            return tops.Count == 0 ? 0 : tops.Max();
        }

        /// <summary>
        /// Left edge (paper mm from the corner) of the leftmost schedule - the right boundary of the
        /// drawing area on A3, where the schedules sit in the right half. Falls back to the frame
        /// width when there are no schedules, leaving the whole width for the views.
        /// </summary>
        public double LeftOfContentMm(ViewSheet sheet, XYZ corner)
        {
            _document.Regenerate();

            var lefts = new FilteredElementCollector(_document, sheet.Id)
                .OfClass(typeof(ScheduleSheetInstance))
                .Select(s => s.get_BoundingBox(sheet))
                .Where(b => b != null)
                .Select(b => (b.Min.X - corner.X).FeetToMm())
                .ToList();

            return lefts.Count == 0 ? FrameWidthMm(sheet, corner) : lefts.Min();
        }

        /// <summary>Paper height of the top edge of the title block, measured from its own corner.</summary>
        public double FrameTopMm(ViewSheet sheet, XYZ corner)
        {
            var box = TitleBlockBox(sheet);
            return box == null ? 0 : (box.Max.Y - corner.Y).FeetToMm();
        }

        /// <summary>Paper width of the title block, measured from its own corner.</summary>
        public double FrameWidthMm(ViewSheet sheet, XYZ corner)
        {
            var box = TitleBlockBox(sheet);
            return box == null ? 0 : (box.Max.X - corner.X).FeetToMm();
        }

        private BoundingBoxXYZ TitleBlockBox(ViewSheet sheet)
        {
            return new FilteredElementCollector(_document, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstOrDefault()?
                .get_BoundingBox(sheet);
        }

        /// <summary>How close to the frame a view has to be before it counts as overrunning it (feet).</summary>
        private const double Tolerance = 1e-6;

        /// <summary>Score added when a candidate overlaps the 3D, so any clear layout always wins.</summary>
        private const double OverlapPenalty = 1e6;

        /// <summary>Result of a layout attempt: whether the views fit, and by how much they overran if not.</summary>
        public struct LayoutOutcome
        {
            public bool Fits;
            public double DeficitMm;
        }

        /// <summary>
        /// Positions the plan and front inside a drawing-area rectangle (paper mm from the title block
        /// corner) clear of the 3D view. Nothing is committed until a placement is chosen: the two
        /// views are measured once where they were provisionally placed, every candidate arrangement -
        /// stacked or side by side, centred or dropped below the 3D - is scored against the frame and
        /// the 3D purely arithmetically, and only the best is written back. When none fits, the
        /// least-bad one is kept and the shortfall returned so the caller can report it.
        /// </summary>
        public LayoutOutcome LayoutPlanAndFront(Viewport plan, Viewport front, Viewport threeD, XYZ corner,
            double leftMm, double rightMm, double bottomMm, double topMm, double preferredCentreXMm)
        {
            if (plan == null || front == null)
            {
                return new LayoutOutcome { Fits = true, DeficitMm = 0 };
            }

            _document.Regenerate();

            var planBox = plan.GetBoxOutline();
            var frontBox = front.GetBoxOutline();
            var planW = planBox.MaximumPoint.X - planBox.MinimumPoint.X;
            var planH = planBox.MaximumPoint.Y - planBox.MinimumPoint.Y;
            var frontW = frontBox.MaximumPoint.X - frontBox.MinimumPoint.X;
            var frontH = frontBox.MaximumPoint.Y - frontBox.MinimumPoint.Y;

            var left = corner.X + leftMm.MmToFeet();
            var right = corner.X + rightMm.MmToFeet();
            var bottom = corner.Y + bottomMm.MmToFeet();
            var top = corner.Y + topMm.MmToFeet();
            var preferredX = corner.X + preferredCentreXMm.MmToFeet();
            var gap = SideGapMm.MmToFeet();
            var threeDBox = threeD?.GetBoxOutline();

            var best = Solve(planW, planH, frontW, frontH, left, right, bottom, top, preferredX, gap, threeDBox);

            plan.SetBoxCenter(best.PlanCentre);
            front.SetBoxCenter(best.FrontCentre);

            return new LayoutOutcome { Fits = best.Badness < Tolerance, DeficitMm = best.Deficit.FeetToMm() };
        }

        /// <summary>A chosen placement plus how badly it misses the frame, used to pick between candidates.</summary>
        private struct Placement
        {
            public XYZ PlanCentre;
            public XYZ FrontCentre;
            public double Badness;
            public double Deficit;
        }

        /// <summary>
        /// Picks the best of a fixed set of arrangements for the plan and front. Everything here is
        /// arithmetic on the measured sizes - no viewport is moved - so the winner is known to fit (or
        /// known not to) before anything is committed. Candidates are tried in order of preference and
        /// the first perfect fit wins.
        /// </summary>
        private static Placement Solve(
            double planW, double planH, double frontW, double frontH,
            double left, double right, double bottom, double top,
            double preferredX, double gap, Outline threeD)
        {
            var midX = (left + right) / 2;
            var stackH = planH + gap + frontH;
            var pairH = Math.Max(planH, frontH);

            // centre a block of height h vertically in the area
            double Centred(double h) => bottom + Math.Max(0, top - bottom - h) / 2;

            // push a block down until its top clears the 3D, but never below the area
            double BelowThreeD(double h)
            {
                var b = Centred(h);
                if (threeD != null)
                {
                    b = Math.Min(b, threeD.MinimumPoint.Y - gap - h);
                }

                return Math.Max(bottom, b);
            }

            var candidates = new List<(XYZ plan, XYZ front)>
            {
                Stacked(preferredX, Centred(stackH), planH, frontH, gap),
                Stacked(midX, Centred(stackH), planH, frontH, gap),
                Stacked(preferredX, BelowThreeD(stackH), planH, frontH, gap),
                Stacked(midX, BelowThreeD(stackH), planH, frontH, gap),
                SideBySide(midX, Centred(pairH), planW, planH, frontW, frontH, gap),
                SideBySide(midX, BelowThreeD(pairH), planW, planH, frontW, frontH, gap),
            };

            var best = default(Placement);
            var haveBest = false;

            foreach (var candidate in candidates)
            {
                var planRect = Rect(candidate.plan, planW, planH);
                var frontRect = Rect(candidate.front, frontW, frontH);

                var overflow = Math.Max(
                    Overflow(planRect, left, right, bottom, top),
                    Overflow(frontRect, left, right, bottom, top));
                var hitThreeD = Overlaps(planRect, threeD) || Overlaps(frontRect, threeD);
                var badness = overflow + (hitThreeD ? OverlapPenalty : 0);

                if (!haveBest || badness < best.Badness - Tolerance)
                {
                    best = new Placement
                    {
                        PlanCentre = candidate.plan,
                        FrontCentre = candidate.front,
                        Badness = badness,
                        Deficit = overflow,
                    };
                    haveBest = true;
                }

                if (best.Badness < Tolerance)
                {
                    break;
                }
            }

            return best;
        }

        /// <summary>Front stacked above the plan, both centred on the same column.</summary>
        private static (XYZ plan, XYZ front) Stacked(double centreX, double blockBottom, double planH, double frontH, double gap)
        {
            var plan = new XYZ(centreX, blockBottom + planH / 2, 0);
            var front = new XYZ(centreX, blockBottom + planH + gap + frontH / 2, 0);
            return (plan, front);
        }

        /// <summary>Plan on the left and front on the right, bottoms aligned, the pair centred on a column.</summary>
        private static (XYZ plan, XYZ front) SideBySide(
            double centreX, double blockBottom, double planW, double planH, double frontW, double frontH, double gap)
        {
            var pairLeft = centreX - (planW + gap + frontW) / 2;
            var plan = new XYZ(pairLeft + planW / 2, blockBottom + planH / 2, 0);
            var front = new XYZ(pairLeft + planW + gap + frontW / 2, blockBottom + frontH / 2, 0);
            return (plan, front);
        }

        /// <summary>A box of the given size centred on a point.</summary>
        private static Outline Rect(XYZ centre, double width, double height)
        {
            return new Outline(
                new XYZ(centre.X - width / 2, centre.Y - height / 2, 0),
                new XYZ(centre.X + width / 2, centre.Y + height / 2, 0));
        }

        /// <summary>How far the box pokes out of the area rectangle, on whichever axis is worse (feet).</summary>
        private static double Overflow(Outline box, double left, double right, double bottom, double top)
        {
            var dx = Math.Max(0, Math.Max(left - box.MinimumPoint.X, box.MaximumPoint.X - right));
            var dy = Math.Max(0, Math.Max(bottom - box.MinimumPoint.Y, box.MaximumPoint.Y - top));
            return Math.Max(dx, dy);
        }

        private static bool Overlaps(Outline a, Outline b)
        {
            if (a == null || b == null)
            {
                return false;
            }

            return a.MinimumPoint.X < b.MaximumPoint.X && a.MaximumPoint.X > b.MinimumPoint.X
                && a.MinimumPoint.Y < b.MaximumPoint.Y && a.MaximumPoint.Y > b.MinimumPoint.Y;
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
