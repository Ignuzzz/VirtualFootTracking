using System;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using VirtualFootTracking.Biomechanics;
using VirtualFootTracking.Calibration;
using VirtualFootTracking.Core;
using VirtualFootTracking.Prediction;
using VirtualFootTracking.SpatialMapping;

namespace VirtualFootTracking.Visualization
{
    public enum VrUiScreen
    {
        MainMenu = 0,
        Calibration = 1,
        LiveTracking = 2,
        Diagnostics = 3,
        Settings = 4
    }

    /// <summary>
    /// Complete 5-panel Quest 3S World-Space VR UI (Main Menu, Calibration, Live Tracking,
    /// Settings, and Diagnostics) built programmatically with zero external font/sprite assets.
    /// </summary>
    public class VirtualFootTrackingVrUi : MonoBehaviour
    {
        [Header("Placement")]
        [SerializeField] private float _distanceMeters = 1.45f;
        [SerializeField] private float _panelVerticalOffsetMeters = 0.08f;
        [SerializeField] private float _liveHudVerticalOffsetMeters = 0.42f;
        [SerializeField] private float _recenterAngleDegrees = 42f;

        private Font _font;
        private GameObject _canvasObj;
        private Canvas _canvas;
        private RectTransform _canvasRect;
        private Image _bgImage;

        private GameObject _mainMenuPanel;
        private GameObject _calibrationPanel;
        private GameObject _livePanel;
        private GameObject _diagnosticsPanel;
        private GameObject _settingsPanel;

        // Dynamic UI labels
        private Text _mainBodyCardText;
        private Text _mainSceneCardText;
        private Text _mainTrackersCardText;

        private Text _calibInstructionText;
        private Image _calibProgressFill;
        private Text _calibValuesText;

        private Text _liveEngineBtnText;
        private Text _liveMirrorBtnText;
        private Text _liveLockBtnText;
        private Text _liveKalmanBtnText;
        private Text _liveDepthBtnText;
        private Text _liveCloudBtnText;
        private Text _liveBgBtnText;
        private Text _liveTrackersSummaryText;

        private Text _diagTelemetryText;

        private Text _settingsTogglesText;
        private Text _settingsSelectedTrackerText;
        private Text _settingsOffsetValuesText;

        private readonly StringBuilder _sb = new StringBuilder(1536);
        private Vector3 _anchoredForward = Vector3.forward;
        private bool _hasAnchoredPose;
        private VrUiScreen _currentScreen = VrUiScreen.MainMenu;
        private VirtualTrackerId _selectedOffsetTracker = VirtualTrackerId.LeftFoot;

        // Bound runtime dependencies
        private GuidedCalibrationProvider _calibration;
        private ProprietaryFootTrackingSolver _proprietarySolver;
        private AdaptiveKalmanMotionPredictor _kalman;
        private SkeletonAndTrackerVisualizer _visualizer;
        private EnvironmentDepthLegDetector _depthDetector;
        private Func<TrackingEngineMode> _getEngineMode;
        private Action<TrackingEngineMode> _setEngineMode;
        private Action _onReloadMruk;
        private Action _onApplyHeightCalibration;
        private Action _onRecenterAll;

        public VrUiScreen CurrentScreen => _currentScreen;
        public Canvas UiCanvas => _canvas;

        public void Initialize(
            Transform headTransform,
            GuidedCalibrationProvider calibration,
            ProprietaryFootTrackingSolver proprietarySolver,
            AdaptiveKalmanMotionPredictor kalman,
            SkeletonAndTrackerVisualizer visualizer,
            EnvironmentDepthLegDetector depthDetector,
            Func<TrackingEngineMode> getEngineMode,
            Action<TrackingEngineMode> setEngineMode,
            Action onReloadMruk,
            Action onApplyHeightCalibration,
            Action onRecenterAll)
        {
            _calibration = calibration;
            _proprietarySolver = proprietarySolver;
            _kalman = kalman;
            _visualizer = visualizer;
            _depthDetector = depthDetector;
            _getEngineMode = getEngineMode;
            _setEngineMode = setEngineMode;
            _onReloadMruk = onReloadMruk;
            _onApplyHeightCalibration = onApplyHeightCalibration;
            _onRecenterAll = onRecenterAll;

            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

            BuildCanvasShell();
            BuildTopNavBar();
            BuildMainMenuPanel();
            BuildCalibrationPanel();
            BuildLiveTrackingPanel();
            BuildDiagnosticsPanel();
            BuildSettingsPanel();

            SetScreen(VrUiScreen.MainMenu);
            if (headTransform != null)
            {
                Recenter(headTransform);
            }
        }

        public void SetScreen(VrUiScreen screen)
        {
            _currentScreen = screen;

            _mainMenuPanel.SetActive(screen == VrUiScreen.MainMenu);
            _calibrationPanel.SetActive(screen == VrUiScreen.Calibration);
            _livePanel.SetActive(screen == VrUiScreen.LiveTracking);
            _diagnosticsPanel.SetActive(screen == VrUiScreen.Diagnostics);
            _settingsPanel.SetActive(screen == VrUiScreen.Settings);

            if (screen == VrUiScreen.LiveTracking)
            {
                _canvasRect.sizeDelta = new Vector2(1020f, 320f);
                _bgImage.color = new Color(0.03f, 0.05f, 0.09f, 0.78f);
            }
            else
            {
                _canvasRect.sizeDelta = new Vector2(1020f, 760f);
                _bgImage.color = new Color(0.03f, 0.05f, 0.09f, 0.90f);
            }
        }

        public void ToggleMainMenuOrLive()
        {
            SetScreen(_currentScreen == VrUiScreen.LiveTracking ? VrUiScreen.MainMenu : VrUiScreen.LiveTracking);
        }

        public void Recenter(Transform headTransform)
        {
            if (headTransform == null || _canvasObj == null)
                return;

            Vector3 fwd = Vector3.ProjectOnPlane(headTransform.forward, Vector3.up);
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            _anchoredForward = fwd.normalized;
            _hasAnchoredPose = true;
            ApplyPlacement(headTransform);
        }

