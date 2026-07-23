using System;
using Autodesk.Revit.UI;
using SDSoftware.RevitTest.Features.AdjustBeam;
using SDSoftware.RevitTest.Features.BearingPlate;
using SDSoftware.RevitTest.Features.Diagnostics;
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
                tooltip: "Trim and extend beams to the right clearance from their supports.",
                longDescription: "Set the clearances, then pick the beams in the model. Every end is moved " +
                                 "to the clearance you asked for from the wall, pillar or beam it runs " +
                                 "into, and the report says what moved and against what.",
                iconFile: "AdjustBeam");

            AddButton<SpiralStairCmd>(
                modelling,
                name: "SpiralStair",
                text: "Spiral\nStair",
                tooltip: "Build a spiral staircase from parameters.",
                longDescription: "Creates a spiral staircase from radius, total height, number of risers, " +
                                 "sweep angle, run width and rotation direction.",
                iconFile: "SpiralStair");

            // Development aid - remove this panel before the final submission.
            var diagnostics = application.CreateRibbonPanel(TabName, "Diagnostics");
            AddButton<ModelProbeCmd>(
                diagnostics,
                name: "ModelProbe",
                text: "Model\nProbe",
                tooltip: "Survey the open model and show the report (read-only).",
                longDescription: "Lists categories, assemblies, candidate bearing plates, title blocks " +
                                 "and view templates so the tools can be written against the real model.",
                iconFile: "ModelProbe");

            AddButton<BearingPlateProbeCmd>(
                diagnostics,
                name: "BearingPlateProbe",
                text: "Plate\nProbe",
                tooltip: "Report one bearing plate assembly and its drawing in full detail (read-only).",
                longDescription: "Select an assembly first to inspect that one. Reports members, assembly " +
                                 "views, sheet layout with viewport centres in mm, dimensions, tags and schedules.",
                iconFile: "ModelProbe");

            AddButton<AdjustBeamProbeCmd>(
                diagnostics,
                name: "AdjustBeamProbe",
                text: "Beam\nProbe",
                tooltip: "Measure every beam end against its supports and show the report (read-only).",
                longDescription: "Select beams first to inspect only those, otherwise every beam in the " +
                                 "active view is measured. Reports the distance from each beam end to the " +
                                 "near face, far face and centre of the walls, columns and beams around it.",
                iconFile: "AdjustBeam");

            AddButton<AdjustBeamExplainCmd>(
                diagnostics,
                name: "AdjustBeamExplain",
                text: "Beam\nExplain",
                tooltip: "Dry run of Adjust Beams: report every decision without changing anything.",
                longDescription: "Pick the beams as usual. For each end it lists the supports the tool " +
                                 "found, the ones it discarded and why, which one governs, and where the " +
                                 "end would move to. The model is not touched.",
                iconFile: "AdjustBeam");

            AddButton<SelectionProbeCmd>(
                diagnostics,
                name: "SelectionProbe",
                text: "Selection\nProbe",
                tooltip: "Report everything about the selected elements and how they sit together (read-only).",
                longDescription: "Select the pieces first - an opening, the beam it cuts, the column beside " +
                                 "it - then run this. Lists geometry, parameters and, for every pair, the " +
                                 "parallel faces with the gap between them.",
                iconFile: "ModelProbe");

            AddButton<ResetGeneratedCmd>(
                diagnostics,
                name: "ResetGenerated",
                text: "Reset\nDrawings",
                tooltip: "Delete the assembly sheets and views so the generator can run again.",
                longDescription: "Removes every sheet and view bound to an assembly. Assemblies, view " +
                                 "templates, title blocks and families are kept.",
                iconFile: "ModelProbe");
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
