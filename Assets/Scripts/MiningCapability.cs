using System;

namespace Ashfall
{
    /// <summary>
    /// DEV-012：最小正式采矿能力枚举（Issue §8）。
    /// 不做完整技能树；只建立 HotRock / 后续特殊块可查询的「能力语义」，
    /// 避免特殊块硬编码具体模块（如直接查 DrillCooling）。
    /// HardnessTier 由独立枚举承载（见 MiningHardness.cs），此处不含硬度。
    /// </summary>
    [Flags]
    public enum MiningCapability
    {
        None = 0,
        /// <summary>冷却能力：能否稳定连续处理 HotRock（V1 provider = DrillCooling 模块）。</summary>
        Cooling = 1 << 0,
        /// <summary>共振能力（预留，不实现）。</summary>
        Resonance = 1 << 1,
        /// <summary>导电能力（预留，不实现）。</summary>
        Conductive = 1 << 2,
        /// <summary>遗迹访问能力（预留，不实现）。</summary>
        RuinAccess = 1 << 3,
    }
}
