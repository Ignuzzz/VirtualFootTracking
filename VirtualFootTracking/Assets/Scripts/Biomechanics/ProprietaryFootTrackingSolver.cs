using UnityEngine;
using VirtualFootTracking.Core;
using VirtualFootTracking.Prediction;

namespace VirtualFootTracking.Biomechanics
{
    /// <summary>
    /// Phase 2 proprietary Foot Tracking Engine solver.
    /// Combines Meta IOBT/Generative Legs joints with MRUK floor plane constraints,
    /// stance/swing foot locking, analytic 2-bone leg IK, and pelvis-based biomechanical fallback.
    /// </summary>
    public class ProprietaryFootTrackingSolver : MonoBehaviour, IFootTrackingSolver
    {
        // Verified OVRPlugin.BoneId indices from Meta XR Core SDK 209.0.0
        private const int HipsJointIndex = 1;           // FullBody_Hips
        private const int HeadJointIndex = 7;           // FullBody_Head
        private const int LeftShoulderIndex = 8;        // FullBody_LeftShoulder
        private const int LeftArmUpperIndex = 10;       // FullBody_LeftArmUpper
        private const int RightShoulderIndex = 13;      // FullBody_RightShoulder
        private const int RightArmUpperIndex = 15;      // FullBody_RightArmUpper
        private const int LeftUpperLegIndex = 70;       // FullBody_LeftUpperLeg
        private const int LeftKneeIndex = 71;           // FullBody_LeftLowerLeg
        private const int LeftAnkleIndex = 73;          // FullBody_LeftFootAnkle
        private const int LeftBallIndex = 76;           // FullBody_LeftFootBall
        private const int RightUpperLegIndex = 77;      // FullBody_RightUpperLeg
        private const int RightKneeIndex = 78;          // FullBody_RightLowerLeg
        private const int RightAnkleIndex = 80;         // FullBody_RightFootAnkle
        private const int RightBallIndex = 83;          // FullBody_RightFootBall

        [Header("Engine Feature Toggles")]
        [SerializeField] private bool _enableFloorClamp = true;
        [SerializeField] private bool _enableFootLocking = true;
        [SerializeField] private bool _enableAnatomicalIk = true;
        [SerializeField] private bool _enableDepthLegFusion = true;

        [Header("Stance / Swing Hysteresis")]
        [SerializeField] private float _stanceEnterSoleHeightMeters = 0.045f;
        [SerializeField] private float _stanceExitSoleHeightMeters = 0.075f;
        [SerializeField] private float _stanceEnterSpeedMps = 0.35f;
        [SerializeField] private float _stanceExitSpeedMps = 0.60f;
        [SerializeField] private float _footLockMaxLeashMeters = 0.09f;
        [SerializeField] private float _maxPlausibleSpeedMps = 20.0f;

        [Header("Depth Fusion Stability")]
        [SerializeField] private float _depthDropoutHoldSeconds = 0.14f;
        [SerializeField] private float _depthMinCutoffHz = 2.0f;
        [SerializeField] private float _depthBetaSpeed = 14.0f;
        [SerializeField] private float _depthStationaryDeadzoneMeters = 0.012f;

        private struct FootState
        {
            public bool HasPrevRaw;
            public Vector3 PrevRawPos;
            public bool HasPrevOutput;
            public Vector3 PrevFootPos;
            public Vector3 PrevKneePos;
            public bool IsFootLocked;
            public Vector2 LockedXZ;
            public float LockedYawDeg;
            public float DepthBlendWeight;
            public Vector3 LastDepthFootPos;
            public Vector3 LastDepthKneePos;
            public bool LastDepthHasKnee;
            public bool LastDepthIsKneeDominant;
            public float LastDepthConfidence;
            public int ConsecutiveDepthFrames;
            public float TimeSinceLastDepthHit;
            public Vector3 DepthFootVelocity;
            public OneEuroFilter3D DepthFootFilter;
            public OneEuroFilter3D DepthKneeFilter;
        }

