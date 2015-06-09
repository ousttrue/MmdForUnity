using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System;
using MMD.PMX;

namespace MMD
{
    public static class IEnumerableExtensions
    {
        public static void ForEach<T>(this IEnumerable<T> enumerable, Action<T, int> pred)
        {
            int i = 0;
            foreach (var e in enumerable)
            {
                pred(e, i++);
            }
        }

        public static void ZipForEach<S, T>(this IEnumerable<S> s, IEnumerable<T> t, Action<S, T, int> pred)
        {
            var lhs = s.GetEnumerator();
            var rhs = t.GetEnumerator();
            int i = 0;
            while(lhs.MoveNext() && rhs.MoveNext())
            {
                pred(lhs.Current, rhs.Current, i++);
            }
        }
    }

    /// <summary>
    /// サブメッシュ
    /// </summary>
    public class Submesh
    {
        //マテリアル
        public int material_index
        {
            get;
            private set;
        }
        //面
        public int[] indices
        {
            get;
            private set;
        }
        //頂点
        public int unique_vertex_count
        {
            get;
            private set;
        }

        public Submesh(int index, IEnumerable<int> indices)
        {
            this.material_index = index;
            this.indices = indices.ToArray();
            unique_vertex_count = this.indices.Distinct().Count(); //重複削除
        }

        /// <summary>
        /// 1マテリアルの頂点数が1メッシュで表現出来ない場合に分割する
        /// </summary>
        /// <returns>メッシュ作成情報のマテリアルパック</returns>
        /// <param name='creation_infos'>メッシュ作成情報のマテリアルパック</param>
        public IEnumerable<Submesh> SplitSubMesh(int max_vertex_count)
        {
            if (max_vertex_count <= unique_vertex_count)
            {
                //1メッシュに収まらないなら
                //分離
                int plane_end = indices.Length;
                int plane_start = 0;
                while (plane_start < plane_end)
                {
                    //まだ面が有るなら
                    int plane_count = 0;
                    int vertex_count = 0;
                    while (true)
                    {
                        //現在の頂点数から考えると、余裕分の1/3迄の数の面は安定して入る
                        //はみ出て欲しいから更に1面(3頂点)を足す
                        plane_count += (max_vertex_count - vertex_count) / 3 * 3 + 3;
                        vertex_count = indices.Skip(plane_start)	//面頂点インデックス取り出し(先頭)
                                                                .Take(plane_count)	//面頂点インデックス取り出し(末尾)
                                                                .Distinct()				//重複削除
                                                                .Count();				//個数取得
                        if (max_vertex_count <= vertex_count)
                        {
                            //1メッシュを超えているなら
                            //此処でのメッシュ超えは必ずc_max_vertex_count_in_meshぎりぎりで有り、1面(3頂点)を1つ取れば収まる様になっている
                            plane_count -= 3;
                            break;
                        }
                        if (plane_end <= (plane_start + plane_count))
                        {
                            //面の最後なら
                            break;
                        }
                    }
                    //分離分を戻り値の追加
                    yield return new Submesh(material_index, indices.Skip(plane_start).Take(plane_count));

                    //開始点を後ろに
                    plane_start += plane_count;
                }
            }
            else
            {
                //1メッシュに収まるなら
                //素通し
                yield return this;
            }
        }
    }

    /// <summary>
    /// メッシュを作成する時に参照するデータの纏め
    /// </summary>
    class MeshCreationInfo
    {
        //meshに含まれる最大頂点数(Unity3D的には65536迄入ると思われるが、ushort.MaxValueは特別な値として使うのでその分を除外)
        const int c_max_vertex_count_in_mesh = 65535;

        public Submesh[] submeshes;
        //総頂点
        public int[] all_vertices;
        //頂点リアサインインデックス用辞書
        public Dictionary<int, int> reassign_dictionary;

