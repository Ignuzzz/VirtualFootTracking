using System;
using System.Collections.Generic;
using Meta.XR;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using VirtualFootTracking.Core;

namespace VirtualFootTracking.SpatialMapping
{
    /// <summary>
    /// Uses Meta's real-time Depth API (EnvironmentRaycastManager) to detect moving
    /// lower-body limbs (kicks, lifted feet, raised knees) inside the forward camera frustum
    /// after subtracting floor, upper body/arms/controllers, MRUK room/surface shells,
    /// planar vertical walls, continuous mid-range static voxels, and optional manual baseline voxels.
    /// </summary>
    public class EnvironmentDepthLegDetector : MonoBehaviour
    {
        private const int MaxBufferPoints = 160;
        private const int MaxAutoStaticVoxels = 4096;

        // Verified OVRPlugin.BoneId indices (Meta XR Core SDK 209.0.0)
        private const int HipsJointIndex = 1;
        private const int HeadJointIndex = 7;
        private const int LeftShoulderIndex = 8;
        private const int LeftArmUpperIndex = 10;
        private const int LeftArmLowerIndex = 11;
        private const int RightShoulderIndex = 13;
        private const int RightArmUpperIndex = 15;
        private const int RightArmLowerIndex = 16;
        private const int LeftHandPalmIndex = 18;
        private const int LeftHandWristIndex = 19;
        private const int RightHandPalmIndex = 44;
        private const int RightHandWristIndex = 45;
        private const int LeftUpperLegIndex = 70;
        private const int RightUpperLegIndex = 77;

        private const MRUKAnchor.SceneLabels FurnitureLabelsMask =
            MRUKAnchor.SceneLabels.TABLE |
            MRUKAnchor.SceneLabels.COUCH |
            MRUKAnchor.SceneLabels.BED |
            MRUKAnchor.SceneLabels.STORAGE |
            MRUKAnchor.SceneLabels.SCREEN |
            MRUKAnchor.SceneLabels.LAMP |
            MRUKAnchor.SceneLabels.PLANT;

        [Header("Scene References")]
        [SerializeField] private EnvironmentRaycastManager _raycastManager;
        [SerializeField] private OVRCameraRig _cameraRig;
        [SerializeField] private MRUK _mruk;

        [Header("Pass 1 Coarse Grid (63 rays max)")]
        [SerializeField] private int _gridColumns = 9;
        [SerializeField] private int _gridRows = 7;
        [SerializeField] private float _yawHalfAngleDeg = 36f;
        [SerializeField] private float _pitchMinDeg = -54f;
        [SerializeField] private float _pitchMaxDeg = 4f;
        [SerializeField] private float _maxRayDistanceMeters = 2.8f;

        [Header("Pass 2 Fine Leg Probe")]
        [SerializeField] private bool _enableFineLegProbe = true;
        [SerializeField] private int _fineProbeColumns = 3;
        [SerializeField] private int _fineProbeRows = 9;

        [Header("Foreground Filtering")]
        [SerializeField] private bool _enabled = true;
        [SerializeField] private float _floorClearanceMeters = 0.10f;
        [SerializeField] private float _pelvisUpperMarginMeters = 0.14f;
        [SerializeField] private float _minDistFromPelvisMeters = 0.20f;
        [SerializeField] private float _handControllerExclusionRadius = 0.17f;
        [SerializeField] private float _forearmExclusionRadius = 0.14f;
        [SerializeField] private float _staticVoxelSizeMeters = 0.11f;
        [SerializeField] private int _minClusterPoints = 3;
        [SerializeField] private float _surfaceToBoneInsetMeters = 0.04f;

        private readonly DepthForegroundPoint[] _foregroundBuffer = new DepthForegroundPoint[MaxBufferPoints];
        private readonly Vector3[] _leftCandidates = new Vector3[MaxBufferPoints];
        private readonly Vector3[] _leftNormals = new Vector3[MaxBufferPoints];
        private readonly Vector3[] _rightCandidates = new Vector3[MaxBufferPoints];
        private readonly Vector3[] _rightNormals = new Vector3[MaxBufferPoints];
        private readonly Vector3[] _inlierBuffer = new Vector3[MaxBufferPoints];
        private readonly Vector3[] _inlierNormals = new Vector3[MaxBufferPoints];
        private readonly float[] _latSortBuffer = new float[MaxBufferPoints];
        private readonly HashSet<Vector3Int> _staticBackgroundVoxels = new HashSet<Vector3Int>(512);
        private readonly HashSet<Vector3Int> _autoStaticVoxels = new HashSet<Vector3Int>(1024);

        private float _backgroundCaptureRemaining;
        private float _backgroundCaptureTotal = 1.5f;
        private bool _hasPrevLeft;
        private Vector3 _prevLeftFoot;
        private bool _hasPrevRight;
        private Vector3 _prevRightFoot;
        private bool _hasPrevLeftRayHint;
        private Vector3 _prevLeftProbeTarget;
        private bool _hasPrevRightRayHint;
        private Vector3 _prevRightProbeTarget;
        private bool _initialized;

        private struct ScanFilterContext
        {
            public Vector3 HeadPos;
            public Vector3 PelvisPos;
            public Vector3 PelvisForwardFlat;
            public Vector3 PelvisRightFlat;
            public float FloorY;
            public float MaxLegReach;
            public float InvVoxel;
            public bool HasLeftHand;
            public Vector3 LeftHandPos;
            public bool HasRightHand;
            public Vector3 RightHandPos;
            public bool HasLArmUp;
            public Vector3 LArmUp;
            public Vector3 LArmLow;
            public Vector3 LWrist;
            public bool HasRArmUp;
            public Vector3 RArmUp;
            public Vector3 RArmLow;
            public Vector3 RWrist;
            public float HandExclSqr;
            public float ArmExclSqr;
            public MRUKRoom Room;
        }

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public bool IsCapturingBackground => _backgroundCaptureRemaining > 0f;
        public int StaticBackgroundVoxelCount => _staticBackgroundVoxels.Count + _autoStaticVoxels.Count;

