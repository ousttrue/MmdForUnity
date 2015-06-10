using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace MMD
{

    public class AlphaReadableTexture : System.IDisposable
    {

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name="texture_path_list">テクスチャ相対パスリスト</param>
        /// <param name="src_dir">カレントディレクトリ("/"終わり、テクスチャの相対パス基点)</param>
        /// <param name="temporary_directory">解析作業用ディレクトリ("/"終わり、このディレクトリの下に解析作業用ディレクトリを作ります)</param>
        public AlphaReadableTexture(string[] texture_list, DirectoryInfo src_dir, DirectoryInfo temporary_directory)
        {
            m_dst_dir = temporary_directory.ChildDirectory(directory_name);
            CreateDirectoryPath(m_dst_dir);
            m_copy_list = texture_list
                .Where(x => !string.IsNullOrEmpty(x)).Distinct()
                .Select(x => m_dst_dir.ChildFile(x))
                .ToArray();

            //テクスチャ作成
            foreach (var x in texture_list.Where(x => !string.IsNullOrEmpty(x)).Distinct())
            {
                CreateReadableTexture(src_dir, m_dst_dir, x);
            }
            AssetDatabase.Refresh();
                
            //テクスチャ取得
            textures = texture_list
                .Select(x => m_dst_dir.ChildFile(x))
                .Select(x => x.GetUnityAssetPath())
                .Select(x => AssetDatabase.LoadAssetAtPath<Texture2D>(x))
                .ToArray();
        }

        /// <summary>
        /// 読み込み可能テクスチャの作成
        /// </summary>
        /// <param name="x">テクスチャパス</param>
        static void CreateReadableTexture(DirectoryInfo src_dir, DirectoryInfo dst_dir, string x)
        {
            var base_texture_path = src_dir.ChildFile(x);
            var readable_texture_path = dst_dir.ChildFile(x);
            bool is_copy_success = AssetDatabase.CopyAsset(base_texture_path.GetUnityAssetPath(), readable_texture_path.GetUnityAssetPath());
            if (!is_copy_success)
            {
                throw new System.InvalidOperationException();
            }
        }

        /// <summary>
        /// 読み込み可能テクスチャの取得
        /// </summary>
        /// <value>読み込み可能テクスチャ</value>
        public Texture2D[] textures { get; private set; }

        /// <summary>
        /// Disposeインターフェース
        /// </summary>
        public void Dispose()
        {
            //テクスチャ破棄
            foreach (var x in m_copy_list)
            {
                AssetDatabase.DeleteAsset(x.GetUnityAssetPath());
            }

            //ディレクトリの破棄
            if (m_dst_dir.Exists)
            {
                System.IO.Directory.Delete(m_dst_dir.FullName, true);
            }
        }

        /// <summary>
        /// 解析対象ディレクトリ名の取得
        /// </summary>
        /// <value>The directory_name.</value>
        public static string directory_name { get { return "AlphaReadableTextureDirectory.MmdForUnity"; } }

        /// <summary>
        /// ディレクトリの作成(親ディレクトリが無ければ再帰的に作成)
        /// </summary>
        /// <param name="path">ディレクトリパス</param>
        private static void CreateDirectoryPath(DirectoryInfo path)
        {
            if (path.Exists) return;

            CreateDirectoryPath(path.Parent);

            path.CreateUnityFolder();
        }

        private FileInfo[] m_copy_list;		//解析するテクスチャリスト
        private DirectoryInfo m_dst_dir;	//解析作業用ディレクトリ
    }
}