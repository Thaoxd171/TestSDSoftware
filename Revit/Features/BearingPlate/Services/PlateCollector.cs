using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Features.BearingPlate.Models;

namespace SDSoftware.RevitTest.Features.BearingPlate.Services
{
    /// <summary>
    /// Finds the bearing plates in a model. In this project a plate is a generic model family
    /// instance - one family and one type per plate, named PL-01, PL-02 and so on.
    /// </summary>
    public class PlateCollector
    {
        private readonly Document _document;

        public PlateCollector(Document document)
        {
            _document = document;
        }

        public List<PlateItem> Collect()
        {
            return new FilteredElementCollector(_document)
                .OfCategory(BuiltInCategory.OST_GenericModel)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Select(plate => new PlateItem(_document, plate))
                .OrderBy(p => p.TypeName)
                .ToList();
        }
    }
}