        public void Initialize(OVRCameraRig cameraRig, MRUK mruk)
        {
            if (cameraRig != null)
                _cameraRig = cameraRig;
            else if (_cameraRig == null)
                _cameraRig = FindAnyObjectByType<OVRCameraRig>();

            if (mruk != null)
                _mruk = mruk;
            else if (_mruk == null)
                _mruk = MRUK.Instance != null ? MRUK.Instance : FindAnyObjectByType<MRUK>();

            if (_raycastManager == null)
                _raycastManager = FindAnyObjectByType<EnvironmentRaycastManager>(FindObjectsInactive.Include);

            if (_raycastManager == null)
            {
                GameObject rayGo = new GameObject("MetaEnvironmentRaycast");
                rayGo.transform.SetParent(transform.parent, false);
                _raycastManager = rayGo.AddComponent<EnvironmentRaycastManager>();
            }
            else if (!_raycastManager.enabled)
            {
                _raycastManager.enabled = true;
            }

            _initialized = true;
        }

        public void StartBackgroundCapture(float durationSeconds = 1.5f)
        {
            _staticBackgroundVoxels.Clear();
            _autoStaticVoxels.Clear();
            _backgroundCaptureTotal = Mathf.Max(0.4f, durationSeconds);
            _backgroundCaptureRemaining = _backgroundCaptureTotal;
        }

        public void ClearBackgroundBaseline()
        {
            _staticBackgroundVoxels.Clear();
            _autoStaticVoxels.Clear();
            _backgroundCaptureRemaining = 0f;
        }

