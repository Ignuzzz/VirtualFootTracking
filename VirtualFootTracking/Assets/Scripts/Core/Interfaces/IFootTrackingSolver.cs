namespace VirtualFootTracking.Core
{
    /// <summary>
    /// Contract for computing the four virtual FBT trackers from body skeleton and floor data.
    /// </summary>
    public interface IFootTrackingSolver
    {
        string SolverName { get; }

        void Initialize(CalibrationProfile profile);
        void UpdateCalibration(CalibrationProfile profile);
        bool Solve(
            in BodySkeletonSnapshot skeleton,
            in FloorPlaneData floor,
            float deltaTime,
            double timestampSeconds,
            out VirtualTrackerFrame frame);
    }
}
