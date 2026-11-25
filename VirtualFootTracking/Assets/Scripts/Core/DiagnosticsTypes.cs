using System;

namespace VirtualFootTracking.Core
{
    /// <summary>
    /// Consolidated telemetry snapshot displayed by the diagnostic HUD and logged by diagnostics.
    /// </summary>
    [Serializable]
    public struct DiagnosticsSnapshot
    {
        public float Fps;
        public float TrackingUpdateHz;
        public double SolverLatencyMs;
        public bool BodyPermissionGranted;
        public bool ScenePermissionGranted;
        public bool BodyTrackingActive;
        public BodyTrackingFidelityMode BodyFidelity;
        public BodyCalibrationState BodyCalibration;
        public float BodyConfidence;
        public bool IsFullBody;
        public bool SceneApiLoaded;
        public FloorDataSource FloorSource;
        public float FloorWorldY;
        public int FloorAnchorCount;
        public VirtualTrackerFrame CurrentFrame;
        public int TrackingLossEventsCount;
        public int ConsecutiveBodyLossFrames;
        public string StatusMessage;
        public string[] RecentErrors;
    }
}
