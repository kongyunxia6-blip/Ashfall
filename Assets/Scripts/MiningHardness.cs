using System;

namespace Ashfall
{
    /// <summary>
    /// DEV-012：硬度分阶（Issue §5 hardnessTier 1~6）。
    /// 与长期六级地层规格一一对应：1=土壤/碎岩 … 6=异常/遗迹层。
    /// 当前运行时测试世界只用 1..3（Shallow/Mid/Deep 代表层），4..6 为长期规格预留。
    /// 硬度语义由 StratumCatalog / MiningCapabilityResolver 集中处理，不散落 magic number。
    /// </summary>
    public enum HardnessTier
    {
        Tier1 = 1,   // 土壤 / 碎岩层 SoilRubble
        Tier2 = 2,   // 普通岩层 NormalRock
        Tier3 = 3,   // 致密岩层 DenseRock
        Tier4 = 4,   // 花岗 / 玄武岩层 GraniteBasalt
        Tier5 = 5,   // 晶化岩层 CrystallizedRock
        Tier6 = 6,   // 异常地层 / 遗迹层 AnomalousRuinLayer
    }

    public static class HardnessTierExt
    {
        /// <summary>稳定 short 名（供 HUD / 数据表 / 验收输出）。</summary>
        public static string ShortName(this HardnessTier t)
        {
            int n = (int)t;
            return n >= 1 && n <= 6 ? new string('★', n) : "?";
        }
    }
}
