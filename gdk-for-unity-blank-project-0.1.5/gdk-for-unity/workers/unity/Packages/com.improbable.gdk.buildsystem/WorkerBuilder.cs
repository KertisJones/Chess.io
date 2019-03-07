using System;
using System.Collections.Generic;
using System.IO;
using Improbable.Gdk.BuildSystem.Configuration;
using Improbable.Gdk.Core;
using Improbable.Gdk.Tools;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Improbable.Gdk.BuildSystem
{
    public static class WorkerBuilder
    {
        internal static readonly string IncompatibleWindowsPlatformsErrorMessage =
            $"Please choose only one of {SpatialBuildPlatforms.Windows32} or {SpatialBuildPlatforms.Windows32} as a build platform.";

        private static readonly string PlayerBuildDirectory =
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), EditorPaths.AssetDatabaseDirectory,
                "worker"));

        private static readonly string AssetDatabaseDirectory =
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), EditorPaths.AssetDatabaseDirectory));

        private const string BuildWorkerTypes = "buildWorkerTypes";

        /// <summary>
        ///     Build method that is invoked by commandline
        /// </summary>
        // ReSharper disable once UnusedMember.Global
        public static void Build()
        {
            try
            {
                var commandLine = Environment.GetCommandLineArgs();
                var buildTargetArg = CommandLineUtility.GetCommandLineValue(commandLine, "buildTarget", "local");

                BuildEnvironment buildEnvironment;
                switch (buildTargetArg.ToLower())
                {
                    case "cloud":
                        buildEnvironment = BuildEnvironment.Cloud;
                        break;
                    case "local":
                        buildEnvironment = BuildEnvironment.Local;
                        break;
                    default:
                        throw new BuildFailedException("Unknown build target value: " + buildTargetArg);
                }

                var workerTypesArg =
                    CommandLineUtility.GetCommandLineValue(commandLine, BuildWorkerTypes,
                        "UnityClient,UnityGameLogic");

                var desiredWorkerTypes = workerTypesArg.Split(',');
                var filteredWorkerTypes = BuildSupportChecker.FilterWorkerTypes(buildEnvironment, desiredWorkerTypes);

                if (desiredWorkerTypes.Length != filteredWorkerTypes.Length)
                {
                    throw new BuildFailedException(
                        "Unable to complete build. Missing build support. Check logs for specific errors.");
                }

                ScriptingImplementation scriptingBackend;
                var wantedScriptingBackend =
                    CommandLineUtility.GetCommandLineValue(commandLine, "scriptingBackend", "mono");
                switch (wantedScriptingBackend)
                {
                    case "mono":
                        scriptingBackend = ScriptingImplementation.Mono2x;
                        break;
                    case "il2cpp":
                        scriptingBackend = ScriptingImplementation.IL2CPP;
                        break;
                    default:
                        throw new BuildFailedException("Unknown scripting backend value: " + wantedScriptingBackend);
                }

                LocalLaunch.BuildConfig();

                foreach (var wantedWorkerType in filteredWorkerTypes)
                {
                    BuildWorkerForEnvironment(wantedWorkerType, buildEnvironment, scriptingBackend);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (e is BuildFailedException)
                {
                    throw;
                }

                throw new BuildFailedException(e);
            }
        }

        public static BuildTarget[] GetBuildTargetsForWorkerForEnvironment(string workerType,
            BuildEnvironment targetEnvironment)
        {
            var environmentConfig = SpatialOSBuildConfiguration.GetInstance()
                .GetEnvironmentConfigForWorker(workerType, targetEnvironment);
            if (environmentConfig == null)
            {
                return new BuildTarget[0];
            }

            return GetUnityBuildTargets(environmentConfig.BuildPlatforms);
        }

        public static void BuildWorkerForEnvironment(string workerType, BuildEnvironment targetEnvironment,
            ScriptingImplementation? scriptingBackend = null)
        {
            var spatialOSBuildConfiguration = SpatialOSBuildConfiguration.GetInstance();
            var environmentConfig =
                spatialOSBuildConfiguration.GetEnvironmentConfigForWorker(workerType, targetEnvironment);
            if (environmentConfig == null)
            {
                Debug.LogWarning($"Skipping build for {workerType}.");
                return;
            }

            var buildPlatforms = environmentConfig.BuildPlatforms;
            var buildOptions = environmentConfig.BuildOptions;

            if (!Directory.Exists(PlayerBuildDirectory))
            {
                Directory.CreateDirectory(PlayerBuildDirectory);
            }

            foreach (var unityBuildTarget in GetUnityBuildTargets(buildPlatforms))
            {
                var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(unityBuildTarget);
                var activeScriptingBackend = PlayerSettings.GetScriptingBackend(buildTargetGroup);
                try
                {
                    if (scriptingBackend != null)
                    {
                        Debug.Log($"Setting scripting backend to {scriptingBackend.Value}");
                        PlayerSettings.SetScriptingBackend(buildTargetGroup, scriptingBackend.Value);
                    }

                    BuildWorkerForTarget(workerType, unityBuildTarget, buildOptions, targetEnvironment);
                }
                catch (Exception e)
                {
                    throw new BuildFailedException(e);
                }
                finally
                {
                    PlayerSettings.SetScriptingBackend(buildTargetGroup, activeScriptingBackend);
                }
            }
        }

        public static void Clean()
        {
            Directory.Delete(AssetDatabaseDirectory, true);
            Directory.Delete(EditorPaths.BuildScratchDirectory, true);
        }

        public static BuildTarget[] GetUnityBuildTargets(SpatialBuildPlatforms actualPlatforms)
        {
            var result = new List<BuildTarget>();
            if ((actualPlatforms & SpatialBuildPlatforms.Current) != 0)
            {
                actualPlatforms |= GetCurrentBuildPlatform();
            }

            if ((actualPlatforms & SpatialBuildPlatforms.Linux) != 0)
            {
                result.Add(BuildTarget.StandaloneLinux64);
            }

            if ((actualPlatforms & SpatialBuildPlatforms.OSX) != 0)
            {
                result.Add(BuildTarget.StandaloneOSX);
            }

            if ((actualPlatforms & SpatialBuildPlatforms.Windows32) != 0)
            {
                if ((actualPlatforms & SpatialBuildPlatforms.Windows64) != 0)
                {
                    throw new Exception(IncompatibleWindowsPlatformsErrorMessage);
                }

                result.Add(BuildTarget.StandaloneWindows);
            }
            else if ((actualPlatforms & SpatialBuildPlatforms.Windows64) != 0)
            {
                result.Add(BuildTarget.StandaloneWindows64);
            }

            if ((actualPlatforms & SpatialBuildPlatforms.Android) != 0)
            {
                result.Add(BuildTarget.Android);
            }

            if ((actualPlatforms & SpatialBuildPlatforms.iOS) != 0)
            {
                result.Add(BuildTarget.iOS);
            }

            return result.ToArray();
        }

        internal static SpatialBuildPlatforms GetCurrentBuildPlatform()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                    return SpatialBuildPlatforms.Windows64;
                case RuntimePlatform.OSXEditor:
                    return SpatialBuildPlatforms.OSX;
                case RuntimePlatform.LinuxEditor:
                    return SpatialBuildPlatforms.Linux;
                default:
                    throw new Exception($"Unsupported platform detected: {Application.platform}");
            }
        }

        private static void BuildWorkerForTarget(string workerType, BuildTarget buildTarget,
            BuildOptions buildOptions, BuildEnvironment targetEnvironment)
        {
            Debug.Log(
                $"Building \"{buildTarget}\" for worker platform: \"{workerType}\", environment: \"{targetEnvironment}\"");

            var spatialOSBuildConfiguration = SpatialOSBuildConfiguration.GetInstance();
            var workerBuildData = new WorkerBuildData(workerType, buildTarget);
            var scenes = spatialOSBuildConfiguration.GetScenePathsForWorker(workerType);

            var buildPlayerOptions = new BuildPlayerOptions
            {
                options = buildOptions,
                target = buildTarget,
                scenes = scenes,
                locationPathName = workerBuildData.BuildScratchDirectory
            };

            var result = BuildPipeline.BuildPlayer(buildPlayerOptions);
            if (result.summary.result != BuildResult.Succeeded)
            {
                throw new BuildFailedException($"Build failed for {workerType}");
            }

            if (buildTarget == BuildTarget.Android || buildTarget == BuildTarget.iOS)
            {
                // Mobile clients can only be run locally, no need to package them
                return;
            }

            var zipPath = Path.Combine(PlayerBuildDirectory, workerBuildData.PackageName);
            var basePath = Path.Combine(EditorPaths.BuildScratchDirectory, workerBuildData.PackageName);
            Zip(zipPath, basePath, targetEnvironment == BuildEnvironment.Cloud);
        }

        private static void Zip(string zipAbsolutePath, string basePath, bool useCompression)
        {
            using (new ShowProgressBarScope($"Package {basePath}"))
            {
                RedirectedProcess.Command(Common.SpatialBinary)
                    .WithArgs("file", "zip", $"--output=\"{Path.GetFullPath(zipAbsolutePath)}\"",
                        $"--basePath=\"{Path.GetFullPath(basePath)}\"", "\"**\"",
                        $"--compression={useCompression}")
                    .Run();
            }
        }
    }
}
