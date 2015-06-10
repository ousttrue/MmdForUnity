using System.Collections.Generic;
using System.Linq;
using MMD.PMX;
using System;
using System.IO;

namespace MMD
{
    /// <summary>
    /// シェーダの種類
    /// </summary>
    public enum ShaderType
    {
        Default,		/// Unityのデフォルトシェーダ
        HalfLambert,	/// もやっとしたLambertっぽくなる
        MMDShader		/// MMDっぽいシェーダ
    }

    /// <summary>
    /// アニメーションタイプ
    /// </summary>
    public enum AnimationType
    {
        GenericMecanim,		//汎用アバターでのMecanim
        HumanMecanim,		//人型アバターでのMecanim
        LegacyAnimation,	//旧式アニメーション
    }

    public static class PMXConverter
    {
        /// <summary>
        /// GameObjectを作成する
        /// </summary>
        /// <param name='format'>内部形式データ</param>
        /// <param name='use_rigidbody'>剛体を使用するか</param>
        /// <param name='animation_type'>アニメーションタイプ</param>
        /// <param name='use_ik'>IKを使用するか</param>
        /// <param name='scale'>スケール</param>
        public static UnityEngine.GameObject CreateGameObject(DirectoryInfo export_folder
            , DirectoryInfo mesh_folder, DirectoryInfo texture_folder, DirectoryInfo material_folder, DirectoryInfo physics_folder
            , PMXFormat format, bool use_rigidbody, AnimationType animation_type, bool use_ik, float scale)
        {
            var root = new UnityEngine.GameObject(format.path.GetNameWithoutExtension());

            //MMDEngine追加
            MMDEngine engine = root.AddComponent<MMDEngine>();
            //スケール・エッジ幅
            engine.scale = scale;
            engine.outline_width = 1.0f;
            engine.material_outline_widths = format.materials.Select(x => x.edge_size).ToArray();
            engine.enable_render_queue = false; //初期値無効
            const int c_render_queue_transparent = 3000;
            engine.render_queue_value = c_render_queue_transparent;

            const float progressUnit = (1.0f / 3.0f) / 7.0f;
            float progress = 1.0f / 3.0f;

            UnityEditor.EditorUtility.DisplayProgressBar("CreatePrefab", "Import Pmx(CreteMesh)...", progress);
            // メッシュを作成する為の情報を作成
            var creation_list = MeshCreationInfo.CreateMeshCreationInfoMulti(format).ToArray();				
            // メッシュの生成・設定
            var meshes = creation_list.Select((c, i)=>{
                var mesh = EntryAttributesForMesh(c, format, scale);
                SetSubMesh(mesh, c);
                var file_name = mesh_folder.ChildFile(i.ToString() + "_" + format.path.GetNameWithoutExtension() + ".asset");
                UnityEngine.Debug.Log(file_name.GetUnityAssetPath());
                UnityEditor.AssetDatabase.CreateAsset(mesh, file_name.GetUnityAssetPath());
                return mesh;
            }).ToArray();									
            progress += progressUnit;

            // テクスチャコピー
            format.textures
                .ForEach((x, i) =>
            {
                var src = format.path.Directory.ChildFile(x);
                var dst = texture_folder.ChildFile(x);
                UnityEditor.AssetDatabase.CopyAsset(src.GetUnityAssetPath(), dst.GetUnityAssetPath());
            });

            // マテリアルの生成・設定
            UnityEditor.EditorUtility.DisplayProgressBar("CreatePrefab", "Import Pmx(CreteMaterials)...", progress);
            var materials =  format.materials
                .Select((x, i) =>
                {
                    var is_transparent = (x.diffuse_color.a < 1.0f) || (x.edge_color.a < 1.0f);
                    var is_transparent_by_morph = IsTransparentByMaterialMorph(format)[i];
                    var list=GetTextureList(export_folder, format);
                    var texture = list[i];
                    var uv = GetUvList(format)[i];
                    var is_transparent_by_texture = texture != null
                                                    ? IsTransparentByTextureAlphaWithUv(texture, uv)
                                                    : false;
                    return ConvertMaterial(format, root, i, is_transparent || is_transparent_by_morph || is_transparent_by_texture, scale);
                })
                .ToArray()
                ;

            materials.ZipForEach(format.materials, (m, src, i) =>
            {
                var file_name = material_folder.ChildFile(i.ToString() + "_" + src.name + ".asset");
                UnityEditor.AssetDatabase.CreateAsset(m, file_name.GetUnityAssetPath());
            });

            //メッシュ単位へ振り分け
            var submesh_materials=creation_list.Select(c => c.submeshes.Select(x => materials[x.material_index]).ToArray()).ToArray();

            progress += progressUnit;

            UnityEditor.EditorUtility.DisplayProgressBar("CreateBones", "Import Pmx(CreteMaterials)...", progress);
            // ボーンの生成・設定
            UnityEngine.GameObject[] bones = CreateBones(format, root.transform, scale);
            // バインドポーズの作成							
            var renderers = BuildingBindpose(root, meshes, submesh_materials, bones).ToArray();	
            progress += progressUnit;

            // モーフの生成・設定
            UnityEditor.EditorUtility.DisplayProgressBar("CreateBones", "Import Pmx(CreateMorph)...", progress);
            CreateMorph(format, meshes, submesh_materials, bones, renderers, creation_list, scale).parent = root.transform;

            progress += progressUnit;

            UnityEditor.EditorUtility.DisplayProgressBar("CreateBones", "Import Pmx(EntryBoneController)...", progress);
            // BoneController・IKの登録(use_ik_を使った判定はEntryBoneController()の中で行う)
            {
                engine.bone_controllers = EntryBoneController(format, bones, use_ik);
                engine.ik_list = engine.bone_controllers.Where(x => null != x.ik_solver)
                                                        .Select(x => x.ik_solver)
                                                        .ToArray();
            }
            progress += progressUnit;

            // 剛体関連
            UnityEditor.EditorUtility.DisplayProgressBar("CreateBones", "Import Pmx(RigidBody)...", progress);
            if (use_rigidbody)
            {
                // 剛体の登録
                var rigids = format.rigidbodies.Select((x, i) => x.ConvertRigidbody(format.path.GetNameWithoutExtension(), i, scale, physics_folder)).ToArray();
                AssignRigidbodyToBone(format, bones, rigids).transform.parent = root.transform;

                SetRigidsSettings(format, bones, rigids);

                // ConfigurableJointの設定
                var joints = SetupConfigurableJoint(format, rigids, scale);
                GlobalizeRigidbody(root, joints);

                // 非衝突グループ
                List<int>[] ignoreGroups = SettingIgnoreRigidGroups(format, rigids);
                var groupTarget = format.rigidbodies.Select(x => (int)x.ignore_collision_group).ToArray();

                MMDEngine.Initialize(engine, groupTarget, ignoreGroups, rigids);
            }
            progress += progressUnit;

            // Mecanim設定
            UnityEditor.EditorUtility.DisplayProgressBar("CreateBones", "Import Pmx(Animation)...", progress);
            if (AnimationType.LegacyAnimation != animation_type)
            {
                //アニメーター追加
                AvatarSettingScript avatar_setting = new AvatarSettingScript(root, bones);
                switch (animation_type)
                {
                    case AnimationType.GenericMecanim: //汎用アバターでのMecanim
                        avatar_setting.SettingGenericAvatar();
                        break;
                    case AnimationType.HumanMecanim: //人型アバターでのMecanim
                        avatar_setting.SettingHumanAvatar();
                        break;
                    default:
                        throw new System.ArgumentException();
                }

                var file_name = export_folder.ChildFile(format.path.GetNameWithoutExtension() + ".avatar.asset");
                avatar_setting.CreateAsset(file_name.GetUnityAssetPath());
            }
            else
            {
                root.AddComponent<UnityEngine.Animation>();	// アニメーション追加
            }
            progress += progressUnit;

            return root;
        }



        /// <summary>
        /// メッシュに基本情報(頂点座標・法線・UV・ボーンウェイト)を登録する
        /// </summary>
        /// <param name='mesh'>対象メッシュ</param>
        /// <param name='creation_list'>メッシュ作成情報</param>
        static UnityEngine.Mesh EntryAttributesForMesh(MeshCreationInfo creation_list, PMXFormat format, float scale)
        {
            var mesh = new UnityEngine.Mesh();
            mesh.vertices = creation_list.all_vertices.Select(x => format.vertices[x].pos * scale).ToArray();
            mesh.normals = creation_list.all_vertices.Select(x => format.vertices[x].normal_vec).ToArray();
            mesh.uv = creation_list.all_vertices.Select(x => format.vertices[x].uv).ToArray();
            if (0 < format.header.additionalUV)
            {
                //追加UVが1つ以上有れば
                //1つ目のみ登録
                mesh.uv2 = creation_list.all_vertices
                    .Select(x => new UnityEngine.Vector2(format.vertices[x].add_uv[0].x, format.vertices[x].add_uv[0].y)).ToArray();
            }
            mesh.boneWeights = creation_list.all_vertices
                .Select(x => ConvertBoneWeight(format.vertices[x].bone_weight)).ToArray();
            // 不透明度にエッジ倍率を0.25倍した情報を仕込む(0～8迄は表せる)
            mesh.colors = creation_list.all_vertices
                .Select(x => new UnityEngine.Color(0.0f, 0.0f, 0.0f, format.vertices[x].edge_magnification * 0.25f)).ToArray();
            return mesh;
        }