        /// <summary>
        /// メッシュを作成する為の情報を作成(複数メッシュ版)
        /// </summary>
        /// <returns>メッシュ作成情報</returns>
        public static IEnumerable<MeshCreationInfo> CreateMeshCreationInfoMulti(PMXFormat format)
        {
            //マテリアル単位のPackを作成する
            var packs = format.CreateMeshCreationInfoPacks()
                //マテリアル細分化
                .SelectMany(pack => pack.SplitSubMesh(c_max_vertex_count_in_mesh))
                //頂点数の多い順に並べる(メッシュ分割アルゴリズム上、後半に行く程頂点数が少ない方が敷き詰め効率が良い)
                .OrderByDescending(x => x.unique_vertex_count)
                .ToArray();

            do
            {
                int vertex_sum = 0;
                MeshCreationInfo info = new MeshCreationInfo();
                //マテリアルパック作成
                info.submeshes = Enumerable.Range(0, packs.Length)
                                        .Where(x => null != packs[x]) //有効なマテリアルに絞る
                                        .Where(x =>
                                        {	//採用しても頂点数が限界を超えないなら
                                            vertex_sum += packs[x].unique_vertex_count;
                                            return vertex_sum < c_max_vertex_count_in_mesh;
                                        })
                                        .Select(x =>
                                        {	//マテリアルの採用と無効化
                                            var pack = packs[x];
                                            packs[x] = null;
                                            return pack;
                                        })
                                        .ToArray();
                //マテリアルインデックスに並べる(メッシュの選定が終わったので見易い様に並びを戻す)
                System.Array.Sort(info.submeshes, (x, y) => ((x.material_index > y.material_index) ? 1 : (x.material_index < y.material_index) ? -1 : 0));
                //総頂点作成
                info.all_vertices = info.submeshes.SelectMany(x => x.indices).OrderBy(x => x).Distinct().ToArray();
                //頂点リアサインインデックス用辞書作成
                info.reassign_dictionary = new Dictionary<int, int>();
                int reassign_index = 0;
                foreach (var i in info.all_vertices)
                {
                    info.reassign_dictionary[i] = reassign_index++;
                }
                //戻り値に追加
                yield return info;
            } while (packs.Any(x => null != x)); //使用していないマテリアルが為るならループ
        }
    }

    static class StringExtensions
    {
        /// <summary>
        /// ファイルパス文字列の取得
        /// </summary>
        /// <returns>ファイルパスに使用可能な文字列</returns>
        /// <param name='src'>ファイルパスに使用したい文字列</param>
        public static string GetFilePathString(this string src)
        {
            return src.Replace('\\', '＼')
                        .Replace('/', '／')
                        .Replace(':', '：')
                        .Replace('*', '＊')
                        .Replace('?', '？')
                        .Replace('"', '”')
                        .Replace('<', '＜')
                        .Replace('>', '＞')
                        .Replace('|', '｜')
                        .Replace("\n", string.Empty)
                        .Replace("\r", string.Empty);
        }

        public static void CreateUnityFolder(this string folder)
        {
            if (System.IO.Directory.Exists(folder)) return;
            UnityEditor.AssetDatabase.CreateFolder(
                System.IO.Path.GetDirectoryName(folder)
                , System.IO.Path.GetFileName(folder));
        }
    }

    static class PMXFormatExtensions
    {
        /// <summary>
        /// 全マテリアルをメッシュ作成情報のマテリアルパックとして返す
        /// </summary>
        /// <returns>メッシュ作成情報のマテリアルパック</returns>
        public static IEnumerable<Submesh> CreateMeshCreationInfoPacks(this PMXFormat format)
        {
            int plane_start = 0;
            //マテリアル単位のPackを作成する
            return format.materials
                            .Select((m, x) =>
                            {
                                var pack = new Submesh(x, format.indices
                                        .Skip(plane_start)
                                        .Take(m.face_vert_count));
                                plane_start += m.face_vert_count;
                                return pack;
                            })
                            ;
        }

        #region Physics
        /// <summary>
        /// Sphere Colliderの設定
        /// </summary>
        /// <param name='rigidbody'>PMX用剛体データ</param>
        /// <param name='obj'>Unity用剛体ゲームオブジェクト</param>
        static void EntrySphereCollider(PMXFormat.Rigidbody rigidbody, UnityEngine.GameObject obj, float scale)
        {
            var collider = obj.AddComponent<UnityEngine.SphereCollider>();
            collider.radius = rigidbody.shape_size.x * scale;
        }

