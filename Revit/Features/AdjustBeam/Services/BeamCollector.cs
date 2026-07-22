using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SDSoftware.RevitTest.Extensions;

namespace SDSoftware.RevitTest.Features.AdjustBeam.Services
{
    /// <summary>
    /// Asks the user for the beams to work on. The pick accepts anything, so a window selection over a
    /// whole bay is enough: Structural Framing is a mixed bag - recesses, skirts and openings live
    /// there too - and the sorting out happens here, on the structural type rather than on the
    /// category or the family name.
    /// </summary>
    public static class BeamCollector
    {
        /// <summary>
        /// Throws <see cref="Autodesk.Revit.Exceptions.OperationCanceledException"/> when the user
        /// presses Esc, which the command base turns into a cancelled result.
        /// </summary>
        public static IList<FamilyInstance> Pick(UIDocument uiDocument)
        {
            var picked = uiDocument.Selection.PickObjects(
                ObjectType.Element,
                "Select the beams to adjust, then click Finish. Anything that is not a beam is ignored.");

            return Filter(uiDocument.Document, picked);
        }

        /// <summary>Keeps only what this tool can move, in the order it was picked.</summary>
        public static IList<FamilyInstance> Filter(Document document, IEnumerable<Reference> references)
        {
            return references
                .Select(reference => document.GetElement(reference))
                .Where(IsAdjustableBeam)
                .Cast<FamilyInstance>()
                .ToList();
        }

        /// <summary>A beam this tool can move: a real beam, running along a straight line.</summary>
        public static bool IsAdjustableBeam(Element element)
        {
            return element is FamilyInstance instance
                   && instance.StructuralType == StructuralType.Beam
                   && instance.GetLocationLine() != null;
        }
    }
}
