using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System;

namespace MMD
{
    public class ModelAgent
    {
        public string FilePath
        {
            get;
            private set;
        }

        public PMX.PMXFormat.Header Header
        {
            get;
            private set;
        }

        public GameObject GameObject
        {
            get;
            private set;
        }

        public string PrefabPath
        {
            get;
            private set;
        }

        public string Message
        {
            get;
            private set;
        }

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name='file'>読み込むファイルパス</param>
        public ModelAgent(string file_path)
        {
            if (string.IsNullOrEmpty(file_path))
            {
                throw new System.ArgumentException();
            }
            FilePath = file_path;

            try
            {
                //PMX読み込みを試みる
                Header = PMXLoaderScript.GetHeader(FilePath);
            }
            catch (System.FormatException)
            {
                //PMXとして読み込めなかったら
                //PMDとして読み込む
                var pmd_header = PMDLoaderScript.GetHeader(FilePath);
                Header = PMXLoaderScript.PMD2PMX(pmd_header);
            }
        }

        /// <summary>
        /// プレファブを作成する
        /// </summary>
        /// <param name='shader_type'>シェーダーの種類</param>
        /// <param name='use_rigidbody'>剛体を使用するか</param>
        /// <param name='animation_type'>アニメーションタイプ</param>
        /// <param name='use_ik'>IKを使用するか</param>
        /// <param name='scale'>スケール</param>
        /// <param name='is_pmx_base_import'>PMX Baseでインポートするか</param>
        public GameObject CreateGameObject(PMDConverter.ShaderType shader_type, bool use_rigidbody, PMXConverter.AnimationType animation_type, bool use_ik, float scale, bool is_pmx_base_import)
        {
            try
            {
                Message = "Successfully converted.";
                if (is_pmx_base_import)
                {
                    return CreateGameObjectFromPmx(shader_type, use_rigidbody, animation_type, use_ik, scale);
                }
                else
                {
                    return CreateGameObjectFromPmd(shader_type, use_rigidbody, animation_type, use_ik, scale);
                }
            }
            catch(Exception ex)
            {
                Message = ex.Message;
                throw;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private GameObject CreateGameObjectFromPmx(PMDConverter.ShaderType shader_type, bool use_rigidbody, PMXConverter.AnimationType animation_type, bool use_ik, float scale)
        {
            EditorUtility.DisplayProgressBar("CreatePrefab", "Import Pmx...", 0);

            //PMX Baseでインポートする
            //PMXファイルのインポート
            PMX.PMXFormat pmx_format = null;
            try
            {
                //PMX読み込みを試みる
                pmx_format = PMXLoaderScript.Import(FilePath);
            }
            catch (System.FormatException)
            {
                //PMXとして読み込めなかったら
                //PMDとして読み込む
                PMD.PMDFormat pmd_format = PMDLoaderScript.Import(FilePath);
                pmx_format = PMXLoaderScript.PMD2PMX(pmd_format);
            }
            Header = pmx_format.header;

            // 出力フォルダ
            if (!System.IO.Directory.Exists(pmx_format.meta_header.export_folder))
            {
                UnityEditor.AssetDatabase.CreateFolder(pmx_format.meta_header.folder, pmx_format.meta_header.export_folder_name);
            }

            // プレファブパスの設定
            PrefabPath = pmx_format.meta_header.export_folder + "/" + pmx_format.meta_header.name + ".prefab";

            return PMXConverter.CreateGameObject(pmx_format, use_rigidbody, animation_type, use_ik, scale);
        }

        [Obsolete]
        private GameObject CreateGameObjectFromPmd(PMDConverter.ShaderType shader_type, bool use_rigidbody, PMXConverter.AnimationType animation_type, bool use_ik, float scale)
        {
            EditorUtility.DisplayProgressBar("CreatePrefab", "Import Pmd...", 0);

            //PMXエクスポーターを使用しない
            //PMDファイルのインポート
            PMD.PMDFormat pmd_format = null;
            try
            {
                //PMX読み込みを試みる
                PMX.PMXFormat pmx_format = PMXLoaderScript.Import(FilePath);
                pmd_format = PMXLoaderScript.PMX2PMD(pmx_format);
            }
            catch (System.FormatException)
            {
                //PMXとして読み込めなかったら
                //PMDとして読み込む
                pmd_format = PMDLoaderScript.Import(FilePath);
            }
            Header = PMXLoaderScript.PMD2PMX(pmd_format.head);

            // プレファブパスの設定
            PrefabPath = pmd_format.folder + "/" + pmd_format.name + ".prefab";

            //ゲームオブジェクトの作成
            bool use_mecanim = PMXConverter.AnimationType.LegacyAnimation == animation_type;
            return PMDConverter.CreateGameObject(pmd_format, shader_type, use_rigidbody, use_mecanim, use_ik, scale);
        }
    }
}
