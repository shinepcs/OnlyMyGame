using UnityEditor;
using UnityEditor.SceneManagement;
using System.IO;
using UnityEditor.Build.Reporting;
using System;
using UnityEngine;

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

        // GitHub Pages는 별도 압축 헤더를 보장하지 않으므로 파일 자체를 그대로 제공한다.
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
        PlayerSettings.WebGL.decompressionFallback = true;
        PlayerSettings.WebGL.threadsSupport = false;

        // WebGL 빌드에서 URP 쉐이더가 제거되지 않도록 항상 포함시킨다.
        EnsureShaderIncluded("Universal Render Pipeline/Lit");
        EnsureShaderIncluded("Universal Render Pipeline/Simple Lit");
        EnsureShaderIncluded("Universal Render Pipeline/Unlit");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/CameraMotionVectors");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/Blit");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/CopyDepth");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/CopyColor");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/FinalBlit");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/PostProcessing");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/UberPost");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/DepthOfField");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/MotionBlur");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/PaniniProjection");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/LensFlareDataDriven");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/ScreenSpaceLensFlare");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/StencilDeferred");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/Deferred");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/GBuffer");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/ShadowCasterPass");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/DepthOnlyPass");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/MainLightShadowCasterPass");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/AdditionalLightsShadowCasterPass");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/SceneViewPicking");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/SceneSelection");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/2D");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/2D/Shadow2D");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/2D/Sprite-Lit-Default");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/2D/Sprite-Unlit-Default");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/2D/SpriteMask");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/2D/SpriteShadowCasterPass");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/2D/SpriteLight");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/2D/SpriteLightOcclusion");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/2D/SpriteLightVolume");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/2D/SpriteLightVolumeOcclusion");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/2D/SpriteNormalMap");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/2D/SpriteNormalMapOcclusion");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/2D/SpriteNormalMapVolume");
        EnsureShaderIncluded("Hidden/Universal Render Pipeline/2D/SpriteNormalMapVolumeOcclusion");

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

    private static void EnsureShaderIncluded(string shaderName)
    {
        var shader = Shader.Find(shaderName);
        if (shader == null) return;
        var graphicsSettings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
        if (graphicsSettings == null || graphicsSettings.Length == 0) return;
        var serializedObject = new SerializedObject(graphicsSettings[0]);
        var alwaysIncluded = serializedObject.FindProperty("m_AlwaysIncludedShaders");
        if (alwaysIncluded == null) return;
        for (int i = 0; i < alwaysIncluded.arraySize; i++)
        {
            if (alwaysIncluded.GetArrayElementAtIndex(i).objectReferenceValue == shader) return;
        }
        alwaysIncluded.InsertArrayElementAtIndex(alwaysIncluded.arraySize);
        alwaysIncluded.GetArrayElementAtIndex(alwaysIncluded.arraySize - 1).objectReferenceValue = shader;
        serializedObject.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
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