        /// <summary>
        /// ボーンウェイトをUnity用に変換する
        /// </summary>
        /// <returns>Unity用ボーンウェイト</returns>
        /// <param name='bone_weight'>PMX用ボーンウェイト</param>
        static UnityEngine.BoneWeight ConvertBoneWeight(PMXFormat.BoneWeight bone_weight)
        {
            //HACK: 取り敢えずボーンウェイトタイプを考えずにBDEFx系として登録する
            var result = new UnityEngine.BoneWeight();
            switch (bone_weight.method)
            {
                case PMXFormat.Vertex.WeightMethod.BDEF1: goto case PMXFormat.Vertex.WeightMethod.BDEF4;
                case PMXFormat.Vertex.WeightMethod.BDEF2: goto case PMXFormat.Vertex.WeightMethod.BDEF4;
                case PMXFormat.Vertex.WeightMethod.BDEF4:
                    //BDEF4なら
                    result.boneIndex0 = (int)bone_weight.bone1_ref;
                    result.weight0 = bone_weight.bone1_weight;
                    result.boneIndex1 = (int)bone_weight.bone2_ref; ;
                    result.weight1 = bone_weight.bone2_weight;
                    result.boneIndex2 = (int)bone_weight.bone3_ref;
                    result.weight2 = bone_weight.bone3_weight;
                    result.boneIndex3 = (int)bone_weight.bone4_ref;
                    result.weight3 = bone_weight.bone4_weight;
                    break;
                case PMXFormat.Vertex.WeightMethod.SDEF:
                    //SDEFなら
                    //HACK: BDEF4と同じ対応
                    goto case PMXFormat.Vertex.WeightMethod.BDEF4;
                case PMXFormat.Vertex.WeightMethod.QDEF:
                    //QDEFなら
                    //HACK: BDEF4と同じ対応
                    goto case PMXFormat.Vertex.WeightMethod.BDEF4;
                default:
                    throw new System.ArgumentOutOfRangeException();
            }
            return result;
        }

        /// <summary>
        /// メッシュにサブメッシュを登録する
        /// </summary>
        /// <param name='mesh'>対象メッシュ</param>
        /// <param name='creation_list'>メッシュ作成情報</param>
        static void SetSubMesh(UnityEngine.Mesh mesh, MeshCreationInfo creation_list)
        {
            // マテリアル対サブメッシュ
            // サブメッシュとはマテリアルに適用したい面頂点データのこと
            // 面ごとに設定するマテリアルはここ
            mesh.subMeshCount = creation_list.submeshes.Length;
            creation_list.submeshes.ForEach((s, i) =>
            {
                //format.indicesを[start](含む)から[start+count](含まず)迄取り出し
                //頂点リアサインインデックス変換
                var indices = s.indices.Select(x => creation_list.reassign_dictionary[x])
                                                                    .ToArray();
                mesh.SetTriangles(indices, i);
            });
        }

        /// <summary>
        /// 材質モーフに依る透過確認
        /// </summary>
        /// <returns>透過かの配列(true:透過, false:不透明)</returns>
        static bool[] IsTransparentByMaterialMorph(PMXFormat format)
        {
            bool[] result = Enumerable.Repeat(false, format.materials.Length)
                                        .ToArray();
            var transparent_material_indices = format.morphs.Where(x => PMXFormat.MorphData.MorphType.Material == x.morph_type) //材質モーフなら
                                                                            .SelectMany(x => x.morph_offset) //材質モーフオフセット取得
                                                                            .Select(x => (PMXFormat.MaterialMorphOffset)x) //材質モーフオフセットにキャスト
                                                                            .Where(x => (PMXFormat.MaterialMorphOffset.OffsetMethod.Mul == x.offset_method) && ((x.diffuse.a < 1.0f) || (x.edge_color.a < 1.0f))) //拡散色かエッジ色が透過に為るなら
                                                                            .Select(x => x.material_index) //マテリアルインデックス取得
                                                                            .Distinct(); //重複除去
            foreach (int material_index in transparent_material_indices)
            {
                //材質モーフに依って透過が要望されているなら
                //透過扱いにする
                if (material_index < format.materials.Length)
                {
                    //単体モーフのマテリアルインデックスなら
                    //対象マテリアルだけ透過扱い
                    result[material_index] = true;
                }
                else
                {
                    //全対象モーフのマテリアルインデックスなら
                    //全て透過扱い
                    result = Enumerable.Repeat(true, result.Length).ToArray();
                    break;
                }
            }
            return result;
        }

        /// <summary>
        /// テクスチャの取得
        /// </summary>
        /// <returns>テクスチャ配列</returns>
        static UnityEngine.Texture2D[] GetTextureList(DirectoryInfo export_folder, PMXFormat format)
        {
            var texture_list = format.materials
                .Select(x => x.usually_texture_index)
                .Select(x => ((x > -1 && x < format.textures.Length) ? format.textures[x] : null))
                ;

            using(var alpha_readable_texture_ = new AlphaReadableTexture(texture_list.ToArray()
                                                            , format.path.Directory
                                                            , export_folder.ChildDirectory("Materials")
                                                            ))
                                                            {
                return alpha_readable_texture_.textures;
            }
        }

        /// <summary>
        /// UVの取得
        /// </summary>
        /// <returns>UV配列</returns>
        /// <remarks>
        /// UVモーフにて改変される場合は未適応(0.0f)と全適応(1.0f)の2段階のみを扱い、中間適応は考慮しない。a
        /// 複数のUVモーフが同一頂点に掛かる場合に多重適応すると単体では参照出来無い領域迄参照出来る様に為るが、これは考慮しない。
        /// 同様にグループモーフに依る1.0f超えも考慮しない。
        /// </remarks>
        static UnityEngine.Vector2[][] GetUvList(PMXFormat format)
        {
            var vertex_list = format.CreateMeshCreationInfoPacks().Select(x => x.indices).ToArray();

            var uv_morphs = format.morphs
                                                            .Where(x => PMXFormat.MorphData.MorphType.Uv == x.morph_type) //UVモーフなら
                                                            .Select(x => x.morph_offset.Select(y => (PMXFormat.UVMorphOffset)y)
                                                                                    .ToDictionary(z => z.vertex_index, z => z.uv_offset) //頂点インデックスでディクショナリ化
                                                                    ) //UVモーフオフセット取得
                                                            .ToArray();

            var uv_list = vertex_list.Select(x => x.Select(y => format.vertices[y].uv).ToList()).ToArray();

            //材質走査
            bool is_cancel = false;
            for (int material_index = 0, material_index_max = uv_list.Length; material_index < material_index_max; ++material_index)
            {
                var indices = vertex_list[material_index];

                //UVモーフ走査
                for (int uv_morph_index = 0, uv_morph_index_max = uv_morphs.Length; uv_morph_index < uv_morph_index_max; ++uv_morph_index)
                {
                    var uv_morph = uv_morphs[uv_morph_index];
                    //ブログレスパー更新
                    is_cancel = UnityEditor.EditorUtility.DisplayCancelableProgressBar("Create UV Area Infomation"
                                                                            , "Material:[" + material_index + "|" + material_index_max + "]"
                                                                                + format.materials[material_index].name
                                                                                + "\t"
                                                                                + "UV Morph:[" + uv_morph_index + "|" + uv_morph_index_max + "]"
                                                                                + format.morphs.Where(x => PMXFormat.MorphData.MorphType.Uv == x.morph_type).Skip(uv_morph_index).First().morph_name
                                                                            , ((((float)uv_morph_index / (float)uv_morph_index_max) + (float)material_index) / (float)material_index_max)
                                                                            );
                    if (is_cancel)
                    {
                        break;
                    }

                    //先行UVモーフ対象確認(三角形構成を無視して全頂点をUVモーフ参照)
                    var vertex_dictionary = indices.Distinct().ToDictionary(x => x, x => true); //(UVモーフに設定されている頂点数依りも三角形構成頂点の方が多いと思うので、そちら側をlogNにする為に辞書作成)
                    if (uv_morph.Keys.Any(x => vertex_dictionary.ContainsKey(x)))
                    {
                        //UVモーフ対象なら
                        //頂点走査(三角形構成頂点走査)
                        for (int vertex_index = 0, vertex_index_max = indices.Length; vertex_index < vertex_index_max; vertex_index += 3)
                        {
                            //三角形構成頂点インデックス取り出し
                            int[] tri_vertices = new[]{indices[vertex_index+0]
														, indices[vertex_index+1]
														, indices[vertex_index+2]
														};
                            //UVモーフ対象確認
                            if (tri_vertices.Any(x => uv_morph.ContainsKey(x)))
                            {
                                //UVモーフ対象なら
                                //適応したUV値を作成
                                var tri_uv = tri_vertices.Select(x => new
                                {
                                    original_uv = format.vertices[x].uv
                                 ,
                                    add_uv = ((uv_morph.ContainsKey(x)) ? uv_morph[x] : UnityEngine.Vector4.zero)
                                }
                                                                )
                                                        .Select(x => new UnityEngine.Vector2(x.original_uv.x + x.add_uv.x, x.original_uv.y + x.add_uv.y));
                                //追加
                                uv_list[material_index].AddRange(tri_uv);
                            }
                        }
                    }
                }
                if (is_cancel)
                {
                    break;
                }
            }

            return uv_list.Select(x => x.ToArray()).ToArray();
        }

        /// <summary>
        /// UV値を考慮した、テクスチャのアルファ値に依る透過確認
        /// </summary>
        /// <returns>透過か(true:透過, false:不透明)</returns>
        /// <param name="texture">テクスチャ</param>
        /// <param name="uvs">UV値(3つ単位で三角形を構成する)</param>
        static bool IsTransparentByTextureAlphaWithUv(UnityEngine.Texture2D texture, UnityEngine.Vector2[] uvs)
        {
            bool result = true;
            if (UnityEngine.TextureFormat.Alpha8 == texture.format)
            {
                //ファイルがDDS以外なら(AlphaReadableTextureDirectoryImporterに依ってDDS以外はAlpha8に為る)
                //alphaIsTransparencyを確認する
                result = texture.alphaIsTransparency; //アルファ値を持たないなら透過フラグが立っていない
            }
            if (result)
            {
                //アルファ値を持つなら
                //詳細確認
                result = Enumerable.Range(0, uvs.Length / 3) //3つ単位で取り出す為の元インデックス
                                        .Select(x => x * 3) //3つ間隔に変換
                                        .Any(x => IsTransparentByTextureAlphaWithUv(texture, uvs[x + 0], uvs[x + 1], uvs[x + 2])); //三角形を透過確認、どれかが透過していたら透過とする
            }
            return result;
        }

