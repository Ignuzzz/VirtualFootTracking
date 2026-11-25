using System;
using System.IO;
using UnityEngine;
using VirtualFootTracking.Core;

namespace VirtualFootTracking.Calibration
{
    /// <summary>
    /// Phase 1 anthropometric auto-calibration and JSON persistence provider.
    /// </summary>
    public class BasicCalibrationProvider : MonoBehaviour, ICalibrationProvider
    {
        private const string ProfileFileName = "calibration_profile.json";

        [SerializeField]
        private CalibrationProfile _profile = new CalibrationProfile();

        [SerializeField]
        private float _eyeToHeadTopOffsetMeters = 0.11f;

        public CalibrationProfile CurrentProfile => _profile;
        public bool IsCalibrating { get; private set; }
        public float CalibrationProgress01 { get; private set; }

        private string ProfilePath => Path.Combine(Application.persistentDataPath, ProfileFileName);

        public void Initialize()
        {
            LoadProfile();
        }

        public bool BeginAutoCalibration(in BodySkeletonSnapshot skeleton, in FloorPlaneData floor)
        {
            IsCalibrating = true;
            CalibrationProgress01 = 0.25f;

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
                IsCalibrating = false;
                CalibrationProgress01 = 0f;
                return false;
            }

            float headsetStandingHeight = Mathf.Clamp(headY - floorY, 0.90f, 2.20f);
            float estimatedUserHeight = Mathf.Clamp(headsetStandingHeight + _eyeToHeadTopOffsetMeters, 1.00f, 2.30f);

            // Standard anthropometric ratios (Drillis & Contini proportions)
            _profile.HeadsetStandingHeightMeters = headsetStandingHeight;
            _profile.UserHeightMeters = estimatedUserHeight;
            _profile.HipHeightMeters = estimatedUserHeight * 0.530f;
            _profile.ThighLengthMeters = estimatedUserHeight * 0.245f;
            _profile.ShinLengthMeters = estimatedUserHeight * 0.246f;
            _profile.AnkleToFloorMeters = estimatedUserHeight * 0.039f;
            _profile.StanceWidthMeters = estimatedUserHeight * 0.220f;
            _profile.FootSpacingMeters = estimatedUserHeight * 0.140f;

            if (skeleton.TryGetJoint(1, out BodyJointSample hips) && hips.PositionValid)
            {
                float measuredHip = hips.WorldPosition.y - floorY;
                if (measuredHip > 0.40f && measuredHip < 1.40f)
                {
                    _profile.HipHeightMeters = measuredHip;
                }
            }

            if (skeleton.TryGetJoint(73, out BodyJointSample leftAnkle) &&
                skeleton.TryGetJoint(80, out BodyJointSample rightAnkle) &&
                leftAnkle.PositionValid &&
                rightAnkle.PositionValid)
            {
                Vector2 deltaXZ = new Vector2(
                    leftAnkle.WorldPosition.x - rightAnkle.WorldPosition.x,
                    leftAnkle.WorldPosition.z - rightAnkle.WorldPosition.z);
                float spacing = deltaXZ.magnitude;
                if (spacing >= 0.08f && spacing <= 0.80f)
                {
                    _profile.FootSpacingMeters = spacing;
                }
            }

            _profile.IsCalibrated = true;
            _profile.IsAutoEstimated = true;
            _profile.CalibratedAtUtcIso = DateTime.UtcNow.ToString("o");

            CalibrationProgress01 = 1f;
            IsCalibrating = false;
            SaveProfile();
            return true;
        }

        public void ApplyManualProfile(CalibrationProfile profile)
        {
            if (profile == null)
                return;

            _profile = profile;
            _profile.IsCalibrated = true;
            _profile.IsAutoEstimated = false;
            _profile.CalibratedAtUtcIso = DateTime.UtcNow.ToString("o");
            SaveProfile();
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
                Debug.LogWarning($"[BasicCalibrationProvider] Failed to save calibration profile: {ex.Message}");
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
                Debug.LogWarning($"[BasicCalibrationProvider] Failed to load calibration profile: {ex.Message}");
            }

            if (_profile == null)
            {
                _profile = new CalibrationProfile();
            }

            return _profile;
        }
    }
}
