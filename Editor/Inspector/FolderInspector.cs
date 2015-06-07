using UnityEditor;
using UnityEngine;


public sealed class FolderInspector : Editor
{
    public static bool CanInspect(Object target)
    {
        var assetPath = AssetDatabase.GetAssetPath(target);
        return AssetDatabase.IsValidFolder(assetPath);
    }

    public override void OnInspectorGUI()
    {
        var path = AssetDatabase.GetAssetPath(target);

        if (!AssetDatabase.IsValidFolder(path))
        {
            return;
        }

        GUI.enabled = true;

        if (GUILayout.Button("Import"))
        {
            AssetDatabase.ImportAsset(path);
        }

        EditorGUILayout.Space();

        if (GUILayout.Button("Create Folder"))
        {
            EditorApplication.ExecuteMenuItem("Assets/Create/Folder");
        }
        if (GUILayout.Button("Create C# Script"))
        {
            EditorApplication.ExecuteMenuItem("Assets/Create/C# Script");
        }
        if (GUILayout.Button("Create Prefab"))
        {
            EditorApplication.ExecuteMenuItem("Assets/Create/Prefab");
        }

        GUI.enabled = false;
    }
}
