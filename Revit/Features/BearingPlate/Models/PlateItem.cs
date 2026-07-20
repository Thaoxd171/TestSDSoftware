using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Extensions;

namespace SDSoftware.RevitTest.Features.BearingPlate.Models
{
    /// <summary>
    /// One bearing plate in the model, together with the assembly it belongs to (if any) and the
    /// sheet already drawn for it.
    /// </summary>
    public class PlateItem
    {
        public PlateItem(Document document, FamilyInstance plate)
        {
            Plate = plate;
            FamilyName = plate.Symbol?.FamilyName;
            TypeName = plate.Symbol?.Name;

            Assembly = document.GetElement(plate.AssemblyInstanceId) as AssemblyInstance;

            if (Assembly != null)
            {
                ExistingSheet = document.OfClass<ViewSheet>()
                    .FirstOrDefault(s => s.AssociatedAssemblyInstanceId == Assembly.Id);
            }
        }

        public FamilyInstance Plate { get; }

        public string FamilyName { get; }

        public string TypeName { get; }

        public AssemblyInstance Assembly { get; private set; }

        public ViewSheet ExistingSheet { get; }

        public bool HasAssembly => Assembly != null;

        public bool HasSheet => ExistingSheet != null;

        /// <summary>Name used for the sheet: the assembly name, falling back to the plate type.</summary>
        public string Name => Assembly?.Name ?? TypeName ?? FamilyName;

        public void AttachAssembly(AssemblyInstance assembly)
        {
            Assembly = assembly;
        }
    }
}
