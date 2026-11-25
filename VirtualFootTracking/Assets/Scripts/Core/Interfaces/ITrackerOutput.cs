namespace VirtualFootTracking.Core
{
    /// <summary>
    /// Contract for publishing resolved virtual tracker frames (logs, local diagnostics, or PC transport).
    /// </summary>
    public interface ITrackerOutput
    {
        string OutputName { get; }
        bool IsActive { get; }

        void Initialize();
        void Publish(in VirtualTrackerFrame frame);
        void Shutdown();
    }
}
