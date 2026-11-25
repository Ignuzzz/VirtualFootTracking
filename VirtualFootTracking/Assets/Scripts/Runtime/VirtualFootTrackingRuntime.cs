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
    /// Top-level Phase 2 orchestrator connecting Meta Body Tracking, MRUK Spatial Mapping,
    /// Guided & Manual Calibration, Proprietary Foot Tracking Solver (with Raw SDK A/B mode),
    /// Adaptive Kalman Motion Predictor, Diagnostics, 4-Screen VR UI, and Ray Pointer.
    /// </summary>
    public class VirtualFootTrackingRuntime : MonoBehaviour
    {
        [Header("Scene Rig")]
        [SerializeField] private OVRCameraRig _cameraRig;

        [Header("Modular Providers")]
        [SerializeField] private MetaOvrBodyTrackingProvider _bodyProvider;
        [SerializeField] private MrukSpatialMappingProvider _spatialProvider;
        [SerializeField] private EnvironmentDepthLegDetector _depthDetector;
        [SerializeField] private GuidedCalibrationProvider _calibrationProvider;
        [SerializeField] private ProprietaryFootTrackingSolver _proprietarySolver;
        [SerializeField] private DiagnosticPassthroughFootSolver _rawDiagnosticSolver;
        [SerializeField] private AdaptiveKalmanMotionPredictor _kalmanPredictor;
        [SerializeField] private NullMotionPredictor _nullPredictor;
        [SerializeField] private DiagnosticLogTrackerOutput _trackerOutput;
        [SerializeField] private RuntimeDiagnosticsProvider _diagnosticsProvider;

        [Header("Visualization & Interaction")]
        [SerializeField] private SkeletonAndTrackerVisualizer _visualizer;
        [SerializeField] private VirtualFootTrackingVrUi _vrUi;
        [SerializeField] private VrControllerLaserPointer _laserPointer;

        [Header("Mode")]
        [SerializeField] private TrackingEngineMode _engineMode = TrackingEngineMode.ProprietaryEngine;

        private IBodyTrackingProvider _body;
        private ISpatialMappingProvider _spatial;
        private ICalibrationProvider _calibration;
        private ITrackerOutput _output;
        private IDiagnosticsProvider _diagnostics;

        private readonly Stopwatch _solveStopwatch = new Stopwatch();
        private bool _initialCalibrationAttempted;

        public TrackingEngineMode EngineMode
        {
            get => _engineMode;
            set
            {
                if (_engineMode == value)
                    return;
                _engineMode = value;
                _kalmanPredictor?.ResetState();
            }
        }

        private void Awake()
        {
            ResolveComponents();
        }

        private void Start()
        {
            _body.Initialize();
            _spatial.Initialize();
            _calibration.Initialize();

            Meta.XR.MRUtilityKit.MRUK mruk = Meta.XR.MRUtilityKit.MRUK.Instance != null
                ? Meta.XR.MRUtilityKit.MRUK.Instance
                : FindAnyObjectByType<Meta.XR.MRUtilityKit.MRUK>();
            _depthDetector.Initialize(_cameraRig, mruk);

            _proprietarySolver.SetSpatialMappingProvider(_spatial);
            _proprietarySolver.Initialize(_calibration.CurrentProfile);
            _rawDiagnosticSolver.Initialize(_calibration.CurrentProfile);

            _kalmanPredictor.ResetState();
            _nullPredictor.ResetState();
            _output.Initialize();

            if (_calibrationProvider != null)
            {
                _calibrationProvider.OnCalibrationUpdated += HandleCalibrationUpdated;
            }

            Transform head = GetHeadTransform();
            if (_visualizer != null)
            {
                _visualizer.Initialize();
            }

            if (_vrUi != null)
            {
                _vrUi.Initialize(
                    head,
                    _calibrationProvider,
                    _proprietarySolver,
                    _kalmanPredictor,
                    _visualizer,
                    _depthDetector,
                    () => _engineMode,
                    mode => EngineMode = mode,
                    () => _spatial.RequestSceneReload(),
                    () => _body.TryApplyHeightCalibration(_calibration.CurrentProfile.UserHeightMeters),
                    () => RecenterAll(head));
            }

            if (_laserPointer != null)
            {
                _laserPointer.Initialize(_cameraRig);
                if (_vrUi != null && _vrUi.UiCanvas != null)
                {
                    _laserPointer.RegisterCanvas(_vrUi.UiCanvas);
                }
            }
        }

        private void OnDestroy()
        {
            if (_calibrationProvider != null)
            {
                _calibrationProvider.OnCalibrationUpdated -= HandleCalibrationUpdated;
            }
        }

        private void Update()
        {
            HandleUserInput();

            BodySkeletonSnapshot skeleton;
            bool hasBody = _body.TryGetLatestSnapshot(out skeleton);

            FloorPlaneData floor;
            _spatial.TryGetFloorData(out floor);

            // Advance guided standing capture if active
            if (_calibrationProvider != null && _calibrationProvider.IsCalibrating)
            {
                if (_calibrationProvider.TickCalibration(skeleton, floor, Time.deltaTime))
                {
                    _body.TryApplyHeightCalibration(_calibrationProvider.CurrentProfile.UserHeightMeters);
                }
            }
            else if (!_initialCalibrationAttempted && !_calibration.CurrentProfile.IsCalibrated && floor.IsValid)
            {
                Transform head = GetHeadTransform();
                if ((hasBody && skeleton.HeadWorldPosition.y > floor.WorldHeightY + 0.8f) ||
                    (head != null && head.position.y > floor.WorldHeightY + 0.8f))
                {
                    if (_calibration.BeginAutoCalibration(skeleton, floor))
                    {
                        _initialCalibrationAttempted = true;
                        _body.TryApplyHeightCalibration(_calibration.CurrentProfile.UserHeightMeters);
                    }
                }
            }

            IFootTrackingSolver activeSolver = _engineMode == TrackingEngineMode.ProprietaryEngine
                ? (IFootTrackingSolver)_proprietarySolver
                : _rawDiagnosticSolver;

            IMotionPredictor activePredictor = _engineMode == TrackingEngineMode.ProprietaryEngine
                ? (IMotionPredictor)_kalmanPredictor
                : _nullPredictor;

            if (_kalmanPredictor != null && floor.IsValid)
            {
                _kalmanPredictor.SetFloorHeight(floor.WorldHeightY);
            }

            _solveStopwatch.Restart();
            double now = Time.realtimeSinceStartupAsDouble;

            DepthObservationFrame depthFrame = _depthDetector.ScanFrame(
                skeleton,
                floor,
                _calibration.CurrentProfile,
                Time.deltaTime);
            _proprietarySolver.SetLatestDepthObservation(depthFrame);

            VirtualTrackerFrame frame;
            activeSolver.Solve(skeleton, floor, Time.deltaTime, now, out frame);

            frame.LeftFoot = activePredictor.ProcessSample(frame.LeftFoot, Time.deltaTime);
            frame.RightFoot = activePredictor.ProcessSample(frame.RightFoot, Time.deltaTime);
            frame.LeftKnee = activePredictor.ProcessSample(frame.LeftKnee, Time.deltaTime);
            frame.RightKnee = activePredictor.ProcessSample(frame.RightKnee, Time.deltaTime);
            _solveStopwatch.Stop();

            double latencyMs = _solveStopwatch.Elapsed.TotalMilliseconds;

            _output.Publish(frame);

            if (_diagnosticsProvider != null)
            {
                bool kalmanOn = _engineMode == TrackingEngineMode.ProprietaryEngine && _kalmanPredictor != null && _kalmanPredictor.Enabled;
                _diagnosticsProvider.SetPipelineInfo(_engineMode, kalmanOn, activeSolver.SolverName);
                _diagnosticsProvider.SetDepthTelemetry(depthFrame, _proprietarySolver.EnableDepthLegFusion);
            }

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
                _visualizer.UpdateDepthVisuals(depthFrame);
            }

            if (_vrUi != null)
            {
                DiagnosticsSnapshot diag = _diagnostics.GetSnapshot();
                _vrUi.UpdateUi(diag, headTransform);
            }
        }

        private void HandleCalibrationUpdated(CalibrationProfile profile)
        {
            _proprietarySolver?.UpdateCalibration(profile);
            _rawDiagnosticSolver?.UpdateCalibration(profile);
        }

        private void HandleUserInput()
        {
            Transform head = GetHeadTransform();

            bool menuToggle = OVRInput.GetDown(OVRInput.Button.Start) || Input.GetKeyDown(KeyCode.Tab);
            if (menuToggle && _vrUi != null)
            {
                _vrUi.ToggleMainMenuOrLive();
            }

            bool calibratePressed = OVRInput.GetDown(OVRInput.Button.One) || Input.GetKeyDown(KeyCode.C);
            if (calibratePressed && _calibrationProvider != null)
            {
                _calibrationProvider.StartGuidedStandingCapture();
                _depthDetector?.StartBackgroundCapture(1.5f);
                _vrUi?.SetScreen(VrUiScreen.Calibration);
            }

            bool captureDepthBg = OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick) || Input.GetKeyDown(KeyCode.D);
            if (captureDepthBg)
            {
                _depthDetector?.StartBackgroundCapture(1.5f);
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

            bool toggleEngineAb = OVRInput.GetDown(OVRInput.Button.Four) || Input.GetKeyDown(KeyCode.F);
            if (toggleEngineAb)
            {
                EngineMode = _engineMode == TrackingEngineMode.ProprietaryEngine
                    ? TrackingEngineMode.RawSdkDiagnostic
                    : TrackingEngineMode.ProprietaryEngine;
            }

            bool recenter = OVRInput.GetDown(OVRInput.Button.SecondaryThumbstick) || Input.GetKeyDown(KeyCode.Space);
            if (recenter)
            {
                RecenterAll(head);
            }
        }

        private void RecenterAll(Transform head)
        {
            if (_vrUi != null)
                _vrUi.Recenter(head);
            if (_visualizer != null)
                _visualizer.RecenterMirrorOffset(head);
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
            if (_depthDetector == null) _depthDetector = GetComponent<EnvironmentDepthLegDetector>() ?? gameObject.AddComponent<EnvironmentDepthLegDetector>();
            if (_calibrationProvider == null) _calibrationProvider = GetComponent<GuidedCalibrationProvider>() ?? gameObject.AddComponent<GuidedCalibrationProvider>();
            if (_proprietarySolver == null) _proprietarySolver = GetComponent<ProprietaryFootTrackingSolver>() ?? gameObject.AddComponent<ProprietaryFootTrackingSolver>();
            if (_rawDiagnosticSolver == null) _rawDiagnosticSolver = GetComponent<DiagnosticPassthroughFootSolver>() ?? gameObject.AddComponent<DiagnosticPassthroughFootSolver>();
            if (_kalmanPredictor == null) _kalmanPredictor = GetComponent<AdaptiveKalmanMotionPredictor>() ?? gameObject.AddComponent<AdaptiveKalmanMotionPredictor>();
            if (_nullPredictor == null) _nullPredictor = GetComponent<NullMotionPredictor>() ?? gameObject.AddComponent<NullMotionPredictor>();
            if (_trackerOutput == null) _trackerOutput = GetComponent<DiagnosticLogTrackerOutput>() ?? gameObject.AddComponent<DiagnosticLogTrackerOutput>();
            if (_diagnosticsProvider == null) _diagnosticsProvider = GetComponent<RuntimeDiagnosticsProvider>() ?? gameObject.AddComponent<RuntimeDiagnosticsProvider>();
            if (_visualizer == null) _visualizer = GetComponent<SkeletonAndTrackerVisualizer>() ?? gameObject.AddComponent<SkeletonAndTrackerVisualizer>();
            if (_vrUi == null) _vrUi = GetComponent<VirtualFootTrackingVrUi>() ?? gameObject.AddComponent<VirtualFootTrackingVrUi>();
            if (_laserPointer == null) _laserPointer = GetComponent<VrControllerLaserPointer>() ?? gameObject.AddComponent<VrControllerLaserPointer>();

            _body = _bodyProvider;
            _spatial = _spatialProvider;
            _calibration = _calibrationProvider;
            _output = _trackerOutput;
            _diagnostics = _diagnosticsProvider;
        }
    }
}
