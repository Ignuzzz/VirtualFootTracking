using UnityEngine;

namespace VirtualFootTracking.Prediction
{
    /// <summary>
    /// Zero-allocation 3D One-Euro adaptive low-pass filter with stationary deadzone.
    /// Suppresses depth/sensor micro-tremor while still responding rapidly to fast kicks.
    /// </summary>
    public struct OneEuroFilter3D
    {
        private bool _initialized;
        private Vector3 _xPrev;
        private Vector3 _dxPrev;

        public void Reset()
        {
            _initialized = false;
            _xPrev = Vector3.zero;
            _dxPrev = Vector3.zero;
        }

        public Vector3 Filter(
            Vector3 x,
            float dt,
            float minCutoff = 2.0f,
            float beta = 14.0f,
            float dCutoff = 1.5f,
            float stationaryDeadzoneMeters = 0.012f)
        {
            float clampedDt = Mathf.Clamp(dt, 0.001f, 0.1f);

            if (!_initialized)
            {
                _initialized = true;
                _xPrev = x;
                _dxPrev = Vector3.zero;
                return x;
            }

            Vector3 dx = (x - _xPrev) / clampedDt;
            float alphaD = Alpha(dCutoff, clampedDt);
            Vector3 dxHat = Vector3.Lerp(_dxPrev, dx, alphaD);
            float speed = dxHat.magnitude;

            if (stationaryDeadzoneMeters > 0f &&
                speed < 0.15f &&
                Vector3.Distance(x, _xPrev) < stationaryDeadzoneMeters)
            {
                _dxPrev *= 0.5f;
                return _xPrev;
            }

            float cutoff = Mathf.Min(45f, Mathf.Max(0.05f, minCutoff) + Mathf.Max(0f, beta) * speed);
            float alpha = Alpha(cutoff, clampedDt);
            Vector3 xHat = Vector3.Lerp(_xPrev, x, alpha);

            _xPrev = xHat;
            _dxPrev = dxHat;
            return xHat;
        }

        private static float Alpha(float cutoffHz, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * Mathf.Max(0.01f, cutoffHz));
            return 1f / (1f + tau / dt);
        }
    }
}
