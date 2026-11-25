using UnityEngine;
using VirtualFootTracking.Core;

namespace VirtualFootTracking.Prediction
{
    /// <summary>
    /// Combat-responsive 3D constant-velocity Kalman filter with adaptive process noise
    /// and bounded damped-velocity extrapolation during temporary tracking loss.
    /// Explicitly tags extrapolated samples as EngineMotionPredicted / TemporarilyLost.
    /// </summary>
    public class AdaptiveKalmanMotionPredictor : MonoBehaviour, IMotionPredictor
    {
        [Header("Kalman Parameters")]
        [SerializeField] private bool _enabled = true;
        [SerializeField] private float _stationaryProcessNoiseQ = 0.25f;
        [SerializeField] private float _combatProcessNoiseQ = 320.0f;
        [SerializeField] private float _measurementNoiseR = 0.00015f;
        [SerializeField] private float _depthMeasurementNoiseR = 0.0018f;
        [SerializeField] private float _maxPredictionHorizonSeconds = 0.35f;
        [SerializeField] private float _velocityDampingPerSec = 6.0f;
        [SerializeField] private bool _clampPredictedToFloor = true;

        private struct TrackerFilterState
        {
            public bool Initialized;
            public Vector3 Position;
            public Vector3 Velocity;
            public Quaternion Rotation;
            public float P00;
            public float P01;
            public float P11;
            public float LostDuration;
            public float LastConfidence;
            public float LastAnkleOffsetGuess;
        }

        private readonly TrackerFilterState[] _states = new TrackerFilterState[4];
        private float _floorY;

        public string PredictorName => _enabled ? "Phase2.AdaptiveKalmanMotionPredictor" : "Phase2.KalmanBypassed";

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public void SetFloorHeight(float floorY)
        {
            _floorY = floorY;
        }

        public void ResetState()
        {
            for (int i = 0; i < _states.Length; i++)
            {
                _states[i] = default;
            }
        }

