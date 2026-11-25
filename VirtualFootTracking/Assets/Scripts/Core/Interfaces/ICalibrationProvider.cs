namespace VirtualFootTracking.Core
{
    /// <summary>
    /// Contract for automatic and manual anthropometric calibration and persistence.
    /// </summary>
    public interface ICalibrationProvider
    {
        CalibrationProfile CurrentProfile { get; }
        bool IsCalibrating { get; }
        float CalibrationProgress01 { get; }

        void Initialize();
        bool BeginAutoCalibration(in BodySkeletonSnapshot skeleton, in FloorPlaneData floor);
        void ApplyManualProfile(CalibrationProfile profile);
        void SaveProfile();
        CalibrationProfile LoadProfile();
    }
}
