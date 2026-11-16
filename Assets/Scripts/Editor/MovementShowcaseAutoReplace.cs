using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-time helper: the first time Unity loads this script with the movement playground open, it swaps the
/// playground's crates, balls, bottles and table for their prefab versions (same as
/// Tools > Movement Showcase > Replace Playground Objects With Prefabs), then offers to save the scene.
/// It runs once per project on this computer and then stays out of the way. Safe to delete this file afterwards.
/// </summary>
[InitializeOnLoad]
static class MovementShowcaseAutoReplace
{
    // Per project, so other Unity projects on this computer aren't affected.
    static string DoneKey => "MovementShowcase.AutoReplacePrefabs.Done." + Application.dataPath.GetHashCode();

    static MovementShowcaseAutoReplace()
    {
        EditorApplication.delayCall += TryRun; // after Unity has finished loading/compiling
    }

    static void TryRun()
    {
        if (EditorPrefs.GetBool(DoneKey, false)) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode) return; // try again on the next reload

        int replaced = MovementShowcaseBuilder.ReplaceObjectsWithPrefabsInScene();
        if (replaced < 0) return; // playground not open right now; try again on the next reload

        EditorPrefs.SetBool(DoneKey, true);

        bool save = EditorUtility.DisplayDialog(
            "Playground objects replaced",
            $"Replaced {replaced} crate(s), ball(s), bottle(s) and table(s) in the movement playground with their " +
            "prefab versions (Assets/Showcase/Prefabs). Bottles now have the upside-down held pose.\n\n" +
            "Save the scene now? (It has unsaved changes.)",
            "Save Scene", "Later");
        if (save) EditorSceneManager.SaveOpenScenes();
    }
}
