using UnityEngine;
using VirtualFootTracking.Core;

namespace VirtualFootTracking.Visualization
{
    /// <summary>
    /// Phase 2 visualizer rendering:
    /// 1) Optional raw 84-joint FullBody SDK skeleton (World + Mirror),
    /// 2) Thick solved biomechanical lower-body leg rig (Hip -> Knee -> Ankle -> Foot Sole),
    /// 3) The 4 virtual FBT trackers with local XYZ axes, floor drop lines, and ground-contact rings,
    /// 4) The MRUK or fallback floor plane.
    /// </summary>
    public class SkeletonAndTrackerVisualizer : MonoBehaviour
    {
        private const int MaxFullBodyJoints = 84;
        private const int MaxDepthPoints = 160;
        private const int HipsJointIndex = 1;
        private const int LeftArmUpperIndex = 10;
        private const int RightArmUpperIndex = 15;
        private const int LeftUpperLegIndex = 70;
        private const int RightUpperLegIndex = 77;
        private const int GroundRingSegments = 20;

        private static readonly (int a, int b)[] BoneConnections =
        {
            // Spine & Head
            (1, 2), (2, 3), (3, 4), (4, 5), (5, 6), (6, 7),
            // Left Arm
            (5, 8), (8, 10), (10, 11), (11, 19), (19, 18),
            // Right Arm
            (5, 13), (13, 15), (15, 16), (16, 45), (45, 44),
            // Left Leg (SDK raw)
            (1, 70), (70, 71), (71, 73), (73, 76),
            // Right Leg (SDK raw)
            (1, 77), (77, 78), (78, 80), (80, 83)
        };

        [Header("Display Options")]
        [SerializeField] private bool _showRawSdkSkeleton = true;
        [SerializeField] private bool _showMirrorRig = true;
        [SerializeField] private bool _showDepthPointCloud = true;
        [SerializeField] private float _mirrorForwardDistanceMeters = 1.35f;

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

        private SolvedLegRig _worldSolvedLegs;
        private SolvedLegRig _mirrorSolvedLegs;

        private GameObject[] _worldDepthSpheres;
        private Material[] _worldDepthMaterials;
        private GameObject[] _mirrorDepthSpheres;
        private Material[] _mirrorDepthMaterials;

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

        private struct SolvedLegRig
        {
            public LineRenderer LeftThigh;
            public LineRenderer LeftShin;
            public LineRenderer RightThigh;
            public LineRenderer RightShin;
            public GameObject LeftFootPlate;
            public Material LeftFootPlateMat;
            public GameObject RightFootPlate;
            public Material RightFootPlateMat;
            public LineRenderer LeftContactRing;
            public LineRenderer RightContactRing;
        }

