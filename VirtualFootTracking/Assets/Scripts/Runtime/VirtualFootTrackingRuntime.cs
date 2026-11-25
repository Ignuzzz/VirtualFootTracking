using System.Diagnostics;
using UnityEngine;
using VirtualFootTracking.Biomechanics;
using VirtualFootTracking.Calibration;
using VirtualFootTracking.Communication;
using VirtualFootTracking.Core;
using VirtualFootTracking.Diagnostics;
using VirtualFootTracking.Prediction;
using VirtualFootTracking.SpatialMapping;
using VirtualFootTracking.Tracking;
using VirtualFootTracking.Visualization;

namespace VirtualFootTracking.Runtime
{
    /// <summary>
    /// Top-level orchestrator connecting Meta Body Tracking, MRUK Spatial Mapping,
    /// Calibration, Foot Tracking Solver, Motion Prediction, Diagnostics, and VR Visualization.
    /// </summary>
    public class VirtualFootTrackingRuntime : MonoBehaviour
    {
        [Header("Scene Rig")]
        [SerializeField] private OVRCameraRig _cameraRig;

        [Header("Modular Providers")]
        [SerializeField] private MetaOvrBodyTrackingProvider _bodyProvider;
        [SerializeField] private MrukSpatialMappingProvider _spatialProvider;
        [SerializeField] private BasicCalibrationProvider _calibrationProvider;
        [SerializeField] private DiagnosticPassthroughFootSolver _footSolver;
        [SerializeField] private NullMotionPredictor _motionPredictor;
        [SerializeField] private DiagnosticLogTrackerOutput _trackerOutput;
        [SerializeField] private RuntimeDiagnosticsProvider _diagnosticsProvider;

        [Header("Visualization")]
        [SerializeField] private SkeletonAndTrackerVisualizer _visualizer;
        [SerializeField] private DiagnosticVrHud _hud;

        private IBodyTrackingProvider _body;
        private ISpatialMappingProvider _spatial;
        private ICalibrationProvider _calibration;
        private IFootTrackingSolver _solver;
        private IMotionPredictor _predictor;
        private ITrackerOutput _output;
        private IDiagnosticsProvider _diagnostics;

        private readonly Stopwatch _solveStopwatch = new Stopwatch();
        private bool _initialCalibrationAttempted;

        private void Awake()
        {
            ResolveComponents();
        }

        private void Start()
        {
            _body.Initialize();
            _spatial.Initialize();
            _calibration.Initialize();
            _solver.Initialize(_calibration.CurrentProfile);
            _predictor.ResetState();
            _output.Initialize();

            Transform head = GetHeadTransform();
            if (_visualizer != null)
            {
                _visualizer.Initialize();
            }
            if (_hud != null)
            {
                _hud.Initialize(head);
            }
        }

        private void Update()
        {
            HandleUserInput();

            BodySkeletonSnapshot skeleton;
            bool hasBody = _body.TryGetLatestSnapshot(out skeleton);

            FloorPlaneData floor;
            _spatial.TryGetFloorData(out floor);

            if (!_initialCalibrationAttempted && !_calibration.CurrentProfile.IsCalibrated && floor.IsValid)
            {
                Transform head = GetHeadTransform();
                if ((hasBody && skeleton.HeadWorldPosition.y > floor.WorldHeightY + 0.8f) ||
                    (head != null && head.position.y > floor.WorldHeightY + 0.8f))
                {
                    if (_calibration.BeginAutoCalibration(skeleton, floor))
                    {
                        _initialCalibrationAttempted = true;
                        _solver.UpdateCalibration(_calibration.CurrentProfile);
                        _body.TryApplyHeightCalibration(_calibration.CurrentProfile.UserHeightMeters);
                    }
                }
            }

            _solveStopwatch.Restart();
            double now = Time.realtimeSinceStartupAsDouble;
            VirtualTrackerFrame frame;
            _solver.Solve(skeleton, floor, Time.deltaTime, now, out frame);

            // Apply motion predictor per tracker (pass-through in Phase 1)
            frame.LeftFoot = _predictor.ProcessSample(frame.LeftFoot, Time.deltaTime);
            frame.RightFoot = _predictor.ProcessSample(frame.RightFoot, Time.deltaTime);
            frame.LeftKnee = _predictor.ProcessSample(frame.LeftKnee, Time.deltaTime);
            frame.RightKnee = _predictor.ProcessSample(frame.RightKnee, Time.deltaTime);
            _solveStopwatch.Stop();

            double latencyMs = _solveStopwatch.Elapsed.TotalMilliseconds;

            _output.Publish(frame);
            _diagnostics.RecordFrame(
                skeleton,
                floor,
                frame,
                latencyMs,
                _body.IsPermissionGranted,
                _spatial.IsPermissionGranted);

            Transform headTransform = GetHeadTransform();
            if (_visualizer != null)
            {
                _visualizer.UpdateVisuals(skeleton, floor, frame, headTransform);
            }

            if (_hud != null)
            {
                bool clampOn = _footSolver != null && _footSolver.EnableFloorPenetrationClamp;
                bool mirrorOn = _visualizer != null && _visualizer.IsMirrorViewEnabled;
                DiagnosticsSnapshot diag = _diagnostics.GetSnapshot();
                _hud.UpdateHud(diag, _calibration.CurrentProfile, mirrorOn, clampOn, headTransform);
            }
        }

