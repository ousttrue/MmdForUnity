#define USE_DEFAULTASSETDISPATCHER

// Inspectorからインポートなどができるようになります
// 他スクリプトと競合してしまう時はコメントアウトしてください

#define USE_INSPECTOR

//----------

#if USE_INSPECTOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;


namespace MMD
{
#if USE_DEFAULTASSETDISPATCHER
    [CustomEditor(typeof(DefaultAsset))]
    public class DefaultAssetDispatcher : Editor
    {
        delegate bool CanInspect(object target);
        public Editor m_cache;
        Editor FindEditor()
        {
            if (m_cache == null)
            {
                //現在のコードを実行しているアセンブリを取得する
                foreach (var t in Assembly.GetExecutingAssembly().GetTypes()
                    .Where(t =>
                        // Editorを継承している
                        t.IsSubclassOf(typeof(Editor))
                    )
                    )
                {
                    // 判定メソッド
                    var method = t.GetMethod("CanInspect", BindingFlags.Static | BindingFlags.Public);
                    if (method != null && (Boolean)method.Invoke(null, new[] { target }))
                    {
                        Editor.CreateCachedEditor(target, t, ref m_cache);
                        break;
                    }
                }
            }

            return m_cache;
        }

        public override void OnInspectorGUI()
        {
            var editor = FindEditor();
            if (editor == null)
            {
                return;
            }

            GUI.enabled = true;
            editor.OnInspectorGUI();
            GUI.enabled = false;
        }
    }

#else
	[InitializeOnLoad]
	public class InspectorBase : Editor
	{
		static InspectorBase()
		{
			EntryEditorApplicationUpdate();
		}

		[DidReloadScripts]
		static void OnDidReloadScripts()
		{
			EntryEditorApplicationUpdate();
		}

		static void EntryEditorApplicationUpdate()
		{
			EditorApplication.update += Update;
		}

		static void Update()
		{
			if (Selection.objects.Length != 0)
			{
				string path = AssetDatabase.GetAssetPath(Selection.activeObject);
				string extension = Path.GetExtension(path).ToLower();

				if (extension == ".pmd" || extension == ".pmx")
				{
					SetupScriptableObject<PMDScriptableObject>(path);
				}
				else if (extension == ".vmd")
				{
					SetupScriptableObject<VMDScriptableObject>(path);
				}
			}
		}

		static void SetupScriptableObject<T>(string path) where T : ScriptableObjectBase
		{
			int count = Selection.objects.OfType<T>().Count();
			if (count != 0) return;
			T scriptableObject = ScriptableObject.CreateInstance<T>();
			scriptableObject.assetPath = path;
			Selection.activeObject = scriptableObject;
			EditorUtility.UnloadUnusedAssetsImmediate();
		}
	}
}
#endif

#endif
}