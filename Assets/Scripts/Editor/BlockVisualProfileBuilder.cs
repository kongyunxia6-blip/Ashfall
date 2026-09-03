#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// DEV-002：生成 placeholder 视觉资产（程序绘制的 Sprite + BlockVisualProfile），
    /// 供「普通岩 / 铁矿」配置独立 visual profile，验证完整 → 裂纹1/2/3 → 崩碎 的视觉管线。
    ///
    /// 说明：
    ///  - placeholder Sprite 是真正的 PNG → Sprite 资产，可在 Inspector 里直接替换成正式美术；
    ///  - 每种矿 5 态（intact/crack1/crack2/crack3/break）用明显可区分的图案 + 颜色区分；
    ///  - 只生成、只赋值 profile 引用，不修改共享 SO 的 digHits / dropId 等数值。
    ///
    /// 用法：菜单「灰烬之下/生成 DEV-002 Placeholder 视觉资产」。
    /// </summary>
    public static class BlockVisualProfileBuilder
    {
        const string SpriteFolder = "Assets/Art/Placeholder/Block";
        const string ProfileFolder = "Assets/Ashfall/Data/Visual";
        const int S = 32;   // 纹理边长（px）；ppu = S → 1 unit = 1 格

        [MenuItem("灰烬之下/生成 DEV-002 Placeholder 视觉资产")]
        public static void Build()
        {
            EnsureFolders();

            // 铁矿（红棕）与普通岩（灰）各一套 5 态
            var iron = BuildProfile("iron", new Color(0.68f, 0.50f, 0.38f), "Iron_铁矿", "Iron_VisualProfile");
            var rock = BuildProfile("rock", new Color(0.55f, 0.55f, 0.58f), "HardRock_硬岩", "HardRock_VisualProfile");

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[DEV-002] Placeholder 视觉资产已生成：\n" +
                      $"  {SpriteFolder}/iron_*.png 与 rock_*.png（各 5 态）\n" +
                      $"  {ProfileFolder}/Iron_VisualProfile.asset（→ Iron_铁矿）\n" +
                      $"  {ProfileFolder}/HardRock_VisualProfile.asset（→ HardRock_硬岩）\n" +
                      "正式美术就绪后，在对应 VisualProfile 的 Sprite 槽拖入新图即可替换。");
        }

        static BlockVisualProfile BuildProfile(string prefix, Color baseColor, string tileAssetName, string profileName)
        {
            var profile = EnsureAsset<BlockVisualProfile>($"{ProfileFolder}/{profileName}.asset");
            profile.intact = BuildSprite($"{prefix}_intact", baseColor, 0, false);
            profile.crack1 = BuildSprite($"{prefix}_crack1", baseColor, 1, false);
            profile.crack2 = BuildSprite($"{prefix}_crack2", baseColor, 2, false);
            profile.crack3 = BuildSprite($"{prefix}_crack3", baseColor, 3, false);
            var brk = BuildSprite($"{prefix}_break", baseColor, 0, true);
            profile.breakFrames = new Sprite[] { brk };
            profile.hitFlashDuration = 0.08f;
            profile.hitFlashColor = Color.white;
            EditorUtility.SetDirty(profile);

            // 赋给对应 TileDefinition（不改数值）
            var tile = AssetDatabase.LoadAssetAtPath<TileDefinition>($"Assets/Ashfall/Data/{tileAssetName}.asset");
            if (tile != null)
            {
                tile.visualProfile = profile;
                EditorUtility.SetDirty(tile);
            }
            else
            {
                Debug.LogWarning($"[DEV-002] 未找到 TileDefinition: Assets/Ashfall/Data/{tileAssetName}.asset");
            }

            return profile;
        }

        // ---------- Sprite 生成 ----------

        static Sprite BuildSprite(string name, Color baseColor, int crackStage, bool isBreak)
        {
            string pngPath = $"{SpriteFolder}/{name}.png";
            var tex = BuildTexture(baseColor, crackStage, isBreak);

            // 写 PNG（物理路径 = 项目根目录 + 相对路径）
            byte[] png = tex.EncodeToPNG();
            string projectRoot = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length);
            string fullPath = Path.Combine(projectRoot, pngPath);
            File.WriteAllBytes(fullPath, png);
            Object.DestroyImmediate(tex);

            // 导入为 Sprite
            AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(pngPath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = S;   // 32px = 1 unit = 1 格
            importer.alphaIsTransparency = true;
            importer.filterMode = FilterMode.Point;
            importer.SaveAndReimport();

            var spr = AssetDatabase.LoadAssetAtPath<Sprite>(pngPath);
            return spr;
        }

        static Texture2D BuildTexture(Color baseColor, int crackStage, bool isBreak)
        {
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            Color edge = baseColor * 0.55f;
            Color crack = baseColor * 0.30f;

            // 基础填充
            for (int x = 0; x < S; x++)
                for (int y = 0; y < S; y++)
                    tex.SetPixel(x, y, baseColor);

            // 1px 边缘描边
            for (int i = 0; i < S; i++)
            {
                tex.SetPixel(i, 0, edge);
                tex.SetPixel(i, S - 1, edge);
                tex.SetPixel(0, i, edge);
                tex.SetPixel(S - 1, i, edge);
            }

            if (isBreak)
            {
                // 崩碎：深色底 + 几块分离碎块
                for (int x = 0; x < S; x++)
                    for (int y = 0; y < S; y++)
                        tex.SetPixel(x, y, baseColor * 0.30f);

                FillRect(tex, 4, 4, 10, 10, baseColor);
                FillRect(tex, 18, 5, 9, 8, baseColor * 0.88f);
                FillRect(tex, 6, 19, 8, 7, baseColor * 0.82f);
                FillRect(tex, 19, 20, 8, 7, baseColor * 0.76f);
            }
            else
            {
                switch (crackStage)
                {
                    case 1:
                        DrawLine(tex, 5, 5, 26, 27, crack);
                        break;
                    case 2:
                        DrawLine(tex, 5, 5, 26, 27, crack);
                        DrawLine(tex, 26, 5, 5, 27, crack);
                        break;
                    case 3:
                        DrawLine(tex, 5, 5, 26, 27, crack);
                        DrawLine(tex, 26, 5, 5, 27, crack);
                        DrawLine(tex, 16, 3, 16, 29, crack);
                        DrawLine(tex, 3, 16, 29, 16, crack);
                        break;
                }
            }

            tex.Apply();
            return tex;
        }

        static void FillRect(Texture2D tex, int x0, int y0, int w, int h, Color c)
        {
            for (int x = x0; x < x0 + w && x < S; x++)
                for (int y = y0; y < y0 + h && y < S; y++)
                    tex.SetPixel(x, y, c);
        }

        static void DrawLine(Texture2D tex, int x0, int y0, int x1, int y1, Color c)
        {
            int dx = Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy, e2;
            while (true)
            {
                if (x0 >= 0 && x0 < S && y0 >= 0 && y0 < S)
                    tex.SetPixel(x0, y0, c);
                if (x0 == x1 && y0 == y1) break;
                e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        // ---------- 工具 ----------

        static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Art"))
                AssetDatabase.CreateFolder("Assets", "Art");
            if (!AssetDatabase.IsValidFolder("Assets/Art/Placeholder"))
                AssetDatabase.CreateFolder("Assets/Art", "Placeholder");
            if (!AssetDatabase.IsValidFolder(SpriteFolder))
                AssetDatabase.CreateFolder("Assets/Art/Placeholder", "Block");
            if (!AssetDatabase.IsValidFolder(ProfileFolder))
                AssetDatabase.CreateFolder("Assets/Ashfall/Data", "Visual");
        }

        static T EnsureAsset<T>(string path) where T : ScriptableObject
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null) return existing;
            var inst = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(inst, path);
            return inst;
        }
    }
}
#endif