        public VirtualTrackerSample ProcessSample(in VirtualTrackerSample input, float deltaTime)
        {
            if (!_enabled)
                return input;

            int idx = Mathf.Clamp((int)input.Id, 0, 3);
            ref TrackerFilterState s = ref _states[idx];
            float dt = Mathf.Clamp(deltaTime, 0.001f, 0.1f);

            bool isFoot = input.Id == VirtualTrackerId.LeftFoot || input.Id == VirtualTrackerId.RightFoot;
            float minY = _floorY + (isFoot ? 0.03f : 0.05f);

            if (input.Validity == TrackerValidityState.Invalid)
            {
                if (s.Initialized && s.LostDuration < _maxPredictionHorizonSeconds)
                {
                    s.LostDuration += dt;
                    float decay = Mathf.Exp(-_velocityDampingPerSec * dt);
                    s.Velocity *= decay;
                    s.Position += s.Velocity * dt;

                    if (_clampPredictedToFloor && s.Position.y < minY)
                    {
                        s.Position.y = minY;
                        if (s.Velocity.y < 0f)
                            s.Velocity.y = 0f;
                    }

                    float horizonFactor = Mathf.Clamp01(1f - (s.LostDuration / _maxPredictionHorizonSeconds));
                    return new VirtualTrackerSample
                    {
                        Id = input.Id,
                        Position = s.Position,
                        Rotation = s.Rotation,
                        LinearVelocity = s.Velocity,
                        AngularVelocity = Vector3.zero,
                        TimestampSeconds = input.TimestampSeconds,
                        Validity = TrackerValidityState.TemporarilyLost,
                        Source = TrackingDataSource.EngineMotionPredicted,
                        Confidence = Mathf.Max(0.1f, s.LastConfidence * horizonFactor * 0.7f),
                        Space = input.Space,
                        HeightAboveFloorMeters = s.Position.y - _floorY - (isFoot ? s.LastAnkleOffsetGuess : 0f),
                        IsGrounded = isFoot && (s.Position.y - _floorY <= 0.08f),
                        FloorClamped = _clampPredictedToFloor && s.Position.y <= minY + 0.001f,
                        IsFootLocked = false,
                        IsIkSolved = false
                    };
                }

                return input;
            }

            if (!s.Initialized)
            {
                s.Initialized = true;
                s.Position = input.Position;
                s.Velocity = input.LinearVelocity;
                s.Rotation = input.Rotation;
                s.P00 = 0.005f;
                s.P01 = 0f;
                s.P11 = 0.5f;
                s.LostDuration = 0f;
                s.LastConfidence = input.Confidence;
                s.LastAnkleOffsetGuess = isFoot ? Mathf.Max(0.03f, input.Position.y - _floorY - input.HeightAboveFloorMeters) : 0f;
                return input;
            }

            // 1. Predict step (constant-velocity state transition)
            Vector3 pPred = s.Position + s.Velocity * dt;
            Vector3 vPred = s.Velocity;

            Vector3 innovVec = input.Position - pPred;
            float innovMag = innovVec.magnitude;
            float speedMag = vPred.magnitude;

            // Scale process noise smoothly from planted stability to rapid combat responsiveness
            float activity = Mathf.Clamp01(Mathf.Max(innovMag / 0.035f, speedMag / 0.85f));
            float q = Mathf.Lerp(_stationaryProcessNoiseQ, _combatProcessNoiseQ, activity * activity);

            float dt2 = dt * dt;
            float dt3 = dt2 * dt;
            float dt4 = dt2 * dt2;

            float p00Pred = s.P00 + 2f * dt * s.P01 + dt2 * s.P11 + q * 0.25f * dt4;
            float p01Pred = s.P01 + dt * s.P11 + q * 0.5f * dt3;
            float p11Pred = s.P11 + q * dt2;

            // Increase measurement noise slightly when input is degraded/fallback, and adaptively damp stationary Depth jitter
            bool isDepth = input.Source == TrackingDataSource.DepthObserved;
            float r;
            if (isDepth)
            {
                r = Mathf.Lerp(_depthMeasurementNoiseR, _measurementNoiseR * 3.0f, activity);
            }
            else
            {
                r = input.Validity == TrackerValidityState.Degraded
                    ? _measurementNoiseR * 2.5f
                    : _measurementNoiseR;
            }

            // 2. Measurement update
            float sCov = Mathf.Max(1e-6f, p00Pred + r);
            float k0 = p00Pred / sCov;
            float k1 = p01Pred / sCov;

            Vector3 pUpdated = pPred + k0 * innovVec;
            Vector3 vUpdated = Vector3.ClampMagnitude(vPred + k1 * innovVec, 22f);

            // Keep foot-locked stance crisp without filter lag, and hold stationary Depth poses steady
            if (input.IsFootLocked && innovMag < 0.015f)
            {
                pUpdated = input.Position;
                vUpdated = Vector3.zero;
            }
            else if (isDepth && innovMag < 0.008f && speedMag < 0.18f)
            {
                pUpdated = s.Position;
                vUpdated = s.Velocity * 0.4f;
            }

            if ((input.FloorClamped || _clampPredictedToFloor) && pUpdated.y < minY)
            {
                pUpdated.y = minY;
            }

            float rotRate = isDepth ? Mathf.Lerp(8f, 24f, activity) : Mathf.Lerp(14f, 40f, activity);
            Quaternion qUpdated = Quaternion.Slerp(s.Rotation, input.Rotation, Mathf.Clamp01(rotRate * dt));

            s.Position = pUpdated;
            s.Velocity = vUpdated;
            s.Rotation = qUpdated;
            s.P00 = Mathf.Max(1e-6f, (1f - k0) * p00Pred);
            s.P01 = (1f - k0) * p01Pred;
            s.P11 = Mathf.Max(1e-4f, p11Pred - k1 * p01Pred);
            s.LostDuration = 0f;
            s.LastConfidence = input.Confidence;
            if (isFoot)
            {
                s.LastAnkleOffsetGuess = Mathf.Max(0.03f, input.Position.y - _floorY - input.HeightAboveFloorMeters);
            }

            VirtualTrackerSample output = input;
            output.Position = pUpdated;
            output.Rotation = qUpdated;
            output.LinearVelocity = vUpdated;
            return output;
        }
    }
}
