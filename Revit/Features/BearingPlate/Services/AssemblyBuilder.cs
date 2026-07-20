using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Extensions;
using SDSoftware.RevitTest.Features.BearingPlate.Models;

namespace SDSoftware.RevitTest.Features.BearingPlate.Services
{
    /// <summary>
    /// Isolates a plate into its own assembly. Plates that already belong to one are left alone -
    /// the reference model ships them pre-assembled.
    /// </summary>
    public class AssemblyBuilder
    {
        /// <summary>How far outside the plate a data element may sit and still count as part of it.</summary>
        private const double CaptureMarginMm = 50.0;

        private readonly Document _document;

        public AssemblyBuilder(Document document)
        {
            _document = document;
        }

        /// <summary>Returns the plate's assembly, creating it when the plate has none.</summary>
        public AssemblyInstance EnsureAssembly(PlateItem plate)
        {
            if (plate.HasAssembly)
            {
                return plate.Assembly;
            }

            var members = new List<ElementId> { plate.Plate.Id };
            members.AddRange(FindDataElementsAround(plate.Plate));

            var assembly = AssemblyInstance.Create(_document, members, plate.Plate.Category.Id);
            _document.Regenerate();

            assembly.AssemblyTypeName = plate.TypeName;
            plate.AttachAssembly(assembly);
            return assembly;
        }

        /// <summary>
        /// The data devices that describe the plate's components sit inside its footprint, so they
        /// are picked up by an expanded bounding box rather than by name.
        /// </summary>
        private IEnumerable<ElementId> FindDataElementsAround(Element plate)
        {
            var box = plate.get_BoundingBox(null);
            if (box == null)
            {
                return Enumerable.Empty<ElementId>();
            }

            var margin = CaptureMarginMm.MmToFeet();
            var outline = new Outline(
                box.Min - new XYZ(margin, margin, margin),
                box.Max + new XYZ(margin, margin, margin));

            return new FilteredElementCollector(_document)
                .OfCategory(BuiltInCategory.OST_DataDevices)
                .WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIsInsideFilter(outline))
                .Where(e => e.AssemblyInstanceId == ElementId.InvalidElementId)
                .Select(e => e.Id)
                .ToList();
        }
    }
}
