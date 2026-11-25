using System.Text;
using UnityEngine;
using UnityEngine.UI;
using VirtualFootTracking.Core;

namespace VirtualFootTracking.Visualization
{
    /// <summary>
    /// Builds a self-contained World-Space VR diagnostic HUD requiring no external TMP or font assets.
    /// </summary>
    public class DiagnosticVrHud : MonoBehaviour
    {
        [Header("HUD Placement")]
        [SerializeField]
        private float _distanceFromHeadMeters = 1.45f;

        [SerializeField]
        private float _verticalOffsetMeters = 0.12f;

        [SerializeField]
        private float _recenterAngleThresholdDegrees = 40f;

        private GameObject _canvasObject;
        private Text _hudText;
        private Vector3 _anchoredForward = Vector3.forward;
        private bool _hasAnchoredPose;
        private readonly StringBuilder _sb = new StringBuilder(1024);

        public void Initialize(Transform headTransform)
        {
            _canvasObject = new GameObject("VFT_DiagnosticHudCanvas");
            _canvasObject.transform.SetParent(transform, false);

            Canvas canvas = _canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            RectTransform canvasRect = _canvasObject.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(1020f, 760f);
            _canvasObject.transform.localScale = Vector3.one * 0.00105f;

            GameObject bgObj = new GameObject("Background");
            bgObj.transform.SetParent(_canvasObject.transform, false);
            Image bgImg = bgObj.AddComponent<Image>();
            bgImg.color = new Color(0.03f, 0.05f, 0.09f, 0.86f);
            RectTransform bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;

            GameObject textObj = new GameObject("TelemetryText");
            textObj.transform.SetParent(bgObj.transform, false);
            _hudText = textObj.AddComponent<Text>();
            _hudText.font = LoadBuiltinFont();
            _hudText.fontSize = 22;
            _hudText.lineSpacing = 1.12f;
            _hudText.alignment = TextAnchor.UpperLeft;
            _hudText.color = new Color(0.92f, 0.96f, 1.0f, 1.0f);
            _hudText.supportRichText = true;

            RectTransform textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(26f, 22f);
            textRect.offsetMax = new Vector2(-26f, -22f);

            if (headTransform != null)
            {
                Recenter(headTransform);
            }
        }

        public void Recenter(Transform headTransform)
        {
            if (headTransform == null || _canvasObject == null)
                return;

            Vector3 flatFwd = Vector3.ProjectOnPlane(headTransform.forward, Vector3.up);
            if (flatFwd.sqrMagnitude < 0.001f)
            {
                flatFwd = Vector3.forward;
            }
            _anchoredForward = flatFwd.normalized;
            _hasAnchoredPose = true;

            ApplyPlacement(headTransform);
        }