        public void UpdateUi(in DiagnosticsSnapshot diag, Transform headTransform)
        {
            if (_canvasObj == null)
                return;

            if (headTransform != null)
            {
                if (!_hasAnchoredPose)
                {
                    Recenter(headTransform);
                }
                else
                {
                    Vector3 flat = Vector3.ProjectOnPlane(headTransform.forward, Vector3.up);
                    if (flat.sqrMagnitude > 0.001f && Vector3.Angle(_anchoredForward, flat.normalized) > _recenterAngleDegrees)
                    {
                        _anchoredForward = Vector3.Slerp(_anchoredForward, flat.normalized, Time.deltaTime * 2.5f).normalized;
                    }
                    ApplyPlacement(headTransform);
                }
            }

            switch (_currentScreen)
            {
                case VrUiScreen.MainMenu:
                    RefreshMainMenu(diag);
                    break;
                case VrUiScreen.Calibration:
                    RefreshCalibration();
                    break;
                case VrUiScreen.LiveTracking:
                    RefreshLiveTracking(diag);
                    break;
                case VrUiScreen.Diagnostics:
                    RefreshDiagnostics(diag);
                    break;
                case VrUiScreen.Settings:
                    RefreshSettings();
                    break;
            }
        }

        private void BuildCanvasShell()
        {
            _canvasObj = new GameObject("VFT_InteractiveVrCanvas");
            _canvasObj.transform.SetParent(transform, false);
            _canvas = _canvasObj.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;

            _canvasRect = _canvasObj.GetComponent<RectTransform>();
            _canvasRect.sizeDelta = new Vector2(1020f, 760f);
            _canvasObj.transform.localScale = Vector3.one * 0.0010f;

            _bgImage = _canvasObj.AddComponent<Image>();
            _bgImage.color = new Color(0.03f, 0.05f, 0.09f, 0.90f);
        }

        private void BuildTopNavBar()
        {
            RectTransform bar = CreatePanel(_canvasRect, "TopNavBar", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(16f, -66f), new Vector2(-16f, -12f), new Color(0.06f, 0.11f, 0.18f, 0.95f));
            CreateText(bar, "Title", "<b><color=#5CE1E6>VIRTUAL FOOT TRACKING v0.3.1 DEPTH</color></b>", 19, TextAnchor.MiddleLeft, new Vector2(0f, 0f), new Vector2(0.36f, 1f), new Vector2(14f, 0f), Vector2.zero);

            CreateButton(bar, "NavMenu", "MENU", new Vector2(0.37f, 0.12f), new Vector2(0.47f, 0.88f), new Color(0.14f, 0.24f, 0.36f), () => SetScreen(VrUiScreen.MainMenu), 15);
            CreateButton(bar, "NavLive", "LIVE", new Vector2(0.48f, 0.12f), new Vector2(0.58f, 0.88f), new Color(0.10f, 0.42f, 0.32f), () => SetScreen(VrUiScreen.LiveTracking), 15);
            CreateButton(bar, "NavCalib", "CALIB", new Vector2(0.59f, 0.12f), new Vector2(0.69f, 0.88f), new Color(0.14f, 0.24f, 0.36f), () => SetScreen(VrUiScreen.Calibration), 15);
            CreateButton(bar, "NavCfg", "CONFIG", new Vector2(0.70f, 0.12f), new Vector2(0.81f, 0.88f), new Color(0.14f, 0.24f, 0.36f), () => SetScreen(VrUiScreen.Settings), 15);
            CreateButton(bar, "NavDiag", "DIAG", new Vector2(0.82f, 0.12f), new Vector2(0.92f, 0.88f), new Color(0.14f, 0.24f, 0.36f), () => SetScreen(VrUiScreen.Diagnostics), 15);
            CreateButton(bar, "NavRecenter", "CTR", new Vector2(0.93f, 0.12f), new Vector2(0.99f, 0.88f), new Color(0.28f, 0.24f, 0.12f), () => _onRecenterAll?.Invoke(), 14);
        }

        private void BuildMainMenuPanel()
        {
            RectTransform p = CreateBodyArea("MainMenuPanel");
            _mainMenuPanel = p.gameObject;

            RectTransform card1 = CreatePanel(p, "BodyCard", new Vector2(0.02f, 0.66f), new Vector2(0.33f, 0.96f), Vector2.zero, Vector2.zero, new Color(0.07f, 0.12f, 0.19f, 0.95f));
            _mainBodyCardText = CreateText(card1, "BodyCardTxt", "", 17, TextAnchor.UpperLeft, Vector2.zero, Vector2.one, new Vector2(14f, 12f), new Vector2(-14f, -12f));

            RectTransform card2 = CreatePanel(p, "SceneCard", new Vector2(0.345f, 0.66f), new Vector2(0.655f, 0.96f), Vector2.zero, Vector2.zero, new Color(0.07f, 0.12f, 0.19f, 0.95f));
            _mainSceneCardText = CreateText(card2, "SceneCardTxt", "", 16, TextAnchor.UpperLeft, Vector2.zero, Vector2.one, new Vector2(14f, 12f), new Vector2(-14f, -12f));

            RectTransform card3 = CreatePanel(p, "TrackersCard", new Vector2(0.67f, 0.66f), new Vector2(0.98f, 0.96f), Vector2.zero, Vector2.zero, new Color(0.07f, 0.12f, 0.19f, 0.95f));
            _mainTrackersCardText = CreateText(card3, "TrackersCardTxt", "", 17, TextAnchor.UpperLeft, Vector2.zero, Vector2.one, new Vector2(14f, 12f), new Vector2(-14f, -12f));

            CreateButton(p, "BtnStartLive", "INICIAR LIVE TRACKING\n<size=15>(Depth 3D + Motor IK + Kalman)</size>", new Vector2(0.04f, 0.36f), new Vector2(0.49f, 0.60f), new Color(0.09f, 0.48f, 0.34f), () => SetScreen(VrUiScreen.LiveTracking), 22);
            CreateButton(p, "BtnCalibration", "CALIBRACAO GUIADA & MANUAL\n<size=15>(Altura, Pernas + Fundo Depth)</size>", new Vector2(0.51f, 0.36f), new Vector2(0.96f, 0.60f), new Color(0.14f, 0.34f, 0.56f), () => SetScreen(VrUiScreen.Calibration), 22);
            CreateButton(p, "BtnSettings", "CONFIGURACOES & OFFSETS\n<size=15>(Depth 3D, Foot Lock, Offsets)</size>", new Vector2(0.04f, 0.08f), new Vector2(0.49f, 0.32f), new Color(0.20f, 0.27f, 0.38f), () => SetScreen(VrUiScreen.Settings), 22);
            CreateButton(p, "BtnDiagnostics", "DIAGNOSTICO COMPLETO\n<size=15>(FPS, Depth 3D, Coordenadas, Erros)</size>", new Vector2(0.51f, 0.08f), new Vector2(0.96f, 0.32f), new Color(0.32f, 0.24f, 0.14f), () => SetScreen(VrUiScreen.Diagnostics), 22);

            CreateText(p, "MenuHint", "<color=#9FB6C8>Gatilho: Clicar  |  [Menu Esq] Menu/Live  |  [Anal. Esq] Capturar Fundo Depth  |  [B] Espelho  |  [Y] Motor A/B</color>", 13, TextAnchor.MiddleCenter, new Vector2(0.02f, 0.0f), new Vector2(0.98f, 0.07f), Vector2.zero, Vector2.zero);
        }