        public bool IsRawSkeletonVisible
        {
            get => _showRawSdkSkeleton;
            set => _showRawSdkSkeleton = value;
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

        public bool IsDepthPointsVisible
        {
            get => _showDepthPointCloud;
            set
            {
                _showDepthPointCloud = value;
                if (!value)
                {
                    HideDepthPool(_worldDepthSpheres);
                    HideDepthPool(_mirrorDepthSpheres);
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

            BuildDepthPool(_worldRoot, "WorldDepth", out _worldDepthSpheres, out _worldDepthMaterials);
            BuildDepthPool(_mirrorRoot, "MirrorDepth", out _mirrorDepthSpheres, out _mirrorDepthMaterials);

            for (int i = 0; i < 4; i++)
            {
                VirtualTrackerId id = (VirtualTrackerId)i;
                _worldTrackerVisuals[i] = CreateTrackerRig(_worldRoot, $"WorldTracker_{id}");
                _mirrorTrackerVisuals[i] = CreateTrackerRig(_mirrorRoot, $"MirrorTracker_{id}");
            }

            _worldSolvedLegs = CreateSolvedLegRig(_worldRoot, "WorldSolved");
            _mirrorSolvedLegs = CreateSolvedLegRig(_mirrorRoot, "MirrorSolved");

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
                _showRawSdkSkeleton,
                _worldJointSpheres,
                _worldJointMaterials,
                _worldBoneLines);

            UpdateSkeletonRig(
                skeleton,
                _mirrorOffsetWorld,
                _showRawSdkSkeleton && _showMirrorRig,
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

            UpdateSolvedLegRig(_worldSolvedLegs, skeleton, frame, floor.WorldHeightY, Vector3.zero, true);
            UpdateSolvedLegRig(_mirrorSolvedLegs, skeleton, frame, floor.WorldHeightY, _mirrorOffsetWorld, _showMirrorRig);
        }

        public void UpdateDepthVisuals(in DepthObservationFrame depthFrame)
        {
            if (_worldDepthSpheres == null || _mirrorDepthSpheres == null)
                return;

            if (!_showDepthPointCloud || !depthFrame.IsReady || depthFrame.ForegroundPoints == null || depthFrame.ForegroundPointCount <= 0)
            {
                HideDepthPool(_worldDepthSpheres);
                HideDepthPool(_mirrorDepthSpheres);
                return;
            }

            int count = Mathf.Min(depthFrame.ForegroundPointCount, MaxDepthPoints);
            Color leftColor = new Color(1.00f, 0.88f, 0.14f);  // Bright Yellow
            Color rightColor = new Color(0.78f, 1.00f, 0.18f); // Lime-Yellow
            Color neutralColor = new Color(1.00f, 0.65f, 0.18f); // Warm Amber

            for (int i = 0; i < MaxDepthPoints; i++)
            {
                if (i < count)
                {
                    DepthForegroundPoint pt = depthFrame.ForegroundPoints[i];
                    Color c = pt.AssignedSide < 0 ? leftColor : (pt.AssignedSide > 0 ? rightColor : neutralColor);

                    _worldDepthSpheres[i].SetActive(true);
                    _worldDepthSpheres[i].transform.position = pt.WorldPosition;
                    _worldDepthMaterials[i].color = c;

                    if (_showMirrorRig)
                    {
                        _mirrorDepthSpheres[i].SetActive(true);
                        _mirrorDepthSpheres[i].transform.position = pt.WorldPosition + _mirrorOffsetWorld;
                        _mirrorDepthMaterials[i].color = c;
                    }
                    else
                    {
                        _mirrorDepthSpheres[i].SetActive(false);
                    }
                }
                else
                {
                    _worldDepthSpheres[i].SetActive(false);
                    _mirrorDepthSpheres[i].SetActive(false);
                }
            }
        }

        private void BuildDepthPool(
            Transform parent,
            string prefix,
            out GameObject[] spheres,
            out Material[] materials)
        {
            spheres = new GameObject[MaxDepthPoints];
            materials = new Material[MaxDepthPoints];

            for (int i = 0; i < MaxDepthPoints; i++)
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = $"{prefix}_{i}";
                sphere.transform.SetParent(parent, false);
                sphere.transform.localScale = Vector3.one * 0.022f;
                DestroyCollider(sphere);

                Material mat = CreateMaterial(new Color(1.00f, 0.88f, 0.14f));
                Renderer rend = sphere.GetComponent<Renderer>();
                if (rend != null)
                {
                    rend.sharedMaterial = mat;
                }

                sphere.SetActive(false);
                spheres[i] = sphere;
                materials[i] = mat;
            }
        }

        private static void HideDepthPool(GameObject[] spheres)
        {
            if (spheres == null)
                return;

            for (int i = 0; i < spheres.Length; i++)
            {
                if (spheres[i] != null && spheres[i].activeSelf)
                {
                    spheres[i].SetActive(false);
                }
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
                sphere.transform.localScale = Vector3.one * 0.018f;
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
                boneLines[i] = CreateLine(parent, $"{prefix}_Bone_{i}", 0.007f, Color.cyan);
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

        private SolvedLegRig CreateSolvedLegRig(Transform parent, string prefix)
        {
            SolvedLegRig rig = new SolvedLegRig
            {
                LeftThigh = CreateLine(parent, $"{prefix}_LThigh", 0.022f, Color.cyan),
                LeftShin = CreateLine(parent, $"{prefix}_LShin", 0.018f, Color.cyan),
                RightThigh = CreateLine(parent, $"{prefix}_RThigh", 0.022f, Color.cyan),
                RightShin = CreateLine(parent, $"{prefix}_RShin", 0.018f, Color.cyan),
                LeftFootPlate = CreateFootPlate(parent, $"{prefix}_LFootPlate", out Material lfMat),
                LeftFootPlateMat = lfMat,
                RightFootPlate = CreateFootPlate(parent, $"{prefix}_RFootPlate", out Material rfMat),
                RightFootPlateMat = rfMat,
                LeftContactRing = CreateRing(parent, $"{prefix}_LContactRing", 0.008f, Color.green),
                RightContactRing = CreateRing(parent, $"{prefix}_RContactRing", 0.008f, Color.green)
            };
            rig.LeftThigh.enabled = false;
            rig.LeftShin.enabled = false;
            rig.RightThigh.enabled = false;
            rig.RightShin.enabled = false;
            rig.LeftFootPlate.SetActive(false);
            rig.RightFootPlate.SetActive(false);
            rig.LeftContactRing.enabled = false;
            rig.RightContactRing.enabled = false;
            return rig;
        }

        private GameObject CreateFootPlate(Transform parent, string name, out Material mat)
        {
            GameObject plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plate.name = name;
            plate.transform.SetParent(parent, false);
            plate.transform.localScale = new Vector3(0.085f, 0.022f, 0.21f);
            DestroyCollider(plate);

            mat = CreateMaterial(Color.cyan);
            Renderer r = plate.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = mat;
            return plate;
        }

        private LineRenderer CreateRing(Transform parent, string name, float width, Color color)
        {
            LineRenderer lr = CreateLine(parent, name, width, color);
            lr.loop = true;
            lr.positionCount = GroundRingSegments;
            return lr;
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
                    spheres[i].SetActive(false);
                for (int i = 0; i < boneLines.Length; i++)
                    boneLines[i].enabled = false;
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

        private void UpdateSolvedLegRig(
            in SolvedLegRig rig,
            in BodySkeletonSnapshot skeleton,
            in VirtualTrackerFrame frame,
            float floorY,
            Vector3 worldOffset,
            bool visible)
        {
            if (!visible)
            {
                rig.LeftThigh.enabled = false;
                rig.LeftShin.enabled = false;
                rig.RightThigh.enabled = false;
                rig.RightShin.enabled = false;
                rig.LeftFootPlate.SetActive(false);
                rig.RightFootPlate.SetActive(false);
                rig.LeftContactRing.enabled = false;
                rig.RightContactRing.enabled = false;
                return;
            }

            Vector3 leftHip = ResolveHipAnchor(skeleton, LeftUpperLegIndex, -0.10f);
            Vector3 rightHip = ResolveHipAnchor(skeleton, RightUpperLegIndex, 0.10f);

            UpdateSingleSolvedLeg(
                leftHip,
                frame.LeftKnee,
                frame.LeftFoot,
                floorY,
                worldOffset,
                rig.LeftThigh,
                rig.LeftShin,
                rig.LeftFootPlate,
                rig.LeftFootPlateMat,
                rig.LeftContactRing);

            UpdateSingleSolvedLeg(
                rightHip,
                frame.RightKnee,
                frame.RightFoot,
                floorY,
                worldOffset,
                rig.RightThigh,
                rig.RightShin,
                rig.RightFootPlate,
                rig.RightFootPlateMat,
                rig.RightContactRing);
        }

        private static Vector3 ResolveHipAnchor(in BodySkeletonSnapshot skeleton, int upperLegIdx, float sideOffset)
        {
            if (skeleton.IsAvailable)
            {
                if (skeleton.TryGetJoint(upperLegIdx, out BodyJointSample ul) && ul.PositionValid)
                    return ul.WorldPosition;

                if (skeleton.TryGetJoint(HipsJointIndex, out BodyJointSample hips) && hips.PositionValid)
                {
                    Vector3 right = Vector3.zero;
                    if (skeleton.TryGetJoint(LeftArmUpperIndex, out BodyJointSample lArm) && lArm.PositionValid &&
                        skeleton.TryGetJoint(RightArmUpperIndex, out BodyJointSample rArm) && rArm.PositionValid)
                    {
                        Vector3 span = Vector3.ProjectOnPlane(rArm.WorldPosition - lArm.WorldPosition, Vector3.up);
                        if (span.sqrMagnitude > 0.08f * 0.08f)
                            right = span.normalized;
                    }
                    if (right.sqrMagnitude < 0.001f && Camera.main != null)
                    {
                        Vector3 camRight = Vector3.ProjectOnPlane(Camera.main.transform.right, Vector3.up);
                        if (camRight.sqrMagnitude > 0.001f)
                            right = camRight.normalized;
                    }
                    if (right.sqrMagnitude < 0.001f)
                        right = Vector3.right;

                    return hips.WorldPosition + right * sideOffset;
                }
            }
            return Vector3.zero;
        }

        private static void UpdateSingleSolvedLeg(
            Vector3 hipWorld,
            in VirtualTrackerSample knee,
            in VirtualTrackerSample foot,
            float floorY,
            Vector3 worldOffset,
            LineRenderer thighLine,
            LineRenderer shinLine,
            GameObject footPlate,
            Material footPlateMat,
            LineRenderer contactRing)
        {
            bool validLeg = hipWorld.sqrMagnitude > 0.0001f &&
                            knee.Validity != TrackerValidityState.Invalid &&
                            foot.Validity != TrackerValidityState.Invalid;

            if (validLeg)
            {
                Vector3 h = hipWorld + worldOffset;
                Vector3 k = knee.Position + worldOffset;
                Vector3 f = foot.Position + worldOffset;
                Color legColor = ColorForSource(foot.Source, foot.Validity);

                thighLine.enabled = true;
                SetWorldLine(thighLine, h, k);
                thighLine.startColor = legColor;
                thighLine.endColor = legColor;

                shinLine.enabled = true;
                SetWorldLine(shinLine, k, f);
                shinLine.startColor = legColor;
                shinLine.endColor = legColor;
            }
            else
            {
                thighLine.enabled = false;
                shinLine.enabled = false;
            }

            if (foot.Validity != TrackerValidityState.Invalid)
            {
                footPlate.SetActive(true);
                Vector3 footPos = foot.Position + worldOffset;
                footPlate.transform.rotation = foot.Rotation;
                // Offset plate slightly downward/forward relative to ankle so it depicts a real shoe sole.
                footPlate.transform.position = footPos + foot.Rotation * new Vector3(0f, -0.045f, 0.055f);
                footPlateMat.color = ColorForSource(foot.Source, foot.Validity);

                if (foot.IsGrounded)
                {
                    contactRing.enabled = true;
                    Color ringColor = foot.IsFootLocked ? new Color(0.20f, 0.96f, 0.45f) : new Color(1.0f, 0.85f, 0.20f);
                    contactRing.startColor = ringColor;
                    contactRing.endColor = ringColor;
                    Vector3 center = new Vector3(footPos.x, floorY + 0.006f, footPos.z);
                    float radius = 0.11f;
                    for (int i = 0; i < GroundRingSegments; i++)
                    {
                        float angle = (i / (float)GroundRingSegments) * Mathf.PI * 2f;
                        contactRing.SetPosition(i, center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius));
                    }
                }
                else
                {
                    contactRing.enabled = false;
                }
            }
            else
            {
                footPlate.SetActive(false);
                contactRing.enabled = false;
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
            if (validity == TrackerValidityState.Invalid)
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
                case TrackingDataSource.DepthObserved:
                    return new Color(1.00f, 0.88f, 0.14f); // Bright Gold-Yellow
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
