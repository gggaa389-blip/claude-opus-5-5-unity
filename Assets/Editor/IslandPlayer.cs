using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// Windows player used for the FPS measurement (run it with -benchmark).
public static class IslandPlayer
{
    public const string ExePath = @"C:\Users\author\unity-br\claude-opus-5-5-test2\Build\IslandBench.exe";

    public static void Build()
    {
        IslandSetup.Configure();
        var opts = new BuildPlayerOptions
        {
            scenes = new[] { IslandBuilder.ScenePath },
            locationPathName = ExePath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };
        var report = BuildPipeline.BuildPlayer(opts);
        Debug.Log($"[IslandPlayer] build {report.summary.result}, {report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalErrors} errors");
        if (report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
    }

    public const string GamePath = @"C:\Users\author\unity-br\claude-opus-5-5-test2\Build\Game\IslandShooter.exe";
    public const string GameDevPath = @"C:\Users\author\unity-br\claude-opus-5-5-test2\Build\GameDev\IslandShooter.exe";

    /// a development build of the game beside the real one, for -perf with -perfMarkers (Unity's own
    /// profiler markers can only be read in a development build)
    public static void BuildGameDev()
    {
        IslandSetup.Configure();
        PlayerSettings.productName = "IslandShooter";
        var opts = new BuildPlayerOptions
        {
            scenes = new[] { IslandBuilder.ScenePath },
            locationPathName = GameDevPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development
        };
        var report = BuildPipeline.BuildPlayer(opts);
        Debug.Log($"[IslandPlayer] development game build {report.summary.result}, {report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalErrors} errors");
        PlayerSettings.productName = "IslandBench";
        if (report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
    }

    /// the playable game (third-person shooter): the same scene, started without -benchmark
    public static void BuildGame()
    {
        IslandSetup.Configure();
        PlayerSettings.productName = "IslandShooter";
        var opts = new BuildPlayerOptions
        {
            scenes = new[] { IslandBuilder.ScenePath },
            locationPathName = GamePath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };
        var report = BuildPipeline.BuildPlayer(opts);
        Debug.Log($"[IslandPlayer] game build {report.summary.result}, {report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalErrors} errors");
        PlayerSettings.productName = "IslandBench";
        if (report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
    }
}