        private void BuildCalibrationPanel()
        {
            RectTransform p = CreateBodyArea("CalibrationPanel");
            _calibrationPanel = p.gameObject;

            _calibInstructionText = CreateText(p, "CalibInst", "", 18, TextAnchor.UpperLeft, new Vector2(0.02f, 0.80f), new Vector2(0.64f, 0.98f), new Vector2(8f, 4f), new Vector2(-8f, -4f));

            RectTransform barBg = CreatePanel(p, "ProgBg", new Vector2(0.02f, 0.72f), new Vector2(0.64f, 0.79f), Vector2.zero, Vector2.zero, new Color(0.10f, 0.14f, 0.20f));
            GameObject fillGo = new GameObject("ProgFill");
            fillGo.transform.SetParent(barBg, false);
            _calibProgressFill = fillGo.AddComponent<Image>();
            _calibProgressFill.color = new Color(0.18f, 0.85f, 0.55f);
            RectTransform fillRt = _calibProgressFill.rectTransform;
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = new Vector2(0f, 1f);
            fillRt.offsetMin = Vector2.zero;
            fillRt.offsetMax = Vector2.zero;

            CreateButton(p, "BtnAutoCalib", "AUTO-CALIBRAR (2.5s)\n<size=13>Postura + Fundo Depth</size>", new Vector2(0.66f, 0.81f), new Vector2(0.98f, 0.98f), new Color(0.09f, 0.48f, 0.34f), () =>
            {
                _calibration?.StartGuidedStandingCapture();
                _depthDetector?.StartBackgroundCapture(1.5f);
            }, 18);

            CreateButton(p, "BtnCalibCaptureDepth", "CAPTURAR FUNDO DEPTH (1.5s)", new Vector2(0.66f, 0.70f), new Vector2(0.98f, 0.80f), new Color(0.45f, 0.36f, 0.10f), () =>
            {
                _depthDetector?.StartBackgroundCapture(1.5f);
            }, 15);

            _calibValuesText = CreateText(p, "CalibVals", "", 18, TextAnchor.UpperLeft, new Vector2(0.02f, 0.14f), new Vector2(0.55f, 0.70f), new Vector2(10f, 8f), new Vector2(-8f, -8f));

            // Stepper controls on right column
            AddStepperRow(p, "Altura:", 0.62f, () => _calibration?.AdjustUserHeight(-0.01f), () => _calibration?.AdjustUserHeight(0.01f));
            AddStepperRow(p, "Coxa:", 0.54f, () => _calibration?.AdjustThighLength(-0.01f), () => _calibration?.AdjustThighLength(0.01f));
            AddStepperRow(p, "Canela:", 0.46f, () => _calibration?.AdjustShinLength(-0.01f), () => _calibration?.AdjustShinLength(0.01f));
            AddStepperRow(p, "Quadril:", 0.38f, () => _calibration?.AdjustHipHeight(-0.01f), () => _calibration?.AdjustHipHeight(0.01f));
            AddStepperRow(p, "Dist. Pes:", 0.30f, () => _calibration?.AdjustFootSpacing(-0.01f), () => _calibration?.AdjustFootSpacing(0.01f));
            AddStepperRow(p, "Largura Base:", 0.22f, () => _calibration?.AdjustStanceWidth(-0.01f), () => _calibration?.AdjustStanceWidth(0.01f));
            AddStepperRow(p, "Yaw Pes:", 0.14f, () => _calibration?.AdjustInitialFootYaw(-2.0f), () => _calibration?.AdjustInitialFootYaw(2.0f));

            CreateButton(p, "BtnSaveCalib", "SALVAR & APLICAR", new Vector2(0.02f, 0.02f), new Vector2(0.36f, 0.11f), new Color(0.14f, 0.38f, 0.58f), () =>
            {
                _calibration?.SaveProfile();
                _onApplyHeightCalibration?.Invoke();
            }, 17);
            CreateButton(p, "BtnResetOffsets", "ZERAR OFFSETS", new Vector2(0.38f, 0.02f), new Vector2(0.68f, 0.11f), new Color(0.38f, 0.20f, 0.16f), () => _calibration?.ResetTrackerOffsets(), 17);
            CreateButton(p, "BtnBackLive", "IR PARA LIVE", new Vector2(0.70f, 0.02f), new Vector2(0.98f, 0.11f), new Color(0.09f, 0.48f, 0.34f), () => SetScreen(VrUiScreen.LiveTracking), 17);
        }

