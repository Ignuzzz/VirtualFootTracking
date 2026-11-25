using UnityEngine;
using VirtualFootTracking.Core;

namespace VirtualFootTracking.Prediction
{
    /// <summary>
    /// Contract for short-horizon motion filtering and occlusion prediction (Phase 2 Kalman / velocity predictor).
    /// </summary>
    public interface IMotionPredictor
    {
        string PredictorName { get; }
        void ResetState();
        VirtualTrackerSample ProcessSample(in VirtualTrackerSample input, float deltaTime);
    }

    /// <summary>
    /// Phase 1 identity pass-through predictor. Preserves raw SDK timing and errors for honest evaluation.
    /// </summary>
    public class NullMotionPredictor : MonoBehaviour, IMotionPredictor
    {
        public string PredictorName => "Phase1.NullMotionPredictor";

        public void ResetState()
        {
        }

        public VirtualTrackerSample ProcessSample(in VirtualTrackerSample input, float deltaTime)
        {
            return input;
        }
    }
}
