using System;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-009：一个「深度区域（Region）」的语义定义。
    ///
    /// 这是 Shallow / Mid / Deep（以及地表 Surface）边界的唯一来源：
    ///  - OreDepthBand / UndergroundSpaceBand 的构建（Builder）从这里取边界，不再各自维护常数；
    ///  - DepthRegionProgression 运行时只查询这里的定义；
    ///  - 任何脚本不得再散落 21/22/43/44/62 这类 magic number。
    ///
    /// 边界语义：minDepth/maxDepth 为网格 y 闭区间（0 = 地表，越大越深），与
    /// OreDepthBand / UndergroundSpaceBand 的闭区间约定一致。
    ///
    /// V1 采用集中静态常量（可序列化类 + 静态集合），不做 ScriptableObject：
    /// Issue 允许「参数集中、可序列化、可测试」即可，避免过早资产化。
    /// </summary>
    [Serializable]
    public class DepthRegionDefinition
    {
        [Tooltip("区域标识（Surface/Shallow/Mid/Deep）。日志与统计用")]
        public string regionId = "Shallow";

        [Tooltip("玩家可见名称（HUD 显示，如 浅层 / 中层 / 深层）")]
        public string displayName = "浅层";

        [Tooltip("排序（0=地表，向下递增）")]
        public int order;

        [Tooltip("区域起始深度（网格 y，闭区间）")]
        public int minDepth;

        [Tooltip("区域结束深度（网格 y，闭区间）")]
        public int maxDepth;

        [Tooltip("一句话描述（日志/未来图鉴用）")]
        public string shortDescription = "";

        [Tooltip("区域深度标签（HUD/提示可选，如 浅层地带）")]
        public string depthLabel = "";

        [Tooltip("UndergroundSpaceBand 的起始深度（闭区间）。多数区域 == minDepth；Shallow 因紧贴地表带/保留区从 y5 起（DEV-008 既有实现细节）。Ore band 恒用 minDepth。")]
        public int spaceStartY;

        /// <summary>该区域覆盖的深度跨度（格，含两端）。</summary>
        public int DepthSpan => Mathf.Max(1, maxDepth - minDepth + 1);

        public override string ToString() => $"{regionId}[{minDepth}..{maxDepth}]";
    }

    /// <summary>
    /// DEV-009：区域边界的【唯一集中来源】。
    ///
    /// 全部区域边界数字只出现在这里：
    ///  - Surface（地表带/Hub）：y 0..2 —— groundRow=2 为玩家站立的地面行；
    ///  - Shallow：y 3..21（Ore 带与空间带起点差异见 spaceStartY）；
    ///  - Mid：y 22..43；
    ///  - Deep：y 44..62。
    ///
    /// Ore band 边界 = [region.minDepth, region.maxDepth]（Shallow 3..21 / Mid 22..43 / Deep 44..62），
    /// 与 DEV-007 OreVeinV1Builder 原常量完全一致。
    /// Space band 边界 = [region.spaceStartY, region.maxDepth]（Shallow 5..21 / Mid 22..43 / Deep 44..62），
    /// 与 DEV-008 UndergroundSpaceV1Builder 原常量完全一致。
    ///
    /// 这样 DEV-007（矿脉）/ DEV-008（空间）/ DEV-009（区域语义）三套「带」共享同一组边界数字，
    /// 不再存在彼此不一致的三套 magic number。所有旧 Builder 必须引用本类，禁止再写裸数字。
    /// </summary>
    public static class DepthRegionLayout
    {
        /// <summary>玩家站立的地面行（网格 y）。DisplayDepth 在此行及以上显示 0m。</summary>
        public const int GroundRow = 2;

        public static readonly DepthRegionDefinition Surface = new DepthRegionDefinition
        {
            regionId = "Surface", displayName = "地表", order = 0,
            minDepth = 0, maxDepth = GroundRow,
            shortDescription = "地表据点与下矿坑口",
            depthLabel = "地表 Surface",
        };

        public static readonly DepthRegionDefinition Shallow = new DepthRegionDefinition
        {
            regionId = "Shallow", displayName = "浅层", order = 1,
            minDepth = 3, maxDepth = 21,
            shortDescription = "首次下矿区域：空间少而小，铁/锡常见",
            depthLabel = "浅层地带 Shallow",
            // 空间带从 y5 起（避免紧贴地表带/保留区的生成细节，DEV-008 原实现允许）
            spaceStartY = 5,
        };

        public static readonly DepthRegionDefinition Mid = new DepthRegionDefinition
        {
            regionId = "Mid", displayName = "中层", order = 2,
            minDepth = 22, maxDepth = 43,
            shortDescription = "空间频率与岔路增加，铜权重提高，路线选择明显",
            depthLabel = "中层地带 Mid",
            spaceStartY = 22,
        };

        public static readonly DepthRegionDefinition Deep = new DepthRegionDefinition
        {
            regionId = "Deep", displayName = "深层", order = 3,
            minDepth = 44, maxDepth = 62,
            shortDescription = "结构更复杂，稀有矿更可能出现，资源价值感上升",
            depthLabel = "深层地带 Deep",
            spaceStartY = 44,
        };

        /// <summary>全部区域（按 order 升序）。Ore/Space band 数组只取 order ≥ 1 的三条。</summary>
        public static readonly DepthRegionDefinition[] All =
            { Surface, Shallow, Mid, Deep };

        /// <summary>带矿脉/空间的三条地下区域（Shallow/Mid/Deep）。</summary>
        public static readonly DepthRegionDefinition[] Underground =
            { Shallow, Mid, Deep };

        /// <summary>
        /// 按网格深度 y 查区域。边界安全：y 落在区间内即命中；超过最浅/最深时 clamp 到两端，
        /// 不抛异常（OOB 安全）。
        /// </summary>
        public static DepthRegionDefinition FindByGridY(int gridY)
        {
            for (int i = 0; i < All.Length; i++)
                if (gridY >= All[i].minDepth && gridY <= All[i].maxDepth)
                    return All[i];
            return gridY < All[0].minDepth ? All[0] : All[All.Length - 1];
        }

        /// <summary>按 order 取区域（越界 clamp 到两端）。</summary>
        public static DepthRegionDefinition ByOrder(int order)
        {
            int i = Mathf.Clamp(order, 0, All.Length - 1);
            return All[i];
        }

        /// <summary>玩家可见深度：地面行及以上显示 0，向下 = 网格深度（1 Block = 1m）。</summary>
        public static int DisplayDepthOf(int gridY)
            => gridY <= GroundRow ? 0 : gridY;
    }
}
