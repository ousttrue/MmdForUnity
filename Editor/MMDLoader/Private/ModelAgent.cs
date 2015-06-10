using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System;
using System.IO;

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

        public FileInfo PrefabPath
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
        public GameObject CreateGameObject(ShaderType shader_type, bool use_rigidbody, AnimationType animation_type, bool use_ik, float scale)
        {
            try
            {
                Message = "Successfully converted.";
                return CreateGameObjectFromPmx(shader_type, use_rigidbody, animation_type, use_ik, scale);
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

        private GameObject CreateGameObjectFromPmx(ShaderType shader_type, bool use_rigidbody, AnimationType animation_type, bool use_ik, float scale)
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

            var export_folder=pmx_format.path.Directory.ChildDirectory(pmx_format.path.GetNameWithoutExtension() + ".convert");
            export_folder.CreateUnityFolder();
            var mesh_folder = export_folder.ChildDirectory("Meshes");
            mesh_folder.CreateUnityFolder();
            var texture_folder = export_folder.ChildDirectory("Textures");
            texture_folder.CreateUnityFolder();
            var material_folder = export_folder.ChildDirectory("Materials");
            material_folder.CreateUnityFolder();
            var physics_folder = export_folder.ChildDirectory("Physics");
            physics_folder.CreateUnityFolder();
            UnityEditor.AssetDatabase.Refresh();

            // プレファブパスの設定
            PrefabPath = export_folder.ChildFile(pmx_format.path.GetNameWithoutExtension() + ".prefab");

            return PMXConverter.CreateGameObject(export_folder
                , mesh_folder, texture_folder, material_folder, physics_folder
                , pmx_format, use_rigidbody, animation_type, use_ik, scale);
        }
    }
}
