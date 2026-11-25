using System;
using UnityEngine;

namespace VirtualFootTracking.Core
{
    /// <summary>
    /// Identifiers for the four simulated physical Full Body Tracking (FBT) devices.
    /// </summary>
    public enum VirtualTrackerId
    {
        LeftFoot = 0,
        RightFoot = 1,
        LeftKnee = 2,
        RightKnee = 3
    }

    /// <summary>
    /// Explicit provenance of a tracker or joint sample.
    /// Never conflates sensor-observed values with SDK estimation or engine prediction.
    /// </summary>
    public enum TrackingDataSource
    {
        Unavailable = 0,
        SdkObserved = 1,
        SdkEstimatedGenerativeLegs = 2,
        EngineBiomechanicalFallback = 3,
        EngineMotionPredicted = 4
    }

    /// <summary>
    /// Runtime validity classification for a virtual tracker sample.
    /// </summary>
    public enum TrackerValidityState
    {
        Invalid = 0,
        Valid = 1,
        Degraded = 2,
        TemporarilyLost = 3
    }

    /// <summary>
    /// Explicit coordinate frame attached to spatial poses.
    /// </summary>
    public enum CoordinateSpace
    {
        UnityWorldSpace = 0,
        TrackingRigLocalSpace = 1,
        HeadsetLocalSpace = 2
    }

    /// <summary>
    /// Standardized state snapshot for a single virtual FBT device.
    /// </summary>
    [Serializable]
    public struct VirtualTrackerSample
    {
        public VirtualTrackerId Id;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;
        public double TimestampSeconds;
        public TrackerValidityState Validity;
        public TrackingDataSource Source;
        public float Confidence;
        public CoordinateSpace Space;
        public float HeightAboveFloorMeters;
        public bool IsGrounded;
        public bool FloorClamped;

        public static VirtualTrackerSample CreateInvalid(VirtualTrackerId id, double timestampSeconds, CoordinateSpace space = CoordinateSpace.UnityWorldSpace)
        {
            return new VirtualTrackerSample
            {
                Id = id,
                Position = Vector3.zero,
                Rotation = Quaternion.identity,
                LinearVelocity = Vector3.zero,
                AngularVelocity = Vector3.zero,
                TimestampSeconds = timestampSeconds,
                Validity = TrackerValidityState.Invalid,
                Source = TrackingDataSource.Unavailable,
                Confidence = 0f,
                Space = space,
                HeightAboveFloorMeters = 0f,
                IsGrounded = false,
                FloorClamped = false
            };
        }

        public string GetShortSourceTag()
        {
            switch (Source)
            {
                case TrackingDataSource.SdkObserved:
                    return "SDK_OBS";
                case TrackingDataSource.SdkEstimatedGenerativeLegs:
                    return "SDK_GEN";
                case TrackingDataSource.EngineBiomechanicalFallback:
                    return "ENG_IK";
                case TrackingDataSource.EngineMotionPredicted:
                    return "ENG_PRED";
                default:
                    return "UNAVAIL";
            }
        }
    }

    /// <summary>
    /// Synchronized frame containing all four virtual tracker outputs.
    /// </summary>
    [Serializable]
    public struct VirtualTrackerFrame
    {
        public long FrameIndex;
        public double TimestampSeconds;
        public float FloorHeightWorldY;
        public VirtualTrackerSample LeftFoot;
        public VirtualTrackerSample RightFoot;
        public VirtualTrackerSample LeftKnee;
        public VirtualTrackerSample RightKnee;

        public static VirtualTrackerFrame CreateEmpty(long frameIndex, double timestampSeconds, float floorHeightWorldY)
        {
            return new VirtualTrackerFrame
            {
                FrameIndex = frameIndex,
                TimestampSeconds = timestampSeconds,
                FloorHeightWorldY = floorHeightWorldY,
                LeftFoot = VirtualTrackerSample.CreateInvalid(VirtualTrackerId.LeftFoot, timestampSeconds),
                RightFoot = VirtualTrackerSample.CreateInvalid(VirtualTrackerId.RightFoot, timestampSeconds),
                LeftKnee = VirtualTrackerSample.CreateInvalid(VirtualTrackerId.LeftKnee, timestampSeconds),
                RightKnee = VirtualTrackerSample.CreateInvalid(VirtualTrackerId.RightKnee, timestampSeconds)
            };
        }

        public VirtualTrackerSample GetSample(VirtualTrackerId id)
        {
            switch (id)
            {
                case VirtualTrackerId.LeftFoot:
                    return LeftFoot;
                case VirtualTrackerId.RightFoot:
                    return RightFoot;
                case VirtualTrackerId.LeftKnee:
                    return LeftKnee;
                case VirtualTrackerId.RightKnee:
                    return RightKnee;
                default:
                    return VirtualTrackerSample.CreateInvalid(id, TimestampSeconds);
            }
        }

        public void SetSample(VirtualTrackerId id, in VirtualTrackerSample sample)
        {
            switch (id)
            {
                case VirtualTrackerId.LeftFoot:
                    LeftFoot = sample;
                    break;
                case VirtualTrackerId.RightFoot:
                    RightFoot = sample;
                    break;
                case VirtualTrackerId.LeftKnee:
                    LeftKnee = sample;
                    break;
                case VirtualTrackerId.RightKnee:
                    RightKnee = sample;
                    break;
            }
        }
    }
}