        /// <summary>
        /// UV値を考慮した、テクスチャのアルファ値に依る透過確認
        /// </summary>
        /// <returns>透過か(true:透過, false:不透明)</returns>
        /// <param name="texture">テクスチャ</param>
        /// <param name="uv1">三角形頂点のUV値</param>
        /// <param name="uv2">三角形頂点のUV値</param>
        /// <param name="uv3">三角形頂点のUV値</param>
        /// <remarks>
        /// 理想ならば全テクセルを確認しなければならないが、
        /// 現在の実装では三角形を構成する各頂点のUV・重心・各辺の中心点の7点のテクセルしか確認していない
        /// </remarks>
        static bool IsTransparentByTextureAlphaWithUv(UnityEngine.Texture2D texture, UnityEngine.Vector2 uv1, UnityEngine.Vector2 uv2, UnityEngine.Vector2 uv3)
        {
            bool result = true; //透過
            do
            {
                //座標系が相違しているので補正
                uv1.Set(uv1.x, 1.0f - uv1.y - (1.0f / texture.height));
                uv2.Set(uv2.x, 1.0f - uv2.y - (1.0f / texture.height));
                uv3.Set(uv3.x, 1.0f - uv3.y - (1.0f / texture.height));

                const float c_threshold = 253.0f / 255.0f; //253程度迄は不透明として見逃す

                //頂点直下
                if (texture.GetPixelBilinear(uv1.x, uv1.y).a < c_threshold)
                {
                    break;
                }
                if (texture.GetPixelBilinear(uv2.x, uv2.y).a < c_threshold)
                {
                    break;
                }
                if (texture.GetPixelBilinear(uv3.x, uv3.y).a < c_threshold)
                {
                    break;
                }

                //重心
                var center = new UnityEngine.Vector2((uv1.x + uv2.x + uv3.x) / 3.0f, (uv1.y + uv2.y + uv3.y) / 3.0f);
                if (texture.GetPixelBilinear(center.x, center.y).a < c_threshold)
                {
                    break;
                }

                //辺中央
                var uv12 = new UnityEngine.Vector2((uv1.x + uv2.x) / 2.0f, (uv1.y + uv2.y) / 2.0f);
                if (texture.GetPixelBilinear(uv12.x, uv12.y).a < c_threshold)
                {
                    break;
                }
                var uv23 = new UnityEngine.Vector2((uv2.x + uv3.x) / 2.0f, (uv2.y + uv3.y) / 2.0f);
                if (texture.GetPixelBilinear(uv23.x, uv23.y).a < c_threshold)
                {
                    break;
                }
                var uv31 = new UnityEngine.Vector2((uv3.x + uv1.x) / 2.0f, (uv3.y + uv1.y) / 2.0f);
                if (texture.GetPixelBilinear(uv31.x, uv31.y).a < c_threshold)
                {
                    break;
                }

                //此処迄来たら不透明
                result = false;
            } while (false);
            return result;
        }

        /// <summary>
        /// マテリアルをUnity用に変換する
        /// </summary>
        /// <returns>Unity用マテリアル</returns>
        /// <param name='material_index'>PMX用マテリアルインデックス</param>
        /// <param name='is_transparent'>透過か</param>
        static UnityEngine.Material ConvertMaterial(PMXFormat format, UnityEngine.GameObject root, int material_index, bool is_transparent, float scale)
        {
            var model_folder = format.path.Directory;

            PMXFormat.Material material = format.materials[material_index];

            //先にテクスチャ情報を検索
            UnityEngine.Texture2D main_texture = null;
            if (material.usually_texture_index > -1 && material.usually_texture_index < format.textures.Length)
            {
                var texture_name=format.textures[material.usually_texture_index];
                if(!String.IsNullOrEmpty(texture_name)){
                    var path = model_folder.ChildFile(texture_name);
                    main_texture = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Texture2D>(path.GetUnityAssetPath());
                }
            }

            //マテリアルに設定
            string shader_path = GetMmdShaderPath(material, main_texture, is_transparent);
            var result = new UnityEngine.Material(UnityEngine.Shader.Find(shader_path));

            // シェーダに依って値が有ったり無かったりするが、設定してもエラーに為らない様なので全部設定
            result.SetColor("_Color", material.diffuse_color);
            result.SetColor("_AmbColor", material.ambient_color);
            result.SetFloat("_Opacity", material.diffuse_color.a);
            result.SetColor("_SpecularColor", material.specular_color);
            result.SetFloat("_Shininess", material.specularity);
            // エッジ
            const float c_default_scale = 0.085f; //0.085fの時にMMDと一致する様にしているので、それ以外なら補正
            result.SetFloat("_OutlineWidth", material.edge_size * scale / c_default_scale);
            result.SetColor("_OutlineColor", material.edge_color);
            //カスタムレンダーキュー
            {
                var engine = root.GetComponent<MMDEngine>();
                if (engine.enable_render_queue && is_transparent)
                {
                    //カスタムレンダーキューが有効 かつ マテリアルが透過なら
                    //マテリアル順に並べる
                    result.renderQueue = engine.render_queue_value + (int)material_index;
                }
                else
                {
                    //非透明なら
                    result.renderQueue = -1;
                }
            }

            // スフィアテクスチャ
            if (material.sphere_texture_index > -1 && material.sphere_texture_index < format.textures.Length)
            {
                string sphere_texture_file_name = format.textures[material.sphere_texture_index];
                var path = model_folder.ChildFile(sphere_texture_file_name);
                var sphere_texture = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Texture2D>(path.GetUnityAssetPath());

                switch (material.sphere_mode)
                {
                    case PMXFormat.Material.SphereMode.AddSphere: // 加算
                        result.SetTexture("_SphereAddTex", sphere_texture);
                        result.SetTextureScale("_SphereAddTex", new UnityEngine.Vector2(1, -1));
                        break;
                    case PMXFormat.Material.SphereMode.MulSphere: // 乗算
                        result.SetTexture("_SphereMulTex", sphere_texture);
                        result.SetTextureScale("_SphereMulTex", new UnityEngine.Vector2(1, -1));
                        break;
                    case PMXFormat.Material.SphereMode.SubTexture: // サブテクスチャ
                        //サブテクスチャ用シェーダーが無いので設定しない
                        break;
                    default:
                        //empty.
                        break;

                }
            }

            // トゥーンテクスチャ
            {
                string toon_texture_name = null;
                if (0 < material.common_toon)
                {
                    //共通トゥーン
                    toon_texture_name = "toon" + material.common_toon.ToString("00") + ".bmp";
                }
                else if (material.toon_texture_index > -1 && material.toon_texture_index < format.textures.Length)
                {
                    //自前トゥーン
                    toon_texture_name = format.textures[material.toon_texture_index];
                }
                if (!string.IsNullOrEmpty(toon_texture_name))
                {
                    string resource_path = UnityEditor.AssetDatabase.GetAssetPath(UnityEngine.Shader.Find("MMD/HalfLambertOutline"));
                    var toon_texture = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Texture2D>(resource_path);
                    result.SetTexture("_ToonTex", toon_texture);
                    result.SetTextureScale("_ToonTex", new UnityEngine.Vector2(1, -1));
                }
            }

            // テクスチャが空でなければ登録
            if (null != main_texture)
            {
                result.mainTexture = main_texture;
                result.mainTextureScale = new UnityEngine.Vector2(1, -1);
            }

            return result;
        }

        /// <summary>
        /// MMDシェーダーパスの取得
        /// </summary>
        /// <returns>MMDシェーダーパス</returns>
        /// <param name='material'>シェーダーを設定するマテリアル</param>
        /// <param name='texture'>シェーダーに設定するメインテクスチャ</param>
        /// <param name='is_transparent'>透過か</param>
        static string GetMmdShaderPath(PMXFormat.Material material, UnityEngine.Texture2D texture, bool is_transparent)
        {
            string result = "MMD/";
            if (is_transparent)
            {
                result += "Transparent/";
            }
            result += "PMDMaterial";
            if (IsEdgeMaterial(material))
            {
                result += "-with-Outline";
            }
            if (IsCullBackMaterial(material))
            {
                result += "-CullBack";
            }
            if (IsNoCastShadowMaterial(material))
            {
                result += "-NoCastShadow";
            }
#if MFU_ENABLE_NO_RECEIVE_SHADOW_SHADER	//影受け無しのシェーダはまだ無いので無効化
			if (IsNoReceiveShadowMaterial(material)) {
				result += "-NoReceiveShadow";
			}
#endif //MFU_ENABLE_NO_RECEIVE_SHADOW_SHADER
            return result;
        }

        /// <summary>
        /// エッジマテリアル確認
        /// </summary>
        /// <returns>true:エッジ有り, false:無エッジ</returns>
        /// <param name='material'>シェーダーを設定するマテリアル</param>
        static bool IsEdgeMaterial(PMXFormat.Material material)
        {
            bool result;
            if (0 != (PMXFormat.Material.Flag.Edge & material.flag))
            {
                //エッジ有りなら
                result = true;
            }
            else
            {
                //エッジ無し
                result = false;
            }
            return result;
        }

        /// <summary>
        /// 背面カリングマテリアル確認
        /// </summary>
        /// <returns>true:背面カリングする, false:背面カリングしない</returns>
        /// <param name='material'>シェーダーを設定するマテリアル</param>
        static bool IsCullBackMaterial(PMXFormat.Material material)
        {
            bool result;
            if (0 != (PMXFormat.Material.Flag.Reversible & material.flag))
            {
                //両面描画なら
                //背面カリングしない
                result = false;
            }
            else
            {
                //両面描画で無いなら
                //背面カリングする
                result = true;
            }
            return result;
        }

