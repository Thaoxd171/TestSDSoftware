using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using SDSoftware.RevitTest.Core;

namespace SDSoftware.RevitTest.Features.AdjustBeam
{
    /// <summary>Adjusts structural framing elements against a reference.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AdjustBeamCmd : CommandBase
    {
        protected override string Title => "Adjust Beams";

        protected override Result Run(CommandContext context)
        {
            // TODO: implemented in the Adjust Beam step.
            ShowInfo("Not implemented yet.", "The Adjust Beams tool is still being built.");
            return Result.Succeeded;
        }
    }
}
