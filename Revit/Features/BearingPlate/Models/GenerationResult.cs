namespace SDSoftware.RevitTest.Features.BearingPlate.Models
{
    public enum GenerationStatus
    {
        Created,
        Skipped,
        Failed,
    }

    /// <summary>What happened to one assembly during a run, shown in the result table.</summary>
    public class GenerationResult
    {
        public string Assembly { get; set; }

        public string SheetNumber { get; set; }

        public int Views { get; set; }

        public int Schedules { get; set; }

        public GenerationStatus Status { get; set; }

        public string Message { get; set; }

        public static GenerationResult Skipped(string assembly, string reason)
        {
            return new GenerationResult { Assembly = assembly, Status = GenerationStatus.Skipped, Message = reason };
        }

        public static GenerationResult Failed(string assembly, string reason)
        {
            return new GenerationResult { Assembly = assembly, Status = GenerationStatus.Failed, Message = reason };
        }
    }
}
