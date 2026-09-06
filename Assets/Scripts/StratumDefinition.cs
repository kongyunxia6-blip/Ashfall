using System;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-012：地层（Stratum）语义定义（Issue §5）。
    ///
    /// 职责：表达「这里是什么岩层 / 有多硬 / 通常出什么矿 / 工具是否适合」。
    /// 与 DepthRegion（Region，当前运行时深度带）是两个独立概念：
    ///  - Region（DepthRegionLayout/Progression）= 当前运行时 CurrentDepth / MaxDepth / first-enter；
    ///  - Stratum（本类）= Block 的地质属性 / 硬度 / 允许矿物语义。
    /// 本 DEV 不建第二套 CurrentDepth，也不把硬度/地层数字散落到 DigGrid / HUD / OreGenerator。
    ///
    /// V1 采用纯 C# 数据类 + StratumCatalog 静态登记（不做 ScriptableObject），
    /// 结构可扩展、可序列化、可测试（Issue §5 允许 V1 不过度 SO 化）。
    /// </summary>
    [Serializable]
    public class StratumDefinition
    {
        /// <summary>稳定 id（如 NormalRock / DenseRock / GraniteBasalt）。</summary>
        public string stableId = "NormalRock";

        /// <summary>玩家可见名称（如 普通岩层）。</summary>
        public string displayName = "普通岩层";

        /// <summary>
        /// 长期世界 minDepth（m；数据规范，非运行时 CurrentDepth）。
        /// 边界约定：[minDepth, maxDepth) 左闭右开。maxDepth 取 open-top sentinel（见 <see cref="StratumCatalog.OpenTopDepth"/>）
        /// 表示「无明确上界」（最深开放层）。
        /// </summary>
        public int longTermMinDepth;

        /// <summary>长期世界 maxDepth（m；左闭右开上界；OpenTopDepth 表示开放上界）。</summary>
        public int longTermMaxDepth;

        /// <summary>硬度分阶 1~6。</summary>
        public HardnessTier hardnessTier = HardnessTier.Tier1;

        /// <summary>基准填充物（value==0 的纯地层块）。当前实际由 TileDatabase 分层决定；此处登记语义参考。</summary>
        public TileDefinition baseTile;

        /// <summary>工具是否适合（★..★★★✓ / ★★★★✕… 的语义，仅登记不做强制）。</summary>
        public string toolSuitability = "✓ 很快";

        /// <summary>一句话描述（日志 / 未来图鉴）。</summary>
        public string shortDescription = "";

        /// <summary>可出现的矿 stableId 列表（weight 语义由 OreCatalog 跨地层权重表达）。V1 测试只启代表矿。</summary>
        public string[] allowedOreIds = Array.Empty<string>();

        public override string ToString() => $"{stableId}({hardnessTier.ShortName()})";
    }

    /// <summary>
    /// DEV-012：地层定义的唯一集中登记处。
    /// 六级长期地层 stable id（Issue §2/§3A）都在这里登记；代表层（测试世界实际启用）
    /// 由 NormalRock/DenseRock/GraniteBasalt 承担。查询走静态只读表，不每帧扫描。
    /// </summary>
    public static class StratumCatalog
    {
        // ---- 六级长期地层 stable id（Issued §2） ----
        public const string SoilRubble = "SoilRubble";               // ★
        public const string NormalRock = "NormalRock";               // ★★
        public const string DenseRock = "DenseRock";                 // ★★★
        public const string GraniteBasalt = "GraniteBasalt";         // ★★★★
        public const string CrystallizedRock = "CrystallizedRock";   // ★★★★★
        public const string AnomalousRuinLayer = "AnomalousRuinLayer";// ★★★★★★

        /// <summary>
        /// 开放上界 sentinel（m）。最深层（AnomalousRuinLayer）用此表示「无明确上界」
        /// （正式规格 1000m+）。任何 <see cref="longTermMaxDepth"/> == 本值的层视为最深开放层。
        /// </summary>
        public const int OpenTopDepth = int.MaxValue;

        // ---- 代表层定义（V1 测试世界实际用） ----
        public static readonly StratumDefinition Normal = new StratumDefinition
        {
            stableId = NormalRock, displayName = "普通岩层",
            longTermMinDepth = 100, longTermMaxDepth = 250,
            hardnessTier = HardnessTier.Tier2,
            toolSuitability = "✓ 较慢",
            shortDescription = "浅层代表地层；普通采矿手感，铁/铜常见。",
        };

        public static readonly StratumDefinition Dense = new StratumDefinition
        {
            stableId = DenseRock, displayName = "致密岩层",
            longTermMinDepth = 250, longTermMaxDepth = 450,
            hardnessTier = HardnessTier.Tier3,
            toolSuitability = "△ 很慢",
            shortDescription = "中层代表地层；第一次装备门槛，银/金可能。",
        };

        public static readonly StratumDefinition Granite = new StratumDefinition
        {
            stableId = GraniteBasalt, displayName = "花岗 / 玄武岩层",
            longTermMinDepth = 450, longTermMaxDepth = 700,
            hardnessTier = HardnessTier.Tier4,
            toolSuitability = "✕（需更高能力）",
            shortDescription = "深层代表地层；金/铂/宝石，HotRock 出现。",
        };

        // ---- 长期规格登记（V1 不铺真实 1000m+ 世界，但深度边界为正式可查询数据） ----
        public static readonly StratumDefinition Soil = new StratumDefinition
        {
            stableId = SoilRubble, displayName = "土壤 / 碎岩层",
            longTermMinDepth = 0, longTermMaxDepth = 100,
            hardnessTier = HardnessTier.Tier1, toolSuitability = "✓ 很快",
            shortDescription = "教学层（0-100m）。",
        };

        public static readonly StratumDefinition Crystallized = new StratumDefinition
        {
            stableId = CrystallizedRock, displayName = "晶化岩层",
            longTermMinDepth = 700, longTermMaxDepth = 1000,
            hardnessTier = HardnessTier.Tier5, toolSuitability = "深层（700-1000m）。",
            shortDescription = "晶化岩层；宝石与能源晶体。",
        };

        public static readonly StratumDefinition AnomalousRuin = new StratumDefinition
        {
            stableId = AnomalousRuinLayer, displayName = "异常地层 / 遗迹层",
            longTermMinDepth = 1000, longTermMaxDepth = OpenTopDepth,
            hardnessTier = HardnessTier.Tier6, toolSuitability = "需特殊工具",
            shortDescription = "异常矿 / 古代合金（DEV-013 复用点）。",
        };

        /// <summary>全部地层（代表层在前，长期规格在后）。</summary>
        public static readonly StratumDefinition[] All =
            { Normal, Dense, Granite, Soil, Crystallized, AnomalousRuin };

        /// <summary>
        /// 按 longTermMinDepth 升序排序的全部地层（深度相邻无重叠的依据，供数据完整性回归断言）。
        /// 静态惰性构建一次；查询不每帧扫描。
        /// </summary>
        public static readonly StratumDefinition[] AllByDepthAsc = BuildAllByDepthAsc();

        static StratumDefinition[] BuildAllByDepthAsc()
        {
            var copy = new StratumDefinition[All.Length];
            System.Array.Copy(All, copy, All.Length);
            System.Array.Sort(copy, (a, b) => a.longTermMinDepth.CompareTo(b.longTermMinDepth));
            return copy;
        }

        /// <summary>深度数据是否相邻无重叠、单调（每层 maxDepth == 下一层 minDepth；最深开放层除外）。</summary>
        public static bool IsDepthDataContiguous()
        {
            var asc = AllByDepthAsc;
            for (int i = 0; i < asc.Length; i++)
            {
                var cur = asc[i];
                if (cur.longTermMinDepth > cur.longTermMaxDepth) return false;           // 区间非法
                if (i + 1 < asc.Length)
                {
                    var next = asc[i + 1];
                    if (cur.longTermMaxDepth != next.longTermMinDepth) return false;     // 必须无缝衔接
                }
                else if (cur.longTermMaxDepth != OpenTopDepth) return false;             // 最深层必须开放上界
            }
            return true;
        }

        /// <summary>给定深度（m）落在哪个地层（左闭右开；开放上界层吞并 [OpenTopDepth,∞)）。未匹配返回 null。</summary>
        public static StratumDefinition ForDepth(int depthMeters)
        {
            var asc = AllByDepthAsc;
            for (int i = 0; i < asc.Length; i++)
            {
                var s = asc[i];
                if (depthMeters >= s.longTermMinDepth && depthMeters < s.longTermMaxDepth)
                    return s;
            }
            return null;
        }

        /// <summary>按 stableId 查地层；未找到返回 null。</summary>
        public static StratumDefinition Find(string stableId)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i].stableId == stableId) return All[i];
            return null;
        }

        /// <summary>按硬度分阶查地层；V1 返回首个该硬度的代表层（同 tier 多者返回第一个登记）。</summary>
        public static StratumDefinition FindByHardness(HardnessTier tier)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i].hardnessTier == tier) return All[i];
            return null;
        }

        /// <summary>测试世界用代表地层——按 Region 取推荐地层（Issue §11 测试映射）。</summary>
        public static StratumDefinition ForRegion(DepthRegionDefinition region)
        {
            if (region == null) return Normal;
            switch (region.regionId)
            {
                case "Shallow": return Normal;              // Shallow → NormalRock
                case "Mid":     return Dense;               // Mid → DenseRock
                case "Deep":    return Granite;             // Deep → GraniteBasalt
                default:        return Normal;              // Surface 归普通（非地下，无地层语义）
            }
        }
    }
}
