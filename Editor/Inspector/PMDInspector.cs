using UnityEngine;
using UnityEditor;
using System.Collections;
using MMD.PMD;
using System.IO;
using System;

namespace MMD
{
    //[CustomEditor(typeof(PMDScriptableObject))]
    public class PMDInspector : Editor
    {
        PMDImportConfig pmd_config;

        // last selected item
        private ModelAgent model_agent;
        private string message = "";

        public static bool CanInspect(UnityEngine.Object target)
        {
            var assetPath = AssetDatabase.GetAssetPath(target);
            var ext=Path.GetExtension(assetPath).ToLower();
            return ext==".pmd" || ext==".pmx";
        }

        /// <summary>
        /// 有効化処理
        /// </summary>
        private void OnEnable()
        {
            var config = MMD.Config.LoadAndCreate();

            // デフォルトコンフィグ
            if (pmd_config == null)
            {
                pmd_config = config.pmd_config.Clone();
            }

            if(model_agent==null)
            {
                // モデル情報
                if (config.inspector_config.use_pmd_preload)
                {
                    var assetPath = AssetDatabase.GetAssetPath(target);
                    model_agent = new ModelAgent(assetPath);
                }
                else
                {
                    model_agent = null;
                }
            }
        }

        /// <summary>
        /// Inspector上のGUI描画処理を行います
        /// </summary>
        public override void OnInspectorGUI()
        {
            // GUIの有効化
            GUI.enabled = !EditorApplication.isPlaying;

            // GUI描画
            pmd_config.OnGUIFunction();

            // Convertボタン
            EditorGUILayout.Space();
            if (message.Length != 0)
            {
                GUILayout.Label(message);
            }
            else
            {
                if (GUILayout.Button("Convert to Prefab"))
                {
                    if (null == model_agent) {
                        var obj = (PMDScriptableObject)target;
                        model_agent = new ModelAgent(obj.assetPath);
                    }
                    try
                    {
                        message = model_agent.CreatePrefab(pmd_config.shader_type
                                                , pmd_config.rigidFlag
                                                , pmd_config.animation_type
                                                , pmd_config.use_ik
                                                , pmd_config.scale
                                                , pmd_config.is_pmx_base_import
                                                );
                    }
                    catch(Exception ex)
                    {
                        message = ex.Message;
                        throw;
                    }
                }
            }
            GUILayout.Space(40);

            // モデル情報
            if (model_agent == null) return;
            EditorGUILayout.LabelField("Model Name");
            EditorGUILayout.LabelField(model_agent.name, EditorStyles.textField);

            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Comment");
            EditorGUILayout.LabelField(model_agent.comment, EditorStyles.textField, GUILayout.Height(300));
        }
    }
}
