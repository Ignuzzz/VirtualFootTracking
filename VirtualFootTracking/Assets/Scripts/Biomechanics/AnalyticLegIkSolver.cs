using UnityEngine;

namespace VirtualFootTracking.Biomechanics
{
    /// <summary>
    /// Output of the analytic 2-bone leg inverse kinematics pass.
    /// </summary>
    public struct LegIkResult
    {
        public Vector3 KneePosition;
        public Quaternion KneeRotation;
        public Vector3 AnklePosition;
        public float KneeFlexionDegrees;
        public bool WasReachClamped;
    }

    /// <summary>
    /// Zero-allocation analytic 2-bone IK solver (Hip -> Knee -> Ankle) enforcing
    /// calibrated thigh/shin lengths and anatomical knee flexion limits.
    /// </summary>
    public static class AnalyticLegIkSolver
    {
        private const float MinSegmentLength = 0.15f;
        private const float MinFlexionDegrees = 2.0f;
        private const float MaxFlexionDegrees = 148.0f;

        public static LegIkResult Solve(
            Vector3 hipPosition,
            Vector3 targetAnklePosition,
            Vector3 rawKneeHintPosition,
            bool hasKneeHint,
            Vector3 anteriorReferenceForward,
            float thighLength,
            float shinLength)
        {
            float l1 = Mathf.Max(thighLength, MinSegmentLength);
            float l2 = Mathf.Max(shinLength, MinSegmentLength);

            // Compute anatomical reach bounds from knee flexion limits [2 deg .. 148 deg].
            float minInteriorAngleRad = (180f - MaxFlexionDegrees) * Mathf.Deg2Rad;
            float maxInteriorAngleRad = (180f - MinFlexionDegrees) * Mathf.Deg2Rad;

            float minReach = Mathf.Sqrt(Mathf.Max(0.0025f, l1 * l1 + l2 * l2 - 2f * l1 * l2 * Mathf.Cos(minInteriorAngleRad)));
            minReach = Mathf.Max(minReach, Mathf.Abs(l1 - l2) + 0.03f);
            float maxReach = Mathf.Sqrt(Mathf.Max(0.0025f, l1 * l1 + l2 * l2 - 2f * l1 * l2 * Mathf.Cos(maxInteriorAngleRad)));
            maxReach = Mathf.Min(maxReach, (l1 + l2) * 0.998f);

            Vector3 hipToAnkle = targetAnklePosition - hipPosition;
            float rawDistance = hipToAnkle.magnitude;
            Vector3 reachDir = rawDistance > 0.0005f ? (hipToAnkle / rawDistance) : Vector3.down;

            float clampedDistance = Mathf.Clamp(rawDistance, minReach, maxReach);
            bool reachClamped = Mathf.Abs(clampedDistance - rawDistance) > 0.0005f;
            Vector3 solvedAnkle = hipPosition + reachDir * clampedDistance;

            // Law of cosines for hip->knee angle relative to hip->ankle axis.
            float cosAlpha = Mathf.Clamp((l1 * l1 + clampedDistance * clampedDistance - l2 * l2) / (2f * l1 * clampedDistance), -1f, 1f);
            float alongReach = l1 * cosAlpha;
            float perpReach = l1 * Mathf.Sqrt(Mathf.Max(0f, 1f - cosAlpha * cosAlpha));

            // Construct anatomically valid anterior pole orthogonal to reachDir.
            Vector3 safeForward = anteriorReferenceForward.sqrMagnitude > 0.001f
                ? anteriorReferenceForward.normalized
                : Vector3.forward;

            // Adding slight upward bias guarantees sitting and high straight-front kicks bend upward/anteriorly, never backward.
            Vector3 anatPole = Vector3.ProjectOnPlane(safeForward + Vector3.up * 0.35f, reachDir);
            if (anatPole.sqrMagnitude < 0.0001f)
            {
                anatPole = Vector3.ProjectOnPlane(Vector3.up, reachDir);
                if (anatPole.sqrMagnitude < 0.0001f)
                {
                    anatPole = Vector3.ProjectOnPlane(Vector3.right, reachDir);
                }
            }
            anatPole.Normalize();

            Vector3 bendDir = anatPole;
            if (hasKneeHint)
            {
                Vector3 hintPerp = Vector3.ProjectOnPlane(rawKneeHintPosition - hipPosition, reachDir);
                if (hintPerp.sqrMagnitude > 0.0001f)
                {
                    Vector3 hintNorm = hintPerp.normalized;
                    float alignment = Vector3.Dot(hintNorm, anatPole);
                    // Reject inverted/hyperextended SDK hints that point behind the knee plane.
                    if (alignment >= -0.15f)
                    {
                        float blend = Mathf.InverseLerp(-0.15f, 0.35f, alignment) * 0.72f;
                        bendDir = Vector3.Slerp(anatPole, hintNorm, blend).normalized;
                    }
                }
            }

            Vector3 solvedKnee = hipPosition + reachDir * alongReach + bendDir * perpReach;

            // Interior angle at knee and anatomical flexion = 180 - interior.
            float cosBeta = Mathf.Clamp((l1 * l1 + l2 * l2 - clampedDistance * clampedDistance) / (2f * l1 * l2), -1f, 1f);
            float flexionDeg = 180f - Mathf.Acos(cosBeta) * Mathf.Rad2Deg;

            // Build stable knee orientation: +Y along thigh toward hip, +Z along knee bend direction.
            Vector3 thighUp = (hipPosition - solvedKnee);
            if (thighUp.sqrMagnitude < 0.0001f)
                thighUp = Vector3.up;
            else
                thighUp.Normalize();

            Vector3 kneeFwd = Vector3.ProjectOnPlane(bendDir, thighUp);
            if (kneeFwd.sqrMagnitude < 0.0001f)
                kneeFwd = Vector3.ProjectOnPlane(safeForward, thighUp);
            if (kneeFwd.sqrMagnitude < 0.0001f)
                kneeFwd = Vector3.forward;
            kneeFwd.Normalize();

            Quaternion kneeRotation = Quaternion.LookRotation(kneeFwd, thighUp);

            return new LegIkResult
            {
                KneePosition = solvedKnee,
                KneeRotation = kneeRotation,
                AnklePosition = solvedAnkle,
                KneeFlexionDegrees = flexionDeg,
                WasReachClamped = reachClamped
            };
        }
    }
}
