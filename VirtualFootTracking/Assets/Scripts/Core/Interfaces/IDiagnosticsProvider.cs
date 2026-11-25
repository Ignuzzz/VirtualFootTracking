namespace VirtualFootTracking.Core
{
    /// <summary>
    /// Contract for collecting runtime FPS, tracking update rate, solver latency, and error telemetry.
    /// </summary>
    public interface IDiagnosticsProvider
    {
        void RecordFrame(
            in BodySkeletonSnapshot skeleton,
            in FloorPlaneData floor,
            in VirtualTrackerFrame frame,
            double solverLatencyMs,
            bool bodyPerm,
            bool scenePerm);

        void RecordError(string message);
        DiagnosticsSnapshot GetSnapshot();
    }
}
