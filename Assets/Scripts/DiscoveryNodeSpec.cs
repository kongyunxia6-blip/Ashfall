using System;

namespace Ashfall
{
    /// <summary>
    /// DEV-014：一种「地下发现节点」的轻量数据描述（Issue #30 §Architecture Boundary 1）。
    ///
    /// 这是【组合现有系统的薄层定义】——它只描述：
    ///  - 这是什么类型、允许出现在哪个深度区域；
    ///  - 建议 footprint / 建议每 region 生成数量档；
    ///  - 它组合哪些 tile（由 DiscoveryNodeGenerator 的公开 TileDefinition 引用按 stableId 解析）；
    ///  - 扫描特征 / 风险 / 一句话简述。
    ///
    /// 它【绝不】拥有第二套 Grid / BlockHP / Inventory / Depth / Ore / Collapse / Heat /
    /// Capability / Ruin / Run settlement。实际落图 = DiscoveryNodeGenerator 调现有
    /// DigGrid.SetTile + 复用现有系统反应（BlockCollapseSystem / HotRockSystem /
    /// RuinSealSystem / MiningCapabilityResolver / InventoryGrid）。
    /// </summary>
    [Serializable]
    public class DiscoveryNodeSpec
    {
        /// <summary>稳定 id（DiscoveryNodeTypeInfo.*）。</summary>
        public DiscoveryNodeType type = DiscoveryNodeType.AbandonedMiningPocket;

        /// <summary>允许出现的探索区域（DepthRegionLayout.regionId：Shallow/Mid/Deep）。</summary>
        public string allowedRegionId = "Shallow";

        /// <summary>建议 footprint 宽（格）。</summary>
        public int footprintWidth = 4;

        /// <summary>建议 footprint 高（格）。</summary>
        public int footprintHeight = 3;

        /// <summary>该区域允许的最大生成个数（每 seed）。</summary>
        public int maxPerSeed = 2;

        /// <summary>扫描特征签名（只读模糊信号匹配，不当作透视依据）。</summary>
        public string scanSignature = "";

        /// <summary>一句话风险/玩法简述（日志/PR）。</summary>
        public string shortDescription = "";
    }

    /// <summary>DEV-014：节点定义的唯一集中登记处（对齐 RuinCatalog / StratumCatalog 风格）。</summary>
    public static class DiscoveryNodeCatalog
    {
        /// <summary>四类节点的登记（V1 各 region 数量档分开调，避免同 seed 挤同一带）。</summary>
        public static readonly DiscoveryNodeSpec[] All =
        {
            new DiscoveryNodeSpec
            {
                type = DiscoveryNodeType.AbandonedMiningPocket,
                allowedRegionId = "Shallow",
                footprintWidth = 5,
                footprintHeight = 4,
                maxPerSeed = 2,
                scanSignature = "abandoned_structure",
                shortDescription = "废弃采矿点：高值矿 + 人工支撑，先拆承重岩有局部坍塌风险（复用 BlockCollapseSystem）。",
            },
            new DiscoveryNodeSpec
            {
                type = DiscoveryNodeType.ThermalVentChamber,
                allowedRegionId = "Mid",
                footprintWidth = 4,
                footprintHeight = 3,
                maxPerSeed = 2,
                scanSignature = "thermal_anomaly",
                shortDescription = "热裂隙室：HotRock 热走廊，危险后高值矿，Cooling 使同路径更可处理（复用 HotRockSystem）。",
            },
            new DiscoveryNodeSpec
            {
                type = DiscoveryNodeType.AncientSignalCache,
                allowedRegionId = "Deep",
                footprintWidth = 3,
                footprintHeight = 3,
                maxPerSeed = 2,
                scanSignature = "weak_ancient_signal",
                shortDescription = "文明信号缓存点：极小 RuinSeal 门 + 文明奖励格，经 RuinAccess 单格打开（不复用完整 AncientRelayRoom）。",
            },
            new DiscoveryNodeSpec
            {
                type = DiscoveryNodeType.CollapsedResourcePocket,
                allowedRegionId = "Mid",
                footprintWidth = 5,
                footprintHeight = 4,
                maxPerSeed = 2,
                scanSignature = "unstable_cavity",
                shortDescription = "坍塌资源囊：LooseRock 压顶 + 奖励矿，挖 SupportRock 顺序不同→不同局部后果（复用 BlockCollapseSystem）。",
            },
        };

        public static DiscoveryNodeSpec Find(DiscoveryNodeType t)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i].type == t) return All[i];
            return null;
        }

        /// <summary>按区域返回允许生成的节点 spec 列表。</summary>
        public static DiscoveryNodeSpec[] ForRegion(string regionId)
        {
            int n = 0;
            for (int i = 0; i < All.Length; i++) if (All[i].allowedRegionId == regionId) n++;
            var arr = new DiscoveryNodeSpec[n];
            int k = 0;
            for (int i = 0; i < All.Length; i++) if (All[i].allowedRegionId == regionId) arr[k++] = All[i];
            return arr;
        }
    }
}
