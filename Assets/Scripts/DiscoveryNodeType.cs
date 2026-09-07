namespace Ashfall
{
    /// <summary>
    /// DEV-014：地下发现节点类型（Issue #30 §Scope A~D）。
    ///
    /// 只做「类型标记 + 显示」，不放任何玩法行为分支。每个类型如何组合现有系统、
    /// 落什么 tile、给什么奖励，全部由 DiscoveryNodeCatalog 的 DiscoveryNodeSpec 描述，
    /// 由 DiscoveryNodeGenerator 统一消费——本枚举绝不新增第二套 Grid/HP/奖励系统。
    /// </summary>
    public enum DiscoveryNodeType
    {
        /// <summary>A. 废弃采矿点：人工支撑 + 少量高值矿 + SupportRock 局部坍塌风险。</summary>
        AbandonedMiningPocket = 0,

        /// <summary>B. 热裂隙室：HotRock 热走廊，危险之后/内部放高值矿，Cooling 能力产生价值。</summary>
        ThermalVentChamber = 1,

        /// <summary>C. 文明信号缓存点：极小 RuinSeal 门 + 背后文明奖励格（不复制完整 AncientRelayRoom）。</summary>
        AncientSignalCache = 2,

        /// <summary>D. 坍塌资源囊：SupportRock/LooseRock/奖励矿脉形成可观察结构，挖掘顺序影响局部结果。</summary>
        CollapsedResourcePocket = 3,
    }

    /// <summary>DEV-014：发现节点类型的只读辅助（显示名 / 稳定 id / 简述）。</summary>
    public static class DiscoveryNodeTypeInfo
    {
        public const string AbandonedMiningPocket = "AbandonedMiningPocket";
        public const string ThermalVentChamber = "ThermalVentChamber";
        public const string AncientSignalCache = "AncientSignalCache";
        public const string CollapsedResourcePocket = "CollapsedResourcePocket";

        public static string StableId(DiscoveryNodeType t)
        {
            switch (t)
            {
                case DiscoveryNodeType.AbandonedMiningPocket: return AbandonedMiningPocket;
                case DiscoveryNodeType.ThermalVentChamber:    return ThermalVentChamber;
                case DiscoveryNodeType.AncientSignalCache:    return AncientSignalCache;
                case DiscoveryNodeType.CollapsedResourcePocket: return CollapsedResourcePocket;
                default: return "Unknown";
            }
        }

        public static string DisplayName(DiscoveryNodeType t)
        {
            switch (t)
            {
                case DiscoveryNodeType.AbandonedMiningPocket: return "废弃采矿点";
                case DiscoveryNodeType.ThermalVentChamber:    return "热裂隙室";
                case DiscoveryNodeType.AncientSignalCache:    return "文明信号缓存点";
                case DiscoveryNodeType.CollapsedResourcePocket: return "坍塌资源囊";
                default: return "未知节点";
            }
        }

        public static string ScanHint(DiscoveryNodeType t)
        {
            switch (t)
            {
                case DiscoveryNodeType.AbandonedMiningPocket: return "侦测到废弃人工结构的微弱回波";
                case DiscoveryNodeType.ThermalVentChamber:    return "侦测到局部的热异常（thermal anomaly）";
                case DiscoveryNodeType.AncientSignalCache:    return "侦测到弱古代文明信号";
                case DiscoveryNodeType.CollapsedResourcePocket: return "侦测到不稳定的空腔信号";
                default: return "";
            }
        }
    }
}
