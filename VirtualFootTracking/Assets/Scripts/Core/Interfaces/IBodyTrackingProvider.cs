namespace VirtualFootTracking.Core
{
    /// <summary>
    /// Contract for any source of full or partial skeletal body tracking data.
    /// Decouples Meta OVRBody/OpenXR from calibration, solving, and visualization.
    /// </summary>
    public interface IBodyTrackingProvider
    {
        bool IsInitialized { get; }
        bool IsPermissionGranted { get; }
        string StatusMessage { get; }

        void Initialize();
        void RequestPermission();
        bool TryGetLatestSnapshot(out BodySkeletonSnapshot snapshot);
        bool TryApplyHeightCalibration(float heightMeters);
        void Shutdown();
    }
}
