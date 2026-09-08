#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// DEV-016：正式地层资产生成器（V1 = 只铺 5 地层岩色）。
    ///
    /// 目标：让 Codex 新导入的 5 地层正式方块美术（soil_rubble/normal_rock/dense_rock/granite/basalt
    /// 的 <stratum>_base_intact）真正驱动 48×640 可玩世界的【基底填充】；矿物仍用 canonical 资产
    /// （Iron_铁矿 等，economy/DEV-015 S7 零影响）由 OreVeinGenerator 按地层带叠加。
    ///
    /// V1 刻意不做「逐地层矿图」（那是 V2，需 Codex 补 crack/break 帧 + 重做渲染或复制矿资产），
    /// 只把《stratum>_base_intact 岩色按地层铺满，让地层过渡肉眼可见。
    ///
    /// 产出（幂等，已存在则就地覆盖，不重复 CreateAsset）：
    ///  1. 5 个地层基底 TileDefinition + BlockVisualProfile（绑对应 <stratum>_base_intact）：
    ///       SoilRubble_土壤碎岩(0-100)/ NormalRock_普通岩(100-250)/ DenseRock_致密岩(250-450)/
    ///       Granite_花岗岩(450-575)/ Basalt_玄武岩(575-640)。
    ///  2. TileDatabase_Strata.asset —— 5 层纯地层基底数据库（每层只一个 value==0 岩格；cave 低）。
    ///  3. OreRegionPreset_Strata.asset —— 5 带矿脉权威源（引用 canonical 矿，覆盖 640m）。
    ///
    /// 菜单：灰烬之下 → DEV-016 搭建：地层基底资产生成（地层岩色）
    /// </summary>
    public static class Dev016StrataSetup
    {
        const string RootFolder = "Assets/Ashfall";
        const string DataFolder = "Assets/Ashfall/Data";
        const string BlockRoot = "Assets/Art/Environment/Blocks";
        const string StrataDbPath = DataFolder + "/TileDatabase_Strata.asset";
        const string StrataPresetPath = DataFolder + "/OreRegionPreset_Strata.asset";
        const string ProfileFolder = DataFolder + "/VisualProfiles_Strata";
        const string StrataDataFolder = DataFolder + "/Strata";

        // 地层：startDepth / 稳定名 / 资产名 / 颜色(占位回退)
        readonly struct StratumSpec
        {
            public readonly string stableId; public readonly int startDepth;
            public readonly string tileName; public readonly string folder;
            public readonly int hardness; public readonly int digHits; public readonly float drillTime;
            public readonly Color color;
            public StratumSpec(string stableId, int startDepth, string tileName, string folder,
                int hardness, int digHits, float drillTime, Color color)
            { this.stableId = stableId; this.startDepth = startDepth; this.tileName = tileName;
              this.folder = folder; this.hardness = hardness; this.digHits = digHits;
              this.drillTime = drillTime; this.color = color; }
        }

        static readonly StratumSpec[] Strata =
        {
            new StratumSpec("SoilRubble",   0,   "SoilRubble_土壤碎岩", "soil_rubble",  1, 1, 0.22f, new Color(0.50f,0.40f,0.30f)),
            new StratumSpec("NormalRock",   100, "NormalRock_普通岩",   "normal_rock",  2, 3, 0.60f, new Color(0.55f,0.57f,0.62f)),
            new StratumSpec("DenseRock",    250, "DenseRock_致密岩",    "dense_rock",   3, 5, 1.00f, new Color(0.42f,0.47f,0.55f)),
            new StratumSpec("Granite",      450, "Granite_花岗岩",      "granite",      4, 8, 1.60f, new Color(0.62f,0.48f,0.42f)),
            new StratumSpec("Basalt",       575, "Basalt_玄武岩",       "basalt",       5, 12, 2.20f, new Color(0.36f,0.32f,0.42f)),
        };

        [MenuItem("灰烬之下/DEV-016 搭建：地层基底资产（地层岩色）")]
        public static void Build()
        {
            EnsureFolder(RootFolder); EnsureFolder(DataFolder);
            EnsureFolder(StrataDataFolder); EnsureFolder(ProfileFolder);

            // 载入 canonical bedrock/empty 引用
            var empty = LoadTile("Empty"); var bedrock = LoadTile("Bedrock");

            // 逐地层：建基底 TileDefinition + VisualProfile（绑 <stratum>_base_intact）
            var baseTiles = new TileDefinition[Strata.Length];
            var layers = new DepthLayer[Strata.Length];
            var bands = new OreDepthBand[Strata.Length];

            for (int i = 0; i < Strata.Length; i++)
            {
                var s = Strata[i];
                Sprite baseSpr = ConfigureAndLoadSprite($"{BlockRoot}/{s.folder}/{s.folder}_base_intact.png");
                var tile = MakeStrataBase(s, baseSpr);   // 建 TileDefinition + 绑 profile
                baseTiles[i] = tile;

                layers[i] = new DepthLayer
                {
                    startDepth = s.startDepth,
                    caveChance = 0.02f + 0.005f * i,     // 越深越易天然空腔，轻微
                    tiles = new[] { tile },
                    weights = new[] { 1f },
                };

                bands[i] = MakeBandForStratum(s, baseTiles); // 按地层词表叠 canonical 矿
            }

            // ---- 写 TileDatabase_Strata ----
            var db = MakeDatabase(StrataDbPath);
            if (empty != null) db.emptyTile = empty;
            if (bedrock != null) db.bedrockTile = bedrock;
            db.layers = layers;
            EditorUtility.SetDirty(db);

            // ---- 写 OreRegionPreset_Strata ----
            var preset = MakePreset(StrataPresetPath);
            preset.bands = bands;
            preset.referenceSeed = -1;
            EditorUtility.SetDirty(preset);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject = db;
            Debug.Log("[DEV-016] 地层基底资产已生成：\n" +
                      $"  TileDatabase: {StrataDbPath}（5 层：0/100/250/450/575，每层纯地层岩色）\n" +
                      $"  OreRegionPreset: {StrataPresetPath}（5 带矿脉，覆盖 640m）\n" +
                      "下一步：菜单「灰烬之下 → DEV-016 搭建：正式地层世界(48×640)」建场景。");
        }

        // ---------- 地层带矿（V1：canonical 矿按地层词表叠加；Anomalous/Unknown 低权重彩蛋） ----------
        static OreDepthBand MakeBandForStratum(StratumSpec s, TileDefinition[] baseTiles)
        {
            // canonical 矿资产引用
            TileDefinition Iron=LoadTile("Iron_铁矿"), Coal=LoadTile("Coal_煤"), Tin=LoadTile("Tin_锡矿"),
                Lead=LoadTile("Lead_铅矿"), Copper=LoadTile("Copper_铜矿"), Silver=LoadTile("Silver_银矿"),
                Gold=LoadTile("Gold_金矿"), Amethyst=LoadTile("Amethyst_紫水晶"), Emerald=LoadTile("Emerald_绿宝石"),
                Sapphire=LoadTile("Sapphire_蓝宝石"), Ruby=LoadTile("Ruby_红宝石"), Platinum=LoadTile("Platinum_铂金"),
                Uranium=LoadTile("Uranium_铀矿"), EnergyCrystal=LoadTile("EnergyCrystal_能量水晶"),
                Diamond=LoadTile("Diamond_钻石"), Anomalous=LoadTile("AnomalousCrystal_异常水晶"),
                Unknown=LoadTile("UnknownMineral_未知矿物");

            // 每层最大深度 = 下一层 startDepth-1；最深层 = 世界底 639
            int maxDepth = NextStratumMaxDepth(s.startDepth);

            switch (s.stableId)
            {
                case "SoilRubble":  // 浅 0-99：廉价金属，出厂 Lv1 可挖
                    return Band(s, 3, 99, new[]{ Iron,Coal,Tin,Copper,Lead },
                        new[]{ 35f,18f,26f,15f,6f }, 2, 4, 0.55f);
                case "NormalRock":  // 100-249：银/金主 + 紫晶/铜过渡
                    return Band(s, 100, 249, new[]{ Silver,Gold,Copper,Amethyst,Tin },
                        new[]{ 34f,28f,10f,18f,10f }, 3, 6, 0.65f);
                case "DenseRock":   // 250-449：金/祖母绿/蓝宝/红宝 + 紫晶
                    return Band(s, 250, 449, new[]{ Gold,Emerald,Amethyst,Sapphire,Ruby },
                        new[]{ 26f,24f,14f,20f,16f }, 4, 7, 0.7f);
                case "Granite":     // 450-574：铂/红宝/钻石 + 铀/能量晶（轻微热）
                    return Band(s, 450, 574, new[]{ Platinum,Ruby,Diamond,Uranium,EnergyCrystal },
                        new[]{ 26f,22f,24f,18f,10f }, 5, 8, 0.8f);
                case "Basalt":      // 575-639：钻石/能量晶/铂 顶值 + Anomalous 极罕见
                    return Band(s, 575, 639, new[]{ Diamond,EnergyCrystal,Platinum,Ruby,Uranium },
                        new[]{ 26f,22f,18f,18f,12f }, 5, 8, 0.9f);
                default:
                    return Band(s, s.startDepth, 639, new[]{ Iron }, new[]{ 1f }, 2, 4, 0.5f);
            }
        }

        static int NextStratumMaxDepth(int thisStart)
        {
            for (int i = 0; i < Strata.Length; i++)
                if (Strata[i].startDepth == thisStart)
                    return (i + 1 < Strata.Length) ? Strata[i + 1].startDepth - 1 : 639;
            return 639;
        }

        static OreDepthBand Band(StratumSpec s, int min, int max, TileDefinition[] ores, float[] weights,
            int veinMin, int veinMax, float freq)
            => new OreDepthBand {
                bandName = s.stableId, minDepth = min, maxDepth = max,
                ores = ores, weights = weights, veinMinSize = veinMin, veinMaxSize = veinMax,
                veinFrequency = freq,
            };

        // ---------- 资产方法（幂等） ----------

        static TileDefinition MakeStrataBase(StratumSpec s, Sprite intact)
        {
            string path = $"{StrataDataFolder}/{s.tileName}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<TileDefinition>(path);
            var tile = existing != null ? existing : ScriptableObject.CreateInstance<TileDefinition>();
            tile.displayName = s.tileName;
            tile.color = s.color;
            tile.hardness = s.hardness;
            tile.digHits = s.digHits;
            tile.drillTime = s.drillTime;
            tile.value = 0;                 // 纯填充，无掉落价值
            tile.weight = 1f;
            tile.isSolid = true;
            tile.isHazard = false;
            tile.hazardDamage = 0;
            tile.blockType = BlockType.Normal;
            tile.dropId = "";
            tile.visualProfile = MakeProfile($"{s.tileName}Profile", intact);
            if (existing == null) AssetDatabase.CreateAsset(tile, path);
            else EditorUtility.SetDirty(tile);
            return tile;
        }

        static BlockVisualProfile MakeProfile(string name, Sprite intact)
        {
            string path = $"{ProfileFolder}/{name}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<BlockVisualProfile>(path);
            var p = existing != null ? existing : ScriptableObject.CreateInstance<BlockVisualProfile>();
            p.name = name;
            // V1：只有 intact 正式图；crack1-3 暂用 intact 占位（DigGrid stage>0 仍显示完整图但调暗由 digHits 阈值决定——
            // 注：用完整图做裂纹会“无裂纹视觉”，但保证非纯色。Codex 补 crack 帧后替换这 3 行即可。
            p.intact = intact;
            p.crack1 = intact;
            p.crack2 = intact;
            p.crack3 = intact;
            p.breakFrames = Array.Empty<Sprite>();
            if (existing == null) AssetDatabase.CreateAsset(p, path);
            else EditorUtility.SetDirty(p);
            return p;
        }

        static TileDatabase MakeDatabase(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<TileDatabase>(path);
            if (existing != null) return existing;
            var db = ScriptableObject.CreateInstance<TileDatabase>();
            AssetDatabase.CreateAsset(db, path);
            return db;
        }

        static OreRegionPreset MakePreset(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<OreRegionPreset>(path);
            if (existing != null) return existing;
            var p = ScriptableObject.CreateInstance<OreRegionPreset>();
            AssetDatabase.CreateAsset(p, path);
            return p;
        }

        static TileDefinition LoadTile(string f)
            => AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/{f}.asset");

        // 配置 PNG 为 Sprite（PPU128/point/single）并加载 Sprite
        static Sprite ConfigureAndLoadSprite(string path)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) { Debug.LogError($"[DEV-016] 缺美术图：{path}"); return null; }
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
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        static void EnsureFolder(string path)
        {
            string[] parts = path.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }
}
#endif