        private void BuildLiveTrackingPanel()
        {
            RectTransform p = CreateBodyArea("LiveTrackingPanel");
            _livePanel = p.gameObject;

            _liveEngineBtnText = CreateButton(p, "BtnEngineAB", "MOTOR: PROPRIO IK", new Vector2(0.02f, 0.75f), new Vector2(0.28f, 0.97f), new Color(0.12f, 0.46f, 0.36f), () =>
            {
                if (_getEngineMode == null || _setEngineMode == null) return;
                TrackingEngineMode next = _getEngineMode() == TrackingEngineMode.ProprietaryEngine
                    ? TrackingEngineMode.RawSdkDiagnostic
                    : TrackingEngineMode.ProprietaryEngine;
                _setEngineMode(next);
            }, 15);

            _liveMirrorBtnText = CreateButton(p, "BtnMirror", "ESPELHO: ON", new Vector2(0.29f, 0.75f), new Vector2(0.49f, 0.97f), new Color(0.14f, 0.30f, 0.46f), () =>
            {
                if (_visualizer != null) _visualizer.IsMirrorViewEnabled = !_visualizer.IsMirrorViewEnabled;
            }, 15);

            _liveLockBtnText = CreateButton(p, "BtnLock", "TRAVA CHAO: ON", new Vector2(0.50f, 0.75f), new Vector2(0.74f, 0.97f), new Color(0.14f, 0.30f, 0.46f), () =>
            {
                if (_proprietarySolver != null) _proprietarySolver.EnableFootLocking = !_proprietarySolver.EnableFootLocking;
            }, 15);

            _liveKalmanBtnText = CreateButton(p, "BtnKalman", "KALMAN: ON", new Vector2(0.75f, 0.75f), new Vector2(0.98f, 0.97f), new Color(0.14f, 0.30f, 0.46f), () =>
            {
                if (_kalman != null) _kalman.Enabled = !_kalman.Enabled;
            }, 15);

            _liveDepthBtnText = CreateButton(p, "BtnLiveDepth", "DEPTH LEG: ON", new Vector2(0.02f, 0.50f), new Vector2(0.34f, 0.72f), new Color(0.42f, 0.34f, 0.10f), () =>
            {
                if (_proprietarySolver != null) _proprietarySolver.EnableDepthLegFusion = !_proprietarySolver.EnableDepthLegFusion;
            }, 15);

            _liveCloudBtnText = CreateButton(p, "BtnLiveCloud", "NUVEM 3D: ON", new Vector2(0.35f, 0.50f), new Vector2(0.66f, 0.72f), new Color(0.16f, 0.30f, 0.44f), () =>
            {
                if (_visualizer != null) _visualizer.IsDepthPointsVisible = !_visualizer.IsDepthPointsVisible;
            }, 15);

            _liveBgBtnText = CreateButton(p, "BtnLiveBgCap", "CAPTURAR FUNDO (1.5s)", new Vector2(0.67f, 0.50f), new Vector2(0.98f, 0.72f), new Color(0.24f, 0.36f, 0.16f), () =>
            {
                _depthDetector?.StartBackgroundCapture(1.5f);
            }, 15);

            _liveTrackersSummaryText = CreateText(p, "LiveSummary", "", 15, TextAnchor.MiddleLeft, new Vector2(0.02f, 0.02f), new Vector2(0.98f, 0.48f), new Vector2(8f, 0f), new Vector2(-8f, 0f));
            _liveTrackersSummaryText.lineSpacing = 1.06f;
        }

        private void BuildDiagnosticsPanel()
        {
            RectTransform p = CreateBodyArea("DiagnosticsPanel");
            _diagnosticsPanel = p.gameObject;

            _diagTelemetryText = CreateText(p, "DiagText", "", 17, TextAnchor.UpperLeft, new Vector2(0.02f, 0.13f), new Vector2(0.98f, 0.98f), new Vector2(10f, 6f), new Vector2(-10f, -6f));

            CreateButton(p, "BtnReloadMruk", "RECARREGAR SALA MRUK", new Vector2(0.02f, 0.02f), new Vector2(0.36f, 0.11f), new Color(0.14f, 0.34f, 0.54f), () => _onReloadMruk?.Invoke(), 16);
            CreateButton(p, "BtnDiagSwap", "ALTERNAR MOTOR (A/B)", new Vector2(0.38f, 0.02f), new Vector2(0.68f, 0.11f), new Color(0.28f, 0.34f, 0.16f), () =>
            {
                if (_getEngineMode == null || _setEngineMode == null) return;
                _setEngineMode(_getEngineMode() == TrackingEngineMode.ProprietaryEngine
                    ? TrackingEngineMode.RawSdkDiagnostic
                    : TrackingEngineMode.ProprietaryEngine);
            }, 16);
            CreateButton(p, "BtnDiagMenu", "VOLTAR AO MENU", new Vector2(0.70f, 0.02f), new Vector2(0.98f, 0.11f), new Color(0.22f, 0.25f, 0.32f), () => SetScreen(VrUiScreen.MainMenu), 16);
        }

