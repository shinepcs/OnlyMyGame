using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Callbacks;

internal static class OnlyMyGameWebGlPostprocessor
{
    private static readonly Regex EnabledAutoSyncPattern = new Regex(
        @"(?m)^[ \t]*config\.autoSyncPersistentDataPath[ \t]*=[ \t]*true[ \t]*;[ \t]*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex DisabledAutoSyncPattern = new Regex(
        @"(?m)^([ \t]*)//[ \t]*config\.autoSyncPersistentDataPath[ \t]*=[ \t]*true[ \t]*;[ \t]*$",
        RegexOptions.CultureInvariant);

    [PostProcessBuild(1000)]
    public static void EnablePersistentDataAutoSync(BuildTarget target, string pathToBuiltProject)
    {
        if (target != BuildTarget.WebGL)
        {
            return;
        }

        string indexPath = Path.Combine(pathToBuiltProject, "index.html");
        if (!File.Exists(indexPath))
        {
            throw new BuildFailedException($"WebGL index was not generated: {indexPath}");
        }

        string html = File.ReadAllText(indexPath);
        if (EnabledAutoSyncPattern.IsMatch(html))
        {
            return;
        }

        if (!DisabledAutoSyncPattern.IsMatch(html))
        {
            throw new BuildFailedException(
                "Unity's WebGL template no longer exposes config.autoSyncPersistentDataPath. " +
                "Update OnlyMyGameWebGlPostprocessor before shipping this build.");
        }

        string patchedHtml = DisabledAutoSyncPattern.Replace(
            html,
            "$1config.autoSyncPersistentDataPath = true;",
            1);
        File.WriteAllText(indexPath, patchedHtml);
    }
}
