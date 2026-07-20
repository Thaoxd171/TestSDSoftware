using System;
using Autodesk.Revit.DB;

namespace SDSoftware.RevitTest.Core
{
    /// <summary>
    /// Wraps Revit transactions so that any exception rolls the document back to its previous state.
    /// </summary>
    public static class TransactionHelper
    {
        public static void Run(Document document, string name, Action action)
        {
            Run(document, name, () =>
            {
                action();
                return true;
            });
        }

        /// <summary>
        /// Runs <paramref name="action"/> inside a transaction. Returning false from the action
        /// rolls back instead of committing.
        /// </summary>
        public static bool Run(Document document, string name, Func<bool> action)
        {
            using (var transaction = new Transaction(document, name))
            {
                transaction.Start();
                try
                {
                    if (!action())
                    {
                        transaction.RollBack();
                        return false;
                    }

                    transaction.Commit();
                    return true;
                }
                catch
                {
                    if (transaction.HasStarted())
                    {
                        transaction.RollBack();
                    }

                    throw;
                }
            }
        }
    }
}
