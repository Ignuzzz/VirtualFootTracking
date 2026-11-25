using System.Collections.Generic;
using System.IO;
using Meta.XR.MRUtilityKit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VirtualFootTracking.Biomechanics;
using VirtualFootTracking.Calibration;
using VirtualFootTracking.Communication;
using VirtualFootTracking.Diagnostics;
using VirtualFootTracking.Prediction;
using VirtualFootTracking.Runtime;
using VirtualFootTracking.SpatialMapping;
using VirtualFootTracking.Tracking;
using VirtualFootTracking.Visualization;

namespace VirtualFootTracking.Editor
{
    /// <summary>
    /// Generates Assets/Scenes/DiagnosticScene.unity wired with OVRCameraRig, OVRBody (FullBody),
    /// OVRPassthroughLayer, MRUK, and VirtualFootTrackingSystem.
    /// </summary>
    public static class DiagnosticSceneBuilder
    {
        public const string DiagnosticScenePath = "Assets/Scenes/DiagnosticScene.unity";
        private const string OvrCameraRigPrefabPath = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";
        private const string MrukPrefabPath = "Packages/com.meta.xr.mrutilitykit/Core/Tools/MRUK.prefab";

        [MenuItem("VirtualFootTracking/2. Build Diagnostic Scene")]
        public static void BuildDiagnosticScene()
        {
            Directory.CreateDirectory("Assets/Scenes");
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateDirectionalLight();
            OVRCameraRig cameraRig = CreateCameraRigWithPassthrough();
            OVRBody ovrBody = CreateOvrBody(cameraRig.gameObject);
            MRUK mruk = CreateMruk();
            CreateVirtualFootTrackingSystem(cameraRig, ovrBody, mruk);

            bool saved = EditorSceneManager.SaveScene(scene, DiagnosticScenePath);
            if (!saved)
            {
                throw new System.InvalidOperationException($"Failed to save scene at {DiagnosticScenePath}");
            }

            RegisterSceneInBuildSettings(DiagnosticScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[VFT.Editor] Built and registered {DiagnosticScenePath}");
        }

        private static void CreateDirectionalLight()
        {
            GameObject lightGo = new GameObject("Directional Light");
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.0f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        private static OVRCameraRig CreateCameraRigWithPassthrough()
        {
            GameObject rigGo = null;
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OvrCameraRigPrefabPath);
            if (prefab != null)
            {
                rigGo = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            }
            else
            {
                Debug.LogWarning($"[VFT.Editor] Prefab not found at {OvrCameraRigPrefabPath}, creating OVRCameraRig manually.");
                rigGo = new GameObject("OVRCameraRig");
                rigGo.AddComponent<OVRCameraRig>();
                rigGo.AddComponent<OVRManager>();
            }

            OVRCameraRig cameraRig = rigGo.GetComponent<OVRCameraRig>();
            cameraRig.EnsureGameObjectIntegrity();

            OVRManager ovrManager = rigGo.GetComponent<OVRManager>() ?? rigGo.AddComponent<OVRManager>();
            ovrManager.trackingOriginType = OVRManager.TrackingOrigin.Stage;
            ovrManager.isInsightPassthroughEnabled = true;

            // Set internal startup permission toggles safely if serialized on OVRManager
            SerializedObject mgrSo = new SerializedObject(ovrManager);
            SetSerializedBoolIfFound(mgrSo, "requestBodyTrackingPermissionOnStartup", true);
            SetSerializedBoolIfFound(mgrSo, "requestScenePermissionOnStartup", true);
            mgrSo.ApplyModifiedPropertiesWithoutUndo();

            OVRPassthroughLayer passthrough = rigGo.GetComponent<OVRPassthroughLayer>() ?? rigGo.AddComponent<OVRPassthroughLayer>();
            passthrough.hidden = false;
            passthrough.textureOpacity = 1.0f;

            ConfigureTransparentCamera(cameraRig.centerEyeAnchor);
            ConfigureTransparentCamera(cameraRig.leftEyeAnchor);
            ConfigureTransparentCamera(cameraRig.rightEyeAnchor);

            return cameraRig;
        }

        private static void ConfigureTransparentCamera(Transform eyeAnchor)
        {
            if (eyeAnchor == null)
                return;

            Camera cam = eyeAnchor.GetComponent<Camera>();
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            }
        }

        private static OVRBody CreateOvrBody(GameObject parentRig)
        {
            GameObject bodyGo = new GameObject("OVRBodyTracker");
            bodyGo.transform.SetParent(parentRig.transform, false);
            OVRBody ovrBody = bodyGo.AddComponent<OVRBody>();
            ovrBody.ProvidedSkeletonType = OVRPlugin.BodyJointSet.FullBody;
            return ovrBody;
        }

        private static MRUK CreateMruk()
        {
            GameObject mrukPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(MrukPrefabPath);
            if (mrukPrefab != null)
            {
                GameObject mrukGo = PrefabUtility.InstantiatePrefab(mrukPrefab) as GameObject;
                return mrukGo.GetComponent<MRUK>();
            }

            Debug.LogWarning($"[VFT.Editor] Prefab not found at {MrukPrefabPath}, creating MRUK component manually.");
            GameObject fallback = new GameObject("MRUK");
            return fallback.AddComponent<MRUK>();
        }

