using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

namespace VirtualFootTracking.Editor
{
    /// <summary>
    /// Configures Unity PlayerSettings, Android IL2CPP ARM64, OpenXR, Meta Quest Support,
    /// and OVRProjectConfig / OVRRuntimeSettings for Meta Quest 3S Body Tracking + Scene API.
    /// </summary>
    public static class QuestProjectConfigurator
    {
        private const string OpenXRLoaderTypeName = "UnityEngine.XR.OpenXR.OpenXRLoader";
        private const string MetaQuestFeatureId = "com.unity.openxr.feature.metaquest";
        private const string MetaXRFeatureId = "com.meta.openxr.feature.metaxr";

        [MenuItem("VirtualFootTracking/1. Configure Quest 3S Project Settings")]
        public static void ConfigureAll()
        {
            Debug.Log("[VFT.Editor] Configuring Quest 3S project settings...");

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            }

            ConfigurePlayerSettings();
            ConfigureXrAndOpenXr();
            ConfigureMetaProjectAndRuntimeSettings();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[VFT.Editor] Quest 3S project configuration complete.");
        }

        private static void ConfigurePlayerSettings()
        {
            PlayerSettings.companyName = "VirtualFootTracking";
            PlayerSettings.productName = "VirtualFootTracking";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.virtualfoottracking.quest");
            PlayerSettings.bundleVersion = "0.3.1";
            PlayerSettings.Android.bundleVersionCode = 4;

            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.GameActivity;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.Android.forceInternetPermission = false;

            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(
                BuildTarget.Android,
                new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });
        }

        private static void ConfigureXrAndOpenXr()
        {
            Directory.CreateDirectory("Assets/XR/Settings");

            XRGeneralSettings androidGeneral = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
            if (androidGeneral == null)
            {
                XRGeneralSettingsPerBuildTarget container = EnsureGeneralSettingsContainer();
                androidGeneral = ScriptableObject.CreateInstance<XRGeneralSettings>();
                androidGeneral.name = "Android XR General Settings";
                AssetDatabase.CreateAsset(androidGeneral, "Assets/XR/AndroidXRGeneralSettings.asset");

                XRManagerSettings mgr = ScriptableObject.CreateInstance<XRManagerSettings>();
                mgr.name = "Android XR Manager Settings";
                AssetDatabase.AddObjectToAsset(mgr, androidGeneral);
                androidGeneral.Manager = mgr;
                androidGeneral.InitManagerOnStart = true;

                container.SetSettingsForBuildTarget(BuildTargetGroup.Android, androidGeneral);
                EditorUtility.SetDirty(androidGeneral);
                EditorUtility.SetDirty(container);
                AssetDatabase.SaveAssets();
            }

            if (androidGeneral.Manager == null)
            {
                XRManagerSettings mgr = ScriptableObject.CreateInstance<XRManagerSettings>();
                mgr.name = "Android XR Manager Settings";
                AssetDatabase.AddObjectToAsset(mgr, androidGeneral);
                androidGeneral.Manager = mgr;
                EditorUtility.SetDirty(androidGeneral);
            }

            bool assigned = XRPackageMetadataStore.IsLoaderAssigned(OpenXRLoaderTypeName, BuildTargetGroup.Android);
            if (!assigned)
            {
                assigned = XRPackageMetadataStore.AssignLoader(
                    androidGeneral.Manager,
                    OpenXRLoaderTypeName,
                    BuildTargetGroup.Android);
                Debug.Log($"[VFT.Editor] Assign OpenXRLoader for Android: {assigned}");
            }

            EnsureOpenXrSettingsAsset();
            FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);

            OpenXRSettings oxrSettings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (oxrSettings != null)
            {
                oxrSettings.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
                EnableFeature(MetaQuestFeatureId, "Meta Quest Support");
                EnableFeature(MetaXRFeatureId, "Meta XR Feature (IOBT + Scene)");
                EnableFeature("com.unity.openxr.feature.input.metaquestplus", "Meta Quest Touch Plus Profile");
                EnableFeature("com.unity.openxr.feature.input.oculustouch", "Oculus Touch Controller Profile");
                EditorUtility.SetDirty(oxrSettings);
            }
            else
            {
                Debug.LogWarning("[VFT.Editor] OpenXRSettings for Android is null; verify in Project Settings > XR Plug-in Management > OpenXR.");
            }
        }

        private static XRGeneralSettingsPerBuildTarget EnsureGeneralSettingsContainer()
        {
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey, out XRGeneralSettingsPerBuildTarget container) || container == null)
            {
                string[] guids = AssetDatabase.FindAssets("t:XRGeneralSettingsPerBuildTarget");
                if (guids.Length > 0)
                {
                    container = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(AssetDatabase.GUIDToAssetPath(guids[0]));
                }
            }

            if (container == null)
            {
                container = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(container, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
                AssetDatabase.SaveAssets();
            }

            EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, container, true);
            return container;
        }

        private static void EnsureOpenXrSettingsAsset()
        {
            if (OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android) != null)
                return;

            // OpenXRPackageSettings is internal in com.unity.xr.openxr; access Instance safely via reflection if needed.
            Assembly oxrEditorAsm = typeof(FeatureHelpers).Assembly;
            Type pkgSettingsType = oxrEditorAsm.GetType("UnityEditor.XR.OpenXR.OpenXRPackageSettings");
            if (pkgSettingsType != null)
            {
                PropertyInfo instProp = pkgSettingsType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                object instance = instProp != null ? instProp.GetValue(null) : null;
                if (instance != null)
                {
                    MethodInfo getOrCreate = pkgSettingsType.GetMethod(
                        "GetSettingsForBuildTargetGroup",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    getOrCreate?.Invoke(instance, new object[] { BuildTargetGroup.Android });
                }
            }
        }

        private static void EnableFeature(string featureId, string label)
        {
            OpenXRFeature feature = FeatureHelpers.GetFeatureWithIdForBuildTarget(BuildTargetGroup.Android, featureId);
            if (feature != null)
            {
                feature.enabled = true;
                EditorUtility.SetDirty(feature);
                Debug.Log($"[VFT.Editor] Enabled OpenXR feature: {label} ({featureId})");
            }
            else
            {
                Debug.LogWarning($"[VFT.Editor] OpenXR feature not found for Android: {label} ({featureId})");
            }
        }

        private static void ConfigureMetaProjectAndRuntimeSettings()
        {
            OVRProjectConfig projectConfig = OVRProjectConfig.CachedProjectConfig;
            if (projectConfig.targetDeviceTypes == null)
            {
                projectConfig.targetDeviceTypes = new List<OVRProjectConfig.DeviceType>();
            }
            if (!projectConfig.targetDeviceTypes.Contains(OVRProjectConfig.DeviceType.Quest3))
            {
                projectConfig.targetDeviceTypes.Add(OVRProjectConfig.DeviceType.Quest3);
            }
            if (!projectConfig.targetDeviceTypes.Contains(OVRProjectConfig.DeviceType.Quest3S))
            {
                projectConfig.targetDeviceTypes.Add(OVRProjectConfig.DeviceType.Quest3S);
            }

            projectConfig.bodyTrackingSupport = OVRProjectConfig.FeatureSupport.Required;
            projectConfig.sceneSupport = OVRProjectConfig.FeatureSupport.Required;
            projectConfig.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled;
            projectConfig.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Supported;
            OVRProjectConfig.CommitProjectConfig(projectConfig);

            OVRRuntimeSettings runtimeSettings = OVRRuntimeSettings.GetRuntimeSettings();
            runtimeSettings.BodyTrackingFidelity = OVRPlugin.BodyTrackingFidelity2.High;
            runtimeSettings.BodyTrackingJointSet = OVRPlugin.BodyJointSet.FullBody;
            OVRRuntimeSettings.CommitRuntimeSettings(runtimeSettings);
        }
    }
}
