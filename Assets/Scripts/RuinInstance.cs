using System.Collections.Generic;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-013：一座已生成遗迹的运行时只读布局（Issue §11 RuinInstance）。
    ///
    /// 由 RuinGenerator 在 ApplyToGrid 时以固定 seed 确定性产出；记录：
    ///  - definition（RuinDefinition 数据引用）；
    ///  - stable instance id（确定性）；
    ///  - 房间外接矩形 bounds（DigGrid 网格坐标，含墙）；
    ///  - 入口 RuinSeal 格、核心 AncientRelayCore 格、奖励节点格；
    ///  - 房间内腔（可通行 empty 格）。
    ///
    /// 本类是**纯数据**，不含发现/调查状态——discovered/investigated 由 RuinDiscoveryService
    /// （运行时）单独承载，避免把 Run/世界状态塞进生成器输出。
    /// </summary>
    public class RuinInstance
    {
        /// <summary>生成时引用的 RuinDefinition。</summary>
        public RuinDefinition definition;

        /// <summary>确定性实例 id（生成 seed + 布局锚点派生）。</summary>
        public string instanceId = "";

        /// <summary>房间外接矩形（DigGrid 网格坐标，包含四周墙；RectInt.xMin/yMin/xMax/yMax）。</summary>
        public RectInt bounds;

        /// <summary>入口 RuinSeal 所在格（DigGrid 网格坐标）。</summary>
        public Vector2Int entranceCell;

        /// <summary>房间内 AncientRelayCore 所在格。</summary>
        public Vector2Int coreCell;

        /// <summary>房间内奖励节点格（ancient_alloy / anomalous_crystal 等可挖矿）。</summary>
        public readonly List<Vector2Int> rewardCells = new List<Vector2Int>();

        /// <summary>房间内可通行（空心）格集合，供连通性/可达性断言。</summary>
        public readonly List<Vector2Int> interiorCells = new List<Vector2Int>();

        /// <summary>生成该遗迹所用的 seed（确定性验证用）。</summary>
        public int usedSeed;

        /// <summary>房间宽（格）。</summary>
        public int Width => bounds.width;

        /// <summary>房间高（格）。</summary>
        public int Height => bounds.height;

        public override string ToString()
        {
            string name = definition != null ? definition.stableId : "?";
            return $"{instanceId}@{name} rect=({bounds.x},{bounds.y},{bounds.width},{bounds.height}) " +
                   $"entrance={entranceCell} core={coreCell} rewards={rewardCells.Count}";
        }
    }
}
