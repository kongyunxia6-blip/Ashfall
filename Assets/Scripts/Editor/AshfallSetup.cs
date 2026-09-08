#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// 一键生成整套方块资产与分层数据库。
    /// 用法：把本文件放在任意 Editor 文件夹下 → 菜单栏点
    ///       Tools → Ashfall → 一键生成默认方块与数据库
    /// 生成后只需把 TileDatabase 拖给 DigGrid 即可开跑。
    /// </summary>
    public static class AshfallSetup
    {
        const string RootFolder = "Assets/Ashfall";
        const string DataFolder = "Assets/Ashfall/Data";

        [MenuItem("Tools/Ashfall/一键生成默认方块与数据库")]
        public static void CreateDefaults()
        {
            if (!AssetDatabase.IsValidFolder(RootFolder))
                AssetDatabase.CreateFolder("Assets", "Ashfall");
            if (!AssetDatabase.IsValidFolder(DataFolder))
                AssetDatabase.CreateFolder(RootFolder, "Data");

            // ---- 基础方块 ----
            var empty = MakeTile("Empty", new Color(0, 0, 0, 0), 1, 0.1f, 0, false, false, 0);
            var bedrock = MakeTile("Bedrock", new Color(0.18f, 0.18f, 0.2f), 999, 999f, 0, true, false, 0);
            var dirt = MakeTile("Dirt_泥土", new Color(0.42f, 0.29f, 0.18f), 1, 0.20f, 0, true, false, 0);
            var hardRock = MakeTile("HardRock_硬岩", new Color(0.29f, 0.29f, 0.31f), 3, 1.20f, 0, true, false, 0);
            var lava = MakeTile("Lava_熔岩", new Color(1f, 0.27f, 0f), 1, 0.30f, 0, true, true, 25);

            // ---- 矿物（深度越深越值钱）----
            // 【DEV-015 校准后价值曲线】12 → 40 → 130 → 260 → 480 → 900 → 1300 → 2600 → 40000
            // 倍率 ×3.3 → ×3.25 → ×2.0 → ×1.85 → ×1.88 → ×1.44 → ×2.0 → ×15.4(终局核心矿)。
            // 中段(Gold 260 起)有意收窄，避免一次暴富；核心矿作为终局大奖允许跳涨。
            //
            // 【v3 重量曲线】越值钱越重（1.0 → 4.5），但【价值密度】单调递增：
            //   12  40   130  260   480   900   1300  2600   40000   （单价）
            //   1.0 1.2  1.5  1.9   2.2   2.6   3.0   3.5    4.5     （重量）
            //   12  33   87   137   218   346   433   743    8889    （单价÷重量）
            // 密度递增 ⇒ 满舱时「丢铁矿换钻石」永远是最优解，这正是 Motherload 的取舍手感。
            var iron = MakeTile("Iron_铁矿", new Color(0.63f, 0.61f, 0.58f), 1, 0.30f, 12, true, false, 0, 1.0f, "iron_ore");
            var copper = MakeTile("Copper_铜矿", new Color(0.72f, 0.45f, 0.20f), 1, 0.35f, 40, true, false, 0, 1.2f);
            var silver = MakeTile("Silver_银矿", new Color(0.75f, 0.75f, 0.78f), 2, 0.40f, 130, true, false, 0, 1.5f);
            var gold = MakeTile("Gold_金矿", new Color(1f, 0.84f, 0f), 2, 0.45f, 260, true, false, 0, 1.9f);
            var emerald = MakeTile("Emerald_绿宝石", new Color(0.31f, 0.78f, 0.47f), 3, 0.55f, 480, true, false, 0, 2.2f);
            var platinum = MakeTile("Platinum_铂金", new Color(0.90f, 0.89f, 0.89f), 3, 0.50f, 900, true, false, 0, 2.6f);
            var ruby = MakeTile("Ruby_红宝石", new Color(0.88f, 0.07f, 0.37f), 4, 0.60f, 1300, true, false, 0, 3.0f);
            var diamond = MakeTile("Diamond_钻石", new Color(0.73f, 0.95f, 1f), 4, 0.70f, 2600, true, false, 0, 3.5f);
            var coreOre = MakeTile("Ashfall_核心矿", new Color(1f, 0.27f, 0.95f), 5, 1.00f, 40000, true, false, 0, 4.5f);

            // ---- 数据库与分层 ----
            var db = MakeDatabase();
            db.emptyTile = empty;
            db.bedrockTile = bedrock;

            // 【v2 分层】起点 0/70/110/180/250/330/420/500/580/620（原为 0/20/60/120/200/280/360/450/500 共 9 层）。
            // 配合地图深度 520 → 640，让母矿区（580+）真正有探索空间。
            db.layers = new[]
            {
                Layer(0,   0.02f, (dirt, 60f), (iron, 40f)),
                Layer(70,  0.03f, (dirt, 15f), (iron, 45f), (copper, 40f)),
                Layer(110, 0.04f, (iron, 20f), (copper, 40f), (silver, 35f), (hardRock, 5f)),
                Layer(180, 0.05f, (copper, 20f), (silver, 40f), (gold, 35f), (hardRock, 5f)),
                Layer(250, 0.05f, (silver, 20f), (gold, 40f), (emerald, 30f), (hardRock, 10f)),
                Layer(330, 0.06f, (gold, 20f), (emerald, 35f), (platinum, 35f), (hardRock, 10f)),
                Layer(420, 0.06f, (emerald, 20f), (platinum, 35f), (ruby, 35f), (hardRock, 10f)),
                Layer(500, 0.07f, (platinum, 20f), (ruby, 35f), (diamond, 35f), (lava, 5f), (hardRock, 5f)),
                Layer(580, 0.07f, (ruby, 15f), (diamond, 45f), (coreOre, 20f), (lava, 15f), (hardRock, 5f)),
                Layer(620, 0.08f, (diamond, 25f), (coreOre, 45f), (lava, 25f), (hardRock, 5f)),
            };

            EditorUtility.SetDirty(db);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.activeObject = db;
            Debug.Log("[AshfallSetup] 已生成方块与数据库：Assets/Ashfall/Data/TileDatabase.asset\n" +
                      "接下来：把该资产拖给场景中 DigGrid 组件的 database 字段即可。");
        }

        // ---------- 工具方法 ----------

        static TileDefinition MakeTile(string name, Color color, int hardness, float drillTime,
                                       int value, bool isSolid, bool isHazard, int hazardDamage,
                                       float weight = 1f, string dropId = "")
        {
            string path = $"{DataFolder}/{name}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<TileDefinition>(path);
            var tile = existing != null ? existing : ScriptableObject.CreateInstance<TileDefinition>();

            tile.displayName = name;
            tile.color = color;
            tile.hardness = hardness;
            tile.drillTime = drillTime;
            tile.value = value;
            tile.isSolid = isSolid;
            tile.isHazard = isHazard;
            tile.hazardDamage = hazardDamage;

            // 通用掉落标识（DEV-001）：非空时挖穿由 BlockDropHook 按 id 派发掉落；空 = 无特殊掉落。
            tile.dropId = dropId;

            // 重量只在 value > 0 的矿物上有意义（泥土/硬岩/熔岩不进背包）。
            // 每次重建都覆盖，保证 AshfallSetup 是数值的唯一来源。
            tile.weight = weight;
            tile.stackLimit = 16;
            tile.gridWidth = 1;

            if (existing == null) AssetDatabase.CreateAsset(tile, path);
            else EditorUtility.SetDirty(tile);

            return tile;
        }

        static TileDatabase MakeDatabase()
        {
            string path = $"{DataFolder}/TileDatabase.asset";
            var existing = AssetDatabase.LoadAssetAtPath<TileDatabase>(path);
            if (existing != null) return existing;

            var db = ScriptableObject.CreateInstance<TileDatabase>();
            AssetDatabase.CreateAsset(db, path);
            return db;
        }

        static DepthLayer Layer(int startDepth, float caveChance,
                                params (TileDefinition tile, float weight)[] entries)
        {
            var layer = new DepthLayer
            {
                startDepth = startDepth,
                caveChance = caveChance,
                tiles = new TileDefinition[entries.Length],
                weights = new float[entries.Length]
            };

            for (int i = 0; i < entries.Length; i++)
            {
                layer.tiles[i] = entries[i].tile;
                layer.weights[i] = entries[i].weight;
            }
            return layer;
        }
    }
}
#endif
