using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-008：一个「地下探索空间带」的配置。
    ///
    /// 把「这段深度里天然空腔/通道/岔路/小房间多密、多大」集中成一条数据，
    /// 与 OreDepthBand（矿脉带）/ TileDatabase.DepthLayer（地层）职责分离：
    ///  - DepthLayer：地层由什么填充（泥土/硬岩…），space 模式下只取 value==0 填充物；
    ///  - OreDepthBand：这段深度矿脉由哪种矿物构成；
    ///  - UndergroundSpaceBand：这段深度天然空间（Empty）的结构种类 / 尺寸 / 数量。
    ///
    /// 设计规则：
    ///  - minDepth/maxDepth 闭区间（网格 y，0=地表，越大越深）；
    ///  - 数量为「目标值」，生成时按独立 System.Random 在 0.5x~1.5x 扰动（确定性但避免僵硬）；
    ///  - 尺寸用范围随机；各 band 数值不同 → 构成随深度可观测的结构差异；
    ///  - 不写死「某深度只出某种空间」：每个 band 都会尝试 Pocket / Tunnel / Branch /
    ///    DeadEnd-SmallRoom，仅数量/尺寸随深度变化。
    /// </summary>
    [Serializable]
    public class UndergroundSpaceBand
    {
        [Tooltip("带名（日志/统计可读，如 Shallow / Mid / Deep）")]
        public string bandName = "Shallow";

        [Tooltip("带起始深度（格，0=地表）。闭区间")]
        public int minDepth;

        [Tooltip("带结束深度（格）。闭区间")]
        public int maxDepth;

        [Header("结构目标数量（每带独立；生成时按 rng 0.5x..1.5x 扰动）")]
        [Tooltip("天然空腔数量（Pocket：6~20 格，宽 3~7 / 高 2~5 的不规则空间）")]
        public int pocketTarget = 6;

        [Tooltip("短通道数量（Tunnel：长 4~12、宽 1 的连续走道）")]
        public int tunnelTarget = 4;

        [Tooltip("岔路数量（Branch：主通道 + 至少 1 条 2~4 格侧道，制造路线选择）")]
        public int branchTarget = 1;

        [Tooltip("小房间/死路数量（SmallRoom/DeadEnd：小空腔或 3+ 格短尽头的窄道）")]
        public int smallRoomTarget = 2;

        [Header("空腔尺寸（Pocket / SmallRoom）")]
        [Tooltip("Pocket 目标格数下限（低于下限判失败丢弃，避免椒盐单孔）")]
        public int pocketMinCells = 6;
        [Tooltip("Pocket 目标格数上限")]
        public int pocketMaxCells = 16;

        [Tooltip("Pocket 相对起点的最大横向半径（格）。控制宽约 3~7")]
        public int pocketWidthMax = 7;
        [Tooltip("Pocket 相对起点的最大纵向半径（格）。控制高约 2~5")]
        public int pocketHeightMax = 5;

        [Tooltip("SmallRoom/DeadEnd 格数下限")]
        public int smallRoomMinCells = 3;
        [Tooltip("SmallRoom/DeadEnd 格数上限")]
        public int smallRoomMaxCells = 5;

        [Header("通道尺寸（Tunnel / Branch）")]
        [Tooltip("Tunnel/Branch 主道长度下限")]
        public int tunnelMinLength = 4;
        [Tooltip("Tunnel/Branch 主道长度上限")]
        public int tunnelMaxLength = 8;

        [Tooltip("Branch 侧道长度范围")]
        public int branchSideMin = 2;
        public int branchSideMax = 4;

        [Tooltip("单结构建造成功率上限（防死循环；0 = 随带内空余自动增加上限）")]
        public int maxTriesPerStructure = 80;

        /// <summary>该带覆盖的深度跨度（格，含两端）。</summary>
        public int DepthSpan => Mathf.Max(1, maxDepth - minDepth + 1);
    }

    /// <summary>
    /// DEV-008：一条已生成的探索空间记录（供统计 / MCP 验收读取）。
    ///
    /// type ∈ Pocket（空腔）| Tunnel（短通道）| Branch（岔路）| DeadEnd | SmallRoom
    /// （DeadEnd = 短窄道自然终止；SmallRoom = 小空腔。两者合计构成 Issue 的
    /// 「Dead End / Small Room」桶）。
    /// </summary>
    [Serializable]
    public class UndergroundSpaceRecord
    {
        /// <summary>结构类型：Pocket / Tunnel / Branch / DeadEnd / SmallRoom。</summary>
        public string type;

        /// <summary>所属带名（UndergroundSpaceBand.bandName）。</summary>
        public string bandName;

        /// <summary>该空间包含的全部 Empty 格（生成即固定）。</summary>
        public List<Vector2Int> cells = new List<Vector2Int>();

        public Vector2Int bboxMin;
        public Vector2Int bboxMax;

        /// <summary>空间尺寸（格）。</summary>
        public int Size => cells.Count;

        /// <summary>是否 8 邻连通：从首格 BFS 可达全部格（同结构生长必然满足，供验收断言）。</summary>
        public bool IsConnected8
        {
            get
            {
                if (cells.Count <= 1) return true;
                var set = new HashSet<Vector2Int>(cells);
                var seen = new HashSet<Vector2Int>();
                var q = new Queue<Vector2Int>();
                q.Enqueue(cells[0]);
                seen.Add(cells[0]);
                while (q.Count > 0)
                {
                    var c = q.Dequeue();
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            var n = new Vector2Int(c.x + dx, c.y + dy);
                            if (set.Contains(n) && seen.Add(n)) q.Enqueue(n);
                        }
                }
                return seen.Count == cells.Count;
            }
        }

        /// <summary>是否 4 邻连通（上下左右）。供更严格的连通性验收。</summary>
        public bool IsConnected4
        {
            get
            {
                if (cells.Count <= 1) return true;
                var set = new HashSet<Vector2Int>(cells);
                var seen = new HashSet<Vector2Int>();
                var q = new Queue<Vector2Int>();
                q.Enqueue(cells[0]);
                seen.Add(cells[0]);
                while (q.Count > 0)
                {
                    var c = q.Dequeue();
                    for (int i = 0; i < 4; i++)
                    {
                        var n = new Vector2Int(c.x + (i == 0 ? 1 : (i == 1 ? -1 : 0)),
                                               c.y + (i == 2 ? 1 : (i == 3 ? -1 : 0)));
                        if (set.Contains(n) && seen.Add(n)) q.Enqueue(n);
                    }
                }
                return seen.Count == cells.Count;
            }
        }
    }
}
