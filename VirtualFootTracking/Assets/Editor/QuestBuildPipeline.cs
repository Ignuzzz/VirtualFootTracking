using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace VirtualFootTracking.Editor
{
    /// <summary>
    /// End-to-end Quest 3S APK build orchestrator callable from Unity menu or headless CI (-executeMethod).
    /// </summary>
    public static class QuestBuildPipeline
    {
        public const string OutputApkPath = "Builds/VirtualFootTracking_v0.3.1_DepthFix.apk";

        [MenuItem("VirtualFootTracking/3. Build Quest 3S Depth APK")]
        public static void ConfigureBuildSceneAndApk()
        {
            bool batch = Application.isBatchMode;
            try
            {
                QuestProjectConfigurator.ConfigureAll();
                DiagnosticSceneBuilder.BuildDiagnosticScene();

                string outDir = Path.GetDirectoryName(OutputApkPath);
                if (!string.IsNullOrEmpty(outDir))
                {
                    Directory.CreateDirectory(outDir);
                }

                BuildPlayerOptions opts = new BuildPlayerOptions
                {
                    scenes = new[] { DiagnosticSceneBuilder.DiagnosticScenePath },
                    locationPathName = OutputApkPath,
                    target = BuildTarget.Android,
                    options = BuildOptions.None
                };

                BuildReport report = BuildPipeline.BuildPlayer(opts);
                BuildSummary summary = report.summary;

                if (summary.result == BuildResult.Succeeded)
                {
                    Debug.Log($"[VFT.Build] Build SUCCEEDED: {OutputApkPath} ({summary.totalSize / (1024f * 1024f):F2} MB, {summary.totalTime.TotalSeconds:F1}s)");
                    if (batch) EditorApplication.Exit(0);
                }
                else
                {
                    Debug.LogError($"[VFT.Build] Build FAILED: {summary.result}, Errors={summary.totalErrors}, Warnings={summary.totalWarnings}");
                    if (batch) EditorApplication.Exit(1);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[VFT.Build] Exception during build: {ex}");
                if (batch) EditorApplication.Exit(1);
            }
        }
    }
}
