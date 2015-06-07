using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public sealed class SceneInspector : Editor
{
    public static bool CanInspect(Object target)
    {
        var assetPath = AssetDatabase.GetAssetPath(target);
        return Path.GetExtension(assetPath) == ".unity";
    }

    public override void OnInspectorGUI()
    {
        var path = AssetDatabase.GetAssetPath(target);

        if (Path.GetExtension(path) != ".unity")
        {
            return;
        }

        GUI.enabled = true;

        EditorGUILayout.LabelField("Scenes In Buid");

        GUI.enabled = !EditorBuildSettings.scenes.Any(c => c.path == path);

        if (GUILayout.Button("Add"))
        {
            var sceneList = EditorBuildSettings.scenes.ToList();
            sceneList.Add(new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = sceneList.ToArray();
        }

        GUI.enabled = true;

        if (GUILayout.Button("Remove"))
        {
            var sceneList = EditorBuildSettings.scenes.ToList();
            sceneList.RemoveAll(c => c.path == path);
            EditorBuildSettings.scenes = sceneList.ToArray();
        }

        GUI.enabled = false;
    }
}