        private CalibrationProfile _profile = new CalibrationProfile();
        private ISpatialMappingProvider _spatialProvider;
        private DepthObservationFrame _latestDepth;
        private FootState _leftState;
        private FootState _rightState;
        private long _frameIndex;

        public string SolverName => "Phase3.ProprietaryDepthFusionSolver_v0.3.3";

        public bool EnableFloorClamp
        {
            get => _enableFloorClamp;
            set => _enableFloorClamp = value;
        }

        public bool EnableFootLocking
        {
            get => _enableFootLocking;
            set
            {
                _enableFootLocking = value;
                if (!_enableFootLocking)
                {
                    _leftState.IsFootLocked = false;
                    _rightState.IsFootLocked = false;
                }
            }
        }

        public bool EnableAnatomicalIk
        {
            get => _enableAnatomicalIk;
            set => _enableAnatomicalIk = value;
        }

        public bool EnableDepthLegFusion
        {
            get => _enableDepthLegFusion;
            set => _enableDepthLegFusion = value;
        }

        public void SetSpatialMappingProvider(ISpatialMappingProvider spatialProvider)
        {
            _spatialProvider = spatialProvider;
        }

        public void SetLatestDepthObservation(in DepthObservationFrame depthFrame)
        {
            _latestDepth = depthFrame;
        }

        public void Initialize(CalibrationProfile profile)
        {
            _profile = profile ?? new CalibrationProfile();
            _latestDepth = default;
            _leftState = default;
            _rightState = default;
            _frameIndex = 0;
        }

        public void UpdateCalibration(CalibrationProfile profile)
        {
            _profile = profile ?? new CalibrationProfile();
        }

        public bool Solve(
            in BodySkeletonSnapshot skeleton,
            in FloorPlaneData floor,
            float deltaTime,
            double timestampSeconds,
            out VirtualTrackerFrame frame)
        {
            _frameIndex++;
            float defaultFloorY = floor.IsValid ? floor.WorldHeightY : 0f;
            frame = VirtualTrackerFrame.CreateEmpty(_frameIndex, timestampSeconds, defaultFloorY);

            if (!skeleton.IsAvailable)
            {
                _leftState.HasPrevRaw = false;
                _rightState.HasPrevRaw = false;
                return false;
            }

            Vector3 pelvisForwardFlat = ResolvePelvisForwardFlat(skeleton);
            Vector3 pelvisRightFlat = Vector3.Cross(Vector3.up, pelvisForwardFlat).normalized;
            float hipSignWidth = Mathf.Clamp(_profile.StanceWidthMeters * 0.28f, 0.08f, 0.16f);

            Vector3 pelvisPos = ResolvePelvisPosition(skeleton, defaultFloorY);

            SolveSingleLeg(
                isLeft: true,
                upperLegIdx: LeftUpperLegIndex,
                kneeIdx: LeftKneeIndex,
                ankleIdx: LeftAnkleIndex,
                ballIdx: LeftBallIndex,
                defaultHipPos: pelvisPos - pelvisRightFlat * hipSignWidth,
                pelvisForwardFlat: pelvisForwardFlat,
                pelvisRightFlat: pelvisRightFlat,
                skeleton: skeleton,
                defaultFloorY: defaultFloorY,
                deltaTime: deltaTime,
                timestampSeconds: timestampSeconds,
                state: ref _leftState,
                out VirtualTrackerSample leftFoot,
                out VirtualTrackerSample leftKnee);

            SolveSingleLeg(
                isLeft: false,
                upperLegIdx: RightUpperLegIndex,
                kneeIdx: RightKneeIndex,
                ankleIdx: RightAnkleIndex,
                ballIdx: RightBallIndex,
                defaultHipPos: pelvisPos + pelvisRightFlat * hipSignWidth,
                pelvisForwardFlat: pelvisForwardFlat,
                pelvisRightFlat: pelvisRightFlat,
                skeleton: skeleton,
                defaultFloorY: defaultFloorY,
                deltaTime: deltaTime,
                timestampSeconds: timestampSeconds,
                state: ref _rightState,
                out VirtualTrackerSample rightFoot,
                out VirtualTrackerSample rightKnee);

            frame.LeftFoot = leftFoot;
            frame.RightFoot = rightFoot;
            frame.LeftKnee = leftKnee;
            frame.RightKnee = rightKnee;
            return true;
        }

