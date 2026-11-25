using System.Collections.Generic;
using UnityEngine;
using VirtualFootTracking.Core;

namespace VirtualFootTracking.Visualization
{
    /// <summary>
    /// Runtime diagnostic visualizer for the 84-joint FullBody skeleton, the 4 virtual trackers,
    /// and the MRUK/fallback floor plane. Differentiates observed vs SDK-estimated vs unavailable data by color.
    /// </summary>
    public class SkeletonAndTrackerVisualizer : MonoBehaviour
    {
        private const int MaxFullBodyJoints = 84;

        private static readonly (int a, int b)[] BoneConnections =
        {
            // Spine & Head
            (1, 2), (2, 3), (3, 4), (4, 5), (5, 6), (6, 7),
            // Left Arm
            (5, 8), (8, 10), (10, 11), (11, 19), (19, 18),
            // Right Arm
            (5, 13), (13, 15), (15, 16), (16, 45), (45, 44),
            // Left Leg (Hip -> UpperLeg -> LowerLeg/Knee -> Ankle -> Ball)
            (1, 70), (70, 71), (71, 73), (73, 76),
            // Right Leg (Hip -> UpperLeg -> LowerLeg/Knee -> Ankle -> Ball)
            (1, 77), (77, 78), (78, 80), (80, 83)
        };

        [Header("Display Options")]
        [SerializeField]
        private bool _showSkeleton = true;

        [SerializeField]
        private bool _showMirrorRig = true;

        [SerializeField]
        private float _mirrorForwardDistanceMeters = 1.35f;

        private Transform _worldRoot;
        private Transform _mirrorRoot;

        private GameObject[] _worldJointSpheres;
        private Material[] _worldJointMaterials;
        private LineRenderer[] _worldBoneLines;

        private GameObject[] _mirrorJointSpheres;
        private Material[] _mirrorJointMaterials;
        private LineRenderer[] _mirrorBoneLines;

        private readonly TrackerVisualRig[] _worldTrackerVisuals = new TrackerVisualRig[4];
        private readonly TrackerVisualRig[] _mirrorTrackerVisuals = new TrackerVisualRig[4];

        private LineRenderer _floorOutline;
        private GameObject _floorQuad;
        private Material _floorQuadMaterial;
        private Vector3 _mirrorOffsetWorld = new Vector3(0f, 0f, 1.35f);
        private bool _mirrorAnchorInitialized;

        private Shader _unlitColorShader;
        private Shader _lineShader;

        private struct TrackerVisualRig
        {
            public GameObject Root;
            public Material BodyMaterial;
            public LineRenderer AxisX;
            public LineRenderer AxisY;
            public LineRenderer AxisZ;
            public LineRenderer FloorDropLine;
        }

        public bool IsMirrorViewEnabled
        {
            get => _showMirrorRig;
            set
            {
                _showMirrorRig = value;
                if (_mirrorRoot != null)
                {
                    _mirrorRoot.gameObject.SetActive(_showMirrorRig);
                }
            }
        }

        public void Initialize()
        {
            _unlitColorShader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            _lineShader = Shader.Find("Sprites/Default") ?? _unlitColorShader;

            _worldRoot = new GameObject("VFT_WorldVisuals").transform;
            _worldRoot.SetParent(transform, false);

            _mirrorRoot = new GameObject("VFT_MirrorVisuals").transform;
            _mirrorRoot.SetParent(transform, false);
            _mirrorRoot.gameObject.SetActive(_showMirrorRig);

            BuildSkeletonPool(_worldRoot, "World", out _worldJointSpheres, out _worldJointMaterials, out _worldBoneLines);
            BuildSkeletonPool(_mirrorRoot, "Mirror", out _mirrorJointSpheres, out _mirrorJointMaterials, out _mirrorBoneLines);

            for (int i = 0; i < 4; i++)
            {
                VirtualTrackerId id = (VirtualTrackerId)i;
                _worldTrackerVisuals[i] = CreateTrackerRig(_worldRoot, $"WorldTracker_{id}");
                _mirrorTrackerVisuals[i] = CreateTrackerRig(_mirrorRoot, $"MirrorTracker_{id}");
            }

            CreateFloorVisuals(_worldRoot);
        }

