using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using SDSoftware.RevitTest.Core;

namespace SDSoftware.RevitTest.Features.BearingPlate
{
    /// <summary>Creates bearing plate detail drawings and places them on sheets.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class BearingPlateCmd : CommandBase
    {
        protected override string Title => "Bearing Plate";

        protected override Result Run(CommandContext context)
        {
            // TODO: implemented in the Bearing Plate step.
            ShowInfo("Not implemented yet.", "The Bearing Plate tool is still being built.");
            return Result.Succeeded;
        }
    }
}