        private void SolveSingleLeg(
            bool isLeft,
            int upperLegIdx,
            int kneeIdx,
            int ankleIdx,
            int ballIdx,
            Vector3 defaultHipPos,
            Vector3 pelvisForwardFlat,
            Vector3 pelvisRightFlat,
            in BodySkeletonSnapshot skeleton,
            float defaultFloorY,
            float deltaTime,
            double timestampSeconds,
            ref FootState state,
            out VirtualTrackerSample footSample,
            out VirtualTrackerSample kneeSample)
        {
            VirtualTrackerId footId = isLeft ? VirtualTrackerId.LeftFoot : VirtualTrackerId.RightFoot;
            VirtualTrackerId kneeId = isLeft ? VirtualTrackerId.LeftKnee : VirtualTrackerId.RightKnee;

            Vector3 hipPos = defaultHipPos;
            if (skeleton.TryGetJoint(upperLegIdx, out BodyJointSample upperLeg) && upperLeg.PositionValid)
            {
                hipPos = upperLeg.WorldPosition;
            }

            bool hasSdkAnkle = skeleton.TryGetJoint(ankleIdx, out BodyJointSample sdkAnkle) && sdkAnkle.PositionValid;
            bool hasSdkKnee = skeleton.TryGetJoint(kneeIdx, out BodyJointSample sdkKnee) && sdkKnee.PositionValid;
            bool hasSdkBall = skeleton.TryGetJoint(ballIdx, out BodyJointSample sdkBall) && sdkBall.PositionValid;

            float ankleHeightOffset = Mathf.Max(0.03f, _profile.AnkleToFloorMeters);
            TrackingDataSource footSource;
            Vector3 rawTargetAnkle;
            Quaternion rawFootRot;
            Vector3 footForwardFlat = pelvisForwardFlat;

            if (hasSdkAnkle)
            {
                footSource = sdkAnkle.DataSource;
                rawTargetAnkle = sdkAnkle.WorldPosition;

                if (hasSdkBall)
                {
                    Vector3 toToe = sdkBall.WorldPosition - sdkAnkle.WorldPosition;
                    Vector3 flat = Vector3.ProjectOnPlane(toToe, Vector3.up);
                    if (flat.sqrMagnitude > 0.0004f)
                    {
                        footForwardFlat = flat.normalized;
                    }
                    Vector3 fwd3D = toToe.sqrMagnitude > 0.0004f ? toToe.normalized : footForwardFlat;
                    rawFootRot = Quaternion.LookRotation(fwd3D, Vector3.up);
                }
                else if (sdkAnkle.OrientationValid)
                {
                    rawFootRot = sdkAnkle.WorldRotation;
                    Vector3 flat = Vector3.ProjectOnPlane(rawFootRot * Vector3.forward, Vector3.up);
                    if (flat.sqrMagnitude > 0.0004f)
                    {
                        footForwardFlat = flat.normalized;
                    }
                }
                else
                {
                    rawFootRot = Quaternion.LookRotation(pelvisForwardFlat, Vector3.up);
                }
            }
            else
            {
                // True biomechanical fallback when SDK lower-body joints are missing: synthesize standing/crouching foot target beneath pelvis.
                footSource = TrackingDataSource.EngineBiomechanicalFallback;
                float sideSign = isLeft ? -1f : 1f;
                float spacing = Mathf.Max(0.14f, _profile.FootSpacingMeters) * 0.5f;
                float localFloorForFallback = QueryFloorY(hipPos, defaultFloorY);
                float maxReachY = Mathf.Max(0.25f, (_profile.ThighLengthMeters + _profile.ShinLengthMeters) * 0.96f);
                float fallbackAnkleY = Mathf.Max(localFloorForFallback + ankleHeightOffset, hipPos.y - maxReachY);
                rawTargetAnkle = new Vector3(
                    hipPos.x + pelvisRightFlat.x * spacing * sideSign,
                    fallbackAnkleY,
                    hipPos.z + pelvisRightFlat.z * spacing * sideSign);
                rawFootRot = Quaternion.LookRotation(pelvisForwardFlat, Vector3.up);
            }

            float localFloorY = QueryFloorY(rawTargetAnkle, defaultFloorY);

            // Depth API real-time leg/kick fusion with hysteresis, One-Euro filtering & 140 ms dropout hold
            DepthLegObservation depthLeg = isLeft ? _latestDepth.LeftLeg : _latestDepth.RightLeg;
            float dtSafe = Mathf.Max(deltaTime, 0.001f);
            bool depthIndicatesSwing = false;
            if (depthLeg.IsDetected)
            {
                float horizFromHip = Vector2.Distance(
                    new Vector2(depthLeg.FootWorldPosition.x, depthLeg.FootWorldPosition.z),
                    new Vector2(hipPos.x, hipPos.z));
                depthIndicatesSwing =
                    depthLeg.IsKneeDominant ||
                    depthLeg.FootWorldPosition.y > localFloorY + ankleHeightOffset + 0.095f ||
                    horizFromHip > 0.22f;
            }

            bool depthValidNow = _enableDepthLegFusion &&
                                 _latestDepth.IsReady &&
                                 depthLeg.IsDetected &&
                                 (depthIndicatesSwing || !state.IsFootLocked);

            if (depthValidNow)
            {
                state.ConsecutiveDepthFrames++;
                state.TimeSinceLastDepthHit = 0f;

                bool confirmedEntry = state.DepthBlendWeight > 0.25f ||
                                      state.ConsecutiveDepthFrames >= 2 ||
                                      depthLeg.PointCount >= 5;

                if (confirmedEntry)
                {
                    if (state.DepthBlendWeight <= 0.001f)
                    {
                        state.DepthFootFilter.Reset();
                        state.DepthKneeFilter.Reset();
                        state.LastDepthFootPos = depthLeg.FootWorldPosition;
                        state.LastDepthKneePos = depthLeg.KneeWorldPosition;
                        state.DepthFootVelocity = Vector3.zero;
                    }

                    Vector3 prevFiltered = state.LastDepthFootPos;
                    state.LastDepthFootPos = state.DepthFootFilter.Filter(
                        depthLeg.FootWorldPosition,
                        dtSafe,
                        _depthMinCutoffHz,
                        _depthBetaSpeed,
                        1.5f,
                        _depthStationaryDeadzoneMeters);

                    Vector3 rawVel = (state.LastDepthFootPos - prevFiltered) / dtSafe;
                    state.DepthFootVelocity = Vector3.ClampMagnitude(
                        Vector3.Lerp(state.DepthFootVelocity, rawVel, Mathf.Clamp01(dtSafe * 18f)),
                        8.0f);

                    if (depthLeg.HasKneeHint)
                    {
                        if (!state.LastDepthHasKnee)
                        {
                            state.DepthKneeFilter.Reset();
                        }
                        state.LastDepthKneePos = state.DepthKneeFilter.Filter(
                            depthLeg.KneeWorldPosition,
                            dtSafe,
                            _depthMinCutoffHz,
                            _depthBetaSpeed,
                            1.5f,
                            _depthStationaryDeadzoneMeters);
                    }

                    state.LastDepthHasKnee = depthLeg.HasKneeHint;
                    state.LastDepthIsKneeDominant = depthLeg.IsKneeDominant;
                    state.LastDepthConfidence = depthLeg.Confidence;
                    state.DepthBlendWeight = Mathf.MoveTowards(state.DepthBlendWeight, 1f, dtSafe * 16.0f);
                }
            }
            else
            {
                state.ConsecutiveDepthFrames = 0;
                state.TimeSinceLastDepthHit += dtSafe;

                if (_enableDepthLegFusion &&
                    state.DepthBlendWeight > 0.25f &&
                    state.TimeSinceLastDepthHit <= _depthDropoutHoldSeconds)
                {
                    // Short 140 ms dropout hold with exponentially damped velocity to eliminate frame-drop flicker
                    state.DepthFootVelocity *= Mathf.Exp(-12f * dtSafe);
                    state.LastDepthFootPos += state.DepthFootVelocity * dtSafe;
                    if (state.LastDepthFootPos.y < localFloorY + ankleHeightOffset)
                    {
                        state.LastDepthFootPos.y = localFloorY + ankleHeightOffset;
                    }
                }
                else
                {
                    state.DepthBlendWeight = Mathf.MoveTowards(state.DepthBlendWeight, 0f, dtSafe * 5.0f);
                    state.DepthFootVelocity = Vector3.zero;
                    if (state.DepthBlendWeight <= 0.001f)
                    {
                        state.DepthFootFilter.Reset();
                        state.DepthKneeFilter.Reset();
                    }
                }
            }

            bool holdingDropout = _enableDepthLegFusion &&
                                  state.DepthBlendWeight > 0.25f &&
                                  state.TimeSinceLastDepthHit <= _depthDropoutHoldSeconds;

            bool depthDrivingLeg = _enableDepthLegFusion && state.DepthBlendWeight > 0.01f;
            if (depthDrivingLeg)
            {
                rawTargetAnkle = Vector3.Lerp(rawTargetAnkle, state.LastDepthFootPos, state.DepthBlendWeight);
                Vector3 kickHoriz = Vector3.ProjectOnPlane(rawTargetAnkle - hipPos, Vector3.up);
                if (kickHoriz.sqrMagnitude > 0.015f)
                {
                    Vector3 kickFwd = kickHoriz.normalized;
                    footForwardFlat = Vector3.Slerp(footForwardFlat, kickFwd, state.DepthBlendWeight).normalized;
                    Quaternion kickRot = Quaternion.LookRotation(footForwardFlat, Vector3.up);
                    rawFootRot = Quaternion.Slerp(rawFootRot, kickRot, state.DepthBlendWeight);
                }
                localFloorY = QueryFloorY(rawTargetAnkle, defaultFloorY);
            }

            // Compute raw speed for stance/swing classification.
            float rawSpeed = 0f;
            if (state.HasPrevRaw && deltaTime > 0.0001f)
            {
                rawSpeed = Vector3.Distance(rawTargetAnkle, state.PrevRawPos) / deltaTime;
                if (rawSpeed > _maxPlausibleSpeedMps)
                    rawSpeed = 0f;
            }
            state.PrevRawPos = rawTargetAnkle;
            state.HasPrevRaw = true;

            float rawSoleClearance = rawTargetAnkle.y - ankleHeightOffset - localFloorY;

            // Hysteresis state machine for Stance (locked to floor) vs Swing (free 3D motion).
            bool allowFloorLock = _enableFootLocking && hasSdkAnkle && !(state.DepthBlendWeight > 0.15f && (depthIndicatesSwing || holdingDropout));
            if (!allowFloorLock)
            {
                state.IsFootLocked = false;
            }
            else
            {
                if (!state.IsFootLocked)
                {
                    if (rawSoleClearance <= _stanceEnterSoleHeightMeters && rawSpeed <= _stanceEnterSpeedMps)
                    {
                        state.IsFootLocked = true;
                        state.LockedXZ = new Vector2(rawTargetAnkle.x, rawTargetAnkle.z);
                        state.LockedYawDeg = Quaternion.LookRotation(footForwardFlat, Vector3.up).eulerAngles.y;
                    }
                }
                else
                {
                    float leashDist = Vector2.Distance(new Vector2(rawTargetAnkle.x, rawTargetAnkle.z), state.LockedXZ);
                    if (rawSoleClearance > _stanceExitSoleHeightMeters ||
                        rawSpeed > _stanceExitSpeedMps ||
                        leashDist > _footLockMaxLeashMeters)
                    {
                        state.IsFootLocked = false;
                    }
                }
            }

            Vector3 targetAnkle = rawTargetAnkle;
            bool footLocked = state.IsFootLocked;
            if (footLocked)
            {
                // Anchor horizontal foot position and gently blend yaw on the floor plane.
                targetAnkle.x = Mathf.Lerp(state.LockedXZ.x, rawTargetAnkle.x, 0.12f);
                targetAnkle.z = Mathf.Lerp(state.LockedXZ.y, rawTargetAnkle.z, 0.12f);
                targetAnkle.y = localFloorY + ankleHeightOffset;
            }

            bool floorClamped = false;
            if (_enableFloorClamp && targetAnkle.y < localFloorY + ankleHeightOffset)
            {
                targetAnkle.y = localFloorY + ankleHeightOffset;
                floorClamped = true;
            }

            // Solve analytic 2-bone IK (Hip -> Knee -> Ankle).
            Vector3 solvedAnkle = targetAnkle;
            Vector3 solvedKnee;
            Quaternion solvedKneeRot;
            bool ikSolved = false;
            bool depthProvenanceActive = _enableDepthLegFusion && state.DepthBlendWeight >= 0.35f;

            bool useKneeHint = hasSdkKnee || (depthProvenanceActive && state.LastDepthHasKnee);
            Vector3 kneeHintPos = hasSdkKnee ? sdkKnee.WorldPosition : Vector3.zero;
            if (depthProvenanceActive && state.LastDepthHasKnee)
            {
                float hintWeight = state.LastDepthIsKneeDominant
                    ? state.DepthBlendWeight
                    : state.DepthBlendWeight * 0.8f;
                kneeHintPos = hasSdkKnee
                    ? Vector3.Lerp(sdkKnee.WorldPosition, state.LastDepthKneePos, hintWeight)
                    : state.LastDepthKneePos;
            }

            if (_enableAnatomicalIk)
            {
                Vector3 anteriorPole = Vector3.Slerp(pelvisForwardFlat, footForwardFlat, 0.55f).normalized;
                LegIkResult ik = AnalyticLegIkSolver.Solve(
                    hipPos,
                    targetAnkle,
                    kneeHintPos,
                    useKneeHint,
                    anteriorPole,
                    _profile.ThighLengthMeters,
                    _profile.ShinLengthMeters);

                solvedKnee = ik.KneePosition;
                solvedKneeRot = ik.KneeRotation;
                solvedAnkle = ik.AnklePosition;
                ikSolved = true;

                if (_enableFloorClamp && solvedAnkle.y < localFloorY + ankleHeightOffset)
                {
                    solvedAnkle.y = localFloorY + ankleHeightOffset;
                    floorClamped = true;
                }
                if (_enableFloorClamp && solvedKnee.y < localFloorY + 0.05f)
                {
                    solvedKnee.y = localFloorY + 0.05f;
                }
            }
            else
            {
                solvedKnee = useKneeHint
                    ? kneeHintPos
                    : Vector3.Lerp(hipPos, solvedAnkle, 0.5f) + pelvisForwardFlat * 0.08f;
                solvedKneeRot = hasSdkKnee && sdkKnee.OrientationValid ? sdkKnee.WorldRotation : Quaternion.LookRotation(pelvisForwardFlat, Vector3.up);
            }

            // Compute final foot orientation: flat onto floor during stance, full 3D during swing.
            Quaternion yawOffset = Quaternion.Euler(0f, _profile.InitialFootYawOffsetDegrees, 0f);
            Quaternion footBaseRot = footLocked
                ? Quaternion.Euler(0f, state.LockedYawDeg, 0f)
                : rawFootRot;
            Quaternion finalFootRot = footBaseRot * yawOffset * _profile.GetRotationOffset(footId);
            Vector3 finalFootPos = solvedAnkle + (finalFootRot * _profile.GetPositionOffset(footId));

            if (_enableFloorClamp && finalFootPos.y < localFloorY + ankleHeightOffset)
            {
                finalFootPos.y = localFloorY + ankleHeightOffset;
                floorClamped = true;
            }

            Quaternion finalKneeRot = solvedKneeRot * _profile.GetRotationOffset(kneeId);
            Vector3 finalKneePos = solvedKnee + (finalKneeRot * _profile.GetPositionOffset(kneeId));

            Vector3 footVel = Vector3.zero;
            Vector3 kneeVel = Vector3.zero;
            if (state.HasPrevOutput && deltaTime > 0.0001f)
            {
                Vector3 fv = (finalFootPos - state.PrevFootPos) / deltaTime;
                if (fv.magnitude <= _maxPlausibleSpeedMps)
                    footVel = fv;

                Vector3 kv = (finalKneePos - state.PrevKneePos) / deltaTime;
                if (kv.magnitude <= _maxPlausibleSpeedMps)
                    kneeVel = kv;
            }
            state.PrevFootPos = finalFootPos;
            state.PrevKneePos = finalKneePos;
            state.HasPrevOutput = true;

            float finalSoleClearance = finalFootPos.y - ankleHeightOffset - localFloorY;
            bool isGrounded = footLocked || finalSoleClearance <= _stanceEnterSoleHeightMeters;

            TrackingDataSource finalFootSource;
            TrackingDataSource finalKneeSource;
            TrackerValidityState footValidity;
            TrackerValidityState kneeValidity;
            float footConfidence;
            float kneeConfidence;

            if (depthProvenanceActive)
            {
                finalFootSource = TrackingDataSource.DepthObserved;
                finalKneeSource = TrackingDataSource.DepthObserved;
                footValidity = TrackerValidityState.Valid;
                kneeValidity = TrackerValidityState.Valid;
                float dConf = Mathf.Max(state.LastDepthConfidence, 0.65f);
                footConfidence = dConf;
                kneeConfidence = dConf;
            }
            else
            {
                finalFootSource = footSource;
                finalKneeSource = hasSdkKnee ? sdkKnee.DataSource : TrackingDataSource.EngineBiomechanicalFallback;
                footValidity = hasSdkAnkle
                    ? (skeleton.Confidence >= 0.35f ? TrackerValidityState.Valid : TrackerValidityState.Degraded)
                    : TrackerValidityState.Degraded;
                kneeValidity = hasSdkKnee || ikSolved
                    ? (hasSdkKnee && skeleton.Confidence >= 0.35f ? TrackerValidityState.Valid : TrackerValidityState.Degraded)
                    : TrackerValidityState.Invalid;
                footConfidence = hasSdkAnkle ? skeleton.Confidence : 0.45f;
                kneeConfidence = hasSdkKnee ? skeleton.Confidence : (ikSolved ? Mathf.Max(0.45f, skeleton.Confidence * 0.7f) : 0f);
            }

            footSample = new VirtualTrackerSample
            {
                Id = footId,
                Position = finalFootPos,
                Rotation = finalFootRot,
                LinearVelocity = footVel,
                AngularVelocity = Vector3.zero,
                TimestampSeconds = timestampSeconds,
                Validity = footValidity,
                Source = finalFootSource,
                Confidence = footConfidence,
                Space = CoordinateSpace.UnityWorldSpace,
                HeightAboveFloorMeters = finalSoleClearance,
                IsGrounded = isGrounded,
                FloorClamped = floorClamped,
                IsFootLocked = footLocked,
                IsIkSolved = ikSolved
            };

            kneeSample = new VirtualTrackerSample
            {
                Id = kneeId,
                Position = finalKneePos,
                Rotation = finalKneeRot,
                LinearVelocity = kneeVel,
                AngularVelocity = Vector3.zero,
                TimestampSeconds = timestampSeconds,
                Validity = kneeValidity,
                Source = finalKneeSource,
                Confidence = kneeConfidence,
                Space = CoordinateSpace.UnityWorldSpace,
                HeightAboveFloorMeters = finalKneePos.y - localFloorY,
                IsGrounded = false,
                FloorClamped = false,
                IsFootLocked = false,
                IsIkSolved = ikSolved
            };
        }

