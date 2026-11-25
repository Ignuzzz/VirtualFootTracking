using System;
using UnityEngine;

namespace VirtualFootTracking.Core
{
    /// <summary>
    /// Origin of the currently active floor plane estimate.
    /// </summary>
    public enum FloorDataSource
    {
        Unavailable = 0,
        MrukSceneApi = 1,
        TrackingOriginFallback = 2
    }

    /// <summary>
    /// Spatial representation of the detected physical floor in Unity world coordinates.
    /// </summary>
    [Serializable]
    public struct FloorPlaneData
    {
        public bool IsValid;
        public FloorDataSource Source;
        public float WorldHeightY;
        public Vector3 CenterWorld;
        public Vector3 NormalWorld;
        public Vector2 PlaneSizeMeters;
        public Vector3[] BoundaryPolygonWorld;
        public Bounds RoomBoundsWorld;
        public bool HasRoomBounds;
        public int AnchorCount;
        public float Confidence;

        public static FloorPlaneData CreateFallback(float floorY, Vector3 centerWorld)
        {
            return new FloorPlaneData
            {
                IsValid = true,
                Source = FloorDataSource.TrackingOriginFallback,
                WorldHeightY = floorY,
                CenterWorld = new Vector3(centerWorld.x, floorY, centerWorld.z),
                NormalWorld = Vector3.up,
                PlaneSizeMeters = new Vector2(3f, 3f),
                BoundaryPolygonWorld = new[]
                {
                    new Vector3(centerWorld.x - 1.5f, floorY, centerWorld.z - 1.5f),
                    new Vector3(centerWorld.x + 1.5f, floorY, centerWorld.z - 1.5f),
                    new Vector3(centerWorld.x + 1.5f, floorY, centerWorld.z + 1.5f),
                    new Vector3(centerWorld.x - 1.5f, floorY, centerWorld.z + 1.5f)
                },
                RoomBoundsWorld = new Bounds(new Vector3(centerWorld.x, floorY + 1.25f, centerWorld.z), new Vector3(3f, 2.5f, 3f)),
                HasRoomBounds = false,
                AnchorCount = 0,
                Confidence = 0.4f
            };
        }
    }
}
