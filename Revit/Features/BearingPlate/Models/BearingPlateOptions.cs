namespace SDSoftware.RevitTest.Features.BearingPlate.Models
{
    /// <summary>
    /// What the user chose in the dialog. Persisted between sessions by name rather than by
    /// ElementId, so the settings survive being used on a different model.
    /// </summary>
    public class BearingPlateOptions
    {
        public const string AssemblyToken = "{assembly}";

        public string TitleBlockTypeName { get; set; }

        public string PlanTemplateName { get; set; }

        public string FrontTemplateName { get; set; }

        public string ThreeDTemplateName { get; set; }

        public string SheetNumberPattern { get; set; } = AssemblyToken + "-PL";

        public string SheetNamePattern { get; set; } = "Bearing Plate " + AssemblyToken;

        /// <summary>Assemblies that already have a sheet are left alone.</summary>
        public bool SkipExisting { get; set; } = true;

        public bool CreateSchedules { get; set; } = true;

        public bool CreateThreeD { get; set; } = true;

        public string SheetNumberFor(string assemblyName)
        {
            return (SheetNumberPattern ?? string.Empty).Replace(AssemblyToken, assemblyName);
        }

        public string SheetNameFor(string assemblyName)
        {
            return (SheetNamePattern ?? string.Empty).Replace(AssemblyToken, assemblyName);
        }
    }
}