        /// <summary>
        /// 無影マテリアル確認
        /// </summary>
        /// <returns>true:無影, false:影放ち</returns>
        /// <param name='material'>シェーダーを設定するマテリアル</param>
        static bool IsNoCastShadowMaterial(PMXFormat.Material material)
        {
            bool result;
            if (0 != (PMXFormat.Material.Flag.CastShadow & material.flag))
            {
                //影放ち
                result = false;
            }
            else
            {
                //無影
                result = true;
            }
            return result;
        }

        /// <summary>
        /// 影受け無しマテリアル確認
        /// </summary>
        /// <returns>true:影受け無し, false:影受け</returns>
        /// <param name='material'>シェーダーを設定するマテリアル</param>
        static bool IsNoReceiveShadowMaterial(PMXFormat.Material material)
        {
            bool result;
            if (0 != (PMXFormat.Material.Flag.ReceiveSelfShadow & material.flag))
            {
                //影受け
                result = false;
            }
            else
            {
                //影受け無し
                result = true;
            }
            return result;
        }

        /// <summary>
        /// ボーン作成
        /// </summary>
        /// <returns>ボーンのゲームオブジェクト</returns>
        static UnityEngine.GameObject[] CreateBones(PMXFormat format, UnityEngine.Transform root, float scale)
        {
            var bones=format.bones.Select(x =>
            {
                var game_object = new UnityEngine.GameObject(x.bone_name);
                game_object.transform.position = x.bone_position * scale;
                return game_object;
            }).ToArray();
            AttachParentsForBone(format, root, bones);
            return bones;
        }

        /// <summary>
        /// 親子関係の構築
        /// </summary>
        /// <param name='bones'>ボーンのゲームオブジェクト</param>
        static void AttachParentsForBone(PMXFormat format, UnityEngine.Transform root, UnityEngine.GameObject[] bones)
        {
            //モデルルートを生成してルートの子供に付ける
            var model_root_transform = (new UnityEngine.GameObject("Model")).transform;
            model_root_transform.parent = root.transform;

            for (int i = 0, i_max = format.bones.Length; i < i_max; ++i)
            {
                int parent_bone_index = format.bones[i].parent_bone_index;
                if (parent_bone_index > -1 && parent_bone_index < bones.Length)
                {
                    //親のボーンが有るなら
                    //それの子に為る
                    bones[i].transform.parent = bones[parent_bone_index].transform;
                }
                else
                {
                    //親のボーンが無いなら
                    //モデルルートの子に為る
                    bones[i].transform.parent = model_root_transform;
                }
            }
        }

        /// <summary>
        /// モーフ作成
        /// </summary>
        /// <param name='mesh'>対象メッシュ</param>
        /// <param name='materials'>対象マテリアル</param>
        /// <param name='bones'>対象ボーン</param>
        /// <param name='renderers'>対象レンダラー</param>
        /// <param name='creation_list'>メッシュ作成情報</param>
        static UnityEngine.Transform CreateMorph(PMXFormat format, UnityEngine.Mesh[] mesh, UnityEngine.Material[][] materials, UnityEngine.GameObject[] bones, UnityEngine.SkinnedMeshRenderer[] renderers, MeshCreationInfo[] creation_list, float scale)
        {
            //表情ルートを生成してルートの子供に付ける
            var expression_root = new UnityEngine.GameObject("Expression");
            var expression_root_transform = expression_root.transform;

            //表情マネージャー
            MorphManager morph_manager = expression_root.AddComponent<MorphManager>();
            morph_manager.uv_morph = new MorphManager.UvMorphPack[1 + format.header.additionalUV]; //UVモーフ数設定

            //個別モーフスクリプト作成
            var morphs = new UnityEngine.GameObject[format.morphs.Length];
            for (int i = 0, i_max = format.morphs.Length; i < i_max; ++i)
            {
                morphs[i] = new UnityEngine.GameObject(format.morphs[i].morph_name);
                // 表情を親ボーンに付ける
                morphs[i].transform.parent = expression_root_transform;
            }

            //グループモーフ作成
            CreateGroupMorph(format, morph_manager, morphs);
            //ボーンモーフ
            morph_manager.bones = bones.Select(x => x.transform).ToArray();
            CreateBoneMorph(format, morph_manager, morphs, scale);
            //頂点モーフ作成
            CreateVertexMorph(format, morph_manager, morphs, creation_list, scale);
            //UV・追加UVモーフ作成
            CreateUvMorph(format, morph_manager, morphs, creation_list);
            //材質モーフ作成
            CreateMaterialMorph(format, morph_manager, morphs, creation_list);
            //モーフ一覧設定(モーフコンポーネントの情報を拾う為、最後に設定する)
            morph_manager.morphs = morphs.Select(x => x.GetComponent<MorphBase>()).ToArray();

            //メッシュ・マテリアル設定
            morph_manager.renderers = renderers;
            morph_manager.mesh = mesh;
            morph_manager.materials = materials;

            return expression_root_transform;
        }

        /// <summary>
        /// グループモーフ作成
        /// </summary>
        /// <param name='morph_manager'>表情マネージャー</param>
        /// <param name='morphs'>モーフのゲームオブジェクト</param>
        static void CreateGroupMorph(PMXFormat format, MorphManager morph_manager, UnityEngine.GameObject[] morphs)
        {
            //インデックスと元データの作成
            List<int> original_indices = format.morphs.Where(x => (PMXFormat.MorphData.MorphType.Group == x.morph_type)) //該当モーフに絞る
                                                                        .SelectMany(x => x.morph_offset.Select(y => ((PMXFormat.GroupMorphOffset)y).morph_index)) //インデックスの取り出しと連結
                                                                        .Distinct() //重複したインデックスの削除
                                                                        .ToList(); //ソートに向けて一旦リスト化
            original_indices.Sort(); //ソート
            int[] indices = original_indices.Select(x => (int)x).ToArray();
            float[] source = Enumerable.Repeat(0.0f, indices.Length) //インデックスを用いて、元データをパック
                                        .ToArray();

            //インデックス逆引き用辞書の作成
            Dictionary<int, int> index_reverse_dictionary = new Dictionary<int, int>();
            for (int i = 0, i_max = indices.Length; i < i_max; ++i)
            {
                index_reverse_dictionary.Add(indices[i], i);
            }

            //個別モーフスクリプトの作成
            GroupMorph[] script = Enumerable.Range(0, format.morphs.Length)
                                            .Where(x => PMXFormat.MorphData.MorphType.Group == format.morphs[x].morph_type) //該当モーフに絞る
                                            .Select(x => AssignGroupMorph(morphs[x], format.morphs[x], index_reverse_dictionary))
                                            .ToArray();

            //表情マネージャーにインデックス・元データ・スクリプトの設定
            morph_manager.group_morph = new MorphManager.GroupMorphPack(indices, source, script);
        }

        /// <summary>
        /// グループモーフ設定
        /// </summary>
        /// <returns>グループモーフスクリプト</returns>
        /// <param name='morph'>モーフのゲームオブジェクト</param>
        /// <param name='data'>PMX用モーフデータ</param>
        /// <param name='index_reverse_dictionary'>インデックス逆引き用辞書</param>
        static GroupMorph AssignGroupMorph(UnityEngine.GameObject morph, PMXFormat.MorphData data, Dictionary<int, int> index_reverse_dictionary)
        {
            GroupMorph result = morph.AddComponent<GroupMorph>();
            result.panel = (MorphManager.PanelType)data.handle_panel;
            result.indices = data.morph_offset.Select(x => ((PMXFormat.GroupMorphOffset)x).morph_index) //インデックスを取り出し
                                                .Select(x => index_reverse_dictionary[x]) //逆変換を掛ける
                                                .ToArray();
            result.values = data.morph_offset.Select(x => ((PMXFormat.GroupMorphOffset)x).morph_rate).ToArray();
            return result;
        }

        /// <summary>
        /// ボーンモーフ作成
        /// </summary>
        /// <param name='morph_manager'>表情マネージャー</param>
        /// <param name='morphs'>モーフのゲームオブジェクト</param>
        static void CreateBoneMorph(PMXFormat format, MorphManager morph_manager, UnityEngine.GameObject[] morphs, float scale)
        {
            //インデックスと元データの作成
            List<int> original_indices = format.morphs.Where(x => (PMXFormat.MorphData.MorphType.Bone == x.morph_type)) //該当モーフに絞る
                                                                        .SelectMany(x => x.morph_offset.Select(y => ((PMXFormat.BoneMorphOffset)y).bone_index)) //インデックスの取り出しと連結
                                                                        .Distinct() //重複したインデックスの削除
                                                                        .ToList(); //ソートに向けて一旦リスト化
            original_indices.Sort(); //ソート
            int[] indices = original_indices.Select(x => (int)x).ToArray();
            BoneMorph.BoneMorphParameter[] source = indices.Where(x => x < format.bones.Length)
                                                            .Select(x =>
                                                            {  //インデックスを用いて、元データをパック
                                                                PMXFormat.Bone y = format.bones[x];
                                                                BoneMorph.BoneMorphParameter result = new BoneMorph.BoneMorphParameter();
                                                                result.position = y.bone_position;
                                                                if (y.parent_bone_index < format.bones.Length)
                                                                {
                                                                    //親が居たらローカル座標化
                                                                    result.position -= format.bones[y.parent_bone_index].bone_position;
                                                                }
                                                                result.position *= scale;
                                                                result.rotation = UnityEngine.Quaternion.identity;
                                                                return result;
                                                            })
                                                            .ToArray();

            //インデックス逆引き用辞書の作成
            Dictionary<int, int> index_reverse_dictionary = new Dictionary<int, int>();
            for (int i = 0, i_max = indices.Length; i < i_max; ++i)
            {
                index_reverse_dictionary.Add(indices[i], i);
            }

            //個別モーフスクリプトの作成
            BoneMorph[] script = Enumerable.Range(0, format.morphs.Length)
                                            .Where(x => PMXFormat.MorphData.MorphType.Bone == format.morphs[x].morph_type) //該当モーフに絞る
                                            .Select(x => AssignBoneMorph(morphs[x], format.morphs[x], index_reverse_dictionary, scale))
                                            .ToArray();

            //表情マネージャーにインデックス・元データ・スクリプトの設定
            morph_manager.bone_morph = new MorphManager.BoneMorphPack(indices, source, script);
        }

