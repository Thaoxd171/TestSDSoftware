using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Features.BearingPlate.Models;

namespace SDSoftware.RevitTest.Features.BearingPlate.Services
{
    /// <summary>
    /// Reads the parts of a plate out of its assembly. The assembly holds one data element per part
    /// plus a dimensioning helper; the parts are the ones carrying a part name, which also keeps
    /// the helper family out without hard-coding its name.
    /// </summary>
    public class ComponentCollector
    {
        private readonly Document _document;

        public ComponentCollector(Document document)
        {
            _document = document;
        }

        /// <summary>
        /// Distinct kinds of part in the assembly. The plate outline comes first because it owns the
        /// row of overall dimensions; the rest follow by mark, which is the order the reference
        /// drawings stack their dimension rows and tags in.
        /// </summary>
        public List<PlateComponent> Collect(AssemblyInstance assembly, BoundingBoxXYZ plateBox)
        {
            return assembly.GetMemberIds()
                .Select(_document.GetElement)
                .Where(e => e != null)
                .Select(e => new { Element = e, Name = NameOf(e) })
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => x.Name)
                .Select(g => new PlateComponent(g.Key, g.Select(x => x.Element).ToList(), plateBox))
                .OrderByDescending(c => c.IsOutline)
                .ThenBy(c => c.Mark, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string NameOf(Element element)
        {
            return element.LookupParameter(PlateComponent.NameParameter)?.AsString();
        }
    }
}