        public DepthObservationFrame ScanFrame(
            in BodySkeletonSnapshot skeleton,
            in FloorPlaneData floor,
            CalibrationProfile profile,
            float deltaTime)
        {
            if (!_initialized)
            {
                Initialize(_cameraRig, _mruk);
            }

            bool supported = EnvironmentRaycastManager.IsSupported;
            int cols = Mathf.Clamp(_gridColumns, 3, 16);
            int rows = Mathf.Clamp(_gridRows, 3, 12);

            DepthObservationFrame frame = new DepthObservationFrame
            {
                IsSupported = supported,
                IsReady = false,
                IsEnabled = _enabled,
                IsCapturingBackground = IsCapturingBackground,
                BackgroundCaptureProgress01 = IsCapturingBackground
                    ? 1f - Mathf.Clamp01(_backgroundCaptureRemaining / Mathf.Max(0.01f, _backgroundCaptureTotal))
                    : (StaticBackgroundVoxelCount > 0 ? 1f : 0f),
                StaticBackgroundVoxelCount = StaticBackgroundVoxelCount,
                TotalRaysCast = 0,
                RawHitCount = 0,
                ForegroundPointCount = 0,
                ForegroundPoints = _foregroundBuffer,
                StatusMessage = string.Empty
            };

            if (!supported)
            {
                _hasPrevLeft = false;
                _hasPrevRight = false;
                frame.StatusMessage = "Depth API unsupported on this runtime";
                return frame;
            }

            if (!_enabled || _raycastManager == null || !_raycastManager.enabled || !_raycastManager.gameObject.activeInHierarchy)
            {
                _hasPrevLeft = false;
                _hasPrevRight = false;
                frame.StatusMessage = _enabled ? "EnvironmentRaycastManager inactive" : "Depth detector disabled";
                return frame;
            }

            Transform headTransform = ResolveHeadTransform();
            if (headTransform == null)
            {
                frame.StatusMessage = "Head transform missing";
                return frame;
            }

            CalibrationProfile cfg = profile ?? new CalibrationProfile();
            float floorY = floor.IsValid ? floor.WorldHeightY : 0f;
            Vector3 headPos = headTransform.position;
            Quaternion headRot = headTransform.rotation;

            ResolvePelvisAndHips(
                skeleton,
                headTransform,
                floorY,
                cfg,
                out Vector3 pelvisPos,
                out Vector3 pelvisForwardFlat,
                out Vector3 pelvisRightFlat,
                out Vector3 leftHipPos,
                out Vector3 rightHipPos);

            float thighLen = Mathf.Max(0.22f, cfg.ThighLengthMeters);
            float shinLen = Mathf.Max(0.22f, cfg.ShinLengthMeters);
            float ankleHeight = Mathf.Max(0.03f, cfg.AnkleToFloorMeters);
            float maxLegReach = thighLen + shinLen + 0.22f;

            // Background baseline calibration mode
            if (_backgroundCaptureRemaining > 0f)
            {
                bool readyThisFrame = RunBackgroundCaptureStep(
                    headPos,
                    headRot,
                    pelvisPos,
                    floorY,
                    cols,
                    rows,
                    deltaTime,
                    ref frame);

                if (readyThisFrame)
                {
                    frame.StatusMessage = _backgroundCaptureRemaining > 0f
                        ? $"Capturando fundo statico ({frame.BackgroundCaptureProgress01 * 100f:F0}%)..."
                        : $"Fundo capturado ({StaticBackgroundVoxelCount} voxels)";
                }
                else
                {
                    frame.StatusMessage = "Aguardando Depth Raycaster inicializar...";
                }
                return frame;
            }

            // Collect arm/hand/controller exclusion segments
            ExtractExclusionGeometry(
                skeleton,
                out bool hasLeftHand, out Vector3 leftHandPos,
                out bool hasRightHand, out Vector3 rightHandPos,
                out bool hasLArmUp, out Vector3 lArmUp, out Vector3 lArmLow, out Vector3 lWrist,
                out bool hasRArmUp, out Vector3 rArmUp, out Vector3 rArmLow, out Vector3 rWrist);

            MRUKRoom room = _mruk != null ? _mruk.GetCurrentRoom() : null;
            ScanFilterContext ctx = new ScanFilterContext
            {
                HeadPos = headPos,
                PelvisPos = pelvisPos,
                PelvisForwardFlat = pelvisForwardFlat,
                PelvisRightFlat = pelvisRightFlat,
                FloorY = floorY,
                MaxLegReach = maxLegReach,
                InvVoxel = 1f / Mathf.Max(0.04f, _staticVoxelSizeMeters),
                HasLeftHand = hasLeftHand,
                LeftHandPos = leftHandPos,
                HasRightHand = hasRightHand,
                RightHandPos = rightHandPos,
                HasLArmUp = hasLArmUp,
                LArmUp = lArmUp,
                LArmLow = lArmLow,
                LWrist = lWrist,
                HasRArmUp = hasRArmUp,
                RArmUp = rArmUp,
                RArmLow = rArmLow,
                RWrist = rWrist,
                HandExclSqr = _handControllerExclusionRadius * _handControllerExclusionRadius,
                ArmExclSqr = _forearmExclusionRadius * _forearmExclusionRadius,
                Room = room
            };

            int fgCount = 0;
            int leftCount = 0;
            int rightCount = 0;
            bool abortedNotReady = false;

            // Pass 1: Coarse full-frustum scan
            for (int r = 0; r < rows && !abortedNotReady; r++)
            {
                float pitchUpDeg = Mathf.Lerp(_pitchMinDeg, _pitchMaxDeg, rows > 1 ? r / (float)(rows - 1) : 0.5f);
                for (int c = 0; c < cols; c++)
                {
                    float yawDeg = Mathf.Lerp(-_yawHalfAngleDeg, _yawHalfAngleDeg, cols > 1 ? c / (float)(cols - 1) : 0.5f);
                    // Unity Euler(+x, y, 0) tilts downward for +x, upward for -x.
                    Vector3 rayDir = headRot * (Quaternion.Euler(-pitchUpDeg, yawDeg, 0f) * Vector3.forward);
                    if (rayDir.y > 0.25f)
                        continue;

                    if (!EvaluateSingleRay(rayDir, ctx, ref frame, ref fgCount, ref leftCount, ref rightCount))
                    {
                        abortedNotReady = true;
                        break;
                    }
                }
            }

            // Pass 2: Fine vertical-dense leg probe centered on detected or previously tracked legs
            if (_enableFineLegProbe && !abortedNotReady && frame.IsReady)
            {
                bool hasLeftSeed = TryGetProbeSeed(_leftCandidates, leftCount, _hasPrevLeftRayHint, _prevLeftProbeTarget, out Vector3 leftSeed);
                bool hasRightSeed = TryGetProbeSeed(_rightCandidates, rightCount, _hasPrevRightRayHint, _prevRightProbeTarget, out Vector3 rightSeed);

                if (hasLeftSeed)
                {
                    if (!RunFineLegProbe(leftSeed, headRot, ctx, ref frame, ref fgCount, ref leftCount, ref rightCount))
                    {
                        abortedNotReady = true;
                    }
                }

                if (!abortedNotReady && hasRightSeed)
                {
                    if (!RunFineLegProbe(rightSeed, headRot, ctx, ref frame, ref fgCount, ref leftCount, ref rightCount))
                    {
                        abortedNotReady = true;
                    }
                }
            }

            if (abortedNotReady)
            {
                frame.StatusMessage = frame.IsSupported
                    ? "Depth Raycaster inicializando..."
                    : "Depth Raycaster nao suportado";
                return frame;
            }

            frame.StaticBackgroundVoxelCount = StaticBackgroundVoxelCount;
            frame.ForegroundPointCount = fgCount;

            frame.LeftLeg = BuildLegObservation(
                _leftCandidates,
                _leftNormals,
                leftCount,
                headPos,
                pelvisPos,
                leftHipPos,
                pelvisForwardFlat,
                pelvisRightFlat,
                floorY,
                thighLen,
                shinLen,
                ankleHeight,
                ctx.InvVoxel,
                ref _hasPrevLeft,
                ref _prevLeftFoot);

            frame.RightLeg = BuildLegObservation(
                _rightCandidates,
                _rightNormals,
                rightCount,
                headPos,
                pelvisPos,
                rightHipPos,
                pelvisForwardFlat,
                pelvisRightFlat,
                floorY,
                thighLen,
                shinLen,
                ankleHeight,
                ctx.InvVoxel,
                ref _hasPrevRight,
                ref _prevRightFoot);

            if (frame.LeftLeg.IsDetected)
            {
                _hasPrevLeftRayHint = true;
                _prevLeftProbeTarget = frame.LeftLeg.HasKneeHint && !frame.LeftLeg.IsKneeDominant
                    ? Vector3.Lerp(frame.LeftLeg.KneeWorldPosition, frame.LeftLeg.FootWorldPosition, 0.5f)
                    : frame.LeftLeg.FootWorldPosition;
            }
            else
            {
                _hasPrevLeftRayHint = false;
            }

            if (frame.RightLeg.IsDetected)
            {
                _hasPrevRightRayHint = true;
                _prevRightProbeTarget = frame.RightLeg.HasKneeHint && !frame.RightLeg.IsKneeDominant
                    ? Vector3.Lerp(frame.RightLeg.KneeWorldPosition, frame.RightLeg.FootWorldPosition, 0.5f)
                    : frame.RightLeg.FootWorldPosition;
            }
            else
            {
                _hasPrevRightRayHint = false;
            }

            frame.StaticBackgroundVoxelCount = StaticBackgroundVoxelCount;

            if (!frame.IsReady)
            {
                frame.StatusMessage = "Depth Raycaster sem resposta";
            }
            else if (frame.LeftLeg.IsDetected || frame.RightLeg.IsDetected)
            {
                string lTag = frame.LeftLeg.IsDetected ? (frame.LeftLeg.IsKneeDominant ? "L:JOELHO" : "L:PE") : "L:-";
                string rTag = frame.RightLeg.IsDetected ? (frame.RightLeg.IsKneeDominant ? "R:JOELHO" : "R:PE") : "R:-";
                frame.StatusMessage = $"Depth 3D ativo [{lTag} | {rTag}] ({fgCount} pts)";
            }
            else
            {
                frame.StatusMessage = $"Depth pronto ({fgCount} pts | autoBg={StaticBackgroundVoxelCount})";
            }

            return frame;
        }