        /// <summary>
        /// ボーンモーフ設定
        /// </summary>
        /// <returns>ボーンモーフスクリプト</returns>
        /// <param name='morph'>モーフのゲームオブジェクト</param>
        /// <param name='data'>PMX用モーフデータ</param>
        /// <param name='index_reverse_dictionary'>インデックス逆引き用辞書</param>
        static BoneMorph AssignBoneMorph(UnityEngine.GameObject morph, PMXFormat.MorphData data, Dictionary<int, int> index_reverse_dictionary, float scale)
        {
            BoneMorph result = morph.AddComponent<BoneMorph>();
            result.panel = (MorphManager.PanelType)data.handle_panel;
            result.indices = data.morph_offset.Select(x => ((PMXFormat.BoneMorphOffset)x).bone_index) //インデックスを取り出し
                                                .Select(x => index_reverse_dictionary[x]) //逆変換を掛ける
                                                .ToArray();
            result.values = data.morph_offset.Select(x =>
            {
                PMXFormat.BoneMorphOffset y = (PMXFormat.BoneMorphOffset)x;
                BoneMorph.BoneMorphParameter param = new BoneMorph.BoneMorphParameter();
                param.position = y.move_value * scale;
                param.rotation = y.rotate_value;
                return param;
            })
                                            .ToArray();
            return result;
        }

        /// <summary>
        /// 頂点モーフ作成
        /// </summary>
        /// <param name='morph_manager'>表情マネージャー</param>
        /// <param name='morphs'>モーフのゲームオブジェクト</param>
        /// <param name='creation_list'>メッシュ作成情報</param>
        static void CreateVertexMorph(PMXFormat format, MorphManager morph_manager, UnityEngine.GameObject[] morphs, MeshCreationInfo[] creation_list, float scale)
        {
            //インデックスと元データの作成
            List<int> original_indices = format.morphs.Where(x => (PMXFormat.MorphData.MorphType.Vertex == x.morph_type)) //該当モーフに絞る
                                                                        .SelectMany(x => x.morph_offset.Select(y => ((PMXFormat.VertexMorphOffset)y).vertex_index)) //インデックスの取り出しと連結
                                                                        .Distinct() //重複したインデックスの削除
                                                                        .ToList(); //ソートに向けて一旦リスト化
            original_indices.Sort(); //ソート
            int[] indices = original_indices.Select(x => (int)x).ToArray();
            var source = indices.Select(x => format.vertices[x].pos * scale) //インデックスを用いて、元データをパック
                                    .ToArray();

            //インデックス逆引き用辞書の作成
            Dictionary<int, int> index_reverse_dictionary = new Dictionary<int, int>();
            for (int i = 0, i_max = indices.Length; i < i_max; ++i)
            {
                index_reverse_dictionary.Add(indices[i], i);
            }

            //個別モーフスクリプトの作成
            VertexMorph[] script = Enumerable.Range(0, format.morphs.Length)
                                            .Where(x => PMXFormat.MorphData.MorphType.Vertex == format.morphs[x].morph_type) //該当モーフに絞る
                                            .Select(x => AssignVertexMorph(morphs[x], format.morphs[x], index_reverse_dictionary, scale))
                                            .ToArray();

            //メッシュ別インデックスの作成
            int invalid_vertex_index = format.vertices.Length;
            MorphManager.VertexMorphPack.Meshes[] multi_indices = new MorphManager.VertexMorphPack.Meshes[creation_list.Length];
            for (int i = 0, i_max = creation_list.Length; i < i_max; ++i)
            {
                multi_indices[i] = new MorphManager.VertexMorphPack.Meshes();
                multi_indices[i].indices = new int[indices.Length];
                for (int k = 0, k_max = indices.Length; k < k_max; ++k)
                {
                    if (creation_list[i].reassign_dictionary.ContainsKey(indices[k]))
                    {
                        //このメッシュで有効なら
                        multi_indices[i].indices[k] = creation_list[i].reassign_dictionary[indices[k]];
                    }
                    else
                    {
                        //このメッシュでは無効なら
                        multi_indices[i].indices[k] = invalid_vertex_index; //最大頂点数を設定(uint.MaxValueでは無いので注意)
                    }
                }
            }

            //表情マネージャーにインデックス・元データ・スクリプトの設定
            morph_manager.vertex_morph = new MorphManager.VertexMorphPack(multi_indices, source, script);
        }

        /// <summary>
        /// 頂点モーフ設定
        /// </summary>
        /// <returns>頂点モーフスクリプト</returns>
        /// <param name='morph'>モーフのゲームオブジェクト</param>
        /// <param name='data'>PMX用モーフデータ</param>
        /// <param name='index_reverse_dictionary'>インデックス逆引き用辞書</param>
        static VertexMorph AssignVertexMorph(UnityEngine.GameObject morph, PMXFormat.MorphData data, Dictionary<int, int> index_reverse_dictionary, float scale)
        {
            VertexMorph result = morph.AddComponent<VertexMorph>();
            result.panel = (MorphManager.PanelType)data.handle_panel;
            result.indices = data.morph_offset.Select(x => ((PMXFormat.VertexMorphOffset)x).vertex_index) //インデックスを取り出し
                                                .Select(x => index_reverse_dictionary[x]) //逆変換を掛ける
                                                .ToArray();
            result.values = data.morph_offset.Select(x => ((PMXFormat.VertexMorphOffset)x).position_offset * scale).ToArray();
            return result;
        }

        /// <summary>
        /// UV・追加UVモーフ作成
        /// </summary>
        /// <param name='morph_manager'>表情マネージャー</param>
        /// <param name='morphs'>モーフのゲームオブジェクト</param>
        /// <param name='creation_list'>メッシュ作成情報</param>
        static void CreateUvMorph(PMXFormat format, MorphManager morph_manager, UnityEngine.GameObject[] morphs, MeshCreationInfo[] creation_list)
        {
            for (int morph_type_index = 0, morph_type_index_max = 1 + format.header.additionalUV; morph_type_index < morph_type_index_max; ++morph_type_index)
            {
                //モーフタイプ
                PMXFormat.MorphData.MorphType morph_type;
                switch (morph_type_index)
                {
                    case 0: morph_type = PMXFormat.MorphData.MorphType.Uv; break;
                    case 1: morph_type = PMXFormat.MorphData.MorphType.Adduv1; break;
                    case 2: morph_type = PMXFormat.MorphData.MorphType.Adduv2; break;
                    case 3: morph_type = PMXFormat.MorphData.MorphType.Adduv3; break;
                    case 4: morph_type = PMXFormat.MorphData.MorphType.Adduv4; break;
                    default: throw new System.ArgumentOutOfRangeException();
                }

                //インデックスと元データの作成
                List<int> original_indices = format.morphs.Where(x => (morph_type == x.morph_type)) //該当モーフに絞る
                                                                            .SelectMany(x => x.morph_offset.Select(y => ((PMXFormat.UVMorphOffset)y).vertex_index)) //インデックスの取り出しと連結
                                                                            .Distinct() //重複したインデックスの削除
                                                                            .ToList(); //ソートに向けて一旦リスト化
                original_indices.Sort(); //ソート
                int[] indices = original_indices.Select(x => (int)x).ToArray();
                UnityEngine.Vector2[] source;
                if (0 == morph_type_index)
                {
                    //通常UV
                    source = indices.Select(x => format.vertices[x].uv) //インデックスを用いて、元データをパック
                                    .Select(x => new UnityEngine.Vector2(x.x, x.y))
                                    .ToArray();
                }
                else
                {
                    //追加UV
                    source = indices.Select(x => format.vertices[x].add_uv[morph_type_index - 1]) //インデックスを用いて、元データをパック
                                    .Select(x => new UnityEngine.Vector2(x.x, x.y))
                                    .ToArray();
                }

                //インデックス逆引き用辞書の作成
                Dictionary<int, int> index_reverse_dictionary = new Dictionary<int, int>();
                for (int i = 0, i_max = indices.Length; i < i_max; ++i)
                {
                    index_reverse_dictionary.Add(indices[i], i);
                }

                //個別モーフスクリプトの作成
                UvMorph[] script = Enumerable.Range(0, format.morphs.Length)
                                            .Where(x => morph_type == format.morphs[x].morph_type) //該当モーフに絞る
                                            .Select(x => AssignUvMorph(morphs[x], format.morphs[x], index_reverse_dictionary))
                                            .ToArray();

                //メッシュ別インデックスの作成
                int invalid_vertex_index = format.vertices.Length;
                MorphManager.UvMorphPack.Meshes[] multi_indices = new MorphManager.UvMorphPack.Meshes[creation_list.Length];
                for (int i = 0, i_max = creation_list.Length; i < i_max; ++i)
                {
                    multi_indices[i] = new MorphManager.UvMorphPack.Meshes();
                    multi_indices[i].indices = new int[indices.Length];
                    for (int k = 0, k_max = indices.Length; k < k_max; ++k)
                    {
                        if (creation_list[i].reassign_dictionary.ContainsKey(indices[k]))
                        {
                            //このメッシュで有効なら
                            multi_indices[i].indices[k] = creation_list[i].reassign_dictionary[indices[k]];
                        }
                        else
                        {
                            //このメッシュでは無効なら
                            multi_indices[i].indices[k] = invalid_vertex_index; //最大頂点数を設定(uint.MaxValueでは無いので注意)
                        }
                    }
                }

                //表情マネージャーにインデックス・元データ・スクリプトの設定
                morph_manager.uv_morph[morph_type_index] = new MorphManager.UvMorphPack(multi_indices, source, script);
            }
        }