        private void HandleUserInput()
        {
            Transform head = GetHeadTransform();

            bool calibratePressed = OVRInput.GetDown(OVRInput.Button.One) || Input.GetKeyDown(KeyCode.C);
            if (calibratePressed)
            {
                _body.TryGetLatestSnapshot(out BodySkeletonSnapshot skel);
                _spatial.TryGetFloorData(out FloorPlaneData flr);
                if (_calibration.BeginAutoCalibration(skel, flr))
                {
                    _solver.UpdateCalibration(_calibration.CurrentProfile);
                    _body.TryApplyHeightCalibration(_calibration.CurrentProfile.UserHeightMeters);
                }
            }

            bool mirrorToggle = OVRInput.GetDown(OVRInput.Button.Two) || Input.GetKeyDown(KeyCode.M);
            if (mirrorToggle && _visualizer != null)
            {
                _visualizer.IsMirrorViewEnabled = !_visualizer.IsMirrorViewEnabled;
            }

            bool reloadScene = OVRInput.GetDown(OVRInput.Button.Three) || Input.GetKeyDown(KeyCode.R);
            if (reloadScene)
            {
                _spatial.RequestSceneReload();
            }

            bool toggleClamp = OVRInput.GetDown(OVRInput.Button.Four) || Input.GetKeyDown(KeyCode.F);
            if (toggleClamp && _footSolver != null)
            {
                _footSolver.EnableFloorPenetrationClamp = !_footSolver.EnableFloorPenetrationClamp;
            }

            bool recenter = OVRInput.GetDown(OVRInput.Button.SecondaryThumbstick) || Input.GetKeyDown(KeyCode.Space);
            if (recenter)
            {
                if (_hud != null)
                {
                    _hud.Recenter(head);
                }
                if (_visualizer != null)
                {
                    _visualizer.RecenterMirrorOffset(head);
                }
            }
        }

        private Transform GetHeadTransform()
        {
            if (_cameraRig != null && _cameraRig.centerEyeAnchor != null)
                return _cameraRig.centerEyeAnchor;

            return Camera.main != null ? Camera.main.transform : null;
        }

        private void ResolveComponents()
        {
            if (_cameraRig == null) _cameraRig = FindAnyObjectByType<OVRCameraRig>();
            if (_bodyProvider == null) _bodyProvider = GetComponent<MetaOvrBodyTrackingProvider>() ?? gameObject.AddComponent<MetaOvrBodyTrackingProvider>();
            if (_spatialProvider == null) _spatialProvider = GetComponent<MrukSpatialMappingProvider>() ?? gameObject.AddComponent<MrukSpatialMappingProvider>();
            if (_calibrationProvider == null) _calibrationProvider = GetComponent<BasicCalibrationProvider>() ?? gameObject.AddComponent<BasicCalibrationProvider>();
            if (_footSolver == null) _footSolver = GetComponent<DiagnosticPassthroughFootSolver>() ?? gameObject.AddComponent<DiagnosticPassthroughFootSolver>();
            if (_motionPredictor == null) _motionPredictor = GetComponent<NullMotionPredictor>() ?? gameObject.AddComponent<NullMotionPredictor>();
            if (_trackerOutput == null) _trackerOutput = GetComponent<DiagnosticLogTrackerOutput>() ?? gameObject.AddComponent<DiagnosticLogTrackerOutput>();
            if (_diagnosticsProvider == null) _diagnosticsProvider = GetComponent<RuntimeDiagnosticsProvider>() ?? gameObject.AddComponent<RuntimeDiagnosticsProvider>();
            if (_visualizer == null) _visualizer = GetComponent<SkeletonAndTrackerVisualizer>() ?? gameObject.AddComponent<SkeletonAndTrackerVisualizer>();
            if (_hud == null) _hud = GetComponent<DiagnosticVrHud>() ?? gameObject.AddComponent<DiagnosticVrHud>();

            _body = _bodyProvider;
            _spatial = _spatialProvider;
            _calibration = _calibrationProvider;
            _solver = _footSolver;
            _predictor = _motionPredictor;
            _output = _trackerOutput;
            _diagnostics = _diagnosticsProvider;
        }
    }
}
