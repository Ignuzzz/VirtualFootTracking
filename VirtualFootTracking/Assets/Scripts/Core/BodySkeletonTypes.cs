using System;
using UnityEngine;

namespace VirtualFootTracking.Core
{
    /// <summary>
    /// Active Meta Body Tracking fidelity mode.
    /// </summary>
    public enum BodyTrackingFidelityMode
    {
        Unknown = 0,
        LowThreePoint = 1,
        HighInsideOut = 2
    }

    /// <summary>
    /// SDK body calibration state reported by Meta OpenXR / OVRPlugin.
    /// </summary>
    public enum BodyCalibrationState
    {
        Invalid = 0,
        Calibrating = 1,
        Valid = 2
    }

    /// <summary>
    /// Per-joint state converted explicitly into Unity left-handed coordinates.
    /// </summary>
    [Serializable]
    public struct BodyJointSample
    {
        public int JointIndex;
        public string JointName;
        public Vector3 TrackingSpacePosition;
        public Quaternion TrackingSpaceRotation;
        public Vector3 WorldPosition;
        public Quaternion WorldRotation;
        public bool PositionValid;
        public bool OrientationValid;
        public bool PositionTracked;
        public bool OrientationTracked;
        public bool IsLowerBodyGenerativeJoint;
        public TrackingDataSource DataSource;
    }

    /// <summary>
    /// Full body skeleton snapshot captured from the body tracking provider.
    /// </summary>
    [Serializable]
    public struct BodySkeletonSnapshot
    {
        public bool IsAvailable;
        public bool IsFullBodyJointSet;
        public float Confidence;
        public BodyTrackingFidelityMode Fidelity;
        public BodyCalibrationState CalibrationState;
        public double SdkTimestampSeconds;
        public uint SkeletonChangedCount;
        public Vector3 HeadWorldPosition;
        public Vector3 HipsWorldPosition;
        public BodyJointSample[] Joints;

        public bool TryGetJoint(int jointIndex, out BodyJointSample sample)
        {
            if (Joints != null && jointIndex >= 0 && jointIndex < Joints.Length)
            {
                sample = Joints[jointIndex];
                return sample.PositionValid;
            }

            sample = default;
            return false;
        }
    }
}
