using System;
using UnityEngine;
using VirtualFootTracking.Core;

namespace VirtualFootTracking.Tracking
{
    /// <summary>
    /// Meta Quest 3S Body Tracking provider backed by OVRBody / OVRPlugin OpenXR FullBody API.
    /// Explicitly tags lower-body joints (70..83) as SDK Generative Legs estimates.
    /// </summary>
    public class MetaOvrBodyTrackingProvider : MonoBehaviour, IBodyTrackingProvider
    {
        private const int MaxFullBodyJointCount = (int)OVRPlugin.BoneId.FullBody_End; // 84

        [SerializeField]
        private OVRBody _ovrBody;

        [SerializeField]
        private OVRCameraRig _cameraRig;

        [SerializeField]
        private OVRPlugin.BodyTrackingFidelity2 _requestedFidelity = OVRPlugin.BodyTrackingFidelity2.High;

        private BodyJointSample[] _jointBuffer;
        private string _statusMessage = "Not initialized";
        private bool _permissionRequested;

        public bool IsInitialized { get; private set; }
        public bool IsPermissionGranted => OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.BodyTracking);
        public string StatusMessage => _statusMessage;

        public void Initialize()
        {
            if (_ovrBody == null)
            {
                _ovrBody = FindAnyObjectByType<OVRBody>();
            }
            if (_cameraRig == null)
            {
                _cameraRig = FindAnyObjectByType<OVRCameraRig>();
            }

            _jointBuffer = new BodyJointSample[MaxFullBodyJointCount];
            OVRPermissionsRequester.PermissionGranted -= OnPermissionGranted;
            OVRPermissionsRequester.PermissionGranted += OnPermissionGranted;

            ConfigureOvrBodySettings();
            IsInitialized = true;

            if (!IsPermissionGranted && !_permissionRequested)
            {
                RequestPermission();
            }
            else
            {
                _statusMessage = _ovrBody != null
                    ? "Initialized (FullBody + High Fidelity IOBT requested)"
                    : "OVRBody component missing from scene";
            }
        }

