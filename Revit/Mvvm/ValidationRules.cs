namespace SDSoftware.RevitTest.Mvvm
{
    /// <summary>
    /// Reusable numeric checks for the tool dialogs. Each method returns an error message,
    /// or null when the value is acceptable.
    /// </summary>
    public static class ValidationRules
    {
        public static string Positive(double value, string displayName)
        {
            return value > 0 ? null : $"{displayName} must be greater than 0.";
        }

        public static string InRange(double value, double min, double max, string displayName, string unit = null)
        {
            if (value >= min && value <= max)
            {
                return null;
            }

            var suffix = string.IsNullOrEmpty(unit) ? string.Empty : " " + unit;
            return $"{displayName} must be between {min:0.##}{suffix} and {max:0.##}{suffix}.";
        }

        public static string AtLeast(int value, int min, string displayName)
        {
            return value >= min ? null : $"{displayName} must be at least {min}.";
        }

        public static string NotEmpty(string value, string displayName)
        {
            return string.IsNullOrWhiteSpace(value) ? $"{displayName} is required." : null;
        }
    }
}
