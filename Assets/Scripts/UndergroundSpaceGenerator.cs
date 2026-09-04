using System.Collections.Generic;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-008：地下探索空间生成器 —— 把「一整块等待挖开的规则网格」升级为
    /// 具有天然空腔 / 短通道 / 岔路 / 死路小房间的可探索地下空间。
    ///
    /// 职责（只做 underground space pass，不碰地层/不碰矿脉/不碰挖掘）：
    ///  - 在 DigGrid 基础地层生成后、OreVeinGenerator 之前执行（Base Strata → Space Pass
    ///    → Ore Vein Pass）：先挖出连贯空间，矿脉再基于剩余实心地层找墙体落点；
    ///  - 四种可读结构（Issue §2）：Pocket 空腔 / Tunnel 短通道 / Branch 岔路 /
    ///    DeadEnd-SmallRoom 死路·小房间，按 UndergroundSpaceBand 数据驱动分带配置；
    ///  - 结构内自己连通、结构间彼此隔离（8 邻至少隔 1 格 Solid），无椒盐单孔；
    ///  - 每格写空走 DigGrid.SetTile（同步耐久 + 刷新 tilemap），DigGrid 仍是唯一真相源；
    ///  - 独立、确定性的 System.Random(EffectiveSeed)，不依赖 Unity 全局随机；
    ///  - 未达标（单格/欠尺寸）的结构整体回滚：绝不留椒盐单孔 / 噪点空腔。
    ///
    /// 挂载：DigGrid 同一物体（或任意物体，把 grid 拖入）。
    /// 由 DigGrid.Generate 在基础地层 + 耐久初始化之后、矿脉 pass 之前自动调用 ApplyToGrid。
    /// </summary>
    public class UndergroundSpaceGenerator : MonoBehaviour
    {
        [Tooltip("要生成的 DigGrid（默认取同一物体上的）。地层必须已生成，本组件只做 space pass")]
        public DigGrid grid;

        [Header("空间带（数据驱动，集中配置，勿散落 magic number）")]
        [Tooltip("按深度从小到大排列的空间带。每条带独立结构数量 / 尺寸 / 宽度半径")]
        public UndergroundSpaceBand[] bands;

        [Header("保留区（空间禁止进入）")]
        [Tooltip("矩形保留区（网格坐标）。与 OreVeinGenerator.reservedRects 共用 OreReservedRect，" +
                 "避免第二套 magic rectangle 系统。地表 Hub / 下矿口上方 / 测试区都应加进来")]
        public OreReservedRect[] reservedRects;

        [Header("随机")]
        [Tooltip("空间随机种子。-1 = 跟随 grid.seed（推荐：同一 grid seed 下地层+空间+矿脉整体可复现）")]
        public int seedOverride = -1;

        // ---------- 生成结果（只读，供统计 / MCP 验收） ----------

        /// <summary>本次生成的全部空间记录（ApplyToGrid 每次重建）。</summary>
        public List<UndergroundSpaceRecord> Spaces { get; } = new List<UndergroundSpaceRecord>();

        /// <summary>本次生成挖出的自然 Empty 总格数（= Σ Spaces.Size）。</summary>
        public int TotalEmptyCells { get; private set; }

        /// <summary>因欠尺寸 / 起点无法孤立而被整体丢弃的结构数（Rollback 回填，零残留）。</summary>
        public int Discarded { get; private set; }

        /// <summary>实际生效的种子。</summary>
        public int EffectiveSeed => seedOverride >= 0
            ? seedOverride
            : (grid != null ? grid.seed : 0);

        /// <summary>是否已配置至少一个有效带。</summary>
        public bool HasBands
        {
            get
            {
                if (bands == null) return false;
                foreach (var b in bands)
                    if (b != null && b.maxDepth >= b.minDepth && b.minDepth >= 1) return true;
                return false;
            }
        }

        // ---------- 内部状态（ApplyToGrid 每次重建） ----------
        DigGrid target;
        TileDefinition empty;
        System.Random rng;
        readonly HashSet<Vector2Int> allCarved = new HashSet<Vector2Int>();
        readonly List<Vector2Int> workCells = new List<Vector2Int>();
        readonly HashSet<Vector2Int> workSet = new HashSet<Vector2Int>();

        // 类型常量
        const string TPocket = "Pocket";
        const string TTunnel = "Tunnel";
        const string TBranch = "Branch";
        const string TDeadEnd = "DeadEnd";
        const string TSmallRoom = "SmallRoom";

        // ---------- space pass ----------

        /// <summary>
        /// 在已生成基础地层的 DigGrid 上执行空间 pass。
        /// DigGrid.Generate 会自动调用（本组件挂在 DigGrid 上时）；测试脚本也可手动调（幂等：每次重建结果）。
        /// </summary>
        public void ApplyToGrid(DigGrid gridTarget)
        {
            grid = gridTarget != null ? gridTarget : grid;
            if (grid == null || grid.database == null)
            {
                Debug.LogWarning("[DEV-008] UndergroundSpaceGenerator.ApplyToGrid: 缺少 DigGrid 或 database，跳过空间生成");
                return;
            }

            Spaces.Clear();
            TotalEmptyCells = 0;
            Discarded = 0;
            if (!HasBands) return;

            target = grid;
            empty = grid.database.emptyTile;
            rng = new System.Random(EffectiveSeed);
            allCarved.Clear();
            workCells.Clear();
            workSet.Clear();

            // 按深度从小到大逐带生成（确定性顺序消费 rng）
            for (int b = 0; b < bands.Length; b++)
            {
                var band = bands[b];
                if (band == null || band.maxDepth < band.minDepth || band.minDepth < 1) continue;

                // 目标数量按 rng 0.5x..1.5x 扰动（确定性扰动，避免每带僵硬同数）
                int pockets = Perturb(band.pocketTarget);
                int tunnels = Perturb(band.tunnelTarget);
                int branches = Perturb(band.branchTarget);
                int smalls = Perturb(band.smallRoomTarget);
                int triesPer = band.maxTriesPerStructure > 0 ? band.maxTriesPerStructure : 80;

                int made = 0;
                for (int t = 0; t < triesPer && made < pockets; t++)
                    if (BuildPocket(band)) made++;

                made = 0;
                for (int t = 0; t < triesPer && made < tunnels; t++)
                    if (BuildCorridor(band, false)) made++;

                made = 0;
                for (int t = 0; t < triesPer && made < branches; t++)
                    if (BuildCorridor(band, true)) made++;

                made = 0;
                for (int t = 0; t < triesPer && made < smalls; t++)
                    if (BuildSmallRoom(band)) made++;
            }
        }

        int Perturb(int v)
        {
            if (v <= 0) return 0;
            double f = 0.5 + rng.NextDouble();        // 0.5 .. 1.5
            int n = Mathf.RoundToInt((float)(v * f));
            return Mathf.Max(0, n);
        }

        // ---------- 结构建造 ----------

        /// <summary>空腔：随机起点 + 8 邻生长到目标尺寸，宽/高受起点半径约束 → 不规则团块。</summary>
        bool BuildPocket(UndergroundSpaceBand band)
        {
            if (!TryPickStart(band, out var start)) return false;

            int lo = Mathf.Max(3, band.pocketMinCells);
            int hi = Mathf.Max(lo, band.pocketMaxCells);
            int sizeTarget = rng.Next(lo, hi + 1);
            int halfW = Mathf.Max(1, band.pocketWidthMax / 2);
            int halfH = Mathf.Max(1, band.pocketHeightMax / 2);

            workCells.Clear();
            workSet.Clear();
            AddCell(start.x, start.y);
            int stall = 0;
            int guard2 = Mathf.Max(64, sizeTarget * 24);
            while (workCells.Count < sizeTarget && guard2-- > 0)
            {
                var anchor = workCells[rng.Next(workCells.Count)];
                int nx = anchor.x + rng.Next(-1, 2);
                int ny = anchor.y + rng.Next(-1, 2);
                if (Mathf.Abs(nx - start.x) > halfW || Mathf.Abs(ny - start.y) > halfH) { stall++; continue; }
                if (CanCarve(band, nx, ny)) { AddCell(nx, ny); stall = 0; }
                else stall++;
                if (stall > 32) break;   // 被围死，接受当前尺寸（不足 min 由 Commit 回滚）
            }

            return CommitWork(band, TPocket, lo);
        }

        /// <summary>小房间 / 死路：一半做「短窄道尽头」（DeadEnd），一半做「小团块」（SmallRoom）。</summary>
        bool BuildSmallRoom(UndergroundSpaceBand band)
        {
            if (!TryPickStart(band, out var start)) return false;

            int lo = Mathf.Max(3, band.smallRoomMinCells);
            int hi = Mathf.Max(lo, band.smallRoomMaxCells);
            int sizeTarget = rng.Next(lo, hi + 1);

            workCells.Clear();
            workSet.Clear();

            if (rng.NextDouble() < 0.5)
            {
                // DeadEnd：小窄道尽头（朝一个主方向走 3..5 格）
                AddCell(start.x, start.y);
                int dir = rng.Next(4);
                for (int i = 1; i < sizeTarget; i++)
                {
                    var last = workCells[workCells.Count - 1];
                    int nx = last.x + DX[dir];
                    int ny = last.y + DY[dir];
                    if (!CanCarve(band, nx, ny))
                    {
                        bool turned = false;
                        for (int k = 1; k <= 3 && !turned; k++)
                        {
                            int td = (dir + k) % 4;
                            var tl = workCells[workCells.Count - 1];
                            if (CanCarve(band, tl.x + DX[td], tl.y + DY[td])) { dir = td; turned = true; }
                        }
                        if (!turned) break;
                        var l2 = workCells[workCells.Count - 1];
                        nx = l2.x + DX[dir]; ny = l2.y + DY[dir];
                        if (!CanCarve(band, nx, ny)) break;
                    }
                    AddCell(nx, ny);
                }
                return CommitWork(band, TDeadEnd, 3);
            }

            // SmallRoom：局部生长小团块（半径 ≤2）
            AddCell(start.x, start.y);
            int g2 = 48;
            while (workCells.Count < sizeTarget && g2-- > 0)
            {
                var anchor = workCells[rng.Next(workCells.Count)];
                int nx = anchor.x + rng.Next(-1, 2);
                int ny = anchor.y + rng.Next(-1, 2);
                if (Mathf.Abs(nx - start.x) > 2 || Mathf.Abs(ny - start.y) > 2) continue;
                if (CanCarve(band, nx, ny)) AddCell(nx, ny);
            }
            return CommitWork(band, TSmallRoom, lo);
        }

        /// <summary>
        /// 通道（branch=false=Tunnel / true=Branch）：随机游走主道；
        /// branch 时强制尝试 ≥1 条 2~4 格侧道（制造岔路）。主道短到 3 格且无岔 → DeadEnd。
        /// </summary>
        bool BuildCorridor(UndergroundSpaceBand band, bool branch)
        {
            if (!TryPickStart(band, out var start)) return false;

            int lo = Mathf.Max(2, band.tunnelMinLength);
            int hi = Mathf.Max(lo, band.tunnelMaxLength);
            int lengthTarget = rng.Next(lo, hi + 1);

            workCells.Clear();
            workSet.Clear();
            AddCell(start.x, start.y);
            int dir = rng.Next(4);
            bool sideTried = false;
            bool sideDone = false;

            for (int i = 1; i < lengthTarget; i++)
            {
                // 60% 保持方向，否则随机转向
                if (rng.NextDouble() < 0.6) { }
                else dir = rng.Next(4);

                bool moved = Step(band, dir);
                if (!moved)
                {
                    bool any = false;
                    for (int k = 1; k <= 3 && !any; k++)
                    {
                        int td = (dir + k) % 4;
                        if (Step(band, td)) { dir = td; any = true; }
                    }
                    if (!any) break;   // 真死胡同：接受当前长度
                }

                // 岔路：主道至少 2 格后，85% 概率尝试一次侧道（不成功不强求）
                if (branch && !sideTried && workCells.Count >= 2)
                {
                    sideTried = true;
                    sideDone = TryBuildSide(band, out _);
                }
            }

            if (branch && sideDone)
                return CommitWork(band, TBranch, lo);
            if (!branch && workCells.Count >= 3 && workCells.Count < lo)
                return CommitWork(band, TDeadEnd, 3);     // 短主道尽头 → DeadEnd
            return CommitWork(band, TTunnel, lo);
        }

        /// <summary>主道单步：从当前末端沿 dir 走一格。不可走返回 false。</summary>
        bool Step(UndergroundSpaceBand band, int dir)
        {
            var last = workCells[workCells.Count - 1];
            int nx = last.x + DX[dir];
            int ny = last.y + DY[dir];
            if (!CanCarve(band, nx, ny)) return false;
            AddCell(nx, ny);
            return true;
        }

        /// <summary>侧道：从主道已建格中随机取锚点，垂直方向开 2~4 格；≥2 格才算成功。</summary>
        bool TryBuildSide(UndergroundSpaceBand band, out int builtOut)
        {
            builtOut = 0;
            int sideLen = rng.Next(band.branchSideMin, Mathf.Max(band.branchSideMin + 1, band.branchSideMax + 1));
            int tries = Mathf.Min(workCells.Count, 12);
            for (int attempt = 0; attempt < tries; attempt++)
            {
                var baseCell = workCells[rng.Next(workCells.Count)];
                // 与主道方向垂直：主道横向(0/1)则侧道纵向；主道纵向(2/3)则侧道横向
                int sd = (baseCell.x != workCells[workCells.Count - 1].x)
                    ? (rng.Next(2) == 0 ? 2 : 3)
                    : (rng.Next(2) == 0 ? 0 : 1);

                // 预检：连续 sideLen 格都可挖（几何链由逐格延伸保证，不依赖 workSet）
                int probe = 0;
                int sx = baseCell.x + DX[sd];
                int sy = baseCell.y + DY[sd];
                int probeDir = sd;
                for (int s = 0; s < sideLen; s++)
                {
                    if (!SideProbeOk(band, sx, sy)) break;
                    probe++;
                    sx += DX[probeDir];
                    sy += DY[probeDir];
                    if (rng.NextDouble() < 0.25)
                        probeDir = (probeDir == 0 || probeDir == 1) ? (rng.Next(2) == 0 ? 2 : 3) : (rng.Next(2) == 0 ? 0 : 1);
                }
                if (probe < 2) continue;

                // 真正落格（重新沿探测方向逐格落）
                sx = baseCell.x + DX[sd];
                sy = baseCell.y + DY[sd];
                int actually = 0;
                for (int s = 0; s < probe && actually < probe; s++)
                {
                    if (CanCarve(band, sx, sy)) { AddCell(sx, sy); actually++; }
                    sx += DX[sd];
                    sy += DY[sd];
                }
                if (actually >= 2) { builtOut = actually; return true; }
            }
            return false;
        }

        // ---------- 落格 / 提交 / 回滚 ----------

        void AddCell(int x, int y)
        {
            var c = new Vector2Int(x, y);
            workCells.Add(c);
            workSet.Add(c);
            allCarved.Add(c);
            target.SetTile(x, y, empty);    // 同步耐久(-1) + 刷新 tilemap；DigGrid 仍是真相源
        }

        /// <summary>尺寸达下限才提交并记录；不足则回滚（填回纯填充物），绝不留欠尺寸空腔。</summary>
        bool CommitWork(UndergroundSpaceBand band, string type, int minSize)
        {
            if (workCells.Count < minSize)
            {
                RollbackWork(band);
                Discarded++;
                return false;
            }

            var rec = new UndergroundSpaceRecord
            {
                type = type,
                bandName = band.bandName ?? band.minDepth + "-" + band.maxDepth,
                cells = new List<Vector2Int>(workCells)
            };
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var c in workCells)
            {
                if (c.x < minX) minX = c.x;
                if (c.x > maxX) maxX = c.x;
                if (c.y < minY) minY = c.y;
                if (c.y > maxY) maxY = c.y;
            }
            rec.bboxMin = new Vector2Int(minX, minY);
            rec.bboxMax = new Vector2Int(maxX, maxY);
            Spaces.Add(rec);
            TotalEmptyCells += rec.Size;
            workCells.Clear();
            workSet.Clear();
            return true;
        }

        /// <summary>
        /// 回滚：把本次临时挖空但未达标的格填回该深度层第一个纯填充物（确定性：不用全局随机）。
        /// space pass 在 ore pass 之前，回滚只会落在 value==0 填充物上，不影响矿脉统计。
        /// </summary>
        void RollbackWork(UndergroundSpaceBand band)
        {
            for (int i = 0; i < workCells.Count; i++)
            {
                var c = workCells[i];
                var layer = target.database.GetLayerAtDepth(c.y);
                var fill = FirstFill(layer);
                if (fill != null) target.SetTile(c.x, c.y, fill);
                allCarved.Remove(c);
            }
            workCells.Clear();
            workSet.Clear();
        }

        static TileDefinition FirstFill(DepthLayer layer)
        {
            if (layer == null || layer.tiles == null) return null;
            for (int i = 0; i < layer.tiles.Length; i++)
            {
                var t = layer.tiles[i];
                if (t != null && t.value == 0 && t.isSolid) return t;
            }
            return null;
        }

        // ---------- 落格判定 ----------

        /// <summary>找合法起点：界内 / 带内 / 可挖空 / 8 邻无任何既有 Empty（起点就保证隔离）。</summary>
        bool TryPickStart(UndergroundSpaceBand band, out Vector2Int start)
        {
            start = default;
            for (int t = 0; t < 24; t++)
            {
                int x = rng.Next(1, target.Width - 1);
                int loY = Mathf.Max(1, band.minDepth);
                int hiY = Mathf.Min(target.Depth - 1, band.maxDepth + 1);
                if (hiY <= loY) hiY = loY + 1;
                int y = Mathf.Clamp(rng.Next(loY, hiY), 1, target.Depth - 2);
                if (IsOpenCell(band, x, y) && !HasForeignNeighbor(x, y))
                {
                    start = new Vector2Int(x, y);
                    return true;
                }
            }
            return false;
        }

        /// <summary>该格是否可挖空：界内 / 非 bedrock / 带内 / 非保留区 / 当前是纯填充地层格。</summary>
        bool IsOpenCell(UndergroundSpaceBand band, int x, int y)
        {
            if (!target.InBounds(x, y)) return false;
            if (x <= 0 || x >= target.Width - 1) return false;
            if (y >= target.Depth - 1) return false;
            if (y < band.minDepth || y > band.maxDepth) return false;

            if (reservedRects != null)
                for (int i = 0; i < reservedRects.Length; i++)
                    if (reservedRects[i] != null && reservedRects[i].Contains(x, y))
                        return false;

            var def = target.GetTile(x, y);
            if (def == null) return false;
            if (!def.isSolid) return false;          // 已是 Empty（空间/坑口）不重复
            if (def.value > 0) return false;         // space pass 理论上先于矿，防御性跳过
            if (def.isHazard) return false;
            if (def.blockType != BlockType.Normal) return false;
            return true;
        }

        /// <summary>落格候选：可挖空 且 8 邻不贴「非本结构」既有 Empty（隔离）
        /// 且 4 邻接入本结构（4 连通 → 玩家实体可达，无仅对角相接的凹角）。</summary>
        bool CanCarve(UndergroundSpaceBand band, int x, int y)
        {
            if (!IsOpenCell(band, x, y)) return false;
            if (allCarved.Contains(new Vector2Int(x, y))) return false;
            if (HasForeignNeighbor(x, y)) return false;
            if (workSet.Count > 0 && !HasOrthoNeighborInSet(x, y)) return false;
            return true;
        }

        /// <summary>该格上下左右是否存在本结构已建格（4 连通锚点）。</summary>
        bool HasOrthoNeighborInSet(int x, int y)
        {
            return workSet.Contains(new Vector2Int(x + 1, y))
                || workSet.Contains(new Vector2Int(x - 1, y))
                || workSet.Contains(new Vector2Int(x, y + 1))
                || workSet.Contains(new Vector2Int(x, y - 1));
        }

        /// <summary>侧道预检专用（不走 workSet 邻接）：只验证可挖/隔离/几何链可行
        /// （真实落格时逐格 AddCell 自然满足 4 邻接入）。</summary>
        bool SideProbeOk(UndergroundSpaceBand band, int x, int y)
        {
            if (!IsOpenCell(band, x, y)) return false;
            if (allCarved.Contains(new Vector2Int(x, y))) return false;
            if (HasForeignNeighbor(x, y)) return false;
            return true;
        }

        /// <summary>8 邻内是否存在「不属于当前在建结构」的空格（隔离性：空间之间至少隔 1 格实心）。</summary>
        bool HasForeignNeighbor(int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (!target.InBounds(nx, ny)) continue;
                    var d = target.GetTile(nx, ny);
                    if (d == null || d.isSolid) continue;          // 填充/基岩不算
                    if (workSet.Contains(new Vector2Int(nx, ny))) continue;   // 属于当前结构 → 允许
                    return true;
                }
            return false;
        }

        // 方向表（y 向下为正：DY=+1 向下）
        static readonly int[] DX = { 1, -1, 0, 0 };
        static readonly int[] DY = { 0, 0, 1, -1 };

        // ---------- 统计辅助（供 HUD / 测试 / MCP 验收） ----------

        public int CountType(string type)
        {
            int c = 0;
            foreach (var s in Spaces) if (s.type == type) c++;
            return c;
        }

        /// <summary>DeadEnd/SmallRoom 桶数量（Issue §2D 合并统计）。</summary>
        public int CountDeadEndOrSmallRoom() => CountType(TDeadEnd) + CountType(TSmallRoom);

        public int CountInBand(string bandName)
        {
            int c = 0;
            foreach (var s in Spaces)
                if (bandName == null || s.bandName == bandName) c++;
            return c;
        }

        /// <summary>某带内挖出的 Empty 格数（供深度差异验收）。</summary>
        public int EmptyCellsInBand(string bandName)
        {
            int c = 0;
            foreach (var s in Spaces)
                if (bandName == null || s.bandName == bandName) c += s.Size;
            return c;
        }
    }
}
