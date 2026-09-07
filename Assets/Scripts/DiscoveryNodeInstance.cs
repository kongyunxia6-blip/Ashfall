using System.Collections.Generic;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-014：一个已生成发现节点的运行时只读布局（Issue #30 §Scope）。
    ///
    /// 由 DiscoveryNodeGenerator 在 ApplyToGrid 时以固定 seed 确定性产出；记录：
    ///  - type（DiscoveryNodeType）；
    ///  - instanceId（确定性：seed + 类型 + 锚点）；
    ///  - bounds（footprint 外接矩形）；
    ///  - 关键格：入口/门格、奖励格、风险格（承重/高温/密封），供 Scanner 只读 + 验收断言。
    ///
    /// 纯数据：不含 discovered / 发放状态。奖励发放 = 玩家实际挖到该格后经既有
    /// DrillVehicle.HandleTileDug → InventoryGrid.AddItem 入 Cargo（节点不持有独立奖励状态）。
    /// </summary>
    public class DiscoveryNodeInstance
    {
        /// <summary>节点类型。</summary>
        public DiscoveryNodeType type;

        /// <summary>确定性实例 id。</summary>
        public string instanceId = "";

        /// <summary>footprint 外接矩形（DigGrid 网格坐标）。</summary>
        public RectInt bounds;

        /// <summary>节点内「门 / 入口」格（C 类 RuinSeal；其余类型 = Vector2Int(-1,-1)）。</summary>
        public Vector2Int sealCell;

        /// <summary>节点内奖励矿格（可挖入 Cargo 的 tile）。</summary>
        public readonly List<Vector2Int> rewardCells = new List<Vector2Int>();

        /// <summary>节点内风险/特殊格（SupportRock / HotRock / LooseRock 等）。</summary>
        public readonly List<Vector2Int> riskCells = new List<Vector2Int>();

        /// <summary>节点覆盖的全部格子（footprint 内本次写入的格）。</summary>
        public readonly List<Vector2Int> cells = new List<Vector2Int>();

        /// <summary>生成所用 seed。</summary>
        public int usedSeed;

        public string StableId => DiscoveryNodeTypeInfo.StableId(type);
        public string DisplayName => DiscoveryNodeTypeInfo.DisplayName(type);

        public override string ToString()
            => $"{instanceId}@{DisplayName} rect=({bounds.x},{bounds.y},{bounds.width},{bounds.height}) " +
               $"seal={sealCell} rewards={rewardCells.Count} risk={riskCells.Count}";
    }
}
