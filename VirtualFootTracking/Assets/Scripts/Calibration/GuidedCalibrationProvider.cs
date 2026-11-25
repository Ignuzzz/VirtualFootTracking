using System;
using System.IO;
using UnityEngine;
using VirtualFootTracking.Core;

namespace VirtualFootTracking.Calibration
{
    /// <summary>
    /// Phase 2 multi-frame guided standing calibration and granular manual anthropometric calibration provider.
    /// </summary>
    public class GuidedCalibrationProvider : MonoBehaviour, ICalibrationProvider
    {
        private const string ProfileFileName = "calibration_profile.json";
        private const int HipsJointIndex = 1;
        private const int LeftAnkleJointIndex = 73;
        private const int LeftBallJointIndex = 76;
        private const int RightAnkleJointIndex = 80;
        private const int RightBallJointIndex = 83;

        [SerializeField] private CalibrationProfile _profile = new CalibrationProfile();
        [SerializeField] private float _captureDurationSeconds = 2.5f;
        [SerializeField] private float _eyeToHeadTopOffsetMeters = 0.11f;

        private float _elapsedCapture;
        private int _sampleCount;
        private float _sumHeadHeight;
        private float _sumHipHeight;
        private int _hipSampleCount;
        private float _sumFootSpacing;
        private int _spacingSampleCount;
        private Vector2 _sumFootFwdXZ;
        private int _yawSampleCount;
        private string _statusMessage = "Pronto";

        public event Action<CalibrationProfile> OnCalibrationUpdated;

        public CalibrationProfile CurrentProfile => _profile;
        public bool IsCalibrating { get; private set; }
        public float CalibrationProgress01 { get; private set; }
        public string CalibrationStatusMessage => _statusMessage;

        private string ProfilePath => Path.Combine(Application.persistentDataPath, ProfileFileName);

        public void Initialize()
        {
            LoadProfile();
        }

        /// <summary>
        /// Starts a 2.5-second multi-frame standing capture that averages head height, hip height,
        /// foot spacing, and initial foot heading.
        /// </summary>
        public void StartGuidedStandingCapture()
        {
            IsCalibrating = true;
            CalibrationProgress01 = 0f;
            _elapsedCapture = 0f;
            _sampleCount = 0;
            _sumHeadHeight = 0f;
            _sumHipHeight = 0f;
            _hipSampleCount = 0;
            _sumFootSpacing = 0f;
            _spacingSampleCount = 0;
            _sumFootFwdXZ = Vector2.zero;
            _yawSampleCount = 0;
            _statusMessage = "Fique em pe ereto e parado...";
        }

        public void CancelGuidedCapture()
        {
            IsCalibrating = false;
            CalibrationProgress01 = _profile.IsCalibrated ? 1f : 0f;
            _statusMessage = "Calibracao cancelada";
        }

