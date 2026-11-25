using System;
using UnityEngine;

namespace VirtualFootTracking.Core
{
    /// <summary>
    /// Anthropometric measurements and user-configurable tracker offsets.
    /// </summary>
    [Serializable]
    public class CalibrationProfile
    {
        public float UserHeightMeters = 1.75f;
        public float HeadsetStandingHeightMeters = 1.64f;
        public float HipHeightMeters = 0.93f;
        public float ThighLengthMeters = 0.43f;
        public float ShinLengthMeters = 0.43f;
        public float AnkleToFloorMeters = 0.07f;
        public float StanceWidthMeters = 0.36f;
        public float FootSpacingMeters = 0.24f;
        public float InitialFootYawOffsetDegrees = 0f;

        public Vector3 LeftFootPositionOffset = Vector3.zero;
        public Vector3 RightFootPositionOffset = Vector3.zero;
        public Vector3 LeftKneePositionOffset = Vector3.zero;
        public Vector3 RightKneePositionOffset = Vector3.zero;

        public Vector3 LeftFootEulerOffset = Vector3.zero;
        public Vector3 RightFootEulerOffset = Vector3.zero;
        public Vector3 LeftKneeEulerOffset = Vector3.zero;
        public Vector3 RightKneeEulerOffset = Vector3.zero;

        public bool IsCalibrated = false;
        public bool IsAutoEstimated = true;
        public string CalibratedAtUtcIso = string.Empty;

        public Vector3 GetPositionOffset(VirtualTrackerId id)
        {
            switch (id)
            {
                case VirtualTrackerId.LeftFoot:
                    return LeftFootPositionOffset;
                case VirtualTrackerId.RightFoot:
                    return RightFootPositionOffset;
                case VirtualTrackerId.LeftKnee:
                    return LeftKneePositionOffset;
                case VirtualTrackerId.RightKnee:
                    return RightKneePositionOffset;
                default:
                    return Vector3.zero;
            }
        }

        public Quaternion GetRotationOffset(VirtualTrackerId id)
        {
            switch (id)
            {
                case VirtualTrackerId.LeftFoot:
                    return Quaternion.Euler(LeftFootEulerOffset);
                case VirtualTrackerId.RightFoot:
                    return Quaternion.Euler(RightFootEulerOffset);
                case VirtualTrackerId.LeftKnee:
                    return Quaternion.Euler(LeftKneeEulerOffset);
                case VirtualTrackerId.RightKnee:
                    return Quaternion.Euler(RightKneeEulerOffset);
                default:
                    return Quaternion.identity;
            }
        }
    }
}
