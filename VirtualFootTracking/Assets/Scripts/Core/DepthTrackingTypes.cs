using System;
using UnityEngine;

namespace VirtualFootTracking.Core
{
    /// <summary>
    /// A single foreground 3D surface hit returned by real-time Depth raycasting
    /// after subtracting floor, walls, furniture, static background voxels, and arms/hands.
    /// </summary>
    [Serializable]
    public struct DepthForegroundPoint
    {
        public Vector3 WorldPosition;
        public sbyte AssignedSide; // -1 = Left leg, 0 = Unassigned, +1 = Right leg
    }

    /// <summary>
    /// Extracted physical limb observation for one leg from the depth foreground point cluster.
    /// </summary>
    [Serializable]
    public struct DepthLegObservation
    {
        public bool IsDetected;
        public bool IsKneeDominant;
        public Vector3 FootWorldPosition;
        public bool HasKneeHint;
        public Vector3 KneeWorldPosition;
        public float Confidence;
        public int PointCount;
    }

    /// <summary>
    /// Per-frame output of the Depth API lower-body foreground detector.
    /// Kept SDK-agnostic inside VirtualFootTracking.Core.
    /// </summary>
    [Serializable]
    public struct DepthObservationFrame
    {
        public bool IsSupported;
        public bool IsReady;
        public bool IsEnabled;
        public bool IsCapturingBackground;
        public float BackgroundCaptureProgress01;
        public int StaticBackgroundVoxelCount;
        public int TotalRaysCast;
        public int RawHitCount;
        public int ForegroundPointCount;
        public DepthForegroundPoint[] ForegroundPoints;
        public DepthLegObservation LeftLeg;
        public DepthLegObservation RightLeg;
        public string StatusMessage;
    }
}
