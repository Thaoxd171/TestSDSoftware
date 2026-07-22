namespace SDSoftware.RevitTest.Features.AdjustBeam.Models
{
    /// <summary>What the run did. The line by line account goes to the progress log as it happens.</summary>
    public class AdjustResult
    {
        public int BeamsExamined { get; set; }

        public int BeamsChanged { get; set; }

        public int EndsMoved { get; set; }

        public int EndsAlreadyCorrect { get; set; }

        public int EndsSkipped { get; set; }

        public bool WasStopped { get; set; }

        public string Summary =>
            (WasStopped ? "Stopped. " : "Done. ") +
            $"{EndsMoved} beam {(EndsMoved == 1 ? "end" : "ends")} adjusted on {BeamsChanged} of " +
            $"{BeamsExamined} beams, {EndsAlreadyCorrect} already correct, {EndsSkipped} left alone.";
    }
}
