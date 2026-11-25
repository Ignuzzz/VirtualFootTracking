using UnityEngine;
using VirtualFootTracking.Core;

namespace VirtualFootTracking.Biomechanics
{
    /// <summary>
    /// Phase 1 honest diagnostic solver.
    /// Exposes Meta FullBody lower-body joint estimates directly with explicit provenance
    /// and floor clearance diagnostics, without masking SDK errors behind aggressive smoothing.
    /// </summary>
    public class DiagnosticPassthroughFootSolver : MonoBehaviour, IFootTrackingSolver
    {
        // Verified OVRPlugin.BoneId indices from Meta XR Core SDK 209.0.0
        private const int LeftKneeJointIndex = 71;   // FullBody_LeftLowerLeg
        private const int LeftAnkleJointIndex = 73;  // FullBody_LeftFootAnkle
        private const int RightKneeJointIndex = 78;  // FullBody_RightLowerLeg
        private const int RightAnkleJointIndex = 80; // FullBody_RightFootAnkle

        [Header("Diagnostic Settings")]
        [SerializeField]
        [Tooltip("Off by default in Phase 1 so raw SDK floor drift is observable. Toggle on to clamp penetration.")]
        private bool _enableFloorPenetrationClamp = false;

        [SerializeField]
        private float _groundedThresholdMeters = 0.06f;

        [SerializeField]
        private float _minimumFootClearanceMeters = 0.02f;

        [SerializeField]
        private float _minimumKneeClearanceMeters = 0.05f;

        [SerializeField]
        private float _maxPlausibleSpeedMetersPerSec = 20f;

        private CalibrationProfile _profile = new CalibrationProfile();
        private long _frameIndex;
        private VirtualTrackerFrame _previousFrame;
        private bool _hasPreviousFrame;

        public string SolverName => "Phase1.DiagnosticPassthroughFootSolver";

        public bool EnableFloorPenetrationClamp
        {
            get => _enableFloorPenetrationClamp;
            set => _enableFloorPenetrationClamp = value;
        }

        public void Initialize(CalibrationProfile profile)
        {
            _profile = profile ?? new CalibrationProfile();
            _frameIndex = 0;
            _hasPreviousFrame = false;
        }

        public void UpdateCalibration(CalibrationProfile profile)
        {
            _profile = profile ?? new CalibrationProfile();
        }

        public bool Solve(
            in BodySkeletonSnapshot skeleton,
            in FloorPlaneData floor,
            float deltaTime,
            double timestampSeconds,
            out VirtualTrackerFrame frame)
        {
            _frameIndex++;
            float floorY = floor.IsValid ? floor.WorldHeightY : 0f;
            frame = VirtualTrackerFrame.CreateEmpty(_frameIndex, timestampSeconds, floorY);

            if (!skeleton.IsAvailable || !skeleton.IsFullBodyJointSet)
            {
                _hasPreviousFrame = false;
                return false;
            }

            frame.LeftFoot = BuildTrackerSample(
                VirtualTrackerId.LeftFoot,
                LeftAnkleJointIndex,
                isFoot: true,
                skeleton,
                floorY,
                deltaTime,
                timestampSeconds);

            frame.RightFoot = BuildTrackerSample(
                VirtualTrackerId.RightFoot,
                RightAnkleJointIndex,
                isFoot: true,
                skeleton,
                floorY,
                deltaTime,
                timestampSeconds);

            frame.LeftKnee = BuildTrackerSample(
                VirtualTrackerId.LeftKnee,
                LeftKneeJointIndex,
                isFoot: false,
                skeleton,
                floorY,
                deltaTime,
                timestampSeconds);

            frame.RightKnee = BuildTrackerSample(
                VirtualTrackerId.RightKnee,
                RightKneeJointIndex,
                isFoot: false,
                skeleton,
                floorY,
                deltaTime,
                timestampSeconds);

            _previousFrame = frame;
            _hasPreviousFrame = true;
            return true;
        }

        private VirtualTrackerSample BuildTrackerSample(
            VirtualTrackerId id,
            int jointIndex,
            bool isFoot,
            in BodySkeletonSnapshot skeleton,
            float floorY,
            float deltaTime,
            double timestampSeconds)
        {
            if (!skeleton.TryGetJoint(jointIndex, out BodyJointSample joint) || !joint.PositionValid)
            {
                return VirtualTrackerSample.CreateInvalid(id, timestampSeconds, CoordinateSpace.UnityWorldSpace);
            }

            Quaternion baseRot = joint.OrientationValid ? joint.WorldRotation : Quaternion.identity;
            Quaternion calRotOffset = _profile != null ? _profile.GetRotationOffset(id) : Quaternion.identity;
            Quaternion finalRot = baseRot * calRotOffset;

            Vector3 calPosOffset = _profile != null ? _profile.GetPositionOffset(id) : Vector3.zero;
            Vector3 finalPos = joint.WorldPosition + (baseRot * calPosOffset);

            bool floorClamped = false;
            float minClearance = isFoot ? _minimumFootClearanceMeters : _minimumKneeClearanceMeters;
            if (_enableFloorPenetrationClamp && finalPos.y < floorY + minClearance)
            {
                finalPos.y = floorY + minClearance;
                floorClamped = true;
            }

            float heightAboveFloor = finalPos.y - floorY;
            bool isGrounded = isFoot && heightAboveFloor <= _groundedThresholdMeters;

            Vector3 linearVelocity = Vector3.zero;
            if (_hasPreviousFrame && deltaTime > 0.0001f)
            {
                VirtualTrackerSample prev = _previousFrame.GetSample(id);
                if (prev.Validity != TrackerValidityState.Invalid)
                {
                    Vector3 rawVel = (finalPos - prev.Position) / deltaTime;
                    if (rawVel.magnitude <= _maxPlausibleSpeedMetersPerSec)
                    {
                        linearVelocity = rawVel;
                    }
                }
            }

            TrackerValidityState validity;
            if (joint.PositionValid && joint.OrientationValid && skeleton.Confidence >= 0.35f)
            {
                validity = TrackerValidityState.Valid;
            }
            else if (joint.PositionValid)
            {
                validity = TrackerValidityState.Degraded;
            }
            else
            {
                validity = TrackerValidityState.Invalid;
            }

            return new VirtualTrackerSample
            {
                Id = id,
                Position = finalPos,
                Rotation = finalRot,
                LinearVelocity = linearVelocity,
                AngularVelocity = Vector3.zero,
                TimestampSeconds = timestampSeconds,
                Validity = validity,
                Source = joint.DataSource,
                Confidence = skeleton.Confidence,
                Space = CoordinateSpace.UnityWorldSpace,
                HeightAboveFloorMeters = heightAboveFloor,
                IsGrounded = isGrounded,
                FloorClamped = floorClamped
            };
        }
    }
}
