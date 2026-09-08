#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// DEV-015 Blocker2/1 收口资产生成器。
    /// 菜单：灰烬之下 → 搭建 DEV-015 经济权威资产与矿种
    ///
    /// 一次性生成（幂等，已存在则就地覆盖字段，不重复 CreateAsset）：
    ///  1. 8 个新矿 TileDefinition 资产（Blocker1 补齐 OreCatalog 覆盖）：
    ///       Coal_煤 / Lead_铅矿 / Amethyst_紫水晶 / Sapphire_蓝宝石 / Uranium_铀矿 /
    ///       EnergyCrystal_能量水晶 / AnomalousCrystal_异常水晶 / UnknownMineral_未知矿物。
    ///  2. OreRegionPreset.asset —— 矿脉/经济分层的【唯一权威配置】(Blocker2 权威源)。
    ///       Deep 带含 Gold/Emerald/Sapphire/Platinum/Ruby/Diamond/Uranium/EnergyCrystal 高值矿，
    ///       使「越深单趟越值钱」在经济与生成自洽（修复 DEV-007 旧带深层无高值矿的断层）。
    ///
    /// 注意：AnomalousCrystal / UnknownMineral 属「稀有特殊」（Discovery Node/彩蛋），
    /// 刻意不进普通带主权重（需绕路/极罕见），本工具不把它们加入 preset.bands。
    /// </summary>
    public static class EconomyPresetTool
    {
        const string DataFolder = "Assets/Ashfall/Data";
        const string PresetPath = DataFolder + "/OreRegionPreset.asset";

        [MenuItem("灰烬之下/搭建 DEV-015 经济权威资产与矿种")]
        public static void Build()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Ashfall"))
                AssetDatabase.CreateFolder("Assets", "Ashfall");
            if (!AssetDatabase.IsValidFolder(DataFolder))
                AssetDatabase.CreateFolder("Assets/Ashfall", "Data");

            // ---- 1. 加载既有基准矿（引用进 preset 用） ----
            var iron = Load("Iron_铁矿"); var tin = Load("Tin_锡矿"); var copper = Load("Copper_铜矿");
            var silver = Load("Silver_银矿"); var gold = Load("Gold_金矿"); var emerald = Load("Emerald_绿宝石");
            var platinum = Load("Platinum_铂金"); var ruby = Load("Ruby_红宝石"); var diamond = Load("Diamond_钻石");
            if (iron == null || tin == null || copper == null || silver == null || gold == null
                || emerald == null || platinum == null || ruby == null || diamond == null)
            {
                Debug.LogError("[DEV-015] 缺少基准矿资产。请先运行 Tools/Ashfall/一键生成默认方块与数据库。");
                return;
            }

            // ---- 2. 新建 8 个矿（草案数值；幂等）----
            var coal      = MakeOre("Coal_煤",               0.25f, 0.25f, 0.27f, 1, 8,    1.0f);
            var lead      = MakeOre("Lead_铅矿",             0.45f, 0.45f, 0.50f, 1, 28,   1.2f);
            var amethyst  = MakeOre("Amethyst_紫水晶",       0.62f, 0.35f, 0.78f, 2, 70,   1.4f);
            var sapphire  = MakeOre("Sapphire_蓝宝石",       0.25f, 0.45f, 0.90f, 3, 420,  2.0f);
            var uranium   = MakeOre("Uranium_铀矿",          0.40f, 0.75f, 0.30f, 3, 780,  2.4f);
            var energyCry = MakeOre("EnergyCrystal_能量水晶",0.90f, 0.60f, 0.20f, 4, 1800, 3.2f);
            var anomalous = MakeOre("AnomalousCrystal_异常水晶", 0.95f, 0.30f, 0.70f, 4, 1100, 0.8f);
            var unknown   = MakeOre("UnknownMineral_未知矿物",   0.70f, 0.95f, 0.95f, 5, 9999, 1.0f);

            // Uranium 轻微 hazard（热）：isHazard true + 低伤害，体现「高值需冒险」
            SetHazard(uranium, true, 6);

            // ---- 3. 构建权威 OreRegionPreset（经济意图完整 3 带）----
            var preset = AssetDatabase.LoadAssetAtPath<OreRegionPreset>(PresetPath);
            if (preset == null)
            {
                preset = ScriptableObject.CreateInstance<OreRegionPreset>();
                AssetDatabase.CreateAsset(preset, PresetPath);
            }

            preset.bands = new[]
            {
                // Shallow：廉价煤 + 基础金属（出厂 Lv1 可挖）
                new OreDepthBand {
                    bandName = "Shallow", minDepth = 3, maxDepth = 21,
                    ores = new[] { iron, coal, tin, copper },
                    weights = new[] { 35f, 20f, 30f, 15f },
                    veinMinSize = 2, veinMaxSize = 4, veinFrequency = 0.7f,
                },
                // Mid：银/金主收益 + 紫晶(硬度2)、铅过渡
                new OreDepthBand {
                    bandName = "Mid", minDepth = 22, maxDepth = 43,
                    ores = new[] { silver, gold, copper, amethyst, lead },
                    weights = new[] { 32f, 30f, 12f, 16f, 10f },
                    veinMinSize = 3, veinMaxSize = 6, veinFrequency = 0.8f,
                },
                // Deep：金/铂/祖母绿/蓝宝石/红宝石/钻石 高值 + 铀(轻微热) + 能量水晶低权重
                new OreDepthBand {
                    bandName = "Deep", minDepth = 44, maxDepth = 62,
                    ores = new[] { gold, platinum, emerald, sapphire, ruby, diamond, uranium, energyCry },
                    weights = new[] { 12f, 22f, 15f, 12f, 18f, 11f, 7f, 3f },
                    veinMinSize = 4, veinMaxSize = 8, veinFrequency = 0.9f,
                },
            };
            preset.referenceSeed = -1;
            EditorUtility.SetDirty(preset);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[DEV-015] 已生成 8 新矿 + 权威 OreRegionPreset：{PresetPath}\n" +
                      "Shallow(煤/铁/锡/铜) → Mid(银/金/铜/紫晶/铅) → Deep(金/铂/祖母绿/蓝宝/红宝/钻石/铀/能量水晶)");
        }

        // ---------- 工具 ----------

        static TileDefinition Load(string f)
            => AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/{f}.asset");

        static TileDefinition MakeOre(string name, float r, float g, float b,
                                      int hardness, int value, float weight)
        {
            string path = $"{DataFolder}/{name}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<TileDefinition>(path);
            var t = existing != null ? existing : ScriptableObject.CreateInstance<TileDefinition>();
            t.displayName = name;
            t.color = new Color(r, g, b);
            t.hardness = hardness;
            t.drillTime = 0.35f + 0.08f * (hardness - 1);   // 高硬度略慢
            t.value = value;
            t.isSolid = true;
            t.isHazard = false;
            t.hazardDamage = 0;
            t.weight = weight;
            t.stackLimit = 16;
            t.gridWidth = 1;
            if (existing == null) AssetDatabase.CreateAsset(t, path);
            else EditorUtility.SetDirty(t);
            return t;
        }

        static void SetHazard(TileDefinition t, bool isHazard, int dmg)
        {
            if (t == null) return;
            t.isHazard = isHazard;
            t.hazardDamage = dmg;
            EditorUtility.SetDirty(t);
        }
    }
}
#endif