        public void RequestPermission()
        {
            _permissionRequested = true;
            if (!IsPermissionGranted)
            {
                _statusMessage = "Requesting com.oculus.permission.BODY_TRACKING";
                OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.BodyTracking });
            }
        }

        public bool TryGetLatestSnapshot(out BodySkeletonSnapshot snapshot)
        {
            snapshot = default;

            if (!IsInitialized)
            {
                _statusMessage = "Provider not initialized";
                return false;
            }

            if (!IsPermissionGranted)
            {
                _statusMessage = "BODY_TRACKING permission not granted";
                return false;
            }

            if (_ovrBody == null)
            {
                _statusMessage = "OVRBody missing in scene";
                return false;
            }

            OVRPlugin.BodyState? maybeState = _ovrBody.BodyState;
            if (!maybeState.HasValue || maybeState.Value.JointLocations == null || maybeState.Value.JointLocations.Length == 0)
            {
                _statusMessage = "Waiting for OVRBody OpenXR FullBody frames";
                return false;
            }

            OVRPlugin.BodyState state = maybeState.Value;
            Transform trackingSpace = _cameraRig != null ? _cameraRig.trackingSpace : null;
            int count = Mathf.Min(state.JointLocations.Length, _jointBuffer.Length);

            for (int i = 0; i < count; i++)
            {
                OVRPlugin.BodyJointLocation loc = state.JointLocations[i];
                bool posValid = loc.PositionValid;
                bool oriValid = loc.OrientationValid;
                bool posTracked = (loc.LocationFlags & OVRPlugin.SpaceLocationFlags.PositionTracked) != 0;
                bool oriTracked = (loc.LocationFlags & OVRPlugin.SpaceLocationFlags.OrientationTracked) != 0;

                // Explicit coordinate conversion: OVR right-handed tracking space -> Unity left-handed tracking space -> Unity world space
                Vector3 tsPos = posValid ? OVRExtensions.FromFlippedZVector3f(loc.Pose.Position) : Vector3.zero;
                Quaternion tsRot = oriValid ? OVRExtensions.FromFlippedZQuatf(loc.Pose.Orientation) : Quaternion.identity;
                Vector3 worldPos = trackingSpace != null ? trackingSpace.TransformPoint(tsPos) : tsPos;
                Quaternion worldRot = trackingSpace != null ? trackingSpace.rotation * tsRot : tsRot;

                bool isLowerBody = i >= (int)OVRPlugin.BoneId.FullBody_LeftUpperLeg &&
                                   i <= (int)OVRPlugin.BoneId.FullBody_RightFootBall;

                TrackingDataSource source;
                if (!posValid)
                {
                    source = TrackingDataSource.Unavailable;
                }
                else if (isLowerBody)
                {
                    // Quest 3S has no downward foot-observing cameras; FullBody legs are Meta Generative Legs estimates.
                    source = TrackingDataSource.SdkEstimatedGenerativeLegs;
                }
                else if (posTracked)
                {
                    source = TrackingDataSource.SdkObserved;
                }
                else
                {
                    source = TrackingDataSource.SdkEstimatedGenerativeLegs;
                }

                _jointBuffer[i] = new BodyJointSample
                {
                    JointIndex = i,
                    JointName = ((OVRPlugin.BoneId)i).ToString(),
                    TrackingSpacePosition = tsPos,
                    TrackingSpaceRotation = tsRot,
                    WorldPosition = worldPos,
                    WorldRotation = worldRot,
                    PositionValid = posValid,
                    OrientationValid = oriValid,
                    PositionTracked = posTracked,
                    OrientationTracked = oriTracked,
                    IsLowerBodyGenerativeJoint = isLowerBody,
                    DataSource = source
                };
            }

            for (int i = count; i < _jointBuffer.Length; i++)
            {
                _jointBuffer[i] = default;
            }

            Vector3 headWorld = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            int headIdx = (int)OVRPlugin.BoneId.FullBody_Head;
            if (headIdx < count && _jointBuffer[headIdx].PositionValid)
            {
                headWorld = _jointBuffer[headIdx].WorldPosition;
            }

            Vector3 hipsWorld = Vector3.zero;
            int hipsIdx = (int)OVRPlugin.BoneId.FullBody_Hips;
            if (hipsIdx < count && _jointBuffer[hipsIdx].PositionValid)
            {
                hipsWorld = _jointBuffer[hipsIdx].WorldPosition;
            }

            snapshot = new BodySkeletonSnapshot
            {
                IsAvailable = true,
                IsFullBodyJointSet = state.JointSet == OVRPlugin.BodyJointSet.FullBody || count >= 84,
                Confidence = state.Confidence,
                Fidelity = MapFidelity(state.Fidelity),
                CalibrationState = MapCalibration(state.CalibrationStatus),
                SdkTimestampSeconds = state.Time,
                SkeletonChangedCount = state.SkeletonChangedCount,
                HeadWorldPosition = headWorld,
                HipsWorldPosition = hipsWorld,
                Joints = _jointBuffer
            };

            _statusMessage = $"Active ({snapshot.Fidelity}, Conf={snapshot.Confidence:F2})";
            return true;
        }

        public bool TryApplyHeightCalibration(float heightMeters)
        {
            if (heightMeters < 0.8f || heightMeters > 2.4f)
                return false;

            return OVRBody.SuggestBodyTrackingCalibrationOverride(heightMeters);
        }

        public void Shutdown()
        {
            OVRPermissionsRequester.PermissionGranted -= OnPermissionGranted;
            IsInitialized = false;
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        private void OnPermissionGranted(string permissionId)
        {
            if (permissionId == OVRPermissionsRequester.BodyTrackingPermission)
            {
                ConfigureOvrBodySettings();
                if (_ovrBody != null && !_ovrBody.enabled)
                {
                    _ovrBody.enabled = true;
                }
                _statusMessage = "BODY_TRACKING permission granted";
            }
        }

        private void ConfigureOvrBodySettings()
        {
            if (_ovrBody != null)
            {
                _ovrBody.ProvidedSkeletonType = OVRPlugin.BodyJointSet.FullBody;
            }

            OVRBody.SetRequestedJointSet(OVRPlugin.BodyJointSet.FullBody);
            OVRBody.Fidelity = _requestedFidelity;
        }

        private static BodyTrackingFidelityMode MapFidelity(OVRPlugin.BodyTrackingFidelity2 fidelity)
        {
            switch (fidelity)
            {
                case OVRPlugin.BodyTrackingFidelity2.High:
                    return BodyTrackingFidelityMode.HighInsideOut;
                case OVRPlugin.BodyTrackingFidelity2.Low:
                    return BodyTrackingFidelityMode.LowThreePoint;
                default:
                    return BodyTrackingFidelityMode.Unknown;
            }
        }

        private static BodyCalibrationState MapCalibration(OVRPlugin.BodyTrackingCalibrationState state)
        {
            switch (state)
            {
                case OVRPlugin.BodyTrackingCalibrationState.Valid:
                    return BodyCalibrationState.Valid;
                case OVRPlugin.BodyTrackingCalibrationState.Calibrating:
                    return BodyCalibrationState.Calibrating;
                default:
                    return BodyCalibrationState.Invalid;
            }
        }
    }
}