        public void RecenterMirrorOffset(Transform headTransform)
        {
            if (headTransform == null)
                return;

            Vector3 forwardFlat = Vector3.ProjectOnPlane(headTransform.forward, Vector3.up);
            if (forwardFlat.sqrMagnitude < 0.001f)
            {
                forwardFlat = Vector3.forward;
            }
            forwardFlat.Normalize();
            _mirrorOffsetWorld = forwardFlat * _mirrorForwardDistanceMeters;
            _mirrorAnchorInitialized = true;
        }

        public void UpdateVisuals(in BodySkeletonSnapshot skeleton, in FloorPlaneData floor, in VirtualTrackerFrame frame, Transform headTransform)
        {
            if (_worldRoot == null)
                return;

            if (!_mirrorAnchorInitialized && headTransform != null)
            {
                RecenterMirrorOffset(headTransform);
            }

            UpdateFloorVisuals(floor);

            UpdateSkeletonRig(
                skeleton,
                Vector3.zero,
                _showSkeleton,
                _worldJointSpheres,
                _worldJointMaterials,
                _worldBoneLines);

            UpdateSkeletonRig(
                skeleton,
                _mirrorOffsetWorld,
                _showSkeleton && _showMirrorRig,
                _mirrorJointSpheres,
                _mirrorJointMaterials,
                _mirrorBoneLines);

            for (int i = 0; i < 4; i++)
            {
                VirtualTrackerId id = (VirtualTrackerId)i;
                VirtualTrackerSample sample = frame.GetSample(id);
                UpdateTrackerRig(_worldTrackerVisuals[i], sample, floor.WorldHeightY, Vector3.zero, true);
                UpdateTrackerRig(_mirrorTrackerVisuals[i], sample, floor.WorldHeightY, _mirrorOffsetWorld, _showMirrorRig);
            }
        }

        private void BuildSkeletonPool(
            Transform parent,
            string prefix,
            out GameObject[] spheres,
            out Material[] materials,
            out LineRenderer[] boneLines)
        {
            spheres = new GameObject[MaxFullBodyJoints];
            materials = new Material[MaxFullBodyJoints];

            for (int i = 0; i < MaxFullBodyJoints; i++)
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = $"{prefix}_Joint_{i}";
                sphere.transform.SetParent(parent, false);
                sphere.transform.localScale = Vector3.one * 0.024f;
                DestroyCollider(sphere);

                Material mat = CreateMaterial(Color.gray);
                Renderer rend = sphere.GetComponent<Renderer>();
                if (rend != null)
                {
                    rend.sharedMaterial = mat;
                }

                sphere.SetActive(false);
                spheres[i] = sphere;
                materials[i] = mat;
            }

            boneLines = new LineRenderer[BoneConnections.Length];
            for (int i = 0; i < BoneConnections.Length; i++)
            {
                boneLines[i] = CreateLine(parent, $"{prefix}_Bone_{i}", 0.010f, Color.cyan);
                boneLines[i].enabled = false;
            }
        }

        private TrackerVisualRig CreateTrackerRig(Transform parent, string rigName)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = rigName;
            cube.transform.SetParent(parent, false);
            cube.transform.localScale = new Vector3(0.055f, 0.045f, 0.075f);
            DestroyCollider(cube);

            Material mat = CreateMaterial(Color.red);
            Renderer rend = cube.GetComponent<Renderer>();
            if (rend != null)
            {
                rend.sharedMaterial = mat;
            }

