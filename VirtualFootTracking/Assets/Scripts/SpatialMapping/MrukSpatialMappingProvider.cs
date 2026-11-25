using System;
using System.Threading.Tasks;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using VirtualFootTracking.Core;

namespace VirtualFootTracking.SpatialMapping
{
    /// <summary>
    /// Spatial mapping provider using Meta MR Utility Kit (MRUK) Scene API to detect
    /// the physical floor plane and room bounds, with explicit fallback to tracking origin Y.
    /// </summary>
    public class MrukSpatialMappingProvider : MonoBehaviour, ISpatialMappingProvider
    {
        [SerializeField]
        private MRUK _mruk;

        [SerializeField]
        private OVRCameraRig _cameraRig;

        private bool _permissionRequested;
        private bool _isLoading;
        private bool _sceneLoadAttempted;
        private string _statusMessage = "Not initialized";

        public bool IsInitialized { get; private set; }
        public bool IsPermissionGranted => OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene);
        public bool IsSceneLoaded => _mruk != null && _mruk.GetCurrentRoom() != null && _mruk.GetCurrentRoom().FloorAnchors.Count > 0;
        public string StatusMessage => _statusMessage;

        public void Initialize()
        {
            if (_mruk == null)
            {
                _mruk = MRUK.Instance != null ? MRUK.Instance : FindAnyObjectByType<MRUK>();
            }
            if (_cameraRig == null)
            {
                _cameraRig = FindAnyObjectByType<OVRCameraRig>();
            }

            if (_mruk != null)
            {
                _mruk.SceneLoadedEvent.RemoveListener(OnSceneLoaded);
                _mruk.SceneLoadedEvent.AddListener(OnSceneLoaded);
                _mruk.RoomCreatedEvent.RemoveListener(OnRoomCreated);
                _mruk.RoomCreatedEvent.AddListener(OnRoomCreated);
            }

            OVRPermissionsRequester.PermissionGranted -= OnPermissionGranted;
            OVRPermissionsRequester.PermissionGranted += OnPermissionGranted;

            IsInitialized = true;

            if (!IsPermissionGranted)
            {
                RequestPermission();
            }
            else if (!IsSceneLoaded && !_sceneLoadAttempted)
            {
                RequestSceneReload();
            }
            else
            {
                _statusMessage = IsSceneLoaded ? "MRUK floor anchor ready" : "Waiting for MRUK room";
            }
        }

