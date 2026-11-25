using System;
using UnityEngine;
using VirtualFootTracking.Core;

namespace VirtualFootTracking.Communication
{
    /// <summary>
    /// Planned protocol DTO for Phase 2 PC SteamVR OpenVR GenericTracker streaming (Wi-Fi UDP / USB).
    /// </summary>
    [Serializable]
    public struct TrackerWireEntry
    {
        public int TrackerId;
        public float PosX;
        public float PosY;
        public float PosZ;
        public float RotX;
        public float RotY;
        public float RotZ;
        public float RotW;
        public float VelX;
        public float VelY;
        public float VelZ;
        public int Validity;
        public int Source;
        public float Confidence;

        public static TrackerWireEntry FromSample(in VirtualTrackerSample sample)
        {
            return new TrackerWireEntry
            {
                TrackerId = (int)sample.Id,
                PosX = sample.Position.x,
                PosY = sample.Position.y,
                PosZ = sample.Position.z,
                RotX = sample.Rotation.x,
                RotY = sample.Rotation.y,
                RotZ = sample.Rotation.z,
                RotW = sample.Rotation.w,
                VelX = sample.LinearVelocity.x,
                VelY = sample.LinearVelocity.y,
                VelZ = sample.LinearVelocity.z,
                Validity = (int)sample.Validity,
                Source = (int)sample.Source,
                Confidence = sample.Confidence
            };
        }
    }

    /// <summary>
    /// Versioned frame packet ready for JSON diagnostics or compact binary UDP serialization in Phase 2.
    /// </summary>
    [Serializable]
    public class TrackerPacketFrame
    {
        public const string ProtocolMagic = "VFT1";
        public const int ProtocolVersion = 1;

        public string Magic = ProtocolMagic;
        public int Version = ProtocolVersion;
        public long SequenceNumber;
        public long MonotonicTimestampNs;
        public float FloorHeightWorldY;
        public TrackerWireEntry[] Trackers = new TrackerWireEntry[4];

        public static TrackerPacketFrame FromTrackerFrame(in VirtualTrackerFrame frame)
        {
            return new TrackerPacketFrame
            {
                SequenceNumber = frame.FrameIndex,
                MonotonicTimestampNs = (long)(frame.TimestampSeconds * 1_000_000_000.0),
                FloorHeightWorldY = frame.FloorHeightWorldY,
                Trackers = new[]
                {
                    TrackerWireEntry.FromSample(frame.LeftFoot),
                    TrackerWireEntry.FromSample(frame.RightFoot),
                    TrackerWireEntry.FromSample(frame.LeftKnee),
                    TrackerWireEntry.FromSample(frame.RightKnee)
                }
            };
        }
    }
}
