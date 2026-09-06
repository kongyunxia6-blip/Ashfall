using System;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-012：矿物元数据（OreDefinition，Issue §6）。
    ///
    /// 实际 Block 数据仍由 TileDefinition（Iron_铁矿.asset 等）承载（value/weight/Sprite/耐久）；
    /// 本类只表达「矿种元数据 + 允许出现在哪些地层」的语义，不接管矿脉算法。
    /// OreVeinGenerator 仍是矿脉的唯一生成器；Stratum 系统只提供「允许关系 / 权重数据」。
    /// Scanner 继续只读。
    /// </summary>
    [Serializable]
    public class OreDefinition
    {
        /// <summary>stable id（与 TileDefinition.dropId 对齐，如 "iron_ore"）。</summary>
        public string oreId = "";

        /// <summary>矿种显示名（如 铁矿）。</summary>
        public string displayName = "";

        /// <summary>映射到的实际 TileDefinition（矿块资产）。可空 → 仅登记未启用。</summary>
        public TileDefinition tile;

        /// <summary>跨地层出现规则：allowedStratumId + 该层权重。同矿可跨多个地层仅权重不同。</summary>
        public OreStratumWeight[] allowedStrata = Array.Empty<OreStratumWeight>();
    }

    /// <summary>某矿在某地层的出现权重。null stratumRef = 该 stableId 尚未登记到 StratumCatalog（忽略该条）。</summary>
    [Serializable]
    public class OreStratumWeight
    {
        public string stratumId = "";
        public float weight = 1f;
    }

    /// <summary>
    /// DEV-012：矿物元数据的唯一集中登记处。
    /// 登记完整矿种规划（Issue §6）但测试只启用代表矿种；不改变 OreVeinGenerator 生成逻辑。
    /// </summary>
    public static class OreCatalog
    {
        /// <summary>代表矿种 oreId（测试世界启用；与 TileDefinition 资产对应）。</summary>
        public const string IronOre = "iron_ore";
        public const string CopperOre = "copper_ore";
        public const string TinOre = "tin_ore";

        // ---- 规划矿种 oreId（Issue §6 完整规划；tile 可 null，仅登记架构规则，不要求掉落/经济） ----
        public const string CoalOre = "coal_ore";
        public const string LeadOre = "lead_ore";
        public const string SilverOre = "silver_ore";
        public const string GoldOre = "gold_ore";
        public const string PlatinumOre = "platinum_ore";
        public const string Amethyst = "amethyst";
        public const string Ruby = "ruby";
        public const string Sapphire = "sapphire";
        public const string Emerald = "emerald";
        public const string Diamond = "diamond";
        public const string UraniumOre = "uranium_ore";
        public const string EnergyCrystal = "energy_crystal";
        public const string AncientAlloy = "ancient_alloy";
        public const string AnomalousCrystal = "anomalous_crystal";
        public const string UnknownMineral = "unknown_mineral";

        /// <summary>按 oreId 查允许地层权重表；无则返回空（不抛）。</summary>
        public static OreStratumWeight[] AllowedStrataOf(string oreId)
        {
            for (int i = 0; i < Registered.Length; i++)
                if (Registered[i].oreId == oreId) return Registered[i].allowedStrata;
            return Array.Empty<OreStratumWeight>();
        }

        /// <summary>某矿是否允许出现在某地层（stableId）。禁止矿不会生成到不允许地层由生成器 + 本查询共同保证。</summary>
        public static bool AllowedInStratum(string oreId, string stratumId)
        {
            var rows = AllowedStrataOf(oreId);
            for (int i = 0; i < rows.Length; i++)
                if (rows[i].stratumId == stratumId) return true;
            return false;
        }

        // ---- 注册表：登记跨地层语义（权重按长期规划，V1 测试不强制用满） ----
        // 说明：TileDefinition 资产在运行时按路径加载（Editor builder 注入），此处不直接持有 asset，
        //       只登记 oreId + 允许地层规则；tile 由场景/测试 rig 通过 LoadTile 注入映射。
        static readonly OreDefinition[] Registered = BuildRegistered();

        /// <summary>
        /// 登记在册的 oreId（含规划矿种）。Registered 之后惰性构建，避免静态初始化顺序问题。
        /// </summary>
        public static readonly string[] RegisteredOreIds = BuildRegisteredOreIds();

        static string[] BuildRegisteredOreIds()
        {
            var list = new string[Registered.Length];
            for (int i = 0; i < Registered.Length; i++) list[i] = Registered[i].oreId;
            return list;
        }

        /// <summary>返回 Registered（编辑回归断言读「实际登记种数」用）。</summary>
        public static OreDefinition[] GetRegisteredSnapshot() => (OreDefinition[])Registered.Clone();

        /// <summary>登记供查询的规则集（tile 留空，由 rig 以真实资产映射）。</summary>
        static OreDefinition[] BuildRegistered()
        {
            // 辅助：把 (stratumId, weight) 元组转成 OreStratumWeight[]。
            static OreStratumWeight[] W(params (string id, float weight)[] rows)
            {
                var arr = new OreStratumWeight[rows.Length];
                for (int i = 0; i < rows.Length; i++)
                    arr[i] = new OreStratumWeight { stratumId = rows[i].id, weight = rows[i].weight };
                return arr;
            }

            var coal = new OreDefinition
            {
                oreId = CoalOre, displayName = "煤矿",
                allowedStrata = W(
                    (StratumCatalog.SoilRubble, 4f), (StratumCatalog.NormalRock, 5f),
                    (StratumCatalog.DenseRock, 2f)),
            };
            var copper = new OreDefinition
            {
                oreId = CopperOre, displayName = "铜矿",
                allowedStrata = W(
                    (StratumCatalog.SoilRubble, 2f), (StratumCatalog.NormalRock, 4f),
                    (StratumCatalog.DenseRock, 3f)),
            };
            var iron = new OreDefinition
            {
                oreId = IronOre, displayName = "铁矿",
                allowedStrata = W(
                    (StratumCatalog.SoilRubble, 3f), (StratumCatalog.NormalRock, 4f),
                    (StratumCatalog.DenseRock, 3f), (StratumCatalog.GraniteBasalt, 1f)),
            };
            var tin = new OreDefinition
            {
                oreId = TinOre, displayName = "锡矿",
                allowedStrata = W(
                    (StratumCatalog.NormalRock, 3f), (StratumCatalog.DenseRock, 2f)),
            };
            var lead = new OreDefinition
            {
                oreId = LeadOre, displayName = "铅矿",
                allowedStrata = W(
                    (StratumCatalog.NormalRock, 2f), (StratumCatalog.DenseRock, 3f),
                    (StratumCatalog.GraniteBasalt, 2f)),
            };
            var silver = new OreDefinition
            {
                oreId = SilverOre, displayName = "银矿",
                allowedStrata = W(
                    (StratumCatalog.DenseRock, 2f), (StratumCatalog.GraniteBasalt, 3f)),
            };
            var gold = new OreDefinition
            {
                oreId = GoldOre, displayName = "金矿",
                allowedStrata = W(
                    (StratumCatalog.GraniteBasalt, 2f), (StratumCatalog.CrystallizedRock, 3f)),
            };
            var platinum = new OreDefinition
            {
                oreId = PlatinumOre, displayName = "铂矿",
                allowedStrata = W(
                    (StratumCatalog.GraniteBasalt, 1f), (StratumCatalog.CrystallizedRock, 3f)),
            };
            var amethyst = new OreDefinition
            {
                oreId = Amethyst, displayName = "紫水晶",
                allowedStrata = W(
                    (StratumCatalog.CrystallizedRock, 4f), (StratumCatalog.AnomalousRuinLayer, 1f)),
            };
            var ruby = new OreDefinition
            {
                oreId = Ruby, displayName = "红宝石",
                allowedStrata = W(
                    (StratumCatalog.CrystallizedRock, 2f), (StratumCatalog.AnomalousRuinLayer, 3f)),
            };
            var sapphire = new OreDefinition
            {
                oreId = Sapphire, displayName = "蓝宝石",
                allowedStrata = W(
                    (StratumCatalog.CrystallizedRock, 2f), (StratumCatalog.AnomalousRuinLayer, 3f)),
            };
            var emerald = new OreDefinition
            {
                oreId = Emerald, displayName = "翡翠",
                allowedStrata = W(
                    (StratumCatalog.CrystallizedRock, 3f), (StratumCatalog.AnomalousRuinLayer, 3f)),
            };
            var diamond = new OreDefinition
            {
                oreId = Diamond, displayName = "钻石",
                allowedStrata = W(
                    (StratumCatalog.CrystallizedRock, 1f), (StratumCatalog.AnomalousRuinLayer, 4f)),
            };
            var uranium = new OreDefinition
            {
                oreId = UraniumOre, displayName = "铀矿",
                allowedStrata = W(
                    (StratumCatalog.GraniteBasalt, 1f), (StratumCatalog.CrystallizedRock, 2f),
                    (StratumCatalog.AnomalousRuinLayer, 3f)),
            };
            var energyCrystal = new OreDefinition
            {
                oreId = EnergyCrystal, displayName = "能源晶体",
                allowedStrata = W(
                    (StratumCatalog.CrystallizedRock, 3f), (StratumCatalog.AnomalousRuinLayer, 3f)),
            };
            var ancientAlloy = new OreDefinition
            {
                oreId = AncientAlloy, displayName = "古代合金",
                allowedStrata = W((StratumCatalog.AnomalousRuinLayer, 4f)),
            };
            var anomalousCrystal = new OreDefinition
            {
                oreId = AnomalousCrystal, displayName = "异常晶体",
                allowedStrata = W((StratumCatalog.AnomalousRuinLayer, 5f)),
            };
            var unknown = new OreDefinition
            {
                oreId = UnknownMineral, displayName = "未知矿物",
                allowedStrata = W((StratumCatalog.AnomalousRuinLayer, 2f)),
            };
            return new[]
            {
                coal, copper, iron, tin, lead, silver, gold, platinum,
                amethyst, ruby, sapphire, emerald, diamond, uranium,
                energyCrystal, ancientAlloy, anomalousCrystal, unknown,
            };
        }
    }
}
