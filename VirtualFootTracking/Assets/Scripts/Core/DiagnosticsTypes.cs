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
        public TrackingEngineMode ActiveEngineMode;
        public bool KalmanPredictorEnabled;
        public string ActiveSolverName;
        public bool DepthSupported;
        public bool DepthReady;
        public bool DepthFusionEnabled;
        public bool DepthCapturingBackground;
        public int DepthBaselineVoxelCount;
        public int DepthRawHitCount;
        public int DepthForegroundPointCount;
        public int DepthLeftHits;
        public int DepthRightHits;
        public int DepthRejFloor;
        public int DepthRejReach;
        public int DepthRejArm;
        public int DepthRejBgVox;
        public int DepthRejMruk;
        public string DepthStatusMessage;
        public string StatusMessage;
        public string[] RecentErrors;
    }
}