        /// <summary>
        /// UV・追加UVモーフ設定
        /// </summary>
        /// <returns>UVモーフスクリプト</returns>
        /// <param name='morph'>モーフのゲームオブジェクト</param>
        /// <param name='data'>PMX用モーフデータ</param>
        /// <param name='index_reverse_dictionary'>インデックス逆引き用辞書</param>
        static UvMorph AssignUvMorph(UnityEngine.GameObject morph, PMXFormat.MorphData data, Dictionary<int, int> index_reverse_dictionary)
        {
            UvMorph result = morph.AddComponent<UvMorph>();
            result.panel = (MorphManager.PanelType)data.handle_panel;
            result.indices = data.morph_offset.Select(x => ((PMXFormat.UVMorphOffset)x).vertex_index) //インデックスを取り出し
                                                .Select(x => index_reverse_dictionary[x]) //逆変換を掛ける
                                                .ToArray();
            result.values = data.morph_offset.Select(x => ((PMXFormat.UVMorphOffset)x).uv_offset)
                                                .Select(x => new UnityEngine.Vector2(x.x, x.y))
                                                .ToArray();
            return result;
        }

        /// <summary>
        /// 材質モーフ作成
        /// </summary>
        /// <param name='morph_manager'>表情マネージャー</param>
        /// <param name='morphs'>モーフのゲームオブジェクト</param>
        /// <param name='creation_list'>メッシュ作成情報</param>
        static void CreateMaterialMorph(PMXFormat format, MorphManager morph_manager, UnityEngine.GameObject[] morphs, MeshCreationInfo[] creation_list)
        {
            //インデックスと元データの作成
            List<int> original_indices = format.morphs.Where(x => (PMXFormat.MorphData.MorphType.Material == x.morph_type)) //該当モーフに絞る
                                                                        .SelectMany(x => x.morph_offset.Select(y => ((PMXFormat.MaterialMorphOffset)y).material_index)) //インデックスの取り出しと連結
                                                                        .Distinct() //重複したインデックスの削除
                                                                        .ToList(); //ソートに向けて一旦リスト化
            original_indices.Sort(); //ソート
            if (-1 == original_indices.LastOrDefault())
            {
                //最後が uint.MaxValue(≒-1) なら
                //全材質対象が存在するので全インデックスを取得
                original_indices = Enumerable.Range(0, format.materials.Length + 1).ToList();
                original_indices[format.materials.Length] = -1; //uint.MaxValueを忘れない
            }
            int[] indices = original_indices.Select(x => (int)x).ToArray();
            MaterialMorph.MaterialMorphParameter[] source = indices.Where(x => x < format.materials.Length)
                                                                    .Select(x =>
                                                                    {  //インデックスを用いて、元データをパック
                                                                        MaterialMorph.MaterialMorphParameter result = new MaterialMorph.MaterialMorphParameter();
                                                                        if (0 <= x)
                                                                        {
                                                                            //-1(全材質対象)で無いなら
                                                                            //元データを取得
                                                                            PMXFormat.Material y = format.materials[x];
                                                                            result.color = y.diffuse_color;
                                                                            result.specular = new UnityEngine.Color(y.specular_color.r, y.specular_color.g, y.specular_color.b, y.specularity);
                                                                            result.ambient = y.ambient_color;
                                                                            result.outline_color = y.edge_color;
                                                                            result.outline_width = y.edge_size;
                                                                            result.texture_color = UnityEngine.Color.white;
                                                                            result.sphere_color = UnityEngine.Color.white;
                                                                            result.toon_color = UnityEngine.Color.white;
                                                                        }
                                                                        else
                                                                        {
                                                                            //-1(全材質対象)なら
                                                                            //適当にでっち上げる
                                                                            result = MaterialMorph.MaterialMorphParameter.zero;
                                                                        }
                                                                        return result;
                                                                    })
                                                                    .ToArray();

            //インデックス逆引き用辞書の作成
            Dictionary<int, int> index_reverse_dictionary = new Dictionary<int, int>();
            for (int i = 0, i_max = indices.Length; i < i_max; ++i)
            {
                index_reverse_dictionary.Add(indices[i], i);
            }

            //個別モーフスクリプトの作成
            MaterialMorph[] script = Enumerable.Range(0, format.morphs.Length)
                                                .Where(x => PMXFormat.MorphData.MorphType.Material == format.morphs[x].morph_type) //該当モーフに絞る
                                                .Select(x => AssignMaterialMorph(morphs[x], format.morphs[x], index_reverse_dictionary))
                                                .ToArray();

            //材質リアサイン辞書の作成
            Dictionary<int, int>[] material_reassign_dictionary = new Dictionary<int, int>[creation_list.Length + 1];
            for (int i = 0, i_max = creation_list.Length; i < i_max; ++i)
            {
                material_reassign_dictionary[i] = new Dictionary<int, int>();
                for (int k = 0, k_max = creation_list[i].submeshes.Length; k < k_max; ++k)
                {
                    material_reassign_dictionary[i][creation_list[i].submeshes[k].material_index] = k;
                }
                if (-1 == indices.LastOrDefault())
                {
                    //indices の最後が -1(≒uint.MaxValue) なら
                    //全材質対象が存在するので材質リアサイン辞書に追加
                    material_reassign_dictionary[i][-1] = -1;
                }
            }

            //メッシュ別インデックスの作成
            int invalid_material_index = format.materials.Length;
            MorphManager.MaterialMorphPack.Meshes[] multi_indices = new MorphManager.MaterialMorphPack.Meshes[creation_list.Length];
            for (int i = 0, i_max = creation_list.Length; i < i_max; ++i)
            {
                multi_indices[i] = new MorphManager.MaterialMorphPack.Meshes();
                multi_indices[i].indices = new int[indices.Length];
                for (int k = 0, k_max = indices.Length; k < k_max; ++k)
                {
                    if (material_reassign_dictionary[i].ContainsKey(indices[k]))
                    {
                        //この材質で有効なら
                        multi_indices[i].indices[k] = material_reassign_dictionary[i][indices[k]];
                    }
                    else
                    {
                        //この材質では無効なら
                        multi_indices[i].indices[k] = invalid_material_index; //最大材質数を設定(uint.MaxValueでは無いので注意)
                    }
                }
            }

            //表情マネージャーにインデックス・元データ・スクリプトの設定
            morph_manager.material_morph = new MorphManager.MaterialMorphPack(multi_indices, source, script);
        }

        /// <summary>
        /// 材質モーフ設定
        /// </summary>
        /// <returns>材質モーフスクリプト</returns>
        /// <param name='morph'>モーフのゲームオブジェクト</param>
        /// <param name='data'>PMX用モーフデータ</param>
        /// <param name='index_reverse_dictionary'>インデックス逆引き用辞書</param>
        static MaterialMorph AssignMaterialMorph(UnityEngine.GameObject morph, PMXFormat.MorphData data, Dictionary<int, int> index_reverse_dictionary)
        {
            MaterialMorph result = morph.AddComponent<MaterialMorph>();
            result.panel = (MorphManager.PanelType)data.handle_panel;
            result.indices = data.morph_offset.Select(x => ((PMXFormat.MaterialMorphOffset)x).material_index) //インデックスを取り出し
                                                .Select(x => (int)index_reverse_dictionary[x]) //逆変換を掛ける
                                                .ToArray();
            result.values = data.morph_offset.Select(x =>
            {
                PMXFormat.MaterialMorphOffset y = (PMXFormat.MaterialMorphOffset)x;
                MaterialMorph.MaterialMorphParameter param = new MaterialMorph.MaterialMorphParameter();
                param.color = y.diffuse;
                param.specular = new UnityEngine.Color(y.specular.r, y.specular.g, y.specular.b, y.specularity);
                param.ambient = y.ambient;
                param.outline_color = y.edge_color;
                param.outline_width = y.edge_size;
                param.texture_color = y.texture_coefficient;
                param.sphere_color = y.sphere_texture_coefficient;
                param.toon_color = y.toon_texture_coefficient;
                return param;
            })
                                            .ToArray();
            result.operation = data.morph_offset.Select(x => (MaterialMorph.OperationType)((PMXFormat.MaterialMorphOffset)x).offset_method)
                                                .ToArray();
            return result;
        }

        /// <summary>
        /// バインドポーズの作成
        /// </summary>
        /// <returns>レンダラー</returns>
        /// <param name='mesh'>対象メッシュ</param>
        /// <param name='materials'>設定するマテリアル</param>
        /// <param name='bones'>設定するボーン</param>
        static IEnumerable<UnityEngine.SkinnedMeshRenderer> BuildingBindpose(UnityEngine.GameObject root
            , IEnumerable<UnityEngine.Mesh> meshes, UnityEngine.Material[][] materials, UnityEngine.GameObject[] bones
            )
        {
            // メッシュルートを生成してルートの子供に付ける
            var mesh_root_transform = (new UnityEngine.GameObject("Mesh")).transform;
            mesh_root_transform.parent = root.transform;

            //モデルルート取得
            var model_root_transform = root.transform.FindChild("Model");
            //ボーン共通データ
            var bindposes = bones.Select(x => x.transform.worldToLocalMatrix).ToArray();
            var bones_transform = bones.Select(x => x.transform).ToArray();

            //レンダー設定
            return meshes.Select((m, i) =>
            {
                var mesh_transform = (new UnityEngine.GameObject("Mesh" + i.ToString())).transform;
                mesh_transform.parent = mesh_root_transform;
                var smr = mesh_transform.gameObject.AddComponent<UnityEngine.SkinnedMeshRenderer>();
                m.bindposes = bindposes;
                smr.sharedMesh = m;
                smr.bones = bones_transform;
                smr.materials = materials[i];
                smr.rootBone = model_root_transform;
                smr.receiveShadows = false; //影を受けない
                return smr;
            });
        }