        private void BuildSettingsPanel()
        {
            RectTransform p = CreateBodyArea("SettingsPanel");
            _settingsPanel = p.gameObject;

            _settingsTogglesText = CreateText(p, "SetTogglesTxt", "", 17, TextAnchor.UpperLeft, new Vector2(0.02f, 0.45f), new Vector2(0.50f, 0.97f), new Vector2(10f, 4f), new Vector2(-8f, -4f));

            CreateButton(p, "TogFloorClamp", "Alternar Floor Clamp", new Vector2(0.52f, 0.90f), new Vector2(0.98f, 0.968f), new Color(0.14f, 0.32f, 0.48f), () =>
            {
                if (_proprietarySolver != null) _proprietarySolver.EnableFloorClamp = !_proprietarySolver.EnableFloorClamp;
            }, 15);
            CreateButton(p, "TogFootLock", "Alternar Foot Locking", new Vector2(0.52f, 0.825f), new Vector2(0.98f, 0.893f), new Color(0.14f, 0.32f, 0.48f), () =>
            {
                if (_proprietarySolver != null) _proprietarySolver.EnableFootLocking = !_proprietarySolver.EnableFootLocking;
            }, 15);
            CreateButton(p, "TogIk", "Alternar IK Anatomica", new Vector2(0.52f, 0.75f), new Vector2(0.98f, 0.818f), new Color(0.14f, 0.32f, 0.48f), () =>
            {
                if (_proprietarySolver != null) _proprietarySolver.EnableAnatomicalIk = !_proprietarySolver.EnableAnatomicalIk;
            }, 15);
            CreateButton(p, "TogKalman", "Alternar Filtro Kalman", new Vector2(0.52f, 0.675f), new Vector2(0.98f, 0.743f), new Color(0.14f, 0.32f, 0.48f), () =>
            {
                if (_kalman != null) _kalman.Enabled = !_kalman.Enabled;
            }, 15);
            CreateButton(p, "TogRawGhost", "Alternar Esqueleto SDK Bruto", new Vector2(0.52f, 0.60f), new Vector2(0.98f, 0.668f), new Color(0.14f, 0.32f, 0.48f), () =>
            {
                if (_visualizer != null) _visualizer.IsRawSkeletonVisible = !_visualizer.IsRawSkeletonVisible;
            }, 15);
            CreateButton(p, "TogDepthFusion", "Alternar Depth Leg Fusion", new Vector2(0.52f, 0.525f), new Vector2(0.98f, 0.593f), new Color(0.42f, 0.34f, 0.10f), () =>
            {
                if (_proprietarySolver != null) _proprietarySolver.EnableDepthLegFusion = !_proprietarySolver.EnableDepthLegFusion;
            }, 15);
            CreateButton(p, "TogDepthCloud", "Alternar Nuvem de Pontos 3D", new Vector2(0.52f, 0.45f), new Vector2(0.98f, 0.518f), new Color(0.18f, 0.34f, 0.42f), () =>
            {
                if (_visualizer != null) _visualizer.IsDepthPointsVisible = !_visualizer.IsDepthPointsVisible;
            }, 15);

            _settingsSelectedTrackerText = CreateText(p, "SelTrackerTxt", "", 18, TextAnchor.MiddleLeft, new Vector2(0.02f, 0.36f), new Vector2(0.45f, 0.44f), new Vector2(10f, 0f), Vector2.zero);

            CreateButton(p, "SelLF", "L-FOOT", new Vector2(0.46f, 0.37f), new Vector2(0.58f, 0.435f), new Color(0.18f, 0.28f, 0.38f), () => _selectedOffsetTracker = VirtualTrackerId.LeftFoot, 14);
            CreateButton(p, "SelRF", "R-FOOT", new Vector2(0.59f, 0.37f), new Vector2(0.71f, 0.435f), new Color(0.18f, 0.28f, 0.38f), () => _selectedOffsetTracker = VirtualTrackerId.RightFoot, 14);
            CreateButton(p, "SelLK", "L-KNEE", new Vector2(0.72f, 0.37f), new Vector2(0.84f, 0.435f), new Color(0.18f, 0.28f, 0.38f), () => _selectedOffsetTracker = VirtualTrackerId.LeftKnee, 14);
            CreateButton(p, "SelRK", "R-KNEE", new Vector2(0.85f, 0.37f), new Vector2(0.98f, 0.435f), new Color(0.18f, 0.28f, 0.38f), () => _selectedOffsetTracker = VirtualTrackerId.RightKnee, 14);

            _settingsOffsetValuesText = CreateText(p, "OffsetValsTxt", "", 18, TextAnchor.MiddleLeft, new Vector2(0.02f, 0.14f), new Vector2(0.52f, 0.34f), new Vector2(10f, 0f), Vector2.zero);

            AddStepperRow(p, "Offset X:", 0.28f, () => _calibration?.AdjustTrackerPositionOffset(_selectedOffsetTracker, new Vector3(-0.01f, 0f, 0f)), () => _calibration?.AdjustTrackerPositionOffset(_selectedOffsetTracker, new Vector3(0.01f, 0f, 0f)));
            AddStepperRow(p, "Offset Y:", 0.20f, () => _calibration?.AdjustTrackerPositionOffset(_selectedOffsetTracker, new Vector3(0f, -0.01f, 0f)), () => _calibration?.AdjustTrackerPositionOffset(_selectedOffsetTracker, new Vector3(0f, 0.01f, 0f)));
            AddStepperRow(p, "Offset Z:", 0.12f, () => _calibration?.AdjustTrackerPositionOffset(_selectedOffsetTracker, new Vector3(0f, 0f, -0.01f)), () => _calibration?.AdjustTrackerPositionOffset(_selectedOffsetTracker, new Vector3(0f, 0f, 0.01f)));

            CreateButton(p, "BtnZeroAllOffsets", "ZERAR OFFSETS", new Vector2(0.02f, 0.02f), new Vector2(0.33f, 0.10f), new Color(0.40f, 0.18f, 0.16f), () => _calibration?.ResetTrackerOffsets(), 15);
            CreateButton(p, "BtnClearDepthBg", "LIMPAR FUNDO DEPTH", new Vector2(0.35f, 0.02f), new Vector2(0.66f, 0.10f), new Color(0.40f, 0.30f, 0.12f), () => _depthDetector?.ClearBackgroundBaseline(), 15);
            CreateButton(p, "BtnSetBack", "VOLTAR AO MENU", new Vector2(0.68f, 0.02f), new Vector2(0.98f, 0.10f), new Color(0.20f, 0.24f, 0.32f), () => SetScreen(VrUiScreen.MainMenu), 15);
        }

