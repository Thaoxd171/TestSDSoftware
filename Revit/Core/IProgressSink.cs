namespace SDSoftware.RevitTest.Core
{
    /// <summary>
    /// How a long running service reports back to the user. Keeping it an interface lets the
    /// services stay free of any WPF reference and be driven by a test double.
    /// </summary>
    public interface IProgressSink
    {
        /// <summary>True once the user has asked to stop; services check it between items.</summary>
        bool IsCancelled { get; }

        /// <summary>Adds a step, or moves an existing one. <paramref name="fraction"/> is 0..1.</summary>
        void Report(string step, double fraction);

        void Log(string message);
    }

    /// <summary>Swallows everything - used when a service runs without a UI.</summary>
    public class NullProgressSink : IProgressSink
    {
        public bool IsCancelled => false;

        public void Report(string step, double fraction)
        {
        }

        public void Log(string message)
        {
        }
    }
}
