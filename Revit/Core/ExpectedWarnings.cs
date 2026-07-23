using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace SDSoftware.RevitTest.Core
{
    /// <summary>
    /// Lets a transaction through warnings it was always going to raise, without letting anything else
    /// past.
    ///
    /// A tool that means to raise a warning should not stop and ask about it. Where a tool cuts an
    /// opening deliberately larger than the thing it cuts - so that the cut comes out clean at the
    /// corners rather than leaving spikes - Revit says the opening only partly cuts its host, which is
    /// true and is the point. Made once per beam end, it is a dialog between the user and the finished
    /// job for no purpose.
    ///
    /// So only the named warnings are let past. Anything else the tool manages to raise is a thing
    /// nobody planned, and it still stops and asks.
    ///
    /// Attached to one transaction, not to the application: it lasts exactly as long as that
    /// transaction and cannot be left switched on by a run that fails half way through.
    /// </summary>
    public class ExpectedWarnings : IFailuresPreprocessor
    {
        private readonly IList<FailureDefinitionId> _expected;

        public ExpectedWarnings(params FailureDefinitionId[] expected)
        {
            _expected = expected;
        }

        /// <summary>How many were let past, so the run can say so rather than swallowing them.</summary>
        public int Count { get; private set; }

        /// <summary>The warning raised by an opening that reaches beyond the element it cuts.</summary>
        public static ExpectedWarnings FromOpeningsThatOverreach()
        {
            return new ExpectedWarnings(BuiltInFailures.OpeningFailures.OpeningPartiallyCutsHost);
        }

        /// <summary>Puts this in charge of the failures raised inside one transaction.</summary>
        public void TakeChargeOf(Transaction transaction)
        {
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(this);
            transaction.SetFailureHandlingOptions(options);
        }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failures)
        {
            foreach (var failure in failures.GetFailureMessages()
                         .Where(failure => failure.GetSeverity() == FailureSeverity.Warning)
                         .Where(failure => _expected.Any(id => id == failure.GetFailureDefinitionId()))
                         .ToList())
            {
                failures.DeleteWarning(failure);
                Count++;
            }

            return FailureProcessingResult.Continue;
        }
    }
}