        private void RefreshMainMenu(in DiagnosticsSnapshot diag)
        {
            string bodyState = diag.BodyTrackingActive ? "<color=#49E870>ATIVO</color>" : "<color=#FF5555>INATIVO</color>";
            string bodyPerm = diag.BodyPermissionGranted ? "OK" : "NEGADA";
            _mainBodyCardText.text =
                $"<b><color=#5CE1E6>BODY TRACKING</color></b>\n" +
                $"Status: {bodyState}\n" +
                $"Permissao: {bodyPerm}\n" +
                $"Modo: {diag.BodyFidelity}\n" +
                $"Juntas: {(diag.IsFullBody ? "FullBody (84)" : "UpperBody")}\n" +
                $"Confianca: {diag.BodyConfidence:F2}";

            string floorColor = diag.FloorSource == FloorDataSource.MrukSceneApi ? "#24D9FF" : "#FFB347";
            string depthStateStr = diag.DepthSupported
                ? (diag.DepthReady ? "<color=#FFE024>READY</color>" : "<color=#FFB347>WAIT</color>")
                : "<color=#FF5555>OFF</color>";
            _mainSceneCardText.text =
                $"<b><color=#5CE1E6>SCENE &amp; DEPTH 3D</color></b>\n" +
                $"USE_SCENE: {(diag.ScenePermissionGranted ? "OK" : "NEGADA")}\n" +
                $"Chao: <color={floorColor}>{diag.FloorSource}</color> ({diag.FloorWorldY:F2}m)\n" +
                $"Depth 3D: {depthStateStr} | Pts={diag.DepthForegroundPointCount}\n" +
                $"Fundo Voxels: {diag.DepthBaselineVoxelCount}\n" +
                $"FPS / Hz: {diag.Fps:F0} / {diag.TrackingUpdateHz:F0}";

            int validCount = 0;
            if (diag.CurrentFrame.LeftFoot.Validity != TrackerValidityState.Invalid) validCount++;
            if (diag.CurrentFrame.RightFoot.Validity != TrackerValidityState.Invalid) validCount++;
            if (diag.CurrentFrame.LeftKnee.Validity != TrackerValidityState.Invalid) validCount++;
            if (diag.CurrentFrame.RightKnee.Validity != TrackerValidityState.Invalid) validCount++;

            _mainTrackersCardText.text =
                $"<b><color=#5CE1E6>TRACKERS VIRTUAIS</color></b>\n" +
                $"Ativos: <b>{validCount}/4</b>\n" +
                $"LF: {diag.CurrentFrame.LeftFoot.GetShortSourceTag()} {(diag.CurrentFrame.LeftFoot.IsFootLocked ? "[LOCK]" : "")}\n" +
                $"RF: {diag.CurrentFrame.RightFoot.GetShortSourceTag()} {(diag.CurrentFrame.RightFoot.IsFootLocked ? "[LOCK]" : "")}\n" +
                $"LK: {diag.CurrentFrame.LeftKnee.GetShortSourceTag()} {(diag.CurrentFrame.LeftKnee.IsIkSolved ? "[IK]" : "")}\n" +
                $"RK: {diag.CurrentFrame.RightKnee.GetShortSourceTag()} {(diag.CurrentFrame.RightKnee.IsIkSolved ? "[IK]" : "")}";
        }

        private void RefreshCalibration()
        {
            if (_calibration == null) return;
            CalibrationProfile c = _calibration.CurrentProfile;
            float prog = Mathf.Clamp01(_calibration.CalibrationProgress01);

            _calibInstructionText.text =
                $"<b><color=#5CE1E6>CALIBRACAO ANTROPOMETRICA</color></b>\n" +
                $"Estado: <color=#FFE066>{_calibration.CalibrationStatusMessage}</color>\n" +
                $"Fique em pe naturalmente sobre o chao mapeado antes de clicar em AUTO-CALIBRAR.";

            Vector2 maxAnchor = _calibProgressFill.rectTransform.anchorMax;
            maxAnchor.x = prog;
            _calibProgressFill.rectTransform.anchorMax = maxAnchor;

            _calibValuesText.text =
                $"<b>Medidas Atuais ({(c.IsAutoEstimated ? "Auto" : "Manual")}):</b>\n\n" +
                $"Altura Usuario:      <b>{c.UserHeightMeters:F2} m</b>\n" +
                $"Comprimento Coxa:    <b>{c.ThighLengthMeters:F2} m</b>\n" +
                $"Comprimento Canela:  <b>{c.ShinLengthMeters:F2} m</b>\n" +
                $"Altura Quadril:      <b>{c.HipHeightMeters:F2} m</b>\n" +
                $"Altura Tornozelo:    <b>{c.AnkleToFloorMeters:F3} m</b>\n" +
                $"Distancia Pes:       <b>{c.FootSpacingMeters:F2} m</b>\n" +
                $"Largura Postura:     <b>{c.StanceWidthMeters:F2} m</b>\n" +
                $"Yaw Inicial Pes:     <b>{c.InitialFootYawOffsetDegrees:F1} deg</b>";
        }