        /// <summary>
        /// Advances guided capture when IsCalibrating is true. Returns true on the exact frame calibration completes.
        /// </summary>
        public bool TickCalibration(in BodySkeletonSnapshot skeleton, in FloorPlaneData floor, float deltaTime)
        {
            if (!IsCalibrating)
                return false;

            float floorY = floor.IsValid ? floor.WorldHeightY : 0f;
            float headY = 0f;
            bool hasHead = false;

            if (skeleton.IsAvailable && skeleton.HeadWorldPosition.sqrMagnitude > 0.0001f)
            {
                headY = skeleton.HeadWorldPosition.y;
                hasHead = true;
            }
            else if (Camera.main != null)
            {
                headY = Camera.main.transform.position.y;
                hasHead = true;
            }

            if (!hasHead)
            {
                _statusMessage = "Aguardando HMD/cabeca valida...";
                return false;
            }

            float standingHead = headY - floorY;
            if (standingHead < 0.85f || standingHead > 2.25f)
            {
                _statusMessage = "Altura fora da faixa (fique em pe)...";
                return false;
            }

            _elapsedCapture += Mathf.Max(deltaTime, 0.001f);
            _sampleCount++;
            _sumHeadHeight += standingHead;

            if (skeleton.IsAvailable)
            {
                if (skeleton.TryGetJoint(HipsJointIndex, out BodyJointSample hips) && hips.PositionValid)
                {
                    float h = hips.WorldPosition.y - floorY;
                    if (h >= 0.45f && h <= 1.35f)
                    {
                        _sumHipHeight += h;
                        _hipSampleCount++;
                    }
                }

                if (skeleton.TryGetJoint(LeftAnkleJointIndex, out BodyJointSample lAnkle) &&
                    skeleton.TryGetJoint(RightAnkleJointIndex, out BodyJointSample rAnkle) &&
                    lAnkle.PositionValid && rAnkle.PositionValid)
                {
                    float spacing = Vector2.Distance(
                        new Vector2(lAnkle.WorldPosition.x, lAnkle.WorldPosition.z),
                        new Vector2(rAnkle.WorldPosition.x, rAnkle.WorldPosition.z));
                    if (spacing >= 0.08f && spacing <= 0.75f)
                    {
                        _sumFootSpacing += spacing;
                        _spacingSampleCount++;
                    }

                    if (skeleton.TryGetJoint(LeftBallJointIndex, out BodyJointSample lBall) && lBall.PositionValid)
                    {
                        Vector3 fwd = lBall.WorldPosition - lAnkle.WorldPosition;
                        _sumFootFwdXZ += new Vector2(fwd.x, fwd.z);
                        _yawSampleCount++;
                    }
                    if (skeleton.TryGetJoint(RightBallJointIndex, out BodyJointSample rBall) && rBall.PositionValid)
                    {
                        Vector3 fwd = rBall.WorldPosition - rAnkle.WorldPosition;
                        _sumFootFwdXZ += new Vector2(fwd.x, fwd.z);
                        _yawSampleCount++;
                    }
                }
            }

            CalibrationProgress01 = Mathf.Clamp01(_elapsedCapture / Mathf.Max(0.5f, _captureDurationSeconds));
            _statusMessage = $"Capturando postura ({CalibrationProgress01 * 100f:F0}%)...";

            if (_elapsedCapture < _captureDurationSeconds)
                return false;

            float avgHeadHeight = Mathf.Clamp(_sumHeadHeight / Mathf.Max(1, _sampleCount), 0.90f, 2.20f);
            float avgHipHeight = _hipSampleCount > 0 ? (_sumHipHeight / _hipSampleCount) : -1f;
            float avgSpacing = _spacingSampleCount > 0 ? (_sumFootSpacing / _spacingSampleCount) : -1f;

            ApplyEstimatedProportions(avgHeadHeight, avgHipHeight, avgSpacing);
            IsCalibrating = false;
            CalibrationProgress01 = 1f;
            _statusMessage = $"Calibrado: {_profile.UserHeightMeters:F2}m";
            OnCalibrationUpdated?.Invoke(_profile);
            return true;
        }

        /// <summary>
        /// Single-frame immediate calibration fallback (used on initial startup auto-calibration).
        /// </summary>
        public bool BeginAutoCalibration(in BodySkeletonSnapshot skeleton, in FloorPlaneData floor)
        {
            float floorY = floor.IsValid ? floor.WorldHeightY : 0f;
            float headY = 0f;
            bool hasHead = false;

            if (skeleton.IsAvailable && skeleton.HeadWorldPosition.sqrMagnitude > 0.0001f)
            {
                headY = skeleton.HeadWorldPosition.y;
                hasHead = true;
            }
            else if (Camera.main != null)
            {
                headY = Camera.main.transform.position.y;
                hasHead = true;
            }

            if (!hasHead)
                return false;

            float standingHead = Mathf.Clamp(headY - floorY, 0.90f, 2.20f);
            float hipH = -1f;
            float spacing = -1f;

            if (skeleton.IsAvailable)
            {
                if (skeleton.TryGetJoint(HipsJointIndex, out BodyJointSample hips) && hips.PositionValid)
                {
                    float measured = hips.WorldPosition.y - floorY;
                    if (measured >= 0.45f && measured <= 1.35f) hipH = measured;
                }
                if (skeleton.TryGetJoint(LeftAnkleJointIndex, out BodyJointSample la) &&
                    skeleton.TryGetJoint(RightAnkleJointIndex, out BodyJointSample ra) &&
                    la.PositionValid && ra.PositionValid)
                {
                    float d = Vector2.Distance(new Vector2(la.WorldPosition.x, la.WorldPosition.z), new Vector2(ra.WorldPosition.x, ra.WorldPosition.z));
                    if (d >= 0.08f && d <= 0.75f) spacing = d;
                }
            }

            ApplyEstimatedProportions(standingHead, hipH, spacing);
            IsCalibrating = false;
            CalibrationProgress01 = 1f;
            _statusMessage = $"Auto-calibrado: {_profile.UserHeightMeters:F2}m";
            OnCalibrationUpdated?.Invoke(_profile);
            return true;
        }

