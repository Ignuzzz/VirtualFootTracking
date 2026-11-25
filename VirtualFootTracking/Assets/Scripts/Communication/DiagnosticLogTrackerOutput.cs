using UnityEngine;
using VirtualFootTracking.Core;

namespace VirtualFootTracking.Communication
{
    /// <summary>
    /// Standalone Phase 1 tracker sink storing recent frames in memory and optionally emitting periodic logcat telemetry.
    /// </summary>
    public class DiagnosticLogTrackerOutput : MonoBehaviour, ITrackerOutput
    {
        [SerializeField]
        private float _logSummaryIntervalSeconds = 5f;

        private const int RingCapacity = 120;
        private readonly VirtualTrackerFrame[] _history = new VirtualTrackerFrame[RingCapacity];
        private int _writeIndex;
        private int _count;
        private float _lastLogTime;

        public string OutputName => "Phase1.DiagnosticLogTrackerOutput";
        public bool IsActive { get; private set; }
        public VirtualTrackerFrame LatestFrame { get; private set; }

        public void Initialize()
        {
            IsActive = true;
            _writeIndex = 0;
            _count = 0;
            _lastLogTime = Time.realtimeSinceStartup;
        }

        public void Publish(in VirtualTrackerFrame frame)
        {
            if (!IsActive)
                return;

            LatestFrame = frame;
            _history[_writeIndex] = frame;
            _writeIndex = (_writeIndex + 1) % RingCapacity;
            if (_count < RingCapacity)
                _count++;

            if (_logSummaryIntervalSeconds > 0f && Time.realtimeSinceStartup - _lastLogTime >= _logSummaryIntervalSeconds)
            {
                _lastLogTime = Time.realtimeSinceStartup;
                Debug.Log(
                    $"[VFT] Frame={frame.FrameIndex} FloorY={frame.FloorHeightWorldY:F3} | " +
                    $"LF={frame.LeftFoot.Position:F2}({frame.LeftFoot.GetShortSourceTag()}) " +
                    $"RF={frame.RightFoot.Position:F2}({frame.RightFoot.GetShortSourceTag()}) " +
                    $"LK={frame.LeftKnee.Position:F2}({frame.LeftKnee.GetShortSourceTag()}) " +
                    $"RK={frame.RightKnee.Position:F2}({frame.RightKnee.GetShortSourceTag()})");
            }
        }

        public int GetHistoryCount() => _count;

        public void Shutdown()
        {
            IsActive = false;
        }
    }
}
