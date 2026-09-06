using System;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-013：遗迹（Ruin）定义层（Issue §4）。
    ///
    /// 职责：表达「一座遗迹是什么 / 允许出现在哪个区域 / 多大 footprint / 入口规则 /
    /// 需要什么能力 / 给什么奖励 / 扫描特征 / 一次性发现语义」。
    /// 与 SpecialBlockType 大枚举解耦——遗迹类型是独立概念，不塞进方块行为枚举。
    ///
    /// V1 采用纯 C# 数据类 + RuinCatalog 静态登记（不做 ScriptableObject），
    /// 结构可扩展、可序列化、可测试（对齐 StratumCatalog / OreCatalog 的既定风格）。
    ///
    /// 坐标/区域语义：RuinGenerator 运行时依 DepthRegionLayout（网格 y 带）落地，
    /// 本类用推荐区域 regionId（Shallow/Mid/Deep）表达「允许出现的探索深度带」。
    /// </summary>
    [Serializable]
    public class RuinDefinition
    {
        /// <summary>稳定 id（如 AncientRelayRoom）。</summary>
        public string stableId = "AncientRelayRoom";

        /// <summary>玩家可见名称（如 古代中继室）。</summary>
        public string displayName = "古代中继室";

        /// <summary>推荐出现的探索区域（DepthRegionLayout.regionId：Surface/Shallow/Mid/Deep）。</summary>
        public string allowedRegionId = "Deep";

        /// <summary>建议 footprint 宽（格）。</summary>
        public int footprintWidth = 6;

        /// <summary>建议 footprint 高（格）。</summary>
        public int footprintHeight = 4;

        /// <summary>入口规则描述（人类可读，验收/日志）。V1 单一 RuinSeal 门。</summary>
        public string entranceRule = "单一入口，由一块 RuinSeal 封印，需 RuinAccess 打开";

        /// <summary>进入/调查所需能力（如 RuinAccess）。V1：入口需 RuinAccess。</summary>
        public MiningCapability requiredCapability = MiningCapability.RuinAccess;

        /// <summary>奖励档案（稳定 stableId + 数量；真实 Tile 资产由运行时 AssetRefs 解析入包）。</summary>
        public RuinRewardSpec[] rewardProfile = Array.Empty<RuinRewardSpec>();

        /// <summary>扫描特征签名（供 RuinDiscovery 只读信号匹配；不当作透视依据）。</summary>
        public string scanSignature = "anomalous_structure";

        /// <summary>一次性首发现语义：世界级（同 seed 世界只首见一次）由 RuinDiscoveryService 承载。</summary>
        public string firstDiscoverySemantics = "discovered_once_per_seed";

        /// <summary>一句话描述（日志/图鉴/PR）。</summary>
        public string shortDescription = "";
    }

    /// <summary>
    /// DEV-013：遗迹单项奖励规格。V1 用 stableId 表达「发什么 + 发几个」，
    /// 真实 TileDefinition 由运行时 RuinAssetRefs（Builder 注入真实 .asset）依 stableId 解析后 AddItem。
    /// stableId 覆盖 AncientDataFragment（文明数据碎片）与 OreCatalog.AncientAlloy（古代合金）等。
    /// </summary>
    [Serializable]
    public class RuinRewardSpec
    {
        public string stableId = "";
        public string displayName = "";
        public int count = 1;
    }

    /// <summary>
    /// DEV-013：遗迹定义的唯一集中登记处。V1 至少登记 AncientRelayRoom；
    /// 后续可扩展其他文明节点（禁止把遗迹类型继续塞进 SpecialBlockType 大枚举）。
    /// </summary>
    public static class RuinCatalog
    {
        // ---- 已实现遗迹 stable id ----
        public const string AncientRelayRoom = "AncientRelayRoom";

        // ---- 文明资源 stable id（奖励包入 Inventory 用） ----
        public const string AncientDataFragment = "AncientDataFragment";   // 文明数据碎片（研究资源，占 Cargo）
        // AncientAlloy 直接复用 OreCatalog.AncientAlloy（== "ancient_alloy"）——不重复登记。

        public static readonly RuinDefinition AncientRelay =
            new RuinDefinition
            {
                stableId = AncientRelayRoom,
                displayName = "古代中继室",
                allowedRegionId = "Deep",          // 第一次在深层被发现
                footprintWidth = 8,
                footprintHeight = 4,
                entranceRule = "单一入口，由一块 RuinSeal 封印，需 RuinAccess 打开",
                requiredCapability = MiningCapability.RuinAccess,
                rewardProfile = new[]
                {
                    new RuinRewardSpec { stableId = AncientDataFragment, displayName = "古代数据碎片", count = 1 },
                    new RuinRewardSpec { stableId = OreCatalog.AncientAlloy, displayName = "古代合金", count = 3 },
                },
                scanSignature = "anomalous_structure",
                firstDiscoverySemantics = "discovered_once_per_seed",
                shortDescription = "失落文明遗留的小型人工结构中继室；核心交互节点 AncientRelayCore 触发一次性调查与奖励。",
            };

        /// <summary>全部已登记遗迹。</summary>
        public static readonly RuinDefinition[] All = { AncientRelay };

        /// <summary>按 stableId 查遗迹；未找到返回 null。</summary>
        public static RuinDefinition Find(string stableId)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i].stableId == stableId) return All[i];
            return null;
        }
    }
}