        private float QueryFloorY(Vector3 worldPos, float defaultFloorY)
        {
            if (_spatialProvider != null && _spatialProvider.TryQueryFloorHeightAt(worldPos, out float queriedY, out _))
            {
                return queriedY;
            }
            return defaultFloorY;
        }

        private Vector3 ResolvePelvisPosition(in BodySkeletonSnapshot skeleton, float floorY)
        {
            if (skeleton.TryGetJoint(HipsJointIndex, out BodyJointSample hips) && hips.PositionValid)
            {
                return hips.WorldPosition;
            }

            if (skeleton.TryGetJoint(HeadJointIndex, out BodyJointSample head) && head.PositionValid)
            {
                float approxDrop = Mathf.Clamp(_profile.UserHeightMeters * 0.41f, 0.5f, 0.9f);
                return new Vector3(head.WorldPosition.x, Mathf.Max(floorY + 0.45f, head.WorldPosition.y - approxDrop), head.WorldPosition.z);
            }

            return new Vector3(0f, floorY + _profile.HipHeightMeters, 0f);
        }

        private static Vector3 ResolvePelvisForwardFlat(in BodySkeletonSnapshot skeleton)
        {
            Vector3 rightFlat = Vector3.zero;

            if (skeleton.TryGetJoint(LeftUpperLegIndex, out BodyJointSample lUl) && lUl.PositionValid &&
                skeleton.TryGetJoint(RightUpperLegIndex, out BodyJointSample rUl) && rUl.PositionValid)
            {
                Vector3 span = Vector3.ProjectOnPlane(rUl.WorldPosition - lUl.WorldPosition, Vector3.up);
                if (span.sqrMagnitude > 0.05f * 0.05f)
                    rightFlat = span.normalized;
            }

            if (rightFlat.sqrMagnitude < 0.001f &&
                skeleton.TryGetJoint(LeftArmUpperIndex, out BodyJointSample lArm) && lArm.PositionValid &&
                skeleton.TryGetJoint(RightArmUpperIndex, out BodyJointSample rArm) && rArm.PositionValid)
            {
                Vector3 span = Vector3.ProjectOnPlane(rArm.WorldPosition - lArm.WorldPosition, Vector3.up);
                if (span.sqrMagnitude > 0.08f * 0.08f)
                    rightFlat = span.normalized;
            }

            if (rightFlat.sqrMagnitude < 0.001f &&
                skeleton.TryGetJoint(LeftShoulderIndex, out BodyJointSample lSh) && lSh.PositionValid &&
                skeleton.TryGetJoint(RightShoulderIndex, out BodyJointSample rSh) && rSh.PositionValid)
            {
                Vector3 span = Vector3.ProjectOnPlane(rSh.WorldPosition - lSh.WorldPosition, Vector3.up);
                if (span.sqrMagnitude > 0.08f * 0.08f)
                    rightFlat = span.normalized;
            }

            if (rightFlat.sqrMagnitude < 0.001f && Camera.main != null)
            {
                Vector3 camRight = Vector3.ProjectOnPlane(Camera.main.transform.right, Vector3.up);
                if (camRight.sqrMagnitude > 0.001f)
                    rightFlat = camRight.normalized;
            }

            if (rightFlat.sqrMagnitude > 0.001f)
            {
                Vector3 fwd = Vector3.Cross(rightFlat, Vector3.up);
                if (fwd.sqrMagnitude > 0.001f)
                {
                    fwd.Normalize();
                    if (Camera.main != null)
                    {
                        Vector3 camFwd = Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up);
                        if (camFwd.sqrMagnitude > 0.001f && Vector3.Dot(fwd, camFwd.normalized) < -0.25f)
                        {
                            fwd = -fwd;
                        }
                    }
                    return fwd;
                }
            }

            return Vector3.forward;
        }
    }
}