            TrackerVisualRig rig = new TrackerVisualRig
            {
                Root = cube,
                BodyMaterial = mat,
                AxisX = CreateLine(cube.transform, "AxisX", 0.005f, Color.red),
                AxisY = CreateLine(cube.transform, "AxisY", 0.005f, Color.green),
                AxisZ = CreateLine(cube.transform, "AxisZ", 0.005f, Color.blue),
                FloorDropLine = CreateLine(parent, $"{rigName}_FloorDrop", 0.004f, new Color(1f, 0.9f, 0.2f, 0.85f))
            };
            cube.SetActive(false);
            rig.FloorDropLine.enabled = false;
            return rig;
        }

        private void CreateFloorVisuals(Transform parent)
        {
            _floorOutline = CreateLine(parent, "FloorBoundaryOutline", 0.015f, new Color(0.1f, 0.95f, 0.8f, 0.95f));
            _floorOutline.loop = true;

            _floorQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _floorQuad.name = "DetectedFloorPlane";
            _floorQuad.transform.SetParent(parent, false);
            _floorQuad.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            DestroyCollider(_floorQuad);

            Shader transparentShader = Shader.Find("Sprites/Default") ?? _unlitColorShader;
            _floorQuadMaterial = new Material(transparentShader);
            _floorQuadMaterial.color = new Color(0.05f, 0.75f, 0.65f, 0.14f);
            Renderer rend = _floorQuad.GetComponent<Renderer>();
            if (rend != null)
            {
                rend.sharedMaterial = _floorQuadMaterial;
            }
        }

        private void UpdateFloorVisuals(in FloorPlaneData floor)
        {
            if (!floor.IsValid)
            {
                _floorOutline.enabled = false;
                _floorQuad.SetActive(false);
                return;
            }

            bool isMruk = floor.Source == FloorDataSource.MrukSceneApi;
            Color outlineColor = isMruk ? new Color(0.1f, 0.95f, 0.8f, 0.95f) : new Color(1f, 0.7f, 0.1f, 0.9f);
            Color fillColor = isMruk ? new Color(0.05f, 0.75f, 0.65f, 0.14f) : new Color(0.95f, 0.65f, 0.1f, 0.10f);

            _floorOutline.startColor = outlineColor;
            _floorOutline.endColor = outlineColor;
            if (_floorOutline.material != null)
            {
                _floorOutline.material.color = outlineColor;
            }

            if (floor.BoundaryPolygonWorld != null && floor.BoundaryPolygonWorld.Length >= 3)
            {
                _floorOutline.enabled = true;
                _floorOutline.positionCount = floor.BoundaryPolygonWorld.Length;
                for (int i = 0; i < floor.BoundaryPolygonWorld.Length; i++)
                {
                    Vector3 p = floor.BoundaryPolygonWorld[i];
                    p.y += 0.004f;
                    _floorOutline.SetPosition(i, p);
                }
            }
            else
            {
                _floorOutline.enabled = false;
            }

            _floorQuad.SetActive(true);
            _floorQuad.transform.position = new Vector3(floor.CenterWorld.x, floor.WorldHeightY + 0.002f, floor.CenterWorld.z);
            Vector2 sz = floor.PlaneSizeMeters.sqrMagnitude > 0.01f ? floor.PlaneSizeMeters : new Vector2(3f, 3f);
            _floorQuad.transform.localScale = new Vector3(sz.x, sz.y, 1f);
            _floorQuadMaterial.color = fillColor;
        }

        private void UpdateSkeletonRig(
            in BodySkeletonSnapshot skeleton,
            Vector3 worldOffset,
            bool visible,
            GameObject[] spheres,
            Material[] materials,
            LineRenderer[] boneLines)
        {
            if (!visible || !skeleton.IsAvailable || skeleton.Joints == null)
            {
                for (int i = 0; i < spheres.Length; i++)
                {
                    spheres[i].SetActive(false);
                }
                for (int i = 0; i < boneLines.Length; i++)
                {
                    boneLines[i].enabled = false;
                }
                return;
            }

            int count = Mathf.Min(skeleton.Joints.Length, spheres.Length);
            for (int i = 0; i < spheres.Length; i++)
            {
                if (i >= count || !skeleton.Joints[i].PositionValid)
                {
                    spheres[i].SetActive(false);
                    continue;
                }

                BodyJointSample joint = skeleton.Joints[i];
                spheres[i].SetActive(true);
                spheres[i].transform.position = joint.WorldPosition + worldOffset;
                spheres[i].transform.rotation = joint.OrientationValid ? joint.WorldRotation : Quaternion.identity;
                materials[i].color = ColorForSource(joint.DataSource, TrackerValidityState.Valid);
            }

            for (int i = 0; i < BoneConnections.Length; i++)
            {
                (int a, int b) = BoneConnections[i];
                if (a < count && b < count && skeleton.Joints[a].PositionValid && skeleton.Joints[b].PositionValid)
                {
                    boneLines[i].enabled = true;
                    boneLines[i].SetPosition(0, skeleton.Joints[a].WorldPosition + worldOffset);
                    boneLines[i].SetPosition(1, skeleton.Joints[b].WorldPosition + worldOffset);
                    Color c = ColorForSource(skeleton.Joints[b].DataSource, TrackerValidityState.Valid);
                    boneLines[i].startColor = c;
                    boneLines[i].endColor = c;
                }
                else
                {
                    boneLines[i].enabled = false;
                }
            }
        }

        private void UpdateTrackerRig(
            in TrackerVisualRig rig,
            in VirtualTrackerSample sample,
            float floorY,
            Vector3 worldOffset,
            bool visible)
        {
            if (!visible || sample.Validity == TrackerValidityState.Invalid)
            {
                rig.Root.SetActive(false);
                rig.FloorDropLine.enabled = false;
                return;
            }

            Vector3 pos = sample.Position + worldOffset;
            rig.Root.SetActive(true);
            rig.Root.transform.position = pos;
            rig.Root.transform.rotation = sample.Rotation;
            rig.BodyMaterial.color = ColorForSource(sample.Source, sample.Validity);

            const float axisLen = 0.12f;
            SetWorldLine(rig.AxisX, pos, pos + sample.Rotation * (Vector3.right * axisLen));
            SetWorldLine(rig.AxisY, pos, pos + sample.Rotation * (Vector3.up * axisLen));
            SetWorldLine(rig.AxisZ, pos, pos + sample.Rotation * (Vector3.forward * axisLen));

            rig.FloorDropLine.enabled = true;
            SetWorldLine(rig.FloorDropLine, pos, new Vector3(pos.x, floorY, pos.z));
            Color dropColor = sample.HeightAboveFloorMeters < 0f
                ? Color.red
                : (sample.IsGrounded ? Color.green : new Color(1f, 0.9f, 0.2f, 0.85f));
            rig.FloorDropLine.startColor = dropColor;
            rig.FloorDropLine.endColor = dropColor;
        }

        private static void SetWorldLine(LineRenderer lr, Vector3 a, Vector3 b)
        {
            lr.positionCount = 2;
            lr.SetPosition(0, a);
            lr.SetPosition(1, b);
        }

        private static Color ColorForSource(TrackingDataSource source, TrackerValidityState validity)
        {
            if (validity == TrackerValidityState.Invalid || validity == TrackerValidityState.TemporarilyLost)
                return new Color(1f, 0.23f, 0.23f);

            switch (source)
            {
                case TrackingDataSource.SdkObserved:
                    return new Color(0.18f, 0.90f, 0.42f); // Green
                case TrackingDataSource.SdkEstimatedGenerativeLegs:
                    return new Color(0.14f, 0.85f, 1.00f); // Cyan
                case TrackingDataSource.EngineBiomechanicalFallback:
                    return new Color(1.00f, 0.60f, 0.10f); // Orange
                case TrackingDataSource.EngineMotionPredicted:
                    return new Color(0.92f, 0.30f, 1.00f); // Magenta
                default:
                    return new Color(1f, 0.23f, 0.23f);    // Red
            }
        }

        private Material CreateMaterial(Color color)
        {
            Material mat = new Material(_unlitColorShader);
            mat.color = color;
            return mat;
        }

        private LineRenderer CreateLine(Transform parent, string objName, float width, Color color)
        {
            GameObject go = new GameObject(objName);
            go.transform.SetParent(parent, false);
            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.positionCount = 2;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.material = new Material(_lineShader) { color = color };
            lr.startColor = color;
            lr.endColor = color;
            return lr;
        }

        private static void DestroyCollider(GameObject go)
        {
            Collider col = go.GetComponent<Collider>();
            if (col != null)
            {
                Destroy(col);
            }
        }
    }
}