        private bool RunBackgroundCaptureStep(
            Vector3 headPos,
            Quaternion headRot,
            Vector3 pelvisPos,
            float floorY,
            int cols,
            int rows,
            float deltaTime,
            ref DepthObservationFrame frame)
        {
            float invVoxel = 1f / Mathf.Max(0.04f, _staticVoxelSizeMeters);
            bool anyReady = false;
            bool abortedNotReady = false;

            for (int r = 0; r < rows && !abortedNotReady; r++)
            {
                float pitchUpDeg = Mathf.Lerp(_pitchMinDeg, _pitchMaxDeg, rows > 1 ? r / (float)(rows - 1) : 0.5f);
                for (int c = 0; c < cols; c++)
                {
                    float yawDeg = Mathf.Lerp(-_yawHalfAngleDeg, _yawHalfAngleDeg, cols > 1 ? c / (float)(cols - 1) : 0.5f);
                    Vector3 rayDir = headRot * (Quaternion.Euler(-pitchUpDeg, yawDeg, 0f) * Vector3.forward);
                    if (rayDir.y > 0.25f)
                        continue;

                    frame.TotalRaysCast++;
                    Ray ray = new Ray(headPos, rayDir);
                    bool didHit = _raycastManager.Raycast(ray, out EnvironmentRaycastHit hit, _maxRayDistanceMeters);

                    if (hit.status == EnvironmentRaycastHitStatus.NotReady)
                    {
                        abortedNotReady = true;
                        break;
                    }
                    if (hit.status == EnvironmentRaycastHitStatus.NotSupported)
                    {
                        frame.IsSupported = false;
                        abortedNotReady = true;
                        _backgroundCaptureRemaining = 0f;
                        break;
                    }

                    anyReady = true;
                    frame.IsReady = true;

                    if (!didHit)
                        continue;

                    frame.RawHitCount++;
                    Vector3 pt = hit.point;
                    if (pt.y <= floorY + _floorClearanceMeters)
                        continue;

                    // Do not bake the user's own standing cylinder into static room voxels.
                    float dx = pt.x - pelvisPos.x;
                    float dz = pt.z - pelvisPos.z;
                    if (dx * dx + dz * dz <= 0.36f * 0.36f && pt.y <= pelvisPos.y + 0.35f)
                        continue;

                    _staticBackgroundVoxels.Add(ToVoxel(pt, invVoxel));
                }
            }

            if (anyReady && _backgroundCaptureRemaining > 0f)
            {
                _backgroundCaptureRemaining = Mathf.Max(0f, _backgroundCaptureRemaining - Mathf.Max(deltaTime, 0f));
            }

            frame.IsCapturingBackground = _backgroundCaptureRemaining > 0f;
            frame.BackgroundCaptureProgress01 = _backgroundCaptureRemaining > 0f
                ? 1f - Mathf.Clamp01(_backgroundCaptureRemaining / Mathf.Max(0.01f, _backgroundCaptureTotal))
                : 1f;
            frame.StaticBackgroundVoxelCount = StaticBackgroundVoxelCount;
            return anyReady;
        }

        private sbyte AssignSide(Vector3 pt, float lateral)
        {
            sbyte anatomicalSide = lateral < 0f ? (sbyte)-1 : (sbyte)1;

            // Only use temporal tie-breaking very near the sagittal midline so Left vs Right stays accurate.
            if (Mathf.Abs(lateral) < 0.09f && _hasPrevLeft && _hasPrevRight)
            {
                float dL = Vector3.Distance(pt, _prevLeftFoot);
                float dR = Vector3.Distance(pt, _prevRightFoot);
                if (dL < 0.24f && dL + 0.06f < dR)
                    return -1;
                if (dR < 0.24f && dR + 0.06f < dL)
                    return 1;
            }

            return anatomicalSide;
        }

