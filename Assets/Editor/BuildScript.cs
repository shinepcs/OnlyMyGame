using UnityEditor;
using UnityEditor.SceneManagement;
using System.IO;
using UnityEditor.Build.Reporting;
using System;

public class BuildScript
{
    public static void BuildWebGL()
    {
        // 빌드 시간을 BuildInfo.txt에 기록
        UpdateBuildInfo();
        
        // 빌드 경로
        string buildPath = "build/webgl";
        
        if (!Directory.Exists(buildPath))
        {
            Directory.CreateDirectory(buildPath);
        }

        // 씬 목록 설정
        string[] scenes = GetScenePathsFromSettings();

        // WebGL 빌드 옵션
        BuildPlayerOptions buildOptions = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = buildPath,
            target = BuildTarget.WebGL,
            options = BuildOptions.None
        };

        // 빌드 실행
        BuildReport report = BuildPipeline.BuildPlayer(buildOptions);

        // 결과 확인
        if (report.summary.result == BuildResult.Succeeded)
        {
            EditorApplication.Exit(0);
        }
        else
        {
            EditorApplication.Exit(1);
        }
    }

    private static string[] GetScenePathsFromSettings()
    {
        EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
        string[] scenePaths = new string[scenes.Length];

        for (int i = 0; i < scenes.Length; i++)
        {
            scenePaths[i] = scenes[i].path;
        }

        return scenePaths;
    }

    private static void UpdateBuildInfo()
    {
        string resourcesPath = "Assets/Resources";
        string buildInfoPath = Path.Combine(resourcesPath, "BuildInfo.txt");
        
        // Resources 폴더가 없으면 생성
        if (!Directory.Exists(resourcesPath))
        {
            Directory.CreateDirectory(resourcesPath);
        }
        
        // 빌드 시간 기록
        string buildTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string content = $"Build Time: {buildTime}";
        
        File.WriteAllText(buildInfoPath, content);
        AssetDatabase.Refresh();
        
        UnityEngine.Debug.Log($"Build info updated: {content}");
    }
}
