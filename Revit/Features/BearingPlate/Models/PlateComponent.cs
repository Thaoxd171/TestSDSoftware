using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace SDSoftware.RevitTest.Features.BearingPlate.Models
{
    /// <summary>
    /// One kind of part on a plate - a hole, a stud, the plate itself. A plate carries a data
    /// element per physical part, but several of them describe the same kind, so the drawing tags
    /// one representative per kind rather than every instance.
    /// </summary>
    public class PlateComponent
    {
        /// <summary>Instance parameter holding the part name, e.g. "Cirkulaert Hul 011".</summary>
        public const string NameParameter = "DATA-Navn";

        public PlateComponent(string name, IReadOnlyList<Element> instances)
        {
            Name = name;
            Instances = instances;
            Representative = instances[0];
            Mark = Representative.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
        }

        public string Name { get; }

        /// <summary>The element that gets tagged.</summary>
        public Element Representative { get; }

        public IReadOnlyList<Element> Instances { get; }

        public string Mark { get; }

        public int Count => Instances.Count;
    }
}