        private DepthLegObservation BuildLegObservation(
            Vector3[] candidates,
            Vector3[] normals,
            int count,
            Vector3 headPos,
            Vector3 pelvisPos,
            Vector3 hipPos,
            Vector3 pelvisForwardFlat,
            Vector3 pelvisRightFlat,
            float floorY,
            float thighLen,
            float shinLen,
            float ankleHeight,
            float invVoxel,
            ref bool hasPrev,
            ref Vector3 prevAnchor)
        {
            if (count < _minClusterPoints)
            {
                hasPrev = false;
                return default;
            }

            // Reject extreme lateral reach (> 0.48 m from this hip socket) before clustering
            int hipValidCount = 0;
            for (int i = 0; i < count; i++)
            {
                float latHip = Vector3.Dot(candidates[i] - hipPos, pelvisRightFlat);
                if (Mathf.Abs(latHip) <= 0.48f)
                {
                    _latSortBuffer[hipValidCount] = latHip;
                    _inlierBuffer[hipValidCount] = candidates[i];
                    _inlierNormals[hipValidCount] = normals[i];
                    hipValidCount++;
                }
            }

            if (hipValidCount < _minClusterPoints)
            {
                hasPrev = false;
                return default;
            }

            // Compute median lateral coordinate in the leg's sagittal plane (preserves full extended shin + foot!)
            Array.Sort(_latSortBuffer, 0, hipValidCount);
            float medianLat = _latSortBuffer[hipValidCount / 2];

            int inlierCount = 0;
            float minLat = float.MaxValue;
            float maxLat = float.MinValue;
            float minY = float.MaxValue;
            float maxY = float.MinValue;

            for (int i = 0; i < hipValidCount; i++)
            {
                Vector3 pt = _inlierBuffer[i];
                float lat = Vector3.Dot(pt - hipPos, pelvisRightFlat);
                if (Mathf.Abs(lat - medianLat) <= 0.22f)
                {
                    _inlierBuffer[inlierCount] = pt;
                    _inlierNormals[inlierCount] = _inlierNormals[i];
                    inlierCount++;

                    if (lat < minLat) minLat = lat;
                    if (lat > maxLat) maxLat = lat;
                    if (pt.y < minY) minY = pt.y;
                    if (pt.y > maxY) maxY = pt.y;
                }
            }

            if (inlierCount < 3)
            {
                hasPrev = false;
                return default;
            }

            // Wide-slab rejection for current frame only (never permanently blacklist voxels inside leg reach)
            if (maxLat - minLat > 0.38f)
            {
                hasPrev = false;
                return default;
            }

            // Planar vertical wall rejection for current frame only
            int vertNormCount = 0;
            Vector3 normSum = Vector3.zero;
            for (int i = 0; i < inlierCount; i++)
            {
                Vector3 n = _inlierNormals[i];
                if (n.sqrMagnitude > 0.5f && Mathf.Abs(n.y) < 0.22f)
                {
                    vertNormCount++;
                    normSum += n;
                }
            }

            if (vertNormCount >= 5 && normSum.sqrMagnitude > 0.2f &&
                ((maxLat - minLat) > 0.28f || (maxY - minY) > 0.60f))
            {
                Vector3 planeNormal = normSum.normalized;
                float minProj = float.MaxValue;
                float maxProj = float.MinValue;
                for (int i = 0; i < inlierCount; i++)
                {
                    float proj = Vector3.Dot(_inlierBuffer[i], planeNormal);
                    if (proj < minProj) minProj = proj;
                    if (proj > maxProj) maxProj = proj;
                }

                if (maxProj - minProj < 0.05f)
                {
                    hasPrev = false;
                    return default;
                }
            }

            // Pick furthest supported inlier from hip without pulling back toward thigh centroid
            const float neighborRadiusSqr = 0.34f * 0.34f;
            int bestIdx = -1;
            float bestDist = -1f;

            for (int i = 0; i < inlierCount; i++)
            {
                bool hasNeighbor = false;
                for (int j = 0; j < inlierCount; j++)
                {
                    if (i == j)
                        continue;
                    if ((_inlierBuffer[i] - _inlierBuffer[j]).sqrMagnitude <= neighborRadiusSqr)
                    {
                        hasNeighbor = true;
                        break;
                    }
                }

                if (!hasNeighbor)
                    continue;

                float dHip = Vector3.Distance(_inlierBuffer[i], hipPos);
                if (dHip > bestDist)
                {
                    bestDist = dHip;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0)
            {
                hasPrev = false;
                return default;
            }

            const float tipClusterRadiusSqr = 0.12f * 0.12f;
            Vector3 bestPt = _inlierBuffer[bestIdx];
            Vector3 tipSum = Vector3.zero;
            int tipCount = 0;
            for (int i = 0; i < inlierCount; i++)
            {
                if ((_inlierBuffer[i] - bestPt).sqrMagnitude <= tipClusterRadiusSqr)
                {
                    tipSum += _inlierBuffer[i];
                    tipCount++;
                }
            }
            Vector3 distalSurface = tipSum / Mathf.Max(1, tipCount);

            // Push slightly inward along camera ray to approximate skeletal joint center inside leg/shoe
            Vector3 rayToSurface = distalSurface - headPos;
            Vector3 insetDir = rayToSurface.sqrMagnitude > 0.0001f ? rayToSurface.normalized : pelvisForwardFlat;
            Vector3 distalCenter = distalSurface + insetDir * _surfaceToBoneInsetMeters;

            float distalDistFromHip = Vector3.Distance(distalCenter, hipPos);
            float confidence = Mathf.Clamp01(0.48f + (inlierCount - 3) * 0.07f);
            confidence = Mathf.Min(confidence, 0.95f);

            DepthLegObservation obs = new DepthLegObservation
            {
                IsDetected = true,
                Confidence = confidence,
                PointCount = inlierCount
            };

            // Estimate knee hint first from mid-leg inliers around thigh length distance
            Vector3 kneeSum = Vector3.zero;
            int kneeInliers = 0;
            float kneeMinDist = thighLen * 0.55f;
            float kneeMaxDist = thighLen * 1.12f;
            for (int i = 0; i < inlierCount; i++)
            {
                float d = Vector3.Distance(_inlierBuffer[i], hipPos);
                if (d >= kneeMinDist && d <= kneeMaxDist)
                {
                    kneeSum += _inlierBuffer[i];
                    kneeInliers++;
                }
            }

            if (kneeInliers >= 2)
            {
                Vector3 kneeSurface = kneeSum / kneeInliers;
                Vector3 kneeDir = (kneeSurface - headPos).sqrMagnitude > 0.0001f ? (kneeSurface - headPos).normalized : pelvisForwardFlat;
                obs.HasKneeHint = true;
                obs.KneeWorldPosition = kneeSurface + kneeDir * _surfaceToBoneInsetMeters;
            }

            // Smooth continuous bent-to-extended transition (eliminates binary cliff)
            Vector3 kneeAnchor = obs.HasKneeHint ? obs.KneeWorldPosition : distalCenter;
            Vector3 hangingFoot = kneeAnchor + Vector3.down * (shinLen * 0.90f) - pelvisForwardFlat * 0.06f;
            hangingFoot.y = Mathf.Max(floorY + ankleHeight, hangingFoot.y);

            float extRatio = Mathf.InverseLerp(thighLen * 1.05f, thighLen * 1.32f, distalDistFromHip);

            if (distalCenter.y > pelvisPos.y - 0.32f && extRatio < 1f)
            {
                Vector3 blendedFoot = Vector3.Lerp(hangingFoot, distalCenter, extRatio);
                blendedFoot.y = Mathf.Max(floorY + ankleHeight, blendedFoot.y);
                obs.FootWorldPosition = blendedFoot;
                obs.IsKneeDominant = extRatio < 0.45f;
                if (!obs.HasKneeHint)
                {
                    obs.HasKneeHint = true;
                    obs.KneeWorldPosition = distalCenter;
                }

                hasPrev = true;
                prevAnchor = obs.IsKneeDominant ? distalCenter : blendedFoot;
                return obs;
            }

            // Observed extended shin / kick / lifted foot
            Vector3 footTarget = distalCenter;

            // Collinear Straight-Shin Extension for fast kicks or moderate extensions where foot tip is sparse/motion-blurred
            if (obs.HasKneeHint && distalDistFromHip >= thighLen + shinLen * 0.28f)
            {
                Vector3 thighVec = obs.KneeWorldPosition - hipPos;
                Vector3 shinVec = distalCenter - obs.KneeWorldPosition;
                if (thighVec.sqrMagnitude > 0.01f && shinVec.sqrMagnitude > 0.005f)
                {
                    float straightness = Vector3.Dot(thighVec.normalized, shinVec.normalized);
                    float straightFactor = Mathf.InverseLerp(0.82f, 0.93f, straightness);
                    if (straightFactor > 0.01f)
                    {
                        Vector3 straightFoot = obs.KneeWorldPosition + shinVec.normalized * (shinLen * 0.96f);
                        if (Vector3.Distance(straightFoot, hipPos) > distalDistFromHip)
                        {
                            footTarget = Vector3.Lerp(distalCenter, straightFoot, straightFactor);
                        }
                    }
                }
            }

            footTarget.y = Mathf.Max(floorY + ankleHeight, footTarget.y);
            obs.FootWorldPosition = footTarget;
            obs.IsKneeDominant = false;

            hasPrev = true;
            prevAnchor = footTarget;
            return obs;
        }

        private bool EvaluateSingleRay(
            Vector3 rayDir,
            in ScanFilterContext ctx,
            ref DepthObservationFrame frame,
            ref int fgCount,
            ref int leftCount,
            ref int rightCount)
        {
            frame.TotalRaysCast++;
            Ray ray = new Ray(ctx.HeadPos, rayDir);
            bool didHit = _raycastManager.Raycast(ray, out EnvironmentRaycastHit hit, _maxRayDistanceMeters);

            if (hit.status == EnvironmentRaycastHitStatus.NotReady)
            {
                frame.IsReady = false;
                return false;
            }

            if (hit.status == EnvironmentRaycastHitStatus.NotSupported)
            {
                frame.IsSupported = false;
                frame.IsReady = false;
                return false;
            }

            frame.IsReady = true;
            if (!didHit)
                return true;

            frame.RawHitCount++;
            Vector3 pt = hit.point;

            // Layer 1: Floor & low carpet subtraction
            if (pt.y <= ctx.FloorY + _floorClearanceMeters)
            {
                frame.RejFloorCount++;
                return true;
            }

            Vector3 fromPelvis = pt - ctx.PelvisPos;
            float distFromPelvis = fromPelvis.magnitude;

            // Continuous automatic static environment learning safely outside max physical leg/shoe reach
            if (distFromPelvis > ctx.MaxLegReach + 0.30f)
            {
                if (pt.y > ctx.FloorY + 0.08f && _autoStaticVoxels.Count < MaxAutoStaticVoxels)
                {
                    _autoStaticVoxels.Add(ToVoxel(pt, ctx.InvVoxel));
                }
                frame.RejReachCount++;
                return true;
            }

            // Layer 2: Upper-body / chest subtraction
            if (pt.y >= ctx.PelvisPos.y + _pelvisUpperMarginMeters)
            {
                frame.RejReachCount++;
                return true;
            }

            // Layer 3: Anatomical leg + shoe reach envelope
            if (distFromPelvis < _minDistFromPelvisMeters || distFromPelvis > ctx.MaxLegReach)
            {
                frame.RejReachCount++;
                return true;
            }

            if (Vector3.Dot(fromPelvis, ctx.PelvisForwardFlat) < -0.18f)
            {
                frame.RejReachCount++;
                return true;
            }

            float lateral = Vector3.Dot(fromPelvis, ctx.PelvisRightFlat);
            if (Mathf.Abs(lateral) > 0.56f)
            {
                frame.RejReachCount++;
                return true;
            }

            // Layer 4: Arms, wrists, hands, controllers exclusion
            if ((ctx.HasLeftHand && (pt - ctx.LeftHandPos).sqrMagnitude <= ctx.HandExclSqr) ||
                (ctx.HasRightHand && (pt - ctx.RightHandPos).sqrMagnitude <= ctx.HandExclSqr) ||
                (ctx.HasLArmUp &&
                 (PointToSegmentSqrDistance(pt, ctx.LArmUp, ctx.LArmLow) <= ctx.ArmExclSqr ||
                  PointToSegmentSqrDistance(pt, ctx.LArmLow, ctx.LWrist) <= ctx.ArmExclSqr)) ||
                (ctx.HasRArmUp &&
                 (PointToSegmentSqrDistance(pt, ctx.RArmUp, ctx.RArmLow) <= ctx.ArmExclSqr ||
                  PointToSegmentSqrDistance(pt, ctx.RArmLow, ctx.RWrist) <= ctx.ArmExclSqr)))
            {
                frame.RejArmCount++;
                return true;
            }

            // Layer 5: Static room environment (manual baseline + safe auto-learned voxels + explicit MRUK wall planes / furniture volumes)
            Vector3Int ptVoxel = ToVoxel(pt, ctx.InvVoxel);
            if (_staticBackgroundVoxels.Contains(ptVoxel) || _autoStaticVoxels.Contains(ptVoxel))
            {
                frame.RejBgVoxelCount++;
                return true;
            }

            if (IsNearMrukWallOrFurniture(ctx.Room, pt))
            {
                frame.RejMrukCount++;
                return true;
            }

            sbyte side = AssignSide(pt, lateral);
            Vector3 validNormal = hit.normalConfidence >= 0.4f ? hit.normal : Vector3.zero;

            if (fgCount < MaxBufferPoints)
            {
                _foregroundBuffer[fgCount] = new DepthForegroundPoint
                {
                    WorldPosition = pt,
                    AssignedSide = side
                };
                fgCount++;
            }

            if (side < 0 && leftCount < MaxBufferPoints)
            {
                _leftCandidates[leftCount] = pt;
                _leftNormals[leftCount] = validNormal;
                leftCount++;
            }
            else if (side > 0 && rightCount < MaxBufferPoints)
            {
                _rightCandidates[rightCount] = pt;
                _rightNormals[rightCount] = validNormal;
                rightCount++;
            }

            return true;
        }

        private bool RunFineLegProbe(
            Vector3 probeAnchorWorld,
            Quaternion headRot,
            in ScanFilterContext ctx,
            ref DepthObservationFrame frame,
            ref int fgCount,
            ref int leftCount,
            ref int rightCount)
        {
            Vector3 toAnchor = probeAnchorWorld - ctx.HeadPos;
            if (toAnchor.sqrMagnitude < 0.04f)
                return true;

            Vector3 localDir = Quaternion.Inverse(headRot) * toAnchor.normalized;
            if (localDir.z <= 0.1f)
                return true;

            float centerPitchUpDeg = Mathf.Asin(Mathf.Clamp(localDir.y, -0.99f, 0.99f)) * Mathf.Rad2Deg;
            float centerYawDeg = Mathf.Atan2(localDir.x, localDir.z) * Mathf.Rad2Deg;

            float pitchMin = Mathf.Max(-58f, centerPitchUpDeg - 24f);
            float pitchMax = Mathf.Min(8f, centerPitchUpDeg + 14f);
            int pCols = Mathf.Clamp(_fineProbeColumns, 1, 7);
            int pRows = Mathf.Clamp(_fineProbeRows, 3, 15);

            for (int r = 0; r < pRows; r++)
            {
                float pitchUpDeg = Mathf.Lerp(pitchMin, pitchMax, pRows > 1 ? r / (float)(pRows - 1) : 0.5f);
                for (int c = 0; c < pCols; c++)
                {
                    float yawOffset = pCols > 1 ? Mathf.Lerp(-6f, 6f, c / (float)(pCols - 1)) : 0f;
                    Vector3 rayDir = headRot * (Quaternion.Euler(-pitchUpDeg, centerYawDeg + yawOffset, 0f) * Vector3.forward);
                    if (rayDir.y > 0.25f)
                        continue;

                    if (!EvaluateSingleRay(rayDir, ctx, ref frame, ref fgCount, ref leftCount, ref rightCount))
                        return false;
                }
            }

            return true;
        }

        private static bool TryGetProbeSeed(
            Vector3[] candidates,
            int count,
            bool hasPrevHint,
            Vector3 prevHint,
            out Vector3 seedWorld)
        {
            if (count >= 2)
            {
                Vector3 sum = Vector3.zero;
                for (int i = 0; i < count; i++)
                {
                    sum += candidates[i];
                }
                seedWorld = sum / count;
                return true;
            }

            if (hasPrevHint)
            {
                seedWorld = prevHint;
                return true;
            }

            seedWorld = Vector3.zero;
            return false;
        }

        private static bool IsNearMrukWallOrFurniture(MRUKRoom room, Vector3 pt)
        {
            if (room == null)
                return false;

            if (room.WallAnchors != null)
            {
                for (int i = 0; i < room.WallAnchors.Count; i++)
                {
                    MRUKAnchor wall = room.WallAnchors[i];
                    if (wall != null && wall.PlaneRect.HasValue)
                    {
                        float d = wall.GetClosestSurfacePosition(pt, out _, out _, MRUKAnchor.ComponentType.Plane);
                        if (d >= 0f && d <= 0.12f)
                            return true;
                    }
                }
            }

            if (room.Anchors != null)
            {
                for (int i = 0; i < room.Anchors.Count; i++)
                {
                    MRUKAnchor anchor = room.Anchors[i];
                    if (anchor != null &&
                        anchor.HasAnyLabel(FurnitureLabelsMask) &&
                        anchor.IsPositionInVolume(pt, true, 0.05f))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void ExtractExclusionGeometry(
            in BodySkeletonSnapshot skeleton,
            out bool hasLeftHand, out Vector3 leftHandPos,
            out bool hasRightHand, out Vector3 rightHandPos,
            out bool hasLArmUp, out Vector3 lArmUp, out Vector3 lArmLow, out Vector3 lWrist,
            out bool hasRArmUp, out Vector3 rArmUp, out Vector3 rArmLow, out Vector3 rWrist)
        {
            hasLeftHand = false;
            leftHandPos = Vector3.zero;
            hasRightHand = false;
            rightHandPos = Vector3.zero;

            if (_cameraRig != null)
            {
                if (_cameraRig.leftHandAnchor != null)
                {
                    hasLeftHand = true;
                    leftHandPos = _cameraRig.leftHandAnchor.position;
                }
                if (_cameraRig.rightHandAnchor != null)
                {
                    hasRightHand = true;
                    rightHandPos = _cameraRig.rightHandAnchor.position;
                }
            }

            hasLArmUp = false;
            lArmUp = Vector3.zero;
            lArmLow = Vector3.zero;
            lWrist = Vector3.zero;
            hasRArmUp = false;
            rArmUp = Vector3.zero;
            rArmLow = Vector3.zero;
            rWrist = Vector3.zero;

            if (!skeleton.IsAvailable)
                return;

            if (!hasLeftHand && skeleton.TryGetJoint(LeftHandPalmIndex, out BodyJointSample lPalm) && lPalm.PositionValid)
            {
                hasLeftHand = true;
                leftHandPos = lPalm.WorldPosition;
            }
            if (!hasRightHand && skeleton.TryGetJoint(RightHandPalmIndex, out BodyJointSample rPalm) && rPalm.PositionValid)
            {
                hasRightHand = true;
                rightHandPos = rPalm.WorldPosition;
            }

            if (skeleton.TryGetJoint(LeftArmUpperIndex, out BodyJointSample lUp) && lUp.PositionValid &&
                skeleton.TryGetJoint(LeftArmLowerIndex, out BodyJointSample lLo) && lLo.PositionValid &&
                skeleton.TryGetJoint(LeftHandWristIndex, out BodyJointSample lWr) && lWr.PositionValid)
            {
                hasLArmUp = true;
                lArmUp = lUp.WorldPosition;
                lArmLow = lLo.WorldPosition;
                lWrist = lWr.WorldPosition;
            }

            if (skeleton.TryGetJoint(RightArmUpperIndex, out BodyJointSample rUp) && rUp.PositionValid &&
                skeleton.TryGetJoint(RightArmLowerIndex, out BodyJointSample rLo) && rLo.PositionValid &&
                skeleton.TryGetJoint(RightHandWristIndex, out BodyJointSample rWr) && rWr.PositionValid)
            {
                hasRArmUp = true;
                rArmUp = rUp.WorldPosition;
                rArmLow = rLo.WorldPosition;
                rWrist = rWr.WorldPosition;
            }
        }

        private void ResolvePelvisAndHips(
            in BodySkeletonSnapshot skeleton,
            Transform headTransform,
            float floorY,
            CalibrationProfile profile,
            out Vector3 pelvisPos,
            out Vector3 pelvisForwardFlat,
            out Vector3 pelvisRightFlat,
            out Vector3 leftHipPos,
            out Vector3 rightHipPos)
        {
            BodyJointSample lUl = default;
            BodyJointSample rUl = default;
            bool hasSdkLeftHip = skeleton.IsAvailable &&
                                 skeleton.TryGetJoint(LeftUpperLegIndex, out lUl) &&
                                 lUl.PositionValid;
            bool hasSdkRightHip = skeleton.IsAvailable &&
                                  skeleton.TryGetJoint(RightUpperLegIndex, out rUl) &&
                                  rUl.PositionValid;

            // Compute right vector directly from bilateral world joint positions (never bone-local WorldRotation * forward).
            pelvisRightFlat = Vector3.zero;
            if (hasSdkLeftHip && hasSdkRightHip)
            {
                Vector3 span = Vector3.ProjectOnPlane(rUl.WorldPosition - lUl.WorldPosition, Vector3.up);
                if (span.sqrMagnitude > 0.05f * 0.05f)
                {
                    pelvisRightFlat = span.normalized;
                }
            }

            if (pelvisRightFlat.sqrMagnitude < 0.001f && skeleton.IsAvailable &&
                skeleton.TryGetJoint(LeftArmUpperIndex, out BodyJointSample lArm) && lArm.PositionValid &&
                skeleton.TryGetJoint(RightArmUpperIndex, out BodyJointSample rArm) && rArm.PositionValid)
            {
                Vector3 span = Vector3.ProjectOnPlane(rArm.WorldPosition - lArm.WorldPosition, Vector3.up);
                if (span.sqrMagnitude > 0.08f * 0.08f)
                {
                    pelvisRightFlat = span.normalized;
                }
            }

            if (pelvisRightFlat.sqrMagnitude < 0.001f && skeleton.IsAvailable &&
                skeleton.TryGetJoint(LeftShoulderIndex, out BodyJointSample lSh) && lSh.PositionValid &&
                skeleton.TryGetJoint(RightShoulderIndex, out BodyJointSample rSh) && rSh.PositionValid)
            {
                Vector3 span = Vector3.ProjectOnPlane(rSh.WorldPosition - lSh.WorldPosition, Vector3.up);
                if (span.sqrMagnitude > 0.08f * 0.08f)
                {
                    pelvisRightFlat = span.normalized;
                }
            }

            if (pelvisRightFlat.sqrMagnitude < 0.001f)
            {
                Vector3 headRight = Vector3.ProjectOnPlane(headTransform.right, Vector3.up);
                pelvisRightFlat = headRight.sqrMagnitude > 0.001f ? headRight.normalized : Vector3.right;
            }

            // Unity left-handed: Right x Up = Forward
            pelvisForwardFlat = Vector3.Cross(pelvisRightFlat, Vector3.up).normalized;
            if (pelvisForwardFlat.sqrMagnitude < 0.001f)
            {
                pelvisForwardFlat = Vector3.forward;
            }

            Vector3 headFwd = Vector3.ProjectOnPlane(headTransform.forward, Vector3.up);
            if (headFwd.sqrMagnitude > 0.001f && Vector3.Dot(pelvisForwardFlat, headFwd.normalized) < -0.25f)
            {
                pelvisForwardFlat = -pelvisForwardFlat;
                pelvisRightFlat = -pelvisRightFlat;
            }

            if (hasSdkLeftHip && hasSdkRightHip)
            {
                leftHipPos = lUl.WorldPosition;
                rightHipPos = rUl.WorldPosition;
                pelvisPos = (leftHipPos + rightHipPos) * 0.5f;
                return;
            }

            if (skeleton.IsAvailable && skeleton.TryGetJoint(HipsJointIndex, out BodyJointSample hips) && hips.PositionValid)
            {
                pelvisPos = hips.WorldPosition;
            }
            else if (skeleton.IsAvailable && skeleton.TryGetJoint(HeadJointIndex, out BodyJointSample head) && head.PositionValid)
            {
                float approxDrop = Mathf.Clamp(profile.UserHeightMeters * 0.41f, 0.5f, 0.9f);
                pelvisPos = new Vector3(head.WorldPosition.x, Mathf.Max(floorY + 0.45f, head.WorldPosition.y - approxDrop), head.WorldPosition.z);
            }
            else
            {
                float approxDrop = Mathf.Clamp(profile.UserHeightMeters * 0.41f, 0.5f, 0.9f);
                pelvisPos = new Vector3(
                    headTransform.position.x,
                    Mathf.Max(floorY + 0.45f, headTransform.position.y - approxDrop),
                    headTransform.position.z);
            }

            float halfHipWidth = Mathf.Clamp(profile.StanceWidthMeters * 0.28f, 0.08f, 0.16f);
            leftHipPos = hasSdkLeftHip ? lUl.WorldPosition : pelvisPos - pelvisRightFlat * halfHipWidth;
            rightHipPos = hasSdkRightHip ? rUl.WorldPosition : pelvisPos + pelvisRightFlat * halfHipWidth;
        }

        private Transform ResolveHeadTransform()
        {
            if (_cameraRig != null && _cameraRig.centerEyeAnchor != null)
                return _cameraRig.centerEyeAnchor;
            return Camera.main != null ? Camera.main.transform : null;
        }

        private static Vector3Int ToVoxel(Vector3 p, float invVoxelSize)
        {
            return new Vector3Int(
                Mathf.FloorToInt(p.x * invVoxelSize),
                Mathf.FloorToInt(p.y * invVoxelSize),
                Mathf.FloorToInt(p.z * invVoxelSize));
        }

        private static float PointToSegmentSqrDistance(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float lenSqr = ab.sqrMagnitude;
            if (lenSqr < 1e-6f)
                return (p - a).sqrMagnitude;

            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / lenSqr);
            Vector3 proj = a + ab * t;
            return (p - proj).sqrMagnitude;
        }
    }
}
