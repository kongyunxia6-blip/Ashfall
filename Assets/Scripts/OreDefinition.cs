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

        /// <summary>登记供查询的规则集（tile 留空，由 rig 以真实资产映射）。</summary>
        static OreDefinition[] BuildRegistered()
        {
            var iron = new OreDefinition
            {
                oreId = IronOre, displayName = "铁矿",
                allowedStrata = new[]
                {
                    new OreStratumWeight { stratumId = StratumCatalog.SoilRubble, weight = 3f },
                    new OreStratumWeight { stratumId = StratumCatalog.NormalRock, weight = 4f },
                    new OreStratumWeight { stratumId = StratumCatalog.DenseRock, weight = 3f },
                    new OreStratumWeight { stratumId = StratumCatalog.GraniteBasalt, weight = 1f },
                },
            };
            var copper = new OreDefinition
            {
                oreId = CopperOre, displayName = "铜矿",
                allowedStrata = new[]
                {
                    new OreStratumWeight { stratumId = StratumCatalog.SoilRubble, weight = 2f },
                    new OreStratumWeight { stratumId = StratumCatalog.NormalRock, weight = 4f },
                    new OreStratumWeight { stratumId = StratumCatalog.DenseRock, weight = 3f },
                },
            };
            var tin = new OreDefinition
            {
                oreId = TinOre, displayName = "锡矿",
                allowedStrata = new[]
                {
                    new OreStratumWeight { stratumId = StratumCatalog.NormalRock, weight = 3f },
                    new OreStratumWeight { stratumId = StratumCatalog.DenseRock, weight = 2f },
                },
            };
            return new[] { iron, copper, tin };
        }
    }
}
