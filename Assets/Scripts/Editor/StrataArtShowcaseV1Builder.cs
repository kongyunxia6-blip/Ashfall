#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// 用正式地下 Block 美术搭建五段纵向地层预览场景。
    /// 每段以 base tile 铺底，并将该目录下的矿物按连续小矿脉嵌入，供正式世界接入前审美验收。
    /// </summary>
    public static class StrataArtShowcaseV1Builder
    {
        const string ScenePath = "Assets/Scenes/StrataArtShowcaseV1.unity";
        const string BlockRoot = "Assets/Art/Environment/Blocks";
        const string EdgeSpritePath = BlockRoot + "/bedrock/bedrock_edge_intact.png";
        const string BedrockAssetPath = "Assets/Ashfall/Data/Bedrock.asset";
        const string ProfileFolder = "Assets/Ashfall/Data/VisualProfiles";
        const string BedrockProfilePath = ProfileFolder + "/BedrockEdgeVisualProfile.asset";
        const int Width = 36;
        const int BandHeight = 7;

        sealed class Band
        {
            public string folder;
            public string title;
            public string depth;
            public Color labelColor;
        }

        static readonly Band[] Bands =
        {
            new Band { folder = "soil_rubble", title = "土壤 / 碎岩层", depth = "0–100m · ★", labelColor = new Color(0.95f, 0.76f, 0.45f) },
            new Band { folder = "normal_rock", title = "普通岩层", depth = "100–250m · ★★", labelColor = new Color(0.77f, 0.84f, 0.91f) },
            new Band { folder = "dense_rock", title = "致密岩层", depth = "250–450m · ★★★", labelColor = new Color(0.62f, 0.72f, 0.82f) },
            new Band { folder = "granite", title = "花岗岩带", depth = "450–575m · ★★★★", labelColor = new Color(0.82f, 0.66f, 0.59f) },
            new Band { folder = "basalt", title = "玄武岩带", depth = "575–700m · ★★★★", labelColor = new Color(0.63f, 0.54f, 0.67f) },
        };

        [MenuItem("灰烬之下/美术预览/搭建地下五层矿脉场景")]
        public static void Build()
        {
            Sprite edgeSprite = ConfigureAndLoadEdgeSprite();
            BindBedrockProfile(edgeSprite);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "StrataArtShowcaseV1";

            var root = new GameObject("StrataArtShowcase");
            var background = new GameObject("Background");
            background.transform.SetParent(root.transform);
            var layers = new GameObject("StrataLayers");
            layers.transform.SetParent(root.transform);
            var labels = new GameObject("Labels");
            labels.transform.SetParent(root.transform);

            MakeSolid(background.transform, "Backdrop", new Vector3(Width * 0.5f, -Bands.Length * BandHeight * 0.5f, 3f),
                new Vector2(80f, Bands.Length * BandHeight + 8f), new Color(0.018f, 0.022f, 0.03f), -30);

            int totalOreSprites = 0;
            for (int bandIndex = 0; bandIndex < Bands.Length; bandIndex++)
            {
                Band band = Bands[bandIndex];
                string folder = $"{BlockRoot}/{band.folder}";
                Sprite baseSprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{folder}/{band.folder}_base_intact.png");
                var ores = LoadOreSprites(folder);
                totalOreSprites += ores.Count;
                if (baseSprite == null)
                {
                    Debug.LogError($"[StrataArt] 缺少基础岩层图：{folder}/{band.folder}_base_intact.png");
                    continue;
                }

                var bandRoot = new GameObject($"Band_{bandIndex + 1:00}_{band.folder}");
                bandRoot.transform.SetParent(layers.transform);
                int yTop = -(bandIndex * BandHeight);

                for (int row = 0; row < BandHeight; row++)
                {
                    for (int x = 0; x < Width; x++)
                        MakeSprite(bandRoot.transform, $"base_{x:00}_{row:00}", baseSprite,
                            new Vector3(x + 0.5f, yTop - row - 0.5f, 0f), 0);
                    if (edgeSprite != null)
                    {
                        MakeSprite(bandRoot.transform, $"edge_left_{row:00}", edgeSprite,
                            new Vector3(-0.5f, yTop - row - 0.5f, -0.2f), 8);
                        var right = MakeSprite(bandRoot.transform, $"edge_right_{row:00}", edgeSprite,
                            new Vector3(Width + 0.5f, yTop - row - 0.5f, -0.2f), 8);
                        right.GetComponent<SpriteRenderer>().flipX = true;
                    }
                }

                // 每种矿生成 2–4 格连续、略带上下起伏的矿脉；同层所有矿图都会出现在场景中。
                for (int oreIndex = 0; oreIndex < ores.Count; oreIndex++)
                {
                    Sprite ore = ores[oreIndex];
                    int span = 2 + oreIndex % 3;
                    int startX = 2 + (oreIndex * 7) % (Width - 6);
                    int localY = 1 + (oreIndex * 3) % (BandHeight - 2);
                    var vein = new GameObject($"Vein_{ore.name}");
                    vein.transform.SetParent(bandRoot.transform);
                    for (int i = 0; i < span; i++)
                    {
                        int x = Mathf.Min(Width - 2, startX + i);
                        int y = Mathf.Clamp(localY + ((i == span - 1 && oreIndex % 2 == 0) ? 1 : 0), 1, BandHeight - 2);
                        MakeSprite(vein.transform, $"{ore.name}_{i + 1:00}", ore,
                            new Vector3(x + 0.5f, yTop - y - 0.5f, -0.1f), 5);
                    }
                }

                MakeLabel(labels.transform, $"Label_{band.folder}", $"{band.title}  {band.depth}",
                    new Vector3(0.5f, yTop - 0.22f, -1f), band.labelColor, 0.105f);
                if (bandIndex > 0)
                    MakeSolid(labels.transform, $"Boundary_{bandIndex:00}", new Vector3(Width * 0.5f, yTop, -0.5f),
                        new Vector2(Width, 0.06f), new Color(1f, 0.75f, 0.35f, 0.55f), 10);
            }

            MakeLabel(labels.transform, "SceneTitle", "ASHFALL · 地下地层与矿脉美术布局", new Vector3(0.5f, 1.25f, -1f), Color.white, 0.14f);
            MakeLabel(labels.transform, "SceneHint", "矿物按小型连续矿脉嵌入岩层；向下依次变硬、变稀有、价值提高", new Vector3(0.5f, 0.48f, -1f), new Color(0.72f, 0.78f, 0.84f), 0.078f);

            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 18.5f;
            camera.backgroundColor = new Color(0.018f, 0.022f, 0.03f);
            camera.transform.position = new Vector3(Width * 0.5f, -(Bands.Length * BandHeight) * 0.5f + 0.5f, -10f);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeGameObject = root;
            Debug.Log($"[StrataArt] 地下五层矿脉场景已搭建：{ScenePath}；5 层，矿物 Sprite {totalOreSprites} 张全部入场。");
        }

        static Sprite ConfigureAndLoadEdgeSprite()
        {
            AssetDatabase.ImportAsset(EdgeSpritePath, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(EdgeSpritePath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogError($"[StrataArt] 边缘基岩资源缺失或导入失败：{EdgeSpritePath}");
                return null;
            }
            bool changed = importer.textureType != TextureImporterType.Sprite
                           || importer.spriteImportMode != SpriteImportMode.Single
                           || Mathf.Abs(importer.spritePixelsPerUnit - 128f) > 0.01f
                           || importer.mipmapEnabled;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 128f;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.filterMode = FilterMode.Point;
            if (changed) importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(EdgeSpritePath);
        }

        static void BindBedrockProfile(Sprite edgeSprite)
        {
            if (edgeSprite == null) return;
            EnsureFolder(ProfileFolder);
            var profile = AssetDatabase.LoadAssetAtPath<BlockVisualProfile>(BedrockProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<BlockVisualProfile>();
                profile.name = "BedrockEdgeVisualProfile";
                AssetDatabase.CreateAsset(profile, BedrockProfilePath);
            }
            profile.intact = edgeSprite;
            profile.crack1 = edgeSprite;
            profile.crack2 = edgeSprite;
            profile.crack3 = edgeSprite;
            profile.breakFrames = Array.Empty<Sprite>();
            EditorUtility.SetDirty(profile);

            var bedrock = AssetDatabase.LoadAssetAtPath<TileDefinition>(BedrockAssetPath);
            if (bedrock == null)
                Debug.LogError($"[StrataArt] 缺少 Bedrock 数据资产：{BedrockAssetPath}");
            else
            {
                bedrock.visualProfile = profile;
                EditorUtility.SetDirty(bedrock);
            }
        }

        static void EnsureFolder(string path)
        {
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        static List<Sprite> LoadOreSprites(string folder)
        {
            var result = new List<Sprite>();
            string[] guids = AssetDatabase.FindAssets("t:Sprite", new[] { folder });
            Array.Sort(guids, (a, b) => string.CompareOrdinal(AssetDatabase.GUIDToAssetPath(a), AssetDatabase.GUIDToAssetPath(b)));
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("_base_intact.png", StringComparison.OrdinalIgnoreCase)) continue;
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite != null) result.Add(sprite);
            }
            return result;
        }

        static GameObject MakeSprite(Transform parent, string name, Sprite sprite, Vector3 position, int sortingOrder)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.position = position;
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.sortingOrder = sortingOrder;
            return go;
        }

        static void MakeLabel(Transform parent, string name, string value, Vector3 position, Color color, float size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.position = position;
            var text = go.AddComponent<TextMesh>();
            text.text = value;
            text.characterSize = size;
            text.fontSize = 48;
            text.anchor = TextAnchor.UpperLeft;
            text.alignment = TextAlignment.Left;
            text.color = color;
            text.GetComponent<MeshRenderer>().sortingOrder = 20;
        }

        static void MakeSolid(Transform parent, string name, Vector3 position, Vector2 size, Color color, int sortingOrder)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent);
            go.transform.position = position;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            var collider = go.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = new Material(Shader.Find("Sprites/Default")) { color = color };
            renderer.sortingOrder = sortingOrder;
        }
    }
}
#endif
