using System.Collections.Generic;
using System.Linq;

namespace SDSoftware.RevitTest.Features.BearingPlate.Models
{
    /// <summary>
    /// Outcome of annotating one view. Tagging fails for ordinary reasons - a part hidden by the
    /// view template, a point Revit will not accept - so the reasons are collected and reported
    /// instead of being swallowed.
    /// </summary>
    public class TagResult
    {
        private readonly List<string> _notes = new List<string>();

        public int Placed { get; private set; }

        public int Failed { get; private set; }

        public IReadOnlyList<string> Notes => _notes;

        public void Success() => Placed++;

        public void Failure(string component, string reason)
        {
            Failed++;
            var note = $"{component}: {reason}";

            // the same reason usually repeats for every component; report it once
            if (!_notes.Contains(note))
            {
                _notes.Add(note);
            }
        }

        public void Note(string message)
        {
            if (!_notes.Contains(message))
            {
                _notes.Add(message);
            }
        }

        public void Add(TagResult other)
        {
            Placed += other.Placed;
            Failed += other.Failed;

            foreach (var note in other._notes.Where(n => !_notes.Contains(n)))
            {
                _notes.Add(note);
            }
        }

        public string Describe(string viewName)
        {
            var text = $"{viewName} {Placed} tag(s)";
            if (Failed > 0)
            {
                text += $", {Failed} failed";
            }

            return _notes.Count == 0 ? text : $"{text} [{string.Join("; ", _notes)}]";
        }
    }
}
