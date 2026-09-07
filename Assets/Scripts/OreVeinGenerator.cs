using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-007：一条已生成的矿脉记录（生成结果，供统计 / 测试 / PR 验收读取）。
    /// </summary>
    [Serializable]
    public class OreVeinRecord
    {
        /// <summary>所属深度带名（OreDepthBand.bandName）。</summary>
        public string bandName;

        /// <summary>这条矿脉的矿物类型。</summary>
        public TileDefinition ore;

        /// <summary>矿脉包含的所有格（生成即固定，后续被挖掉也不影响记录）。</summary>
        public List<Vector2Int> cells = new List<Vector2Int>();

        /// <summary>矿脉包围盒（含两端）。</summary>
        public Vector2Int bboxMin;
        public Vector2Int bboxMax;

        /// <summary>矿脉尺寸（格）。</summary>
        public int Size => cells.Count;

        /// <summary>该矿脉是否空间连续：从第一格沿 8 邻可达全部格子。</summary>
        public bool IsConnected8
        {
            get
            {
                if (cells.Count <= 1) return true;
                var set = new HashSet<Vector2Int>(cells);
                var seen = new HashSet<Vector2Int>();
                var queue = new Queue<Vector2Int>();
                queue.Enqueue(cells[0]);
                seen.Add(cells[0]);
                while (queue.Count > 0)
                {
                    var c = queue.Dequeue();
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            var n = new Vector2Int(c.x + dx, c.y + dy);
                            if (set.Contains(n) && seen.Add(n)) queue.Enqueue(n);
                        }
                }
                return seen.Count == cells.Count;
            }
        }
    }

    /// <summary>
    /// DEV-007：矿脉生成器 —— 把「矿物从随机散点升级为有结构的矿脉」。
    ///
    /// 职责（只做 ore vein pass，不碰地层生成/不碰挖掘）：
    ///  - 在 DigGrid 的基础地层生成完成后，按 OreDepthBand 的深度带权重种矿脉；
    ///  - 每条矿脉：随机起点 + 8 邻 random walk / 邻域扩张，目标尺寸 2~8 格；
    ///  - 事务化提交：先在临时列表里规划整条矿脉，达到 veinMinSize 后才一次性写入
    ///    DigGrid —— 起点被洞穴 / 保留区 / 带边界 / 已有矿 / bedrock 困住而长不到
    ///    最小尺寸的矿脉整体判失败丢弃，绝不留下单点矿或欠尺寸矿；
    ///  - 同一 seed 完全可复现（内部用独立 System.Random，不依赖 Unity 全局随机流）；
    ///  - 保留区（地表 Hub / 出生 / 竖井 / 测试通道）与不可覆盖格（bedrock / empty /
    ///    已有 value&gt;0 矿物 / 特殊 block）一律跳过，不强制覆盖；
    ///  - 一条矿脉失败只放弃该起点，不进入死循环。
    ///
    /// 设计规则：
    ///  - 只把「纯填充地层格」（value==0 且 isSolid 且 blockType==Normal 且非 hazard）替换成矿，
    ///    因此不会破坏 SupportRock / LooseRock / Lava 等特殊格语义；
    ///  - 每次落格走 DigGrid.SetTile（同步耐久 + 刷新 tilemap），DigGrid 仍是 Block 数据的唯一真相源；
    ///  - 不新增任何特殊 Block，不改动 TileDefinition / TileDatabase / DrillVehicle / 掉落经济。
    ///
    /// 挂载：DigGrid 同一物体（或任意物体，把 grid 拖入）。
    /// 由 DigGrid.Generate 在基础地层 + 耐久初始化之后、RefreshAll 之前自动调用 ApplyToGrid。
    /// </summary>
    public class OreVeinGenerator : MonoBehaviour
    {
        [Tooltip("要生成的 DigGrid（默认取同一物体上的）。地层必须已生成，本组件只做 ore pass")]
        public DigGrid grid;

        [Header("深度带（数据驱动，集中配置）")]
        [Tooltip("按深度从小到大排列的矿脉带。每条带独立矿物权重 / 矿脉尺寸 / 频率")]
        public OreDepthBand[] bands;

        [Header("保留区（矿脉禁止进入）")]
        [Tooltip("矩形保留区（网格坐标）。地表 Hub / 出生点 / 下矿竖井 / 测试手工布局都应加进来")]
        public OreReservedRect[] reservedRects;

        [Header("随机")]
        [Tooltip("矿脉随机种子。-1 = 跟随 grid.seed（推荐：同一 grid seed 下地层+矿脉整体可复现）。" +
                 "显式给值则矿脉与 grid.seed 解耦（只测矿脉时用）")]
        public int seedOverride = -1;

        [Tooltip("单条矿脉生长时，每多长 1 格最多尝试几步（防死循环的余量，不是精确控制）")]
        [Min(4)] public int growthBackoffPerCell = 12;

        // ---------- 生成结果（只读，供统计 / MCP 验收） ----------

        /// <summary>本次生成的全部矿脉（ApplyToGrid 每次重建）。</summary>
        public List<OreVeinRecord> Veins { get; } = new List<OreVeinRecord>();

        /// <summary>本次生成实际种下的矿脉总格数。</summary>
        public int TotalCellsPlanted { get; private set; }

        /// <summary>本次生成中因长不到 veinMinSize 而被整体丢弃的矿脉数（事务化失败，零落格）。</summary>
        public int SubMinDiscards { get; private set; }

        /// <summary>实际生效的种子。</summary>
        public int EffectiveSeed => seedOverride >= 0
            ? seedOverride
            : (grid != null ? grid.seed : 0);

        /// <summary>是否已配置至少一个有效带（带含 ≥1 种矿）。</summary>
        public bool HasBands
        {
            get
            {
                if (bands == null) return false;
                foreach (var b in bands)
                    if (b != null && b.ores != null && b.ores.Length > 0) return true;
                return false;
            }
        }

        // ---------- ore pass ----------

        /// <summary>
        /// 在已生成基础地层的 DigGrid 上执行矿脉 pass。
        /// DigGrid.Generate 会自动调用（本组件挂在 DigGrid 上时）；测试脚本也可手动调（幂等：每次重建结果）。
        /// </summary>
        public void ApplyToGrid(DigGrid target)
        {
            grid = target != null ? target : grid;
            if (grid == null || grid.database == null)
            {
                Debug.LogWarning("[DEV-007] OreVeinGenerator.ApplyToGrid: 缺少 DigGrid 或 database，跳过矿脉生成");
                return;
            }

            Veins.Clear();
            TotalCellsPlanted = 0;
            SubMinDiscards = 0;
            if (!HasBands) return;

            var rng = new System.Random(EffectiveSeed);

            foreach (var band in bands)
            {
                if (band == null || band.ores == null || band.ores.Length == 0) continue;

                // 期望起点数 = 带深度跨度 × 每格深度频率（clamp 避免小图爆量）
                int attempts = Mathf.Clamp(
                    Mathf.RoundToInt(band.DepthSpan * band.veinFrequency), 0, 512);
                for (int a = 0; a < attempts; a++)
                    TryGrowVein(band, rng);
            }
        }

        /// <summary>
        /// 尝试生长一条矿脉（事务化）：随机起点（最多 16 次重试）→ 随机矿种 → 目标尺寸 →
        /// 先在【临时列表】里 8 邻随机扩张到目标尺寸或尝试耗尽；只有最终尺寸 ≥ veinMinSize
        /// 才一次性写入 DigGrid 并记录。长不到最小尺寸的矿脉整体判失败丢弃，零落格 ——
        /// 不会出现「起点写进网格却长不出去，留下单点矿」的边界情况。
        /// 起点/尺寸不足失败只放弃该起点，不重试整条（无死循环，带内矿脉密度由频率配置决定）。
        /// </summary>
        void TryGrowVein(OreDepthBand band, System.Random rng)
        {
            // 1. 找合法起点（界内 / 带内 / 可落格）
            Vector2Int? start = null;
            for (int t = 0; t < 16 && start == null; t++)
            {
                int x = rng.Next(1, grid.Width - 1);          // 避开左右 bedrock 列
                int y = Mathf.Clamp(rng.Next(band.minDepth, band.maxDepth + 1),
                                    0, grid.Depth - 2);       // 避开底部 bedrock 行
                if (CanPlant(band, x, y)) start = new Vector2Int(x, y);
            }
            if (start == null) return;

            // 2. 随机矿种
            TileDefinition ore = PickWeightedOre(band, rng);
            if (ore == null) return;

            // 3. 目标尺寸（min≥1；builder 配置 2/3/4 起，保证矿脉 ≥2 的 DEV-007 核心规则）
            int lo = Mathf.Max(1, band.veinMinSize);
            int hi = Mathf.Max(lo, band.veinMaxSize);
            int targetSize = rng.Next(lo, hi + 1);

            // 4. 先在临时列表规划（不写网格）：随机取已占格作锚点，朝随机 8 邻试探扩张
            var cells = new List<Vector2Int> { start.Value };
            int guard = targetSize * growthBackoffPerCell;
            while (cells.Count < targetSize && guard-- > 0)
            {
                var anchor = cells[rng.Next(cells.Count)];
                int dx = rng.Next(-1, 2);
                int dy = rng.Next(-1, 2);
                if (dx == 0 && dy == 0) continue;

                int nx = anchor.x + dx;
                int ny = anchor.y + dy;
                if (!CanPlant(band, nx, ny)) continue;
                if (cells.Contains(new Vector2Int(nx, ny))) continue;

                cells.Add(new Vector2Int(nx, ny));
            }

            // 5. 事务化提交：不足 veinMinSize → 整体判失败，网格零改动、零残留
            if (cells.Count < lo)
            {
                SubMinDiscards++;
                return;
            }
            foreach (var c in cells)
            {
                grid.SetTile(c.x, c.y, ore);
                TotalCellsPlanted++;
            }

            // 6. 记录（含包围盒）
            var rec = MakeRecord(band.bandName ?? band.minDepth + "-" + band.maxDepth, ore, cells);
            Veins.Add(rec);
        }

        /// <summary>把某条已生长/已落格的矿格列表登记成一条 OreVeinRecord（含包围盒；复用账实）。</summary>
        static OreVeinRecord MakeRecord(string bandName, TileDefinition ore, List<Vector2Int> cells)
        {
            var rec = new OreVeinRecord { bandName = bandName, ore = ore, cells = cells };
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var c in cells)
            {
                if (c.x < minX) minX = c.x;
                if (c.x > maxX) maxX = c.x;
                if (c.y < minY) minY = c.y;
                if (c.y > maxY) maxY = c.y;
            }
            rec.bboxMin = new Vector2Int(minX, minY);
            rec.bboxMax = new Vector2Int(maxX, maxY);
            return rec;
        }

        /// <summary>
        /// DEV-014 Blocker2 复用接口：在指定锚点格种一条【真实矿脉】并登记进 Veins（供 DiscoveryNodeGenerator
        /// 作节点奖励矿，避免第二个手工矿源）。与 TryGrowVein 共用纯填充地层落格规则、事务化、登记语义；
        /// 区别是起点/矿种由调用方显式给定（节点已选好区域锚点），不再走带内随机起点。
        ///
        /// 账实一致：落格走 grid.SetTile + 计入 TotalCellsPlanted + 登记 Veins（连通/统计/回归可见），
        /// 与普通矿脉完全同构。若锚点被已有矿/特殊块/空格占据，或长不足 minSize，则整体回滚并返回 null（零残留）。
        /// </summary>
        public OreVeinRecord PlantRewardVein(string bandName, TileDefinition ore, Vector2Int start,
            int minSize, int maxSize, System.Random rng, RectInt stayInside = default)
        {
            if (grid == null || grid.database == null || ore == null) return null;
            if (start.x <= 0 || start.x >= grid.Width - 1 || start.y <= 0 || start.y >= grid.Depth - 1) return null;
            if (rng == null) rng = new System.Random(EffectiveSeed);
            bool constrained = stayInside.width > 0 && stayInside.height > 0;

            // 锚点必须落在可落矿格（宽松判定：纯填充地层格；若约束内则须在约束内）
            if (!CanPlantOnFill(start.x, start.y)) return null;
            if (constrained && !Inside(start.x, start.y, stayInside)) return null;

            int lo = System.Math.Max(2, minSize);
            int hi = System.Math.Max(lo, maxSize);
            int target = lo == hi ? lo : rng.Next(lo, hi + 1);

            // 临时列表从锚点 8 邻生长，只落在纯填充地层格（且可选约束框内）
            var cells = new List<Vector2Int> { start };
            int guard = target * 24 + 64;
            while (cells.Count < target && guard-- > 0)
            {
                var anchor = cells[rng.Next(cells.Count)];
                int nx = anchor.x + rng.Next(-1, 2);
                int ny = anchor.y + rng.Next(-1, 2);
                if (nx == anchor.x && ny == anchor.y) continue;
                if (constrained && !Inside(nx, ny, stayInside)) continue;
                if (!CanPlantOnFill(nx, ny)) continue;
                if (cells.Contains(new Vector2Int(nx, ny))) continue;
                cells.Add(new Vector2Int(nx, ny));
            }
            if (cells.Count < lo) return null;   // 长不足 → 零残留

            foreach (var c in cells) grid.SetTile(c.x, c.y, ore);
            TotalCellsPlanted += cells.Count;
            var rec = MakeRecord(bandName, ore, cells);
            Veins.Add(rec);
            return rec;
        }

        static bool Inside(int x, int y, RectInt r)
            => x >= r.xMin && x < r.xMax && y >= r.yMin && y < r.yMax;

        /// <summary>宽松「可落矿格」判定：界内、非左右/底部 bedrock、实心、value==0、非 hazard、blockType==Normal。</summary>
        bool CanPlantOnFill(int x, int y)
        {
            if (!grid.InBounds(x, y)) return false;
            if (x <= 0 || x >= grid.Width - 1) return false;
            if (y >= grid.Depth - 1) return false;
            var def = grid.GetTile(x, y);
            if (def == null) return false;
            if (!def.isSolid) return false;
            if (def.value > 0) return false;         // 已有矿不覆盖
            if (def.isHazard) return false;
            if (def.blockType != BlockType.Normal) return false;
            return true;
        }

        /// <summary>
        /// 某格是否可被矿脉落格：
        ///  - 界内、非 bedrock 边界列/底行；
        ///  - 位于该带深度闭区间（矿脉不跨带，统计归属干净）；
        ///  - 不在任何保留区；
        ///  - 当前是「可替换的纯填充地层格」：isSolid && value==0 && blockType==Normal && !isHazard。
        ///    已有矿(value&gt;0) / 特殊 block / Lava / empty / bedrock 一律不覆盖。
        /// </summary>
        bool CanPlant(OreDepthBand band, int x, int y)
        {
            if (!grid.InBounds(x, y)) return false;
            if (x <= 0 || x >= grid.Width - 1) return false;      // 左右 bedrock
            if (y >= grid.Depth - 1) return false;                // 底部 bedrock
            if (y < band.minDepth || y > band.maxDepth) return false;

            if (reservedRects != null)
                for (int i = 0; i < reservedRects.Length; i++)
                    if (reservedRects[i] != null && reservedRects[i].Contains(x, y))
                        return false;

            var def = grid.GetTile(x, y);
            if (def == null) return false;
            if (!def.isSolid) return false;                       // empty / 洞穴
            if (def.value > 0) return false;                      // 已有矿：不重复覆盖
            if (def.isHazard) return false;                       // Lava 等
            if (def.blockType != BlockType.Normal) return false;  // Support / Loose 等特殊格
            return true;
        }

        /// <summary>按带内权重随机选一种矿（独立 System.Random，可复现）。</summary>
        static TileDefinition PickWeightedOre(OreDepthBand band, System.Random rng)
        {
            float total = 0f;
            int n = band.ores.Length;
            for (int i = 0; i < n; i++)
            {
                if (band.ores[i] == null) continue;
                float w = (band.weights != null && i < band.weights.Length)
                    ? Mathf.Max(0f, band.weights[i]) : 1f;
                total += w;
            }
            if (total <= 0f) return band.ores[0];

            double roll = rng.NextDouble() * total;
            for (int i = 0; i < n; i++)
            {
                if (band.ores[i] == null) continue;
                float w = (band.weights != null && i < band.weights.Length)
                    ? Mathf.Max(0f, band.weights[i]) : 1f;
                roll -= w;
                if (roll <= 0d) return band.ores[i];
            }
            return band.ores[n - 1];
        }

        // ---------- 统计辅助（供 HUD / 测试 / MCP 验收） ----------

        /// <summary>某条记录是否属于给定带名（null = 全部）。</summary>
        public int CountVeinsIn(string bandName)
        {
            int c = 0;
            foreach (var v in Veins)
                if (bandName == null || v.bandName == bandName) c++;
            return c;
        }

        /// <summary>统计某带内各矿种的【格数】（遍历矿脉记录；用于深度分层验收）。</summary>
        public Dictionary<TileDefinition, int> CountCellsByOre(string bandName)
        {
            var d = new Dictionary<TileDefinition, int>();
            foreach (var v in Veins)
            {
                if (bandName != null && v.bandName != bandName) continue;
                d.TryGetValue(v.ore, out var cur);
                d[v.ore] = cur + v.cells.Count;
            }
            return d;
        }
    }
}
