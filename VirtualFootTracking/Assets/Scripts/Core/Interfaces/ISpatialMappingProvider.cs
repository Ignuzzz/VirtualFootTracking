using UnityEngine;

namespace VirtualFootTracking.Core
{
    /// <summary>
    /// Contract for spatial room understanding, floor plane detection, and floor height queries.
    /// </summary>
    public interface ISpatialMappingProvider
    {
        bool IsInitialized { get; }
        bool IsPermissionGranted { get; }
        bool IsSceneLoaded { get; }
        string StatusMessage { get; }

        void Initialize();
        void RequestPermission();
        void RequestSceneReload();
        bool TryGetFloorData(out FloorPlaneData floorData);
        bool TryQueryFloorHeightAt(Vector3 worldXZ, out float floorY, out FloorDataSource source);
        void Shutdown();
    }
}