        public void UpdateHud(
            in DiagnosticsSnapshot diag,
            CalibrationProfile calibration,
            bool mirrorEnabled,
            bool floorClampEnabled,
            Transform headTransform)
        {
            if (_hudText == null)
                return;

            if (headTransform != null)
            {
                if (!_hasAnchoredPose)
                {
                    Recenter(headTransform);
                }
                else
                {
                    Vector3 flatFwd = Vector3.ProjectOnPlane(headTransform.forward, Vector3.up);
                    if (flatFwd.sqrMagnitude > 0.001f)
                    {
                        float angle = Vector3.Angle(_anchoredForward, flatFwd.normalized);
                        if (angle > _recenterAngleThresholdDegrees)
                        {
                            _anchoredForward = Vector3.Slerp(_anchoredForward, flatFwd.normalized, Time.deltaTime * 2.5f).normalized;
                        }
                    }
                    ApplyPlacement(headTransform);
                }
            }

            _sb.Clear();
            _sb.AppendLine("<b><color=#5CE1E6>VIRTUAL FOOT TRACKING v0.1 — QUEST 3S PHASE 1 DIAGNOSTIC</color></b>");
            _sb.AppendLine(
                $"FPS: <b>{diag.Fps:F1}</b>   |   Body Hz: <b>{diag.TrackingUpdateHz:F0} Hz</b>   |   Solver: <b>{diag.SolverLatencyMs:F3} ms</b>");

            string bodyPerm = diag.BodyPermissionGranted ? "<color=#49E870>GRANTED</color>" : "<color=#FF5555>DENIED/WAIT</color>";
            string scenePerm = diag.ScenePermissionGranted ? "<color=#49E870>GRANTED</color>" : "<color=#FF5555>DENIED/WAIT</color>";
            _sb.AppendLine($"Perms -> BODY_TRACKING: {bodyPerm}   |   USE_SCENE: {scenePerm}");

            string bodyActive = diag.BodyTrackingActive ? "<color=#49E870>ACTIVE</color>" : "<color=#FF5555>INACTIVE</color>";
            string jointSet = diag.IsFullBody ? "FullBody(84)" : "UpperBody";
            _sb.AppendLine(
                $"Body SDK -> {bodyActive} | Fidelity: {diag.BodyFidelity} | Joints: {jointSet} | Calib: {diag.BodyCalibration} | Conf: {diag.BodyConfidence:F2}");

            string floorTag = diag.FloorSource == FloorDataSource.MrukSceneApi
                ? "<color=#24D9FF>MRUK Scene API</color>"
                : "<color=#FFB347>TrackingOrigin Fallback</color>";
            _sb.AppendLine(
                $"Floor -> Source: {floorTag} | FloorY: {diag.FloorWorldY:F3}m | Anchors: {diag.FloorAnchorCount}");

            if (calibration != null)
            {
                _sb.AppendLine(
                    $"Profile -> Height: {calibration.UserHeightMeters:F2}m | Hip: {calibration.HipHeightMeters:F2}m | Thigh/Shin: {calibration.ThighLengthMeters:F2}/{calibration.ShinLengthMeters:F2}m | Calibrated: {calibration.IsCalibrated}");
            }

            _sb.AppendLine($"Status: <color=#FFE066>{diag.StatusMessage}</color>   (LossEvents: {diag.TrackingLossEventsCount})");
            _sb.AppendLine("--------------------------------------------------------------------------------");
            _sb.AppendLine("<b>TRACKER     WORLD (X, Y, Z) [m]       FLOOR dY    GROUNDED   SOURCE    STATE</b>");

            AppendTrackerRow("L-FOOT", diag.CurrentFrame.LeftFoot);
            AppendTrackerRow("R-FOOT", diag.CurrentFrame.RightFoot);
            AppendTrackerRow("L-KNEE", diag.CurrentFrame.LeftKnee);
            AppendTrackerRow("R-KNEE", diag.CurrentFrame.RightKnee);

            _sb.AppendLine("--------------------------------------------------------------------------------");
            _sb.AppendLine(
                "Legend: <color=#49E870>GREEN=SDK_OBS</color>  <color=#24D9FF>CYAN=SDK_GEN (Generative Legs)</color>  <color=#FF991A>ORANGE=ENG_IK</color>  <color=#FF5555>RED=LOST</color>");
            _sb.AppendLine(
                $"Controls: [A] Auto-Calib  [B] MirrorRig({(mirrorEnabled ? "ON" : "OFF")})  [X] Reload MRUK  [Y] FloorClamp({(floorClampEnabled ? "ON" : "OFF")})  [R-Stick] Recenter");

            if (diag.RecentErrors != null && diag.RecentErrors.Length > 0)
            {
                _sb.AppendLine($"<color=#FF7777>Last Error: {diag.RecentErrors[diag.RecentErrors.Length - 1]}</color>");
            }

            _hudText.text = _sb.ToString();
        }

        private void AppendTrackerRow(string label, in VirtualTrackerSample s)
        {
            string sourceColor = s.Source switch
            {
                TrackingDataSource.SdkObserved => "#49E870",
                TrackingDataSource.SdkEstimatedGenerativeLegs => "#24D9FF",
                TrackingDataSource.EngineBiomechanicalFallback => "#FF991A",
                TrackingDataSource.EngineMotionPredicted => "#EB4DFF",
                _ => "#FF5555"
            };

            string gnd = s.IsGrounded ? "YES" : "NO ";
            string dy = s.HeightAboveFloorMeters >= 0f ? $"+{s.HeightAboveFloorMeters:F3}" : $"{s.HeightAboveFloorMeters:F3}";
            _sb.AppendLine(
                $"{label,-10} ({s.Position.x,5:F2}, {s.Position.y,5:F2}, {s.Position.z,5:F2})   {dy,7}m     {gnd}      <color={sourceColor}>{s.GetShortSourceTag(),-8}</color>  {s.Validity}");
        }

        private void ApplyPlacement(Transform headTransform)
        {
            Vector3 targetPos = headTransform.position + _anchoredForward * _distanceFromHeadMeters + Vector3.up * _verticalOffsetMeters;
            _canvasObject.transform.position = targetPos;
            _canvasObject.transform.rotation = Quaternion.LookRotation(_anchoredForward, Vector3.up);
        }

        private static Font LoadBuiltinFont()
        {
            Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null)
            {
                f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            return f;
        }
    }
}