        public void ApplyManualProfile(CalibrationProfile profile)
        {
            if (profile == null)
                return;

            _profile = profile;
            MarkManualAndSave();
        }

        public void AdjustUserHeight(float deltaMeters)
        {
            float newHeight = Mathf.Clamp(_profile.UserHeightMeters + deltaMeters, 1.10f, 2.30f);
            float ratio = newHeight / Mathf.Max(0.5f, _profile.UserHeightMeters);
            _profile.UserHeightMeters = newHeight;
            _profile.HeadsetStandingHeightMeters = Mathf.Max(0.95f, newHeight - _eyeToHeadTopOffsetMeters);
            _profile.HipHeightMeters = Mathf.Clamp(_profile.HipHeightMeters * ratio, 0.45f, 1.40f);
            _profile.ThighLengthMeters = Mathf.Clamp(_profile.ThighLengthMeters * ratio, 0.22f, 0.75f);
            _profile.ShinLengthMeters = Mathf.Clamp(_profile.ShinLengthMeters * ratio, 0.22f, 0.75f);
            _profile.AnkleToFloorMeters = Mathf.Clamp(newHeight * 0.039f, 0.04f, 0.14f);
            MarkManualAndSave();
        }

        public void AdjustThighLength(float deltaMeters)
        {
            _profile.ThighLengthMeters = Mathf.Clamp(_profile.ThighLengthMeters + deltaMeters, 0.22f, 0.75f);
            MarkManualAndSave();
        }

        public void AdjustShinLength(float deltaMeters)
        {
            _profile.ShinLengthMeters = Mathf.Clamp(_profile.ShinLengthMeters + deltaMeters, 0.22f, 0.75f);
            MarkManualAndSave();
        }

        public void AdjustHipHeight(float deltaMeters)
        {
            _profile.HipHeightMeters = Mathf.Clamp(_profile.HipHeightMeters + deltaMeters, 0.45f, 1.40f);
            MarkManualAndSave();
        }

        public void AdjustFootSpacing(float deltaMeters)
        {
            _profile.FootSpacingMeters = Mathf.Clamp(_profile.FootSpacingMeters + deltaMeters, 0.10f, 0.65f);
            MarkManualAndSave();
        }

        public void AdjustStanceWidth(float deltaMeters)
        {
            _profile.StanceWidthMeters = Mathf.Clamp(_profile.StanceWidthMeters + deltaMeters, 0.20f, 0.75f);
            MarkManualAndSave();
        }

        public void AdjustInitialFootYaw(float deltaDegrees)
        {
            _profile.InitialFootYawOffsetDegrees = Mathf.Clamp(_profile.InitialFootYawOffsetDegrees + deltaDegrees, -45f, 45f);
            MarkManualAndSave();
        }

