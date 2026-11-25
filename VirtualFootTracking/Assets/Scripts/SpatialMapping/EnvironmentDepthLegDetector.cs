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
    /// after subtracting floor, upper body/arms/controllers, MRUK room volumes, and optional
    /// static background voxels.
    /// </summary>
    public class EnvironmentDepthLegDetector : MonoBehaviour
    {
        private const int MaxBufferPoints = 96;

        // Verified OVRPlugin.BoneId indices (Meta XR Core SDK 209.0.0)
        private const int HipsJointIndex = 1;
        private const int HeadJointIndex = 7;
        private const int LeftArmUpperIndex = 10;
        private const int LeftArmLowerIndex = 11;
        private const int LeftHandPalmIndex = 18;
        private const int LeftHandWristIndex = 19;
        private const int RightArmUpperIndex = 15;
        private const int RightArmLowerIndex = 16;
        private const int RightHandPalmIndex = 44;
        private const int RightHandWristIndex = 45;
        private const int LeftUpperLegIndex = 70;
        private const int RightUpperLegIndex = 77;

        [Header("Scene References")]
        [SerializeField] private EnvironmentRaycastManager _raycastManager;
        [SerializeField] private OVRCameraRig _cameraRig;
        [SerializeField] private MRUK _mruk;

        [Header("Scan Grid (88 rays max/frame)")]
        [SerializeField] private int _gridColumns = 11;
        [SerializeField] private int _gridRows = 8;
        [SerializeField] private float _yawHalfAngleDeg = 36f;
        [SerializeField] private float _pitchMinDeg = -46f;
        [SerializeField] private float _pitchMaxDeg = 6f;
        [SerializeField] private float _maxRayDistanceMeters = 2.3f;

        [Header("Foreground Filtering")]
        [SerializeField] private bool _enabled = true;
        [SerializeField] private float _floorClearanceMeters = 0.10f;
        [SerializeField] private float _pelvisUpperMarginMeters = 0.14f;
        [SerializeField] private float _minDistFromPelvisMeters = 0.20f;
        [SerializeField] private float _handControllerExclusionRadius = 0.17f;
        [SerializeField] private float _forearmExclusionRadius = 0.14f;
        [SerializeField] private float _staticVoxelSizeMeters = 0.11f;
        [SerializeField] private int _minClusterPoints = 4;
        [SerializeField] private float _surfaceToBoneInsetMeters = 0.04f;

        private readonly DepthForegroundPoint[] _foregroundBuffer = new DepthForegroundPoint[MaxBufferPoints];
        private readonly Vector3[] _leftCandidates = new Vector3[MaxBufferPoints];
        private readonly Vector3[] _rightCandidates = new Vector3[MaxBufferPoints];
        private readonly Vector3[] _inlierBuffer = new Vector3[MaxBufferPoints];
        private readonly float[] _topDist = new float[3];
        private readonly Vector3[] _topPts = new Vector3[3];
        private readonly HashSet<Vector3Int> _staticBackgroundVoxels = new HashSet<Vector3Int>(512);

        private float _backgroundCaptureRemaining;
        private float _backgroundCaptureTotal = 1.5f;
        private bool _hasPrevLeft;
        private Vector3 _prevLeftFoot;
        private bool _hasPrevRight;
        private Vector3 _prevRightFoot;
        private bool _initialized;

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public bool IsCapturingBackground => _backgroundCaptureRemaining > 0f;
        public int StaticBackgroundVoxelCount => _staticBackgroundVoxels.Count;

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
            _backgroundCaptureTotal = Mathf.Max(0.4f, durationSeconds);
            _backgroundCaptureRemaining = _backgroundCaptureTotal;
        }

        public void ClearBackgroundBaseline()
        {
            _staticBackgroundVoxels.Clear();
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
                    : (_staticBackgroundVoxels.Count > 0 ? 1f : 0f),
                StaticBackgroundVoxelCount = _staticBackgroundVoxels.Count,
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
            float maxLegReach = thighLen + shinLen + 0.24f;

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
                        : $"Fundo capturado ({_staticBackgroundVoxels.Count} voxels)";
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
            float handExclSqr = _handControllerExclusionRadius * _handControllerExclusionRadius;
            float armExclSqr = _forearmExclusionRadius * _forearmExclusionRadius;
            float invVoxel = 1f / Mathf.Max(0.04f, _staticVoxelSizeMeters);

            int fgCount = 0;
            int leftCount = 0;
            int rightCount = 0;
            bool abortedNotReady = false;

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

                    frame.TotalRaysCast++;
                    Ray ray = new Ray(headPos, rayDir);
                    bool didHit = _raycastManager.Raycast(ray, out EnvironmentRaycastHit hit, _maxRayDistanceMeters);

                    if (hit.status == EnvironmentRaycastHitStatus.NotReady)
                    {
                        frame.IsReady = false;
                        abortedNotReady = true;
                        break;
                    }

                    if (hit.status == EnvironmentRaycastHitStatus.NotSupported)
                    {
                        frame.IsSupported = false;
                        frame.IsReady = false;
                        abortedNotReady = true;
                        break;
                    }

                    frame.IsReady = true;
                    if (!didHit)
                        continue;

                    frame.RawHitCount++;
                    Vector3 pt = hit.point;

                    // Layer 1: Floor & low carpet subtraction
                    if (pt.y <= floorY + _floorClearanceMeters)
                        continue;

                    // Layer 2: Upper-body / chest subtraction
                    if (pt.y >= pelvisPos.y + _pelvisUpperMarginMeters)
                        continue;

                    // Layer 3: Anatomical leg reach envelope
                    Vector3 fromPelvis = pt - pelvisPos;
                    float distFromPelvis = fromPelvis.magnitude;
                    if (distFromPelvis < _minDistFromPelvisMeters || distFromPelvis > maxLegReach)
                        continue;

                    if (Vector3.Dot(fromPelvis, pelvisForwardFlat) < -0.35f)
                        continue;

                    // Layer 4: Arms, wrists, hands, controllers exclusion
                    if (hasLeftHand && (pt - leftHandPos).sqrMagnitude <= handExclSqr)
                        continue;
                    if (hasRightHand && (pt - rightHandPos).sqrMagnitude <= handExclSqr)
                        continue;

                    if (hasLArmUp &&
                        (PointToSegmentSqrDistance(pt, lArmUp, lArmLow) <= armExclSqr ||
                         PointToSegmentSqrDistance(pt, lArmLow, lWrist) <= armExclSqr))
                        continue;

                    if (hasRArmUp &&
                        (PointToSegmentSqrDistance(pt, rArmUp, rArmLow) <= armExclSqr ||
                         PointToSegmentSqrDistance(pt, rArmLow, rWrist) <= armExclSqr))
                        continue;

                    // Layer 5: Static room environment (captured baseline voxels + MRUK scene volumes)
                    if (_staticBackgroundVoxels.Count > 0 &&
                        _staticBackgroundVoxels.Contains(ToVoxel(pt, invVoxel)))
                        continue;

                    if (room != null &&
                        room.IsPositionInSceneVolume(pt, out MRUKAnchor volumeAnchor, true, 0.07f) &&
                        volumeAnchor != null &&
                        !volumeAnchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.CEILING))
                        continue;

                    // Surviving foreground point -> assign Left (-1) vs Right (+1)
                    sbyte side = AssignSide(pt, fromPelvis, pelvisRightFlat);

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
                        _leftCandidates[leftCount++] = pt;
                    }
                    else if (side > 0 && rightCount < MaxBufferPoints)
                    {
                        _rightCandidates[rightCount++] = pt;
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

            frame.ForegroundPointCount = fgCount;

            frame.LeftLeg = BuildLegObservation(
                _leftCandidates,
                leftCount,
                headPos,
                pelvisPos,
                leftHipPos,
                pelvisForwardFlat,
                floorY,
                thighLen,
                shinLen,
                ankleHeight,
                ref _hasPrevLeft,
                ref _prevLeftFoot);

            frame.RightLeg = BuildLegObservation(
                _rightCandidates,
                rightCount,
                headPos,
                pelvisPos,
                rightHipPos,
                pelvisForwardFlat,
                floorY,
                thighLen,
                shinLen,
                ankleHeight,
                ref _hasPrevRight,
                ref _prevRightFoot);

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
                frame.StatusMessage = _staticBackgroundVoxels.Count > 0
                    ? $"Depth pronto ({fgCount} pts < min {_minClusterPoints})"
                    : $"Depth pronto ({fgCount} pts; capture fundo se houver moveis)";
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
            frame.StaticBackgroundVoxelCount = _staticBackgroundVoxels.Count;
            return anyReady;
        }

        private sbyte AssignSide(Vector3 pt, Vector3 fromPelvis, Vector3 pelvisRightFlat)
        {
            float lateral = Vector3.Dot(fromPelvis, pelvisRightFlat);
            sbyte anatomicalSide = lateral < 0f ? (sbyte)-1 : (sbyte)1;

            if (_hasPrevLeft && _hasPrevRight)
            {
                float dL = Vector3.Distance(pt, _prevLeftFoot);
                float dR = Vector3.Distance(pt, _prevRightFoot);
                if (dL < 0.26f && dL + 0.08f < dR)
                    return -1;
                if (dR < 0.26f && dR + 0.08f < dL)
                    return 1;
            }

            return anatomicalSide;
        }

        private DepthLegObservation BuildLegObservation(
            Vector3[] candidates,
            int count,
            Vector3 headPos,
            Vector3 pelvisPos,
            Vector3 hipPos,
            Vector3 pelvisForwardFlat,
            float floorY,
            float thighLen,
            float shinLen,
            float ankleHeight,
            ref bool hasPrev,
            ref Vector3 prevAnchor)
        {
            if (count < _minClusterPoints)
            {
                hasPrev = false;
                return default;
            }

            Vector3 centroid = Vector3.zero;
            for (int i = 0; i < count; i++)
            {
                centroid += candidates[i];
            }
            centroid /= count;

            const float maxClusterRadiusSqr = 0.38f * 0.38f;
            int inlierCount = 0;
            for (int i = 0; i < count; i++)
            {
                if ((candidates[i] - centroid).sqrMagnitude <= maxClusterRadiusSqr)
                {
                    _inlierBuffer[inlierCount++] = candidates[i];
                }
            }

            if (inlierCount < 3)
            {
                hasPrev = false;
                return default;
            }

            // Pick top-3 most distal hits from hip (weighted slightly toward forward kick direction)
            for (int k = 0; k < 3; k++)
            {
                _topDist[k] = float.MinValue;
                _topPts[k] = Vector3.zero;
            }

            for (int i = 0; i < inlierCount; i++)
            {
                Vector3 pt = _inlierBuffer[i];
                float dHip = Vector3.Distance(pt, hipPos);
                float fwd = Vector3.Dot(pt - hipPos, pelvisForwardFlat);
                float score = dHip + Mathf.Max(0f, fwd) * 0.15f;

                if (score > _topDist[0])
                {
                    _topDist[2] = _topDist[1];
                    _topPts[2] = _topPts[1];
                    _topDist[1] = _topDist[0];
                    _topPts[1] = _topPts[0];
                    _topDist[0] = score;
                    _topPts[0] = pt;
                }
                else if (score > _topDist[1])
                {
                    _topDist[2] = _topDist[1];
                    _topPts[2] = _topPts[1];
                    _topDist[1] = score;
                    _topPts[1] = pt;
                }
                else if (score > _topDist[2])
                {
                    _topDist[2] = score;
                    _topPts[2] = pt;
                }
            }

            int topN = Mathf.Min(3, inlierCount);
            Vector3 distalSurface = Vector3.zero;
            for (int k = 0; k < topN; k++)
            {
                distalSurface += _topPts[k];
            }
            distalSurface /= topN;

            // Push slightly inward along camera ray to approximate skeletal joint center inside leg/shoe
            Vector3 rayToSurface = distalSurface - headPos;
            Vector3 insetDir = rayToSurface.sqrMagnitude > 0.0001f ? rayToSurface.normalized : pelvisForwardFlat;
            Vector3 distalCenter = distalSurface + insetDir * _surfaceToBoneInsetMeters;

            float distalDistFromHip = Vector3.Distance(distalCenter, hipPos);
            float confidence = Mathf.Clamp01(0.45f + (inlierCount - 3) * 0.08f);
            confidence = Mathf.Min(confidence, 0.95f);

            DepthLegObservation obs = new DepthLegObservation
            {
                IsDetected = true,
                Confidence = confidence,
                PointCount = inlierCount
            };

            // Case A: Raised bent knee where thigh/knee rises above hip-thigh standing depth while lower shin hangs down.
            if (distalDistFromHip < thighLen * 1.18f && distalCenter.y > pelvisPos.y - 0.30f)
            {
                obs.IsKneeDominant = true;
                obs.HasKneeHint = true;
                obs.KneeWorldPosition = distalCenter;

                Vector3 hangingFoot = distalCenter + Vector3.down * (shinLen * 0.92f) - pelvisForwardFlat * 0.08f;
                hangingFoot.y = Mathf.Max(floorY + ankleHeight, hangingFoot.y);
                obs.FootWorldPosition = hangingFoot;

                hasPrev = true;
                prevAnchor = distalCenter;
                return obs;
            }

            // Case B: Extended kick or lifted foot/shin
            Vector3 footTarget = distalCenter;
            footTarget.y = Mathf.Max(floorY + ankleHeight, footTarget.y);
            obs.FootWorldPosition = footTarget;

            // Optional knee hint from mid-leg inliers around thigh length distance
            Vector3 kneeSum = Vector3.zero;
            int kneeInliers = 0;
            float kneeMinDist = thighLen * 0.65f;
            float kneeMaxDist = thighLen * 1.15f;
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

            hasPrev = true;
            prevAnchor = footTarget;
            return obs;
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
            pelvisForwardFlat = Vector3.ProjectOnPlane(headTransform.forward, Vector3.up);
            if (skeleton.IsAvailable && skeleton.TryGetJoint(HipsJointIndex, out BodyJointSample hipsOri) && hipsOri.OrientationValid)
            {
                Vector3 hFwd = Vector3.ProjectOnPlane(hipsOri.WorldRotation * Vector3.forward, Vector3.up);
                if (hFwd.sqrMagnitude > 0.001f)
                {
                    pelvisForwardFlat = hFwd;
                }
            }
            if (pelvisForwardFlat.sqrMagnitude < 0.001f)
                pelvisForwardFlat = Vector3.forward;
            pelvisForwardFlat.Normalize();
            pelvisRightFlat = Vector3.Cross(Vector3.up, pelvisForwardFlat).normalized;

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
            leftHipPos = pelvisPos - pelvisRightFlat * halfHipWidth;
            rightHipPos = pelvisPos + pelvisRightFlat * halfHipWidth;

            if (skeleton.IsAvailable)
            {
                if (skeleton.TryGetJoint(LeftUpperLegIndex, out BodyJointSample lUl) && lUl.PositionValid)
                    leftHipPos = lUl.WorldPosition;
                if (skeleton.TryGetJoint(RightUpperLegIndex, out BodyJointSample rUl) && rUl.PositionValid)
                    rightHipPos = rUl.WorldPosition;
            }
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
