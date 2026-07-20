using System;
using Autodesk.Revit.UI;
using SDSoftware.RevitTest.Features.AdjustBeam;
using SDSoftware.RevitTest.Features.BearingPlate;
using SDSoftware.RevitTest.Features.SpiralStair;

namespace SDSoftware.RevitTest.Core
{
    /// <summary>
    /// Creates the "SD Software" ribbon tab and its buttons.
    /// </summary>
    internal static class RibbonBuilder
    {
        private const string TabName = "SD Software";

        public static void Build(UIControlledApplication application)
        {
            application.CreateRibbonTab(TabName);

            var detailing = application.CreateRibbonPanel(TabName, "Detailing");
            AddButton<BearingPlateCmd>(
                detailing,
                name: "BearingPlate",
                text: "Bearing\nPlate",
                tooltip: "Create bearing plate detail drawings and place them on sheets.",
                longDescription: "Collects the bearing plates in the model, creates a detail view for each " +
                                 "plate type, adds dimensions and tags, then places the views on sheets.",
                iconFile: "BearingPlate");

            var modelling = application.CreateRibbonPanel(TabName, "Modelling");
            AddButton<AdjustBeamCmd>(
                modelling,
                name: "AdjustBeam",
                text: "Adjust\nBeams",
                tooltip: "Adjust structural framing elements against a reference.",
                longDescription: "Extends, trims or aligns the selected structural framing elements and " +
                                 "reports every element that was changed.",
                iconFile: "AdjustBeam");

            AddButton<SpiralStairCmd>(
                modelling,
                name: "SpiralStair",
                text: "Spiral\nStair",
                tooltip: "Build a spiral staircase from parameters.",
                longDescription: "Creates a spiral staircase from radius, total height, number of risers, " +
                                 "sweep angle, run width and rotation direction.",
                iconFile: "SpiralStair");
        }

        private static PushButton AddButton<TCommand>(
            RibbonPanel panel,
            string name,
            string text,
            string tooltip,
            string longDescription,
            string iconFile)
            where TCommand : IExternalCommand
        {
            var data = new PushButtonData(name, text, App.AssemblyPath, typeof(TCommand).FullName)
            {
                ToolTip = tooltip,
                LongDescription = longDescription,
                AvailabilityClassName = typeof(ProjectDocumentAvailability).FullName,
            };

            var button = (PushButton)panel.AddItem(data);
            button.Image = ImageUtils.LoadEmbeddedPng(iconFile + "16.png");
            button.LargeImage = ImageUtils.LoadEmbeddedPng(iconFile + "32.png");
            return button;
        }
    }
}