        public void RequestPermission()
        {
            _permissionRequested = true;
            if (!IsPermissionGranted)
            {
                _statusMessage = "Requesting com.oculus.permission.USE_SCENE";
                OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.Scene });
            }
        }

        public void RequestSceneReload()
        {
            if (_mruk == null)
            {
                _statusMessage = "MRUK instance missing";
                return;
            }

            if (!IsPermissionGranted)
            {
                _statusMessage = "USE_SCENE permission not granted";
                return;
            }

            if (!_isLoading)
            {
                _ = LoadFromDeviceAsync();
            }
        }

        public bool TryGetFloorData(out FloorPlaneData floorData)
        {
            MRUKRoom room = _mruk != null ? _mruk.GetCurrentRoom() : null;
            if (room != null && room.FloorAnchors != null && room.FloorAnchors.Count > 0)
            {
                MRUKAnchor bestFloor = SelectBestFloorAnchor(room.FloorAnchors);
                if (bestFloor != null)
                {
                    Transform t = bestFloor.transform;
                    Vector3 center = t.position;
                    Vector2 size = bestFloor.PlaneRect.HasValue ? bestFloor.PlaneRect.Value.size : new Vector2(3f, 3f);

                    Vector3[] polygonWorld = null;
                    if (bestFloor.PlaneBoundary2D != null && bestFloor.PlaneBoundary2D.Count >= 3)
                    {
                        polygonWorld = new Vector3[bestFloor.PlaneBoundary2D.Count];
                        for (int i = 0; i < bestFloor.PlaneBoundary2D.Count; i++)
                        {
                            Vector2 pt = bestFloor.PlaneBoundary2D[i];
                            polygonWorld[i] = t.TransformPoint(new Vector3(pt.x, pt.y, 0f));
                        }
                    }

                    bool hasRoomBounds = room.CeilingAnchors != null && room.CeilingAnchors.Count > 0;
                    Bounds roomBounds = hasRoomBounds
                        ? room.GetRoomBounds()
                        : new Bounds(center + Vector3.up * 1.25f, new Vector3(size.x, 2.5f, size.y));

                    floorData = new FloorPlaneData
                    {
                        IsValid = true,
                        Source = FloorDataSource.MrukSceneApi,
                        WorldHeightY = center.y,
                        CenterWorld = center,
                        NormalWorld = Vector3.up,
                        PlaneSizeMeters = size,
                        BoundaryPolygonWorld = polygonWorld,
                        RoomBoundsWorld = roomBounds,
                        HasRoomBounds = hasRoomBounds,
                        AnchorCount = room.FloorAnchors.Count,
                        Confidence = 0.95f
                    };
                    return true;
                }
            }

            // Explicit fallback to TrackingSpace Y when MRUK scene data or permission is unavailable.
            float fallbackY = _cameraRig != null && _cameraRig.trackingSpace != null
                ? _cameraRig.trackingSpace.position.y
                : 0f;
            Vector3 fallbackCenter = _cameraRig != null && _cameraRig.centerEyeAnchor != null
                ? new Vector3(_cameraRig.centerEyeAnchor.position.x, fallbackY, _cameraRig.centerEyeAnchor.position.z)
                : new Vector3(0f, fallbackY, 0f);

            floorData = FloorPlaneData.CreateFallback(fallbackY, fallbackCenter);
            return false;
        }

        public bool TryQueryFloorHeightAt(Vector3 worldXZ, out float floorY, out FloorDataSource source)
        {
            MRUKRoom room = _mruk != null ? _mruk.GetCurrentRoom() : null;
            if (room != null && room.FloorAnchors != null && room.FloorAnchors.Count > 0)
            {
                float headY = _cameraRig != null && _cameraRig.centerEyeAnchor != null
                    ? _cameraRig.centerEyeAnchor.position.y
                    : room.FloorAnchors[0].transform.position.y + 1.6f;

                Ray ray = new Ray(new Vector3(worldXZ.x, headY + 1.0f, worldXZ.z), Vector3.down);
                LabelFilter floorFilter = new LabelFilter(MRUKAnchor.SceneLabels.FLOOR);
                if (room.Raycast(ray, 5.0f, floorFilter, out RaycastHit hit, out MRUKAnchor _))
                {
                    floorY = hit.point.y;
                    source = FloorDataSource.MrukSceneApi;
                    return true;
                }
            }

            TryGetFloorData(out FloorPlaneData fallback);
            floorY = fallback.WorldHeightY;
            source = fallback.Source;
            return fallback.IsValid;
        }

        public void Shutdown()
        {
            if (_mruk != null)
            {
                _mruk.SceneLoadedEvent.RemoveListener(OnSceneLoaded);
                _mruk.RoomCreatedEvent.RemoveListener(OnRoomCreated);
            }
            OVRPermissionsRequester.PermissionGranted -= OnPermissionGranted;
            IsInitialized = false;
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        private async Task LoadFromDeviceAsync()
        {
            _isLoading = true;
            _sceneLoadAttempted = true;
            _statusMessage = "Loading MRUK scene from device...";

            try
            {
                MRUK.LoadDeviceResult result = await _mruk.LoadSceneFromDevice(
                    requestSceneCaptureIfNoDataFound: false,
                    removeMissingRooms: true);

                _statusMessage = result == MRUK.LoadDeviceResult.Success
                    ? "MRUK scene loaded"
                    : $"MRUK LoadSceneFromDevice: {result} (using fallback floor)";
            }
            catch (Exception ex)
            {
                _statusMessage = $"MRUK load error: {ex.Message}";
                Debug.LogWarning($"[MrukSpatialMappingProvider] {ex}");
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void OnPermissionGranted(string permissionId)
        {
            if (permissionId == OVRPermissionsRequester.ScenePermission)
            {
                _statusMessage = "USE_SCENE permission granted";
                RequestSceneReload();
            }
        }

        private void OnSceneLoaded()
        {
            _statusMessage = IsSceneLoaded ? "MRUK SceneLoaded with floor anchor" : "MRUK SceneLoaded without floor anchor";
        }

        private void OnRoomCreated(MRUKRoom room)
        {
            if (room != null)
            {
                _statusMessage = $"MRUK RoomCreated (Floors={room.FloorAnchors.Count})";
            }
        }

        private static MRUKAnchor SelectBestFloorAnchor(System.Collections.Generic.List<MRUKAnchor> anchors)
        {
            MRUKAnchor best = null;
            float bestArea = -1f;

            for (int i = 0; i < anchors.Count; i++)
            {
                MRUKAnchor a = anchors[i];
                if (a == null)
                    continue;

                float area = a.PlaneRect.HasValue
                    ? a.PlaneRect.Value.width * a.PlaneRect.Value.height
                    : 0f;

                if (best == null || area > bestArea)
                {
                    best = a;
                    bestArea = area;
                }
            }

            return best;
        }
    }
}
