using System;
using Autodesk.Revit.DB;

namespace SDSoftware.RevitTest.Extensions
{
    /// <summary>
    /// The Revit API works in decimal feet. Every value shown to or entered by the user is in
    /// millimetres / degrees, so conversion happens in exactly one place.
    /// </summary>
    public static class UnitExtensions
    {
        public static double MmToFeet(this double millimetres)
        {
            return UnitUtils.ConvertToInternalUnits(millimetres, UnitTypeId.Millimeters);
        }

        public static double FeetToMm(this double feet)
        {
            return UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
        }

        public static double DegreesToRadians(this double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        public static double RadiansToDegrees(this double radians)
        {
            return radians * 180.0 / Math.PI;
        }
    }
}
