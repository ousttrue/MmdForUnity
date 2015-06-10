using UnityEngine;
using System.Collections;
using UnityEditor;
using MMD;

public class PMDLoaderWindow : EditorWindow
{
    Object pmdFile;
    MMD.PMDImportConfig pmd_config;

    [MenuItem("MMD for Unity/PMD Loader")]
    static void Init()
    {
        var window = (PMDLoaderWindow)EditorWindow.GetWindow<PMDLoaderWindow>(true, "PMDLoader");
        window.Show();
    }

    void OnEnable()
    {
        // デフォルトコンフィグ
        pmdFile = null;
        pmd_config = MMD.Config.LoadAndCreate().pmd_config.Clone();
    }

    void OnGUI()
    {
        // GUIの有効化
        GUI.enabled = !EditorApplication.isPlaying;

        // GUI描画
        pmdFile = EditorGUILayout.ObjectField("PMD File", pmdFile, typeof(Object), false);
        pmd_config.OnGUIFunction();

        {
            bool gui_enabled_old = GUI.enabled;
            GUI.enabled = !EditorApplication.isPlaying && (pmdFile != null);
            if (GUILayout.Button("Convert"))
            {
                LoadModel();
                pmdFile = null;		// 読み終わったので空にする 
            }
            GUI.enabled = gui_enabled_old;
        }
    }

    void LoadModel()
    {
        string file_path = AssetDatabase.GetAssetPath(pmdFile);
        var model_agent = new MMD.ModelAgent(file_path);
        var go = model_agent.CreateGameObject(pmd_config.shader_type
                                , pmd_config.rigidFlag
                                , pmd_config.animation_type
                                , pmd_config.use_ik
                                , pmd_config.scale
                                );

        // プレファブ化
        PrefabUtility.CreatePrefab(model_agent.PrefabPath.GetUnityAssetPath(), go, ReplacePrefabOptions.ConnectToPrefab);

        // アセットリストの更新
        AssetDatabase.Refresh();

        // 読み込み完了メッセージ
        var window = LoadedWindow.Init();
        window.Text = string.Format(
            "----- model name -----\n{0}\n\n----- comment -----\n{1}",
            model_agent.Header.model_name,
            model_agent.Header.comment
        );
        window.Show();
    }
}
