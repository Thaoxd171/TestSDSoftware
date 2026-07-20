using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using SDSoftware.RevitTest.Core;

namespace SDSoftware.RevitTest.Features.SpiralStair
{
    /// <summary>Builds a spiral staircase from user supplied parameters.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SpiralStairCmd : CommandBase
    {
        protected override string Title => "Spiral Stair";

        protected override Result Run(CommandContext context)
        {
            // TODO: implemented in the Spiral Stair step.
            ShowInfo("Not implemented yet.", "The Spiral Stair tool is still being built.");
            return Result.Succeeded;
        }
    }
}
