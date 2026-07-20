using Autodesk.Revit.UI;

namespace SDSoftware.RevitTest.Core
{
    /// <summary>
    /// Add-in entry point. Referenced by SDRevitTest.addin.
    /// </summary>
    public class App : IExternalApplication
    {
        /// <summary>Full path of this assembly, used to register commands on the ribbon.</summary>
        public static string AssemblyPath { get; private set; }

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                AssemblyPath = typeof(App).Assembly.Location;
                RibbonBuilder.Build(application);
                return Result.Succeeded;
            }
            catch (System.Exception ex)
            {
                AppLog.ShowError("Startup", ex);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
    }
}