        private static void CreateVirtualFootTrackingSystem(OVRCameraRig cameraRig, OVRBody ovrBody, MRUK mruk)
        {
            GameObject sysGo = new GameObject("VirtualFootTrackingSystem");

            MetaOvrBodyTrackingProvider bodyProvider = sysGo.AddComponent<MetaOvrBodyTrackingProvider>();
            MrukSpatialMappingProvider spatialProvider = sysGo.AddComponent<MrukSpatialMappingProvider>();
            GuidedCalibrationProvider calibrationProvider = sysGo.AddComponent<GuidedCalibrationProvider>();
            ProprietaryFootTrackingSolver proprietarySolver = sysGo.AddComponent<ProprietaryFootTrackingSolver>();
            DiagnosticPassthroughFootSolver rawDiagnosticSolver = sysGo.AddComponent<DiagnosticPassthroughFootSolver>();
            AdaptiveKalmanMotionPredictor kalmanPredictor = sysGo.AddComponent<AdaptiveKalmanMotionPredictor>();
            NullMotionPredictor nullPredictor = sysGo.AddComponent<NullMotionPredictor>();
            DiagnosticLogTrackerOutput trackerOutput = sysGo.AddComponent<DiagnosticLogTrackerOutput>();
            RuntimeDiagnosticsProvider diagnosticsProvider = sysGo.AddComponent<RuntimeDiagnosticsProvider>();
            SkeletonAndTrackerVisualizer visualizer = sysGo.AddComponent<SkeletonAndTrackerVisualizer>();
            VirtualFootTrackingVrUi vrUi = sysGo.AddComponent<VirtualFootTrackingVrUi>();
            VrControllerLaserPointer laserPointer = sysGo.AddComponent<VrControllerLaserPointer>();
            VirtualFootTrackingRuntime runtime = sysGo.AddComponent<VirtualFootTrackingRuntime>();

            SerializedObject bodySo = new SerializedObject(bodyProvider);
            SetSerializedObjectRefIfFound(bodySo, "_ovrBody", ovrBody);
            SetSerializedObjectRefIfFound(bodySo, "_cameraRig", cameraRig);
            bodySo.ApplyModifiedPropertiesWithoutUndo();

            SerializedObject spatialSo = new SerializedObject(spatialProvider);
            SetSerializedObjectRefIfFound(spatialSo, "_mruk", mruk);
            SetSerializedObjectRefIfFound(spatialSo, "_cameraRig", cameraRig);
            spatialSo.ApplyModifiedPropertiesWithoutUndo();

            SerializedObject lpSo = new SerializedObject(laserPointer);
            SetSerializedObjectRefIfFound(lpSo, "_cameraRig", cameraRig);
            lpSo.ApplyModifiedPropertiesWithoutUndo();

            SerializedObject rtSo = new SerializedObject(runtime);
            SetSerializedObjectRefIfFound(rtSo, "_cameraRig", cameraRig);
            SetSerializedObjectRefIfFound(rtSo, "_bodyProvider", bodyProvider);
            SetSerializedObjectRefIfFound(rtSo, "_spatialProvider", spatialProvider);
            SetSerializedObjectRefIfFound(rtSo, "_calibrationProvider", calibrationProvider);
            SetSerializedObjectRefIfFound(rtSo, "_proprietarySolver", proprietarySolver);
            SetSerializedObjectRefIfFound(rtSo, "_rawDiagnosticSolver", rawDiagnosticSolver);
            SetSerializedObjectRefIfFound(rtSo, "_kalmanPredictor", kalmanPredictor);
            SetSerializedObjectRefIfFound(rtSo, "_nullPredictor", nullPredictor);
            SetSerializedObjectRefIfFound(rtSo, "_trackerOutput", trackerOutput);
            SetSerializedObjectRefIfFound(rtSo, "_diagnosticsProvider", diagnosticsProvider);
            SetSerializedObjectRefIfFound(rtSo, "_visualizer", visualizer);
            SetSerializedObjectRefIfFound(rtSo, "_vrUi", vrUi);
            SetSerializedObjectRefIfFound(rtSo, "_laserPointer", laserPointer);
            rtSo.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void RegisterSceneInBuildSettings(string scenePath)
        {
            List<EditorBuildSettingsScene> scenes = new List<EditorBuildSettingsScene>
            {
                new EditorBuildSettingsScene(scenePath, true)
            };
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        private static void SetSerializedBoolIfFound(SerializedObject so, string propName, bool value)
        {
            SerializedProperty prop = so.FindProperty(propName);
            if (prop != null && prop.propertyType == SerializedPropertyType.Boolean)
            {
                prop.boolValue = value;
            }
        }

        private static void SetSerializedObjectRefIfFound(SerializedObject so, string propName, Object value)
        {
            SerializedProperty prop = so.FindProperty(propName);
            if (prop != null && prop.propertyType == SerializedPropertyType.ObjectReference)
            {
                prop.objectReferenceValue = value;
            }
        }
    }
}