        /// <summary>
        /// Box Colliderの設定
        /// </summary>
        /// <param name='rigidbody'>PMX用剛体データ</param>
        /// <param name='obj'>Unity用剛体ゲームオブジェクト</param>
        static void EntryBoxCollider(PMXFormat.Rigidbody rigidbody, UnityEngine.GameObject obj, float scale)
        {
            var collider = obj.AddComponent<UnityEngine.BoxCollider>();
            collider.size = rigidbody.shape_size * 2.0f * scale;
        }

        /// <summary>
        /// Capsule Colliderの設定
        /// </summary>
        /// <param name='rigidbody'>PMX用剛体データ</param>
        /// <param name='obj'>Unity用剛体ゲームオブジェクト</param>
        static void EntryCapsuleCollider(PMXFormat.Rigidbody rigidbody, UnityEngine.GameObject obj, float scale)
        {
            var collider = obj.AddComponent<UnityEngine.CapsuleCollider>();
            collider.radius = rigidbody.shape_size.x * scale;
            collider.height = (rigidbody.shape_size.y + rigidbody.shape_size.x * 2.0f) * scale;
        }

        /// <summary>
        /// 物理素材の作成
        /// </summary>
        /// <returns>物理素材</returns>
        /// <param name='rigidbody'>PMX用剛体データ</param>
        /// <param name='index'>剛体インデックス</param>
        static UnityEngine.PhysicMaterial CreatePhysicMaterial(PMXFormat.Rigidbody rigidbody, String model_name, int index)
        {
            //PMXFormat.Rigidbody[] rigidbodys = format.rigidbodies;
            //PMXFormat.Rigidbody rigidbody = rigidbodys[index];
            var material = new UnityEngine.PhysicMaterial(model_name + "_r" + rigidbody.name);
            material.bounciness = rigidbody.recoil;
            material.staticFriction = rigidbody.friction;
            material.dynamicFriction = rigidbody.friction;
            return material;
        }

        /// <summary>
        /// 剛体をUnity用に変換する
        /// </summary>
        /// <returns>Unity用剛体ゲームオブジェクト</returns>
        /// <param name='rigidbody'>PMX用剛体データ</param>
        public static UnityEngine.GameObject ConvertRigidbody(this PMXFormat.Rigidbody rigidbody, string model_name, int i, float scale, string export_folder)
        {
            var result = new UnityEngine.GameObject("r" + rigidbody.name);
            //result.AddComponent<Rigidbody>();	// 1つのゲームオブジェクトに複数の剛体が付く事が有るので本体にはrigidbodyを適用しない

            //位置・回転の設定
            result.transform.position = rigidbody.collider_position * scale;
            result.transform.rotation = UnityEngine.Quaternion.Euler(rigidbody.collider_rotation * UnityEngine.Mathf.Rad2Deg);

            // Colliderの設定
            switch (rigidbody.shape_type)
            {
                case PMXFormat.Rigidbody.ShapeType.Sphere:
                    EntrySphereCollider(rigidbody, result, scale);
                    break;
                case PMXFormat.Rigidbody.ShapeType.Box:
                    EntryBoxCollider(rigidbody, result, scale);
                    break;
                case PMXFormat.Rigidbody.ShapeType.Capsule:
                    EntryCapsuleCollider(rigidbody, result, scale);
                    break;
                default:
                    throw new System.ArgumentException();
            }

            // マテリアルの設定
            var material = CreatePhysicMaterial(rigidbody, model_name, i);
            string file_name = export_folder + "/Physics/" + i.ToString() + "_" + rigidbody.name.GetFilePathString() + ".asset";
            UnityEditor.AssetDatabase.CreateAsset(material, file_name);
            result.GetComponent<UnityEngine.Collider>().material = material;

            return result;
        }
        #endregion
    }
}