        private void RefreshLiveTracking(in DiagnosticsSnapshot diag)
        {
            bool engineOn = _getEngineMode == null || _getEngineMode() == TrackingEngineMode.ProprietaryEngine;
            _liveEngineBtnText.text = engineOn ? "MOTOR: PROPRIO IK" : "MOTOR: RAW SDK (A/B)";
            _liveMirrorBtnText.text = (_visualizer != null && _visualizer.IsMirrorViewEnabled) ? "ESPELHO: ON" : "ESPELHO: OFF";
            _liveLockBtnText.text = (_proprietarySolver != null && _proprietarySolver.EnableFootLocking) ? "TRAVA CHAO: ON" : "TRAVA CHAO: OFF";
            _liveKalmanBtnText.text = (_kalman != null && _kalman.Enabled) ? "KALMAN: ON" : "KALMAN: OFF";
            _liveDepthBtnText.text = (_proprietarySolver != null && _proprietarySolver.EnableDepthLegFusion) ? "DEPTH LEG: ON" : "DEPTH LEG: OFF";
            _liveCloudBtnText.text = (_visualizer != null && _visualizer.IsDepthPointsVisible) ? "NUVEM 3D: ON" : "NUVEM 3D: OFF";
            _liveBgBtnText.text = diag.DepthCapturingBackground ? "CAPTURANDO FUNDO..." : $"CAPTURAR FUNDO ({diag.DepthBaselineVoxelCount})";

            string dStat = diag.DepthCapturingBackground
                ? "<color=#FFE024>CAPTURANDO</color>"
                : (diag.DepthReady ? "<color=#FFE024>READY</color>" : (diag.DepthSupported ? "<color=#FFB347>INIT</color>" : "<color=#FF5555>UNSUP</color>"));

            _sb.Clear();
            _sb.AppendLine($"FPS: <b>{diag.Fps:F0}</b> | Hz: <b>{diag.TrackingUpdateHz:F0}</b> | Lat: <b>{diag.SolverLatencyMs:F2}ms</b> | Chao: {diag.FloorSource} ({diag.FloorWorldY:F2}m)");
            _sb.AppendLine($"Depth 3D: {dStat} | FgPts=<b>{diag.DepthForegroundPointCount}</b> (L={diag.DepthLeftHits} R={diag.DepthRightHits}) | Raw={diag.DepthRawHitCount} | Fundo={diag.DepthBaselineVoxelCount} vox");
            AppendLiveRow("LF", diag.CurrentFrame.LeftFoot, "RF", diag.CurrentFrame.RightFoot);
            AppendLiveRow("LK", diag.CurrentFrame.LeftKnee, "RK", diag.CurrentFrame.RightKnee);
            _liveTrackersSummaryText.text = _sb.ToString();
        }

        private void AppendLiveRow(string l1, in VirtualTrackerSample s1, string l2, in VirtualTrackerSample s2)
        {
            _sb.AppendLine(
                $"{l1}: dY={s1.HeightAboveFloorMeters:+0.000;-0.000}m {(s1.IsFootLocked ? "[LOCK]" : (s1.IsGrounded ? "[GND] " : "[AIR] "))} [{s1.GetShortSourceTag()}]  |  " +
                $"{l2}: dY={s2.HeightAboveFloorMeters:+0.000;-0.000}m {(s2.IsFootLocked ? "[LOCK]" : (s2.IsGrounded ? "[GND] " : "[AIR] "))} [{s2.GetShortSourceTag()}]");
        }

