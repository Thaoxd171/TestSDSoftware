using System.Collections.Generic;

namespace SDSoftware.RevitTest.Features.AdjustBeam.Models
{
    /// <summary>
    /// What the run did. The line by line account goes to the progress log as it happens.
    ///
    /// The job is done in rounds until it settles, so the counts fall into two sorts. What was written
    /// is remembered across the whole run, and an end written twice is still one end adjusted. What
    /// describes the finished model - the openings, the ends left alone - is cleared at the start of
    /// each round, because every round rebuilds them and only the last one is still standing.
    /// </summary>
    public class AdjustResult
    {
        private readonly HashSet<string> _endsWritten = new HashSet<string>();

        private readonly HashSet<long> _beamsWritten = new HashSet<long>();

        public int BeamsExamined { get; set; }

        public int BeamsChanged => _beamsWritten.Count;

        public int EndsMoved => _endsWritten.Count;

        public int EndsAlreadyCorrect { get; set; }

        public int EndsSkipped { get; set; }

        /// <summary>Ends that were written but are not where they were sent. Should always be zero.</summary>
        public int EndsOffTarget { get; set; }

        /// <summary>Openings created to square off skewed ends.</summary>
        public int CutsCreated { get; set; }

        /// <summary>Cuts the solver asked for that the cutter could not make. Should always be zero.</summary>
        public int CutsRefused { get; set; }

        /// <summary>Openings created to take a bearing block out of a crossing beam's way.</summary>
        public int NotchesCreated { get; set; }

        public bool WasStopped { get; set; }

        /// <summary>Set when the rounds ran out with ends still moving. Should always be false.</summary>
        public bool DidNotSettle { get; set; }

        public string Summary =>
            (WasStopped ? "Stopped. " : "Done. ") +
            $"{EndsMoved} beam {(EndsMoved == 1 ? "end" : "ends")} adjusted on {BeamsChanged} of " +
            $"{BeamsExamined} beams, {EndsAlreadyCorrect} already correct, {EndsSkipped} left alone." +
            (CutsCreated > 0 ? $" {CutsCreated} skewed end(s) squared off with an opening." : string.Empty) +
            (NotchesCreated > 0 ? $" {NotchesCreated} bearing block(s) cut back." : string.Empty) +
            (CutsRefused > 0 ? $" WARNING: {CutsRefused} skewed end(s) were left uncut." : string.Empty) +
            (EndsOffTarget > 0 ? $" WARNING: {EndsOffTarget} did not end up where they were sent." : string.Empty) +
            (DidNotSettle ? " WARNING: the ends were still moving when the rounds ran out." : string.Empty);

        /// <summary>Notes an end as written. Writing the same end again in a later round adds nothing.</summary>
        public void RecordMove(long beamId, int end)
        {
            _endsWritten.Add(beamId + ":" + end);
            _beamsWritten.Add(beamId);
        }

        /// <summary>Clears what describes the model, ready for another round to describe it again.</summary>
        public void Reset()
        {
            EndsAlreadyCorrect = 0;
            EndsSkipped = 0;
            CutsCreated = 0;
            CutsRefused = 0;
            NotchesCreated = 0;
        }
    }
}
