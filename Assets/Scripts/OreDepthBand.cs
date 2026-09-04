using System;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-007：一个深度资源带（Depth Band）的配置。
    ///
    /// 把「某段深度里主要出什么矿、矿脉多大、多密」集中成一条数据，而不是散落在生成代码里。
    /// 与 TileDatabase.DepthLayer（地层本身）职责分离：
    ///  - DepthLayer：这段深度里地层由什么填充（泥土/硬岩…），矿脉模式下只取 value==0 的填充物；
    ///  - OreDepthBand：这段深度里【矿脉】由哪种矿物构成（Iron/Tin/Copper…），与矿脉尺寸/频率。
    ///
    /// 设计规则：
    ///  - minDepth/maxDepth 都是网格深度（0 = 地表，越大越深），区间为闭区间 [minDepth, maxDepth]；
    ///  - ores/weights 一一对应（权重可随便填比例，不要求归一化）；
    ///  - 同一个矿物可以出现在多个 band，但各 band 权重不同 → 构成随深度可观测的变化；
    ///  - 不写「某深度以下只出某一种矿」的死表：每个 band 至少 2 种矿（builder/Inspector 保证）。
    /// </summary>
    [Serializable]
    public class OreDepthBand
    {
        [Tooltip("带名（仅用于日志/统计可读性，如 Shallow / Mid / Deep）")]
        public string bandName = "Shallow";

        [Tooltip("带起始深度（格，0 = 地表）。闭区间")]
        public int minDepth;

        [Tooltip("带结束深度（格）。闭区间")]
        public int maxDepth;

        [Tooltip("该带可能出现的矿物（建议至少 2 种，避免死表）")]
        public TileDefinition[] ores;

        [Tooltip("与 ores 一一对应的权重（不需要归一化）")]
        public float[] weights;

        [Header("矿脉尺寸与频率")]
        [Tooltip("矿脉最小尺寸（格）。V1 建议小脉 2、中脉 4、大脉 6 起")]
        [Min(1)] public int veinMinSize = 2;

        [Tooltip("矿脉最大尺寸（格）。V1 上限 8，避免巨型矿团")]
        [Min(1)] public int veinMaxSize = 5;

        [Tooltip("矿脉频率：每 1 格深度期望尝试生成几条矿脉起点（约数）。0.5 ≈ 每 2 格深度 1 条")]
        [Min(0f)] public float veinFrequency = 0.4f;

        /// <summary>该带覆盖的深度跨度（格数，含两端）</summary>
        public int DepthSpan => Mathf.Max(1, maxDepth - minDepth + 1);
    }

    /// <summary>
    /// DEV-007：矿脉生成保留区（矩形，网格坐标）。
    /// 矿脉起点与生长都不允许落入任何保留区。集中配置，不散落 magic number。
    /// 用途：地表 Hub 带、出生点、下矿竖井通道、测试场景手工布局、明确不可覆盖 cell。
    /// </summary>
    [Serializable]
    public class OreReservedRect
    {
        public string label = "";

        [Tooltip("保留区左上角网格 x")]
        public int x;

        [Tooltip("保留区左上角网格 y（0 = 地表）")]
        public int y;

        [Tooltip("保留区宽度（格）")]
        public int w = 1;

        [Tooltip("保留区高度（格）")]
        public int h = 1;

        /// <summary>该矩形是否包含指定网格格（含边界）。</summary>
        public bool Contains(int cx, int cy)
            => cx >= x && cx < x + w && cy >= y && cy < y + h;

        public override string ToString()
            => $"{label ?? ""}[(x={x},y={y}) {w}x{h}]";
    }
}