        private void RefreshDiagnostics(in DiagnosticsSnapshot diag)
        {
            _sb.Clear();
            _sb.AppendLine($"FPS: <b>{diag.Fps:F1}</b>  |  Body Hz: <b>{diag.TrackingUpdateHz:F0} Hz</b>  |  Solver+Kalman: <b>{diag.SolverLatencyMs:F3} ms</b>  |  Motor: <b>{diag.ActiveEngineMode}</b>");
            _sb.AppendLine($"Perms -> BODY_TRACKING: {(diag.BodyPermissionGranted ? "<color=#49E870>OK</color>" : "<color=#FF5555>NEGADA</color>")} | USE_SCENE: {(diag.ScenePermissionGranted ? "<color=#49E870>OK</color>" : "<color=#FF5555>NEGADA</color>")}");
            _sb.AppendLine($"Body -> {(diag.BodyTrackingActive ? "ATIVO" : "INATIVO")} | {diag.BodyFidelity} | Calib={diag.BodyCalibration} | Conf={diag.BodyConfidence:F2} | Perdas={diag.TrackingLossEventsCount}");
            _sb.AppendLine($"Floor -> {diag.FloorSource} | Y={diag.FloorWorldY:F3}m | Ancoras={diag.FloorAnchorCount} | {diag.StatusMessage}");
            _sb.AppendLine($"Depth -> Sup={diag.DepthSupported} Ready={diag.DepthReady} Fusion={diag.DepthFusionEnabled} | Raw={diag.DepthRawHitCount} Fg={diag.DepthForegroundPointCount} (L={diag.DepthLeftHits} R={diag.DepthRightHits}) | Fundo={diag.DepthBaselineVoxelCount} vox | {diag.DepthStatusMessage}");
            _sb.AppendLine("----------------------------------------------------------------------------------------");
            _sb.AppendLine("<b>ID      POSICAO (X, Y, Z)       VEL(m/s)   CHAO dY   FLAGS           FONTE     ESTADO</b>");
            AppendDiagRow("L-FOOT", diag.CurrentFrame.LeftFoot);
            AppendDiagRow("R-FOOT", diag.CurrentFrame.RightFoot);
            AppendDiagRow("L-KNEE", diag.CurrentFrame.LeftKnee);
            AppendDiagRow("R-KNEE", diag.CurrentFrame.RightKnee);
            _sb.AppendLine("----------------------------------------------------------------------------------------");
            _sb.AppendLine("Cores: <color=#49E870>VERDE=SDK_OBS</color>  <color=#FFE024>AMARELO=DEPTH_3D</color>  <color=#24D9FF>CIANO=SDK_GEN</color>  <color=#FF991A>LARANJA=ENG_IK</color>  <color=#EB4DFF>MAGENTA=ENG_PRED</color>  <color=#FF5555>VERMELHO=LOST</color>");
            if (diag.RecentErrors != null && diag.RecentErrors.Length > 0)
            {
                _sb.AppendLine($"<color=#FF7777>Ultimo erro: {diag.RecentErrors[diag.RecentErrors.Length - 1]}</color>");
            }
            _diagTelemetryText.text = _sb.ToString();
        }

        private void AppendDiagRow(string label, in VirtualTrackerSample s)
        {
            string flags = $"{(s.IsGrounded ? "G" : "-")}{(s.IsFootLocked ? "L" : "-")}{(s.IsIkSolved ? "I" : "-")}{(s.FloorClamped ? "C" : "-")}";
            _sb.AppendLine(
                $"{label,-7} ({s.Position.x,5:F2},{s.Position.y,5:F2},{s.Position.z,5:F2})   {s.LinearVelocity.magnitude,5:F2}     {s.HeightAboveFloorMeters,+6:F3}m   {flags}            {s.GetShortSourceTag(),-8}  {s.Validity}");
        }

        private void RefreshSettings()
        {
            bool fc = _proprietarySolver != null && _proprietarySolver.EnableFloorClamp;
            bool fl = _proprietarySolver != null && _proprietarySolver.EnableFootLocking;
            bool ik = _proprietarySolver != null && _proprietarySolver.EnableAnatomicalIk;
            bool km = _kalman != null && _kalman.Enabled;
            bool raw = _visualizer != null && _visualizer.IsRawSkeletonVisible;
            bool df = _proprietarySolver != null && _proprietarySolver.EnableDepthLegFusion;
            bool dc = _visualizer != null && _visualizer.IsDepthPointsVisible;
            int vox = _depthDetector != null ? _depthDetector.StaticBackgroundVoxelCount : 0;

            _settingsTogglesText.text =
                $"<b><color=#5CE1E6>PARAMETROS DO ENGINE</color></b>\n\n" +
                $"Floor Clamp (Anti-Penetracao): <b>{(fc ? "<color=#49E870>ON</color>" : "<color=#FF5555>OFF</color>")}</b>\n" +
                $"Foot Locking (Trava Piso):     <b>{(fl ? "<color=#49E870>ON</color>" : "<color=#FF5555>OFF</color>")}</b>\n" +
                $"IK Anatomica (2-Bone):         <b>{(ik ? "<color=#49E870>ON</color>" : "<color=#FF5555>OFF</color>")}</b>\n" +
                $"Filtro Kalman Adaptativo:      <b>{(km ? "<color=#49E870>ON</color>" : "<color=#FF5555>OFF</color>")}</b>\n" +
                $"Exibir Esqueleto SDK Bruto:    <b>{(raw ? "<color=#49E870>ON</color>" : "<color=#FF5555>OFF</color>")}</b>\n" +
                $"Depth Leg Fusion (3D):         <b>{(df ? "<color=#FFE024>ON</color>" : "<color=#FF5555>OFF</color>")}</b>\n" +
                $"Nuvem Pontos Depth (Fundo={vox}): <b>{(dc ? "<color=#49E870>ON</color>" : "<color=#FF5555>OFF</color>")}</b>";

            _settingsSelectedTrackerText.text = $"<b>Tracker Offset ({_selectedOffsetTracker}):</b>";

            if (_calibration != null)
            {
                Vector3 off = _calibration.CurrentProfile.GetPositionOffset(_selectedOffsetTracker);
                _settingsOffsetValuesText.text =
                    $"Offset Atual ({_selectedOffsetTracker}):\n" +
                    $"X = <b>{off.x * 100f:+0.0;-0.0;0.0} cm</b>  |  Y = <b>{off.y * 100f:+0.0;-0.0;0.0} cm</b>  |  Z = <b>{off.z * 100f:+0.0;-0.0;0.0} cm</b>";
            }
        }

        private void ApplyPlacement(Transform headTransform)
        {
            float yOff = _currentScreen == VrUiScreen.LiveTracking ? _liveHudVerticalOffsetMeters : _panelVerticalOffsetMeters;
            Vector3 pos = headTransform.position + _anchoredForward * _distanceMeters + Vector3.up * yOff;
            _canvasObj.transform.position = pos;
            _canvasObj.transform.rotation = Quaternion.LookRotation(_anchoredForward, Vector3.up);
        }

        private RectTransform CreateBodyArea(string objName)
        {
            return CreatePanel(_canvasRect, objName, Vector2.zero, Vector2.one, new Vector2(12f, 12f), new Vector2(-12f, -74f), new Color(0f, 0f, 0f, 0f));
        }

        private void AddStepperRow(RectTransform parent, string label, float yMin, Action onMinus, Action onPlus)
        {
            float yMax = yMin + 0.068f;
            CreateText(parent, label + "_lbl", label, 15, TextAnchor.MiddleLeft, new Vector2(0.56f, yMin), new Vector2(0.75f, yMax), new Vector2(4f, 0f), Vector2.zero);
            CreateButton(parent, label + "_minus", "-", new Vector2(0.76f, yMin + 0.004f), new Vector2(0.86f, yMax - 0.004f), new Color(0.28f, 0.20f, 0.22f), onMinus, 18);
            CreateButton(parent, label + "_plus", "+", new Vector2(0.87f, yMin + 0.004f), new Vector2(0.97f, yMax - 0.004f), new Color(0.16f, 0.34f, 0.24f), onPlus, 18);
        }

        private RectTransform CreatePanel(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color bgColor)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Image img = go.AddComponent<Image>();
            img.color = bgColor;
            RectTransform rt = img.rectTransform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            return rt;
        }

        private Text CreateText(RectTransform parent, string name, string content, int fontSize, TextAnchor alignment, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Text txt = go.AddComponent<Text>();
            txt.font = _font;
            txt.fontSize = fontSize;
            txt.alignment = alignment;
            txt.color = new Color(0.93f, 0.96f, 1.0f, 1.0f);
            txt.supportRichText = true;
            txt.lineSpacing = 1.12f;
            txt.text = content;
            RectTransform rt = txt.rectTransform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            return txt;
        }

        private Text CreateButton(RectTransform parent, string name, string label, Vector2 anchorMin, Vector2 anchorMax, Color bgColor, Action onClick, int fontSize)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Image img = go.AddComponent<Image>();
            img.color = bgColor;
            Button btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => onClick?.Invoke());

            RectTransform rt = img.rectTransform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            return CreateText(rt, name + "_Label", label, fontSize, TextAnchor.MiddleCenter, Vector2.zero, Vector2.one, new Vector2(6f, 4f), new Vector2(-6f, -4f));
        }
    }
}
