using System.Collections.Generic;
using System.Linq;
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

        /// <summary>Height below which a part counts as flat, in feet.</summary>
        private const double FlatTolerance = 1e-6;

        public PlateComponent(string name, IReadOnlyList<Element> instances)
        {
            Name = name;
            Instances = instances;
            Representative = instances[0];
            Mark = Representative.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();

            var levels = instances
                .Select(e => (e.Location as LocationPoint)?.Point.Z)
                .Where(z => z.HasValue)
                .Select(z => z.Value)
                .ToList();

            Height = levels.Count == 0 ? 0 : levels.Max() - levels.Min();
        }

        public string Name { get; }

        /// <summary>The element that gets tagged.</summary>
        public Element Representative { get; }

        public IReadOnlyList<Element> Instances { get; }

        public string Mark { get; }

        public int Count => Instances.Count;

        /// <summary>
        /// How far the part reaches vertically, taken from the spread of its markers. The plate and
        /// anything welded to it are marked top and bottom; a hole through the plate is marked once.
        /// </summary>
        public double Height { get; }

        /// <summary>
        /// True when the part stands proud of the plate. Only these are worth listing down the side
        /// of an elevation - a hole has no height of its own to call out there.
        /// </summary>
        public bool HasHeight => Height > FlatTolerance;
    }
}