        public void AdjustTrackerPositionOffset(VirtualTrackerId id, Vector3 deltaMeters)
        {
            switch (id)
            {
                case VirtualTrackerId.LeftFoot:
                    _profile.LeftFootPositionOffset = ClampOffset(_profile.LeftFootPositionOffset + deltaMeters);
                    break;
                case VirtualTrackerId.RightFoot:
                    _profile.RightFootPositionOffset = ClampOffset(_profile.RightFootPositionOffset + deltaMeters);
                    break;
                case VirtualTrackerId.LeftKnee:
                    _profile.LeftKneePositionOffset = ClampOffset(_profile.LeftKneePositionOffset + deltaMeters);
                    break;
                case VirtualTrackerId.RightKnee:
                    _profile.RightKneePositionOffset = ClampOffset(_profile.RightKneePositionOffset + deltaMeters);
                    break;
            }
            MarkManualAndSave();
        }

        public void ResetTrackerOffsets()
        {
            _profile.LeftFootPositionOffset = Vector3.zero;
            _profile.RightFootPositionOffset = Vector3.zero;
            _profile.LeftKneePositionOffset = Vector3.zero;
            _profile.RightKneePositionOffset = Vector3.zero;
            _profile.LeftFootEulerOffset = Vector3.zero;
            _profile.RightFootEulerOffset = Vector3.zero;
            _profile.LeftKneeEulerOffset = Vector3.zero;
            _profile.RightKneeEulerOffset = Vector3.zero;
            _profile.InitialFootYawOffsetDegrees = 0f;
            MarkManualAndSave();
        }

        public void SaveProfile()
        {
            try
            {
                string json = JsonUtility.ToJson(_profile, true);
                File.WriteAllText(ProfilePath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GuidedCalibrationProvider] Failed to save profile: {ex.Message}");
            }
        }

        public CalibrationProfile LoadProfile()
        {
            try
            {
                if (File.Exists(ProfilePath))
                {
                    string json = File.ReadAllText(ProfilePath);
                    CalibrationProfile loaded = JsonUtility.FromJson<CalibrationProfile>(json);
                    if (loaded != null && loaded.UserHeightMeters > 0.5f)
                    {
                        _profile = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GuidedCalibrationProvider] Failed to load profile: {ex.Message}");
            }

            if (_profile == null)
            {
                _profile = new CalibrationProfile();
            }

            return _profile;
        }

        private void ApplyEstimatedProportions(float standingHeadHeight, float measuredHipHeight, float measuredFootSpacing)
        {
            float estimatedUserHeight = Mathf.Clamp(standingHeadHeight + _eyeToHeadTopOffsetMeters, 1.05f, 2.30f);
            _profile.HeadsetStandingHeightMeters = standingHeadHeight;
            _profile.UserHeightMeters = estimatedUserHeight;

            float hipH = measuredHipHeight > 0f ? measuredHipHeight : estimatedUserHeight * 0.530f;
            _profile.HipHeightMeters = hipH;

            // Proportion thigh and shin relative to measured hip height when available.
            float legSpan = Mathf.Max(0.45f, hipH - estimatedUserHeight * 0.039f);
            _profile.ThighLengthMeters = legSpan * 0.50f;
            _profile.ShinLengthMeters = legSpan * 0.50f;
            _profile.AnkleToFloorMeters = estimatedUserHeight * 0.039f;
            _profile.StanceWidthMeters = estimatedUserHeight * 0.220f;
            _profile.FootSpacingMeters = measuredFootSpacing > 0f ? measuredFootSpacing : estimatedUserHeight * 0.145f;
            _profile.IsCalibrated = true;
            _profile.IsAutoEstimated = true;
            _profile.CalibratedAtUtcIso = DateTime.UtcNow.ToString("o");
            SaveProfile();
        }

        private void MarkManualAndSave()
        {
            _profile.IsCalibrated = true;
            _profile.IsAutoEstimated = false;
            _profile.CalibratedAtUtcIso = DateTime.UtcNow.ToString("o");
            SaveProfile();
            OnCalibrationUpdated?.Invoke(_profile);
        }

        private static Vector3 ClampOffset(Vector3 v)
        {
            return new Vector3(
                Mathf.Clamp(v.x, -0.35f, 0.35f),
                Mathf.Clamp(v.y, -0.35f, 0.35f),
                Mathf.Clamp(v.z, -0.35f, 0.35f));
        }
    }
}
