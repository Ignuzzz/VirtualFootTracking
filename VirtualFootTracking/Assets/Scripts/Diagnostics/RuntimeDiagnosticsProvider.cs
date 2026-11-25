using System.Collections.Generic;
using UnityEngine;
using VirtualFootTracking.Core;

namespace VirtualFootTracking.Diagnostics
{
    /// <summary>
    /// Collects real-time FPS, SDK update frequency, solver latency, and tracking loss events.
    /// </summary>
    public class RuntimeDiagnosticsProvider : MonoBehaviour, IDiagnosticsProvider
    {
        private const int MaxRecentErrors = 8;

        private readonly Queue<double> _trackingSampleTimes = new Queue<double>(128);
        private readonly Queue<string> _recentErrors = new Queue<string>(MaxRecentErrors);

        private float _smoothedDeltaTime = 1f / 72f;
        private double _lastSdkTimestamp = -1.0;
        private uint _lastSkeletonChangedCount = uint.MaxValue;
        private bool _wasBodyAvailableLastFrame;
        private DiagnosticsSnapshot _latestSnapshot;

        public void RecordFrame(
            in BodySkeletonSnapshot skeleton,
            in FloorPlaneData floor,
            in VirtualTrackerFrame frame,
            double solverLatencyMs,
            bool bodyPerm,
            bool scenePerm)
        {
            float dt = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
            _smoothedDeltaTime = Mathf.Lerp(_smoothedDeltaTime, dt, 0.1f);
            float fps = 1f / Mathf.Max(_smoothedDeltaTime, 0.0001f);

            double now = Time.realtimeSinceStartupAsDouble;
            if (skeleton.IsAvailable)
            {
                bool isNewSample =
                    System.Math.Abs(skeleton.SdkTimestampSeconds - _lastSdkTimestamp) > 1e-6 ||
                    skeleton.SkeletonChangedCount != _lastSkeletonChangedCount;

                if (isNewSample)
                {
                    _lastSdkTimestamp = skeleton.SdkTimestampSeconds;
                    _lastSkeletonChangedCount = skeleton.SkeletonChangedCount;
                    _trackingSampleTimes.Enqueue(now);
                }

                _latestSnapshot.ConsecutiveBodyLossFrames = 0;
                _wasBodyAvailableLastFrame = true;
            }
            else
            {
                if (_wasBodyAvailableLastFrame)
                {
                    _latestSnapshot.TrackingLossEventsCount++;
                    _wasBodyAvailableLastFrame = false;
                }
                _latestSnapshot.ConsecutiveBodyLossFrames++;
            }

            while (_trackingSampleTimes.Count > 0 && now - _trackingSampleTimes.Peek() > 1.0)
            {
                _trackingSampleTimes.Dequeue();
            }

            _latestSnapshot.Fps = fps;
            _latestSnapshot.TrackingUpdateHz = _trackingSampleTimes.Count;
            _latestSnapshot.SolverLatencyMs = solverLatencyMs;
            _latestSnapshot.BodyPermissionGranted = bodyPerm;
            _latestSnapshot.ScenePermissionGranted = scenePerm;
            _latestSnapshot.BodyTrackingActive = skeleton.IsAvailable;
            _latestSnapshot.BodyFidelity = skeleton.Fidelity;
            _latestSnapshot.BodyCalibration = skeleton.CalibrationState;
            _latestSnapshot.BodyConfidence = skeleton.Confidence;
            _latestSnapshot.IsFullBody = skeleton.IsFullBodyJointSet;
            _latestSnapshot.SceneApiLoaded = floor.Source == FloorDataSource.MrukSceneApi;
            _latestSnapshot.FloorSource = floor.Source;
            _latestSnapshot.FloorWorldY = floor.WorldHeightY;
            _latestSnapshot.FloorAnchorCount = floor.AnchorCount;
            _latestSnapshot.CurrentFrame = frame;
            _latestSnapshot.RecentErrors = _recentErrors.ToArray();

            if (!bodyPerm)
            {
                _latestSnapshot.StatusMessage = "Missing BODY_TRACKING permission";
            }
            else if (!skeleton.IsAvailable)
            {
                _latestSnapshot.StatusMessage = "Waiting for OVRBody OpenXR FullBody data";
            }
            else if (!scenePerm)
            {
                _latestSnapshot.StatusMessage = "Body active; USE_SCENE permission pending (floor fallback)";
            }
            else if (floor.Source != FloorDataSource.MrukSceneApi)
            {
                _latestSnapshot.StatusMessage = "Body active; MRUK room not scanned (floor fallback)";
            }
            else
            {
                _latestSnapshot.StatusMessage = "Nominal: IOBT/FullBody + MRUK Scene Floor active";
            }
        }

        public void SetPipelineInfo(TrackingEngineMode mode, bool kalmanEnabled, string solverName)
        {
            _latestSnapshot.ActiveEngineMode = mode;
            _latestSnapshot.KalmanPredictorEnabled = kalmanEnabled;
            _latestSnapshot.ActiveSolverName = solverName ?? string.Empty;
        }

        public void SetDepthTelemetry(in DepthObservationFrame depth, bool depthFusionEnabled)
        {
            _latestSnapshot.DepthSupported = depth.IsSupported;
            _latestSnapshot.DepthReady = depth.IsReady;
            _latestSnapshot.DepthFusionEnabled = depthFusionEnabled;
            _latestSnapshot.DepthCapturingBackground = depth.IsCapturingBackground;
            _latestSnapshot.DepthBaselineVoxelCount = depth.StaticBackgroundVoxelCount;
            _latestSnapshot.DepthRawHitCount = depth.RawHitCount;
            _latestSnapshot.DepthForegroundPointCount = depth.ForegroundPointCount;
            _latestSnapshot.DepthLeftHits = depth.LeftLeg.PointCount;
            _latestSnapshot.DepthRightHits = depth.RightLeg.PointCount;
            _latestSnapshot.DepthStatusMessage = depth.StatusMessage ?? string.Empty;
        }

        public void RecordError(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            string stamped = $"[{Time.realtimeSinceStartup:F1}s] {message}";
            if (_recentErrors.Count >= MaxRecentErrors)
            {
                _recentErrors.Dequeue();
            }
            _recentErrors.Enqueue(stamped);
            _latestSnapshot.RecentErrors = _recentErrors.ToArray();
        }

        public DiagnosticsSnapshot GetSnapshot()
        {
            return _latestSnapshot;
        }
    }
}