        /// <summary>
        /// IK作成
        /// </summary>
        /// <returns>ボーンコントローラースクリプト</returns>
        /// <param name='bones'>ボーンのゲームオブジェクト</param>
        static BoneController[] EntryBoneController(PMXFormat format, UnityEngine.GameObject[] bones, bool use_ik)
        {
            //BoneControllerが他のBoneControllerを参照するので先に全ボーンに付与
            foreach (var bone in bones)
            {
                bone.AddComponent<BoneController>();
            }
            BoneController[] result = Enumerable.Range(0, format.bones.Length)
                                                .OrderBy(x => (int)(PMXFormat.Bone.Flag.PhysicsTransform & format.bones[x].bone_flag)) //物理後変形を後方へ
                                                .ThenBy(x => format.bones[x].transform_level) //変形階層で安定ソート
                                                .Select(x => ConvertBoneController(format, format.bones[x], x, bones, use_ik)) //ConvertIk()を呼び出す
                                                .ToArray();
            return result;
        }

        /// <summary>
        /// ボーンをボーンコントローラースクリプトに変換する
        /// </summary>
        /// <returns>ボーンコントローラースクリプト</returns>
        /// <param name='ik_data'>PMX用ボーンデータ</param>
        /// <param name='bone_index'>該当IKデータのボーン通しインデックス</param>
        /// <param name='bones'>ボーンのゲームオブジェクト</param>
        static BoneController ConvertBoneController(PMXFormat format, PMXFormat.Bone bone, int bone_index, UnityEngine.GameObject[] bones, bool use_ik)
        {
            BoneController result = bones[bone_index].GetComponent<BoneController>();
            if (0.0f != bone.additional_rate)
            {
                //付与親が有るなら
                result.additive_parent = bones[bone.additional_parent_index].GetComponent<BoneController>();
                result.additive_rate = bone.additional_rate;
                result.add_local = (0 != (PMXFormat.Bone.Flag.AddLocal & bone.bone_flag));
                result.add_move = (0 != (PMXFormat.Bone.Flag.AddMove & bone.bone_flag));
                result.add_rotate = (0 != (PMXFormat.Bone.Flag.AddRotation & bone.bone_flag));
            }
            if (use_ik)
            {
                //IKを使用するなら
                if (0 != (PMXFormat.Bone.Flag.IkFlag & bone.bone_flag))
                {
                    //IKが有るなら
                    result.ik_solver = bones[bone_index].AddComponent<CCDIKSolver>();
                    result.ik_solver.target = bones[bone.ik_data.ik_bone_index].transform;
                    result.ik_solver.controll_weight = bone.ik_data.limit_angle / 4; //HACK: CCDIKSolver側で4倍している様なので此処で逆補正
                    result.ik_solver.iterations = (int)bone.ik_data.iterations;
                    result.ik_solver.chains = bone.ik_data.ik_link.Select(x => x.target_bone_index).Select(x => bones[x].transform).ToArray();
                    //IK制御下のBoneController登録
                    result.ik_solver_targets = Enumerable.Repeat(result.ik_solver.target, 1)
                                                        .Concat(result.ik_solver.chains)
                                                        .Select(x => x.GetComponent<BoneController>())
                                                        .ToArray();

                    //IK制御先のボーンについて、物理演算の挙動を調べる
                    var operation_types = Enumerable.Repeat(bone.ik_data.ik_bone_index, 1) //IK対象先をEnumerable化
                                                    .Concat(bone.ik_data.ik_link.Select(x => x.target_bone_index)) //IK制御下を追加
                                                    .Join(format.rigidbodies, x => x, y => y.rel_bone_index, (x, y) => y.operation_type); //剛体リストから関連ボーンにIK対象先・IK制御下と同じボーンを持つ物を列挙し、剛体タイプを返す
                    foreach (var operation_type in operation_types)
                    {
                        if (PMXFormat.Rigidbody.OperationType.Static != operation_type)
                        {
                            //ボーン追従で無い(≒物理演算)なら
                            //IK制御の無効化
                            result.ik_solver.enabled = false;
                            break;
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 剛体とボーンを接続する
        /// </summary>
        /// <param name='bones'>ボーンのゲームオブジェクト</param>
        /// <param name='rigids'>剛体のゲームオブジェクト</param>
        static UnityEngine.Transform AssignRigidbodyToBone(PMXFormat format, UnityEngine.GameObject[] bones, UnityEngine.GameObject[] rigids)
        {
            // 物理演算ルートを生成してルートの子供に付ける
            var physics_root_transform = (new UnityEngine.GameObject("Physics", typeof(PhysicsManager))).transform;

            // 剛体の数だけ回す
            for (int i = 0, i_max = rigids.Length; i < i_max; ++i)
            {
                // 剛体を親ボーンに格納
                int rel_bone_index = GetRelBoneIndexFromNearbyRigidbody(format, i);
                if (rel_bone_index < bones.Length)
                {
                    //親と為るボーンが有れば
                    //それの子と為る
                    rigids[i].transform.parent = bones[rel_bone_index].transform;
                }
                else
                {
                    //親と為るボーンが無ければ
                    //物理演算ルートの子と為る
                    rigids[i].transform.parent = physics_root_transform;
                }
            }

            return physics_root_transform;
        }

        /// <summary>
        /// 剛体とボーンを接続する
        /// </summary>
        /// <param name='bones'>ボーンのゲームオブジェクト</param>
        /// <param name='rigids'>剛体のゲームオブジェクト</param>
        static int GetRelBoneIndexFromNearbyRigidbody(PMXFormat format, int rigidbody_index)
        {
            int bone_count = format.bones.Length;
            //関連ボーンを探す
            int result = format.rigidbodies[rigidbody_index].rel_bone_index;
            if (result < bone_count)
            {
                //関連ボーンが有れば
                return result;
            }
            //関連ボーンが無ければ
            //ジョイントに接続されている剛体の関連ボーンを探しに行く
            //HACK: 深さ優先探索に為っているけれど、関連ボーンとの類似性を考えれば幅優先探索の方が良いと思われる

            //ジョイントのAを探しに行く(自身はBに接続されている)
            var joint_a_list = format.joints.Where(x => x.rigidbody_b == rigidbody_index) //自身がBに接続されているジョイントに絞る
                                                                .Where(x => x.rigidbody_a < bone_count) //Aが有効な剛体に縛る
                                                                .Select(x => x.rigidbody_a); //Aを返す
            foreach (var joint_a in joint_a_list)
            {
                result = GetRelBoneIndexFromNearbyRigidbody(format, joint_a);
                if (result < bone_count)
                {
                    //関連ボーンが有れば
                    return result;
                }
            }
            //ジョイントのAに無ければ
            //ジョイントのBを探しに行く(自身はAに接続されている)
            var joint_b_list = format.joints.Where(x => x.rigidbody_a == rigidbody_index) //自身がAに接続されているジョイントに絞る
                                                                .Where(x => x.rigidbody_b < bone_count) //Bが有効な剛体に縛る
                                                                .Select(x => x.rigidbody_b); //Bを返す
            foreach (var joint_b in joint_b_list)
            {
                result = GetRelBoneIndexFromNearbyRigidbody(format, joint_b);
                if (result < bone_count)
                {
                    //関連ボーンが有れば
                    return result;
                }
            }
            //それでも無ければ
            //諦める
            result = -1;
            return result;
        }

        /// <summary>
        /// 剛体の値を設定する
        /// </summary>
        /// <param name='bones'>ボーンのゲームオブジェクト</param>
        /// <param name='rigids'>剛体のゲームオブジェクト</param>
        static void SetRigidsSettings(PMXFormat format, UnityEngine.GameObject[] bones, UnityEngine.GameObject[] rigid)
        {
            var bone_count = format.bones.Length;
            for (int i = 0, i_max = format.rigidbodies.Length; i < i_max; ++i)
            {
                PMXFormat.Rigidbody rigidbody = format.rigidbodies[i];
                UnityEngine.GameObject target;
                if (rigidbody.rel_bone_index < bone_count)
                {
                    //関連ボーンが有るなら
                    //関連ボーンに付与する
                    target = bones[rigidbody.rel_bone_index];
                }
                else
                {
                    //関連ボーンが無いなら
                    //剛体に付与する
                    target = rigid[i];
                }
                UnityRigidbodySetting(rigidbody, target);
            }
        }

        /// <summary>
        /// Unity側のRigidbodyの設定を行う
        /// </summary>
        /// <param name='rigidbody'>PMX用剛体データ</param>
        /// <param name='targetBone'>設定対象のゲームオブジェクト</param>
        static void UnityRigidbodySetting(PMXFormat.Rigidbody pmx_rigidbody, UnityEngine.GameObject target)
        {
            var rigidbody = target.GetComponent<UnityEngine.Rigidbody>();
            if (null != rigidbody)
            {
                //既にRigidbodyが付与されているなら
                //質量は合算する
                rigidbody.mass += pmx_rigidbody.weight;
                //減衰値は平均を取る
                rigidbody.drag = (rigidbody.drag + pmx_rigidbody.position_dim) * 0.5f;
                rigidbody.angularDrag = (rigidbody.angularDrag + pmx_rigidbody.rotation_dim) * 0.5f;
            }
            else
            {
                //まだRigidbodyが付与されていないなら
                rigidbody = target.AddComponent<UnityEngine.Rigidbody>();
                rigidbody.isKinematic = (PMXFormat.Rigidbody.OperationType.Static == pmx_rigidbody.operation_type);
                rigidbody.mass = UnityEngine.Mathf.Max(float.Epsilon, pmx_rigidbody.weight);
                rigidbody.drag = pmx_rigidbody.position_dim;
                rigidbody.angularDrag = pmx_rigidbody.rotation_dim;
            }
        }

        /// <summary>
        /// ConfigurableJointの設定
        /// </summary>
        /// <remarks>
        /// 先に設定してからFixedJointを設定する
        /// </remarks>
        /// <returns>ジョイントのゲームオブジェクト</returns>
        /// <param name='rigids'>剛体のゲームオブジェクト</param>
        static UnityEngine.GameObject[] SetupConfigurableJoint(PMXFormat format, UnityEngine.GameObject[] rigids, float scale)
        {
            var result_list = new List<UnityEngine.GameObject>();
            foreach (PMXFormat.Joint joint in format.joints)
            {
                //相互接続する剛体の取得
                var transform_a = rigids[joint.rigidbody_a].transform;
                var rigidbody_a = transform_a.GetComponent<UnityEngine.Rigidbody>();
                if (null == rigidbody_a)
                {
                    rigidbody_a = transform_a.parent.GetComponent<UnityEngine.Rigidbody>();
                }
                var transform_b = rigids[joint.rigidbody_b].transform;
                var rigidbody_b = transform_b.GetComponent<UnityEngine.Rigidbody>();
                if (null == rigidbody_b)
                {
                    rigidbody_b = transform_b.parent.GetComponent<UnityEngine.Rigidbody>();
                }
                if (rigidbody_a != rigidbody_b)
                {
                    //接続する剛体が同じ剛体を指さないなら
                    //(本来ならPMDの設定が間違っていない限り同一を指す事は無い)
                    //ジョイント設定
                    var config_joint = rigidbody_b.gameObject.AddComponent<UnityEngine.ConfigurableJoint>();
                    config_joint.connectedBody = rigidbody_a;
                    SetAttributeConfigurableJoint(joint, config_joint, scale);

                    result_list.Add(config_joint.gameObject);
                }
            }
            return result_list.ToArray();
        }

        /// <summary>
        /// ConfigurableJointの値を設定する
        /// </summary>
        /// <param name='joint'>PMX用ジョイントデータ</param>
        /// <param name='conf'>Unity用ジョイント</param>
        static void SetAttributeConfigurableJoint(PMXFormat.Joint joint, UnityEngine.ConfigurableJoint conf, float scale)
        {
            SetMotionAngularLock(joint, conf);
            SetDrive(joint, conf, scale);
        }

        /// <summary>
        /// ジョイントに移動・回転制限のパラメータを設定する
        /// </summary>
        /// <param name='joint'>PMX用ジョイントデータ</param>
        /// <param name='conf'>Unity用ジョイント</param>
        static void SetMotionAngularLock(PMXFormat.Joint joint, UnityEngine.ConfigurableJoint conf)
        {
            UnityEngine.SoftJointLimit jlim;

            // Motionの固定
            if (joint.constrain_pos_lower.x == 0.0f && joint.constrain_pos_upper.x == 0.0f)
            {
                conf.xMotion = UnityEngine.ConfigurableJointMotion.Locked;
            }
            else
            {
                conf.xMotion = UnityEngine.ConfigurableJointMotion.Limited;
            }

            if (joint.constrain_pos_lower.y == 0.0f && joint.constrain_pos_upper.y == 0.0f)
            {
                conf.yMotion = UnityEngine.ConfigurableJointMotion.Locked;
            }
            else
            {
                conf.yMotion = UnityEngine.ConfigurableJointMotion.Limited;
            }

            if (joint.constrain_pos_lower.z == 0.0f && joint.constrain_pos_upper.z == 0.0f)
            {
                conf.zMotion = UnityEngine.ConfigurableJointMotion.Locked;
            }
            else
            {
                conf.zMotion = UnityEngine.ConfigurableJointMotion.Limited;
            }

            // 角度の固定
            if (joint.constrain_rot_lower.x == 0.0f && joint.constrain_rot_upper.x == 0.0f)
            {
                conf.angularXMotion = UnityEngine.ConfigurableJointMotion.Locked;
            }
            else
            {
                conf.angularXMotion = UnityEngine.ConfigurableJointMotion.Limited;
                float hlim = UnityEngine.Mathf.Max(-joint.constrain_rot_lower.x, -joint.constrain_rot_upper.x); //回転方向が逆なので負数
                float llim = UnityEngine.Mathf.Min(-joint.constrain_rot_lower.x, -joint.constrain_rot_upper.x);
                var jhlim = new UnityEngine.SoftJointLimit();
                jhlim.limit = UnityEngine.Mathf.Clamp(hlim * UnityEngine.Mathf.Rad2Deg, -180.0f, 180.0f);
                conf.highAngularXLimit = jhlim;

                var jllim = new UnityEngine.SoftJointLimit();
                jllim.limit = UnityEngine.Mathf.Clamp(llim * UnityEngine.Mathf.Rad2Deg, -180.0f, 180.0f);
                conf.lowAngularXLimit = jllim;
            }

            if (joint.constrain_rot_lower.y == 0.0f && joint.constrain_rot_upper.y == 0.0f)
            {
                conf.angularYMotion = UnityEngine.ConfigurableJointMotion.Locked;
            }
            else
            {
                // 値がマイナスだとエラーが出るので注意
                conf.angularYMotion = UnityEngine.ConfigurableJointMotion.Limited;
                float lim = UnityEngine.Mathf.Min(UnityEngine.Mathf.Abs(joint.constrain_rot_lower.y), UnityEngine.Mathf.Abs(joint.constrain_rot_upper.y));//絶対値の小さい方
                jlim = new UnityEngine.SoftJointLimit();
                jlim.limit = lim * UnityEngine.Mathf.Clamp(UnityEngine.Mathf.Rad2Deg, 0.0f, 180.0f);
                conf.angularYLimit = jlim;
            }

            if (joint.constrain_rot_lower.z == 0f && joint.constrain_rot_upper.z == 0f)
            {
                conf.angularZMotion = UnityEngine.ConfigurableJointMotion.Locked;
            }
            else
            {
                conf.angularZMotion = UnityEngine.ConfigurableJointMotion.Limited;
                float lim = UnityEngine.Mathf.Min(UnityEngine.Mathf.Abs(-joint.constrain_rot_lower.z), UnityEngine.Mathf.Abs(-joint.constrain_rot_upper.z));//絶対値の小さい方//回転方向が逆なので負数
                jlim = new UnityEngine.SoftJointLimit();
                jlim.limit = UnityEngine.Mathf.Clamp(lim * UnityEngine.Mathf.Rad2Deg, 0.0f, 180.0f);
                conf.angularZLimit = jlim;
            }
        }

        /// <summary>
        /// ジョイントにばねなどのパラメータを設定する
        /// </summary>
        /// <param name='joint'>PMX用ジョイントデータ</param>
        /// <param name='conf'>Unity用ジョイント</param>
        static void SetDrive(PMXFormat.Joint joint, UnityEngine.ConfigurableJoint conf, float scale)
        {
            UnityEngine.JointDrive drive;

            // Position
            if (joint.spring_position.x != 0.0f)
            {
                drive = new UnityEngine.JointDrive();
                drive.positionSpring = joint.spring_position.x * scale;
                conf.xDrive = drive;
            }
            if (joint.spring_position.y != 0.0f)
            {
                drive = new UnityEngine.JointDrive();
                drive.positionSpring = joint.spring_position.y * scale;
                conf.yDrive = drive;
            }
            if (joint.spring_position.z != 0.0f)
            {
                drive = new UnityEngine.JointDrive();
                drive.positionSpring = joint.spring_position.z * scale;
                conf.zDrive = drive;
            }

            // Angular
            if (joint.spring_rotation.x != 0.0f)
            {
                drive = new UnityEngine.JointDrive();
                drive.mode = UnityEngine.JointDriveMode.PositionAndVelocity;
                drive.positionSpring = joint.spring_rotation.x;
                conf.angularXDrive = drive;
            }
            if (joint.spring_rotation.y != 0.0f || joint.spring_rotation.z != 0.0f)
            {
                drive = new UnityEngine.JointDrive();
                drive.mode = UnityEngine.JointDriveMode.PositionAndVelocity;
                drive.positionSpring = (joint.spring_rotation.y + joint.spring_rotation.z) * 0.5f;
                conf.angularYZDrive = drive;
            }
        }

        /// <summary>
        /// 剛体のグローバル座標化
        /// </summary>
        /// <param name='joints'>ジョイントのゲームオブジェクト</param>
        static void GlobalizeRigidbody(UnityEngine.GameObject root, UnityEngine.GameObject[] joints)
        {
            var physics_root_transform = root.transform.Find("Physics");
            PhysicsManager physics_manager = physics_root_transform.gameObject.GetComponent<PhysicsManager>();

            if ((null != joints) && (0 < joints.Length))
            {
                // PhysicsManagerに移動前の状態を覚えさせる(幾つか重複しているので重複は削除)
                physics_manager.connect_bone_list = joints.Select(x => x.gameObject)
                                                            .Distinct()
                                                            .Select(x => new PhysicsManager.ConnectBone(x, x.transform.parent.gameObject))
                                                            .ToArray();

                //isKinematicで無くConfigurableJointを持つ場合はグローバル座標化
                foreach (var joint in joints.Where(x => !x.GetComponent<UnityEngine.Rigidbody>().isKinematic)
                                                            .Select(x => x.GetComponent<UnityEngine.ConfigurableJoint>()))
                {
                    joint.transform.parent = physics_root_transform;
                }
            }
        }

        /// <summary>
        /// 非衝突剛体の設定
        /// </summary>
        /// <returns>非衝突剛体のリスト</returns>
        /// <param name='rigids'>剛体のゲームオブジェクト</param>
        static List<int>[] SettingIgnoreRigidGroups(PMXFormat format, UnityEngine.GameObject[] rigids)
        {
            // 非衝突グループ用リストの初期化
            const int MaxGroup = 16;	// グループの最大数
            List<int>[] result = new List<int>[MaxGroup];
            for (int i = 0, i_max = MaxGroup; i < i_max; ++i)
            {
                result[i] = new List<int>();
            }

            // それぞれの剛体が所属している非衝突グループを追加していく
            for (int i = 0, i_max = format.rigidbodies.Length; i < i_max; ++i)
            {
                result[format.rigidbodies[i].group_index].Add(i);
            }
            return result;
        }
    }
}
