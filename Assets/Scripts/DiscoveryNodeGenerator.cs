using System.Collections.Generic;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-014：地下发现节点生成器（Issue #30 §Architecture Boundary）—— 一个【组合现有系统的
    /// 轻量 Composer + placement pass】。
    ///
    /// 职责：
    ///  - 依附现有 DigGrid，只经 DigGrid.SetTile / OreVeinGenerator.PlantRewardVein 落图（不建第二套 Grid）；
    ///  - 在 DigGrid.Generate 的 Ruin pass 之后、RefreshAll 之前由 DigGrid 调用（生成顺序集中定义，
    ///    不推翻 Base→Space→Vein→Ruin）；
    ///  - 确定性：seed = seedOverride>=0 ? seedOverride : grid.seed，用 new System.Random(EffectiveSeed)，
    ///    绝不碰 UnityEngine.Random（保证同 seed 反复生成逐格一致）；
    ///  - 保留避让：自己的 reservedRects + 若 grid.ruinGenerator 存在，把每个 RuinInstance.bounds 外扩
    ///    1 格转保留区 → 不覆盖/破坏 AncientRelayRoom；
    ///  - 原子提交：每个节点先「规划全部结构格 → 校验合法 → 落结构 → 经 OreVeinGenerator 种+登记奖励矿」；
    ///    任一步失败则整节点回滚（恢复结构格原 tile），绝不写半个节点；
    ///  - 【Blocker2 修复】A/B/D 的奖励矿不再由注入的独立 reward tile 手工 SetTile，而是经
    ///    grid.oreVeinGenerator.PlantRewardVein 在节点奖励区种一条【真实矿脉】并登记进 Veins ——
    ///    奖励走现有 vein 系统（连通/账实/统计一致），无第二个手工矿源；结构块（SupportRock/LooseRock/
    ///    HotRock）落在奖励区之外的纯地层格，绝不覆盖已有 vein/space；
    ///  - 四类节点只组合现有系统反应：A/D 走 BlockCollapseSystem（SupportRock→LooseRock）、B 走
    ///    HotRockSystem + HasCapability(Cooling)、C 走 RuinSealSystem + HasCapability(RuinAccess) +
    ///    InventoryGrid/HandleTileDug。本类不持有第二套 collapse/heat/capability/ruin/奖励系统。
    ///
    /// 挂载：任意物体（常与 DigGrid 同物体）。由场景 Builder 注入结构 tile、oreVeinSource 与 reservedRects，
    /// 并赋给 digGrid.discoveryNodeGenerator 后 RegenerateFromDatabase()。
    /// </summary>
    public class DiscoveryNodeGenerator : MonoBehaviour
    {
        [Tooltip("目标 DigGrid（常与挂载物体相同）。")]
        public DigGrid grid;

        [Tooltip("-1 = 跟随 grid.seed；否则用本覆盖值（确定性测试）。")]
        public int seedOverride = -1;

        [Tooltip("避开/保护的网格保留区（含地表带；Builder 注入）。")]
        public OreReservedRect[] reservedRects;

        [Tooltip("每轮最多尝试多少个候选锚点后放弃（防死循环）。")]
        public int maxPlacementAttempts = 64;

        // ---- 结构 / 环境 tile（Builder 注入真实 .asset；null 时对应节点跳过） ----

        [Header("结构 / 环境 tile")]
        [Tooltip("承重岩（A/D）。null → 对应节点不铺支撑结构。")]
        public TileDefinition supportRockTile;
        [Tooltip("松散岩（A/D）。null → 对应节点不铺松散段。")]
        public TileDefinition looseRockTile;
        [Tooltip("高温岩（B）。null → B 节点不铺热走廊。")]
        public TileDefinition hotRockTile;
        [Tooltip("遗迹封印门（C）。null → C 节点不铺。")]
        public TileDefinition sealTile;

        [Header("Blocker2：奖励矿来源（经现有 OreVeinGenerator 种植+登记，避免第二矿源）")]
        [Tooltip("非空时：A/B/D 的奖励矿由本 OreVeinGenerator.PlantRewardVein 在节点奖励区种真实矿脉并登记。\n" +
                 "奖励矿种 = 该 region 对应 vein band 里价值最高的矿（复用 band 真实矿池，非新 tile）。\n" +
                 "为 null 时 A/B/D 只落结构不产奖励（旧场景降级，回归不会崩）。")]
        public OreVeinGenerator oreVeinSource;

        [Tooltip("C 类文明奖励 tile（非 vein 语义；保留独立注入）。")]
        public TileDefinition ancientDataTile;
        [Tooltip("C 类文明奖励 tile（非 vein 语义；保留独立注入）。")]
        public TileDefinition ancientAlloyTile;

        /// <summary>本次 pass 生成的节点（只读布局；验收查询）。</summary>
        public readonly List<DiscoveryNodeInstance> Nodes = new List<DiscoveryNodeInstance>();

        /// <summary>本次实际使用 seed（确定性验证）。</summary>
        public int EffectiveSeed { get; private set; }

        /// <summary>是否登记了至少一个节点。</summary>
        public bool HasNodes => Nodes.Count > 0;

        /// <summary>最近一次 ApplyToGrid 结果（ok / reason）。</summary>
        public string LastVerdict { get; private set; } = "none";

        /// <summary>清空上一次生成结果（供 Regenerate 前调用）。</summary>
        public void ClearNodes() => Nodes.Clear();

        public int ResolveSeed()
        {
            if (seedOverride >= 0) return seedOverride;
            return grid != null ? grid.seed : 20260907;
        }

        /// <summary>
        /// 执行节点生成 pass。返回 true = 登记了 ≥1 个节点。由 DigGrid.Generate 在 ruin pass 之后调用。
        /// 确定性：相同 seed + 相同输入 → 相同节点布局。
        /// </summary>
        public bool ApplyToGrid(DigGrid target)
        {
            grid = target;
            Nodes.Clear();
            if (grid == null || grid.database == null)
            {
                LastVerdict = "fail:no_grid_or_db";
                return false;
            }
            // Blocker2：奖励矿经现有 OreVeinGenerator；若 grid 上挂了则自动采用
            if (oreVeinSource == null) oreVeinSource = grid.oreVeinGenerator;

            EffectiveSeed = ResolveSeed();
            var rng = new System.Random(EffectiveSeed);

            // 构建保留集：自身 reservedRects + Ruin bounds 外扩 1 格（避免覆盖/破坏 AncientRelayRoom）
            var protect = new List<RectInt>();
            if (reservedRects != null)
                for (int i = 0; i < reservedRects.Length; i++)
                {
                    var r = reservedRects[i];
                    if (r != null)
                        protect.Add(new RectInt(r.x, r.y, r.w, r.h));
                }
            if (grid.ruinGenerator != null)
                for (int i = 0; i < grid.ruinGenerator.Instances.Count; i++)
                {
                    var b = grid.ruinGenerator.Instances[i].bounds;
                    protect.Add(new RectInt(b.xMin - 1, b.yMin - 1, b.width + 2, b.height + 2));
                }

            bool any = false;
            var placedCells = new HashSet<Vector2Int>();
            for (int i = 0; i < DiscoveryNodeCatalog.All.Length; i++)
            {
                var spec = DiscoveryNodeCatalog.All[i];
                int count = PlaceType(spec, rng, protect, placedCells);
                if (count > 0) any = true;
            }

            LastVerdict = any ? $"ok:{Nodes.Count}" : "ok:none_placed";
            return any;
        }

        int PlaceType(DiscoveryNodeSpec spec, System.Random rng, List<RectInt> protect, HashSet<Vector2Int> placedCells)
        {
            var region = FindRegion(spec.allowedRegionId);
            if (region == null) return 0;

            int w = spec.footprintWidth, h = spec.footprintHeight;
            int placed = 0;
            int want = System.Math.Max(1, spec.maxPerSeed);
            int guard = 0;
            while (placed < want && guard++ < maxPlacementAttempts)
            {
                // 候选锚点：footprint 左上角，避左右基岩与顶部；y 在区域带内，底部留边
                int x0 = rng.Next(2, System.Math.Max(3, grid.width - 2 - w));
                int yMin = region.minDepth;
                int yMax = System.Math.Min(region.maxDepth, grid.depth - 2 - h);
                if (yMax < yMin) break;
                int y0 = yMin + rng.Next(0, System.Math.Max(1, yMax - yMin + 1));

                if (OverlapsAny(x0, y0, w, h, protect)) continue;
                // Blocker2：不再要求整 footprint 纯岩。结构区需能落（纯地层格），奖励区可含已有 vein/space；
                // 结构格不能落在已有 vein/space/特殊块（不覆盖），奖励区在结构区下方独立。
                if (!StructureRegionPlantable(spec, x0, y0, w, h, placedCells)) continue;

                // 整节点原子提交：结构 + 奖励矿
                var inst = new DiscoveryNodeInstance
                {
                    type = spec.type,
                    instanceId = $"{DiscoveryNodeTypeInfo.StableId(spec.type)}_x{x0}_y{y0}",
                    usedSeed = EffectiveSeed,
                };
                inst.bounds = new RectInt(x0, y0, w, h);

                bool committed = TryCommit(spec, inst, x0, y0, rng, placedCells);
                if (!committed) continue;

                for (int yy = 0; yy < inst.cells.Count; yy++) placedCells.Add(inst.cells[yy]);
                Nodes.Add(inst);
                placed++;
            }
            return placed;
        }

        /// <summary>
        /// Blocker2：结构区可落判定。对 A/B/D，结构块只落在「footprint 内、结构带（非奖励区）且当前为
        /// 纯填充地层格（可覆盖）且未被占用/保留」；奖励区允许已有 vein/space。C 结构即 seal 列，落在纯地层格。
        /// </summary>
        bool StructureRegionPlantable(DiscoveryNodeSpec spec, int x0, int y0, int w, int h, HashSet<Vector2Int> placedCells)
        {
            // 结构格集合（与 Plan* 一致）：返回该 footprint 下需要 SetTile 的结构格
            var structCells = CollectStructureCells(spec, x0, y0, w, h);
            if (structCells.Count == 0) return false;
            for (int i = 0; i < structCells.Count; i++)
            {
                var c = structCells[i];
                if (!grid.InBounds(c.x, c.y)) return false;
                if (placedCells.Contains(c)) return false;
                var t = grid.GetTile(c.x, c.y);
                // 结构格必须在实心普通岩 value0（可覆盖），不落在 vein(value>0)/empty/特殊块
                if (t == null || !t.isSolid || t.blockType != BlockType.Normal || t.value > 0) return false;
            }
            return true;
        }

        /// <summary>收集某 footprint 下需 SetTile 的结构格（不写、仅计算；供预校验）。</summary>
        List<Vector2Int> CollectStructureCells(DiscoveryNodeSpec spec, int x0, int y0, int w, int h)
        {
            var cells = new List<Vector2Int>();
            switch (spec.type)
            {
                case DiscoveryNodeType.AbandonedMiningPocket:
                    {
                        int ry = y0 + h - 1;              // 奖励区在底部（不含结构列）
                        int cx = x0 + w / 2;              // 中央列
                        // 承重横梁在奖励区上一行（结构带）；梁上 LooseRock 段
                        int supY = ry - 1;
                        if (supY >= y0) { cells.Add(new Vector2Int(cx, supY)); AddCellUnique(cells, cx, supY); }
                        if (supY - 1 >= y0) { cells.Add(new Vector2Int(cx, supY - 1)); AddCellUnique(cells, cx, supY - 1); }
                        break;
                    }
                case DiscoveryNodeType.ThermalVentChamber:
                    {
                        // 左侧热走廊两格（结构带 = 左列），奖励区在右侧/中部
                        int hyMid = y0 + h / 2;
                        int hyBot = y0 + h - 1;
                        cells.Add(new Vector2Int(x0, hyMid)); AddCellUnique(cells, x0, hyMid);
                        if (hyBot != hyMid) { cells.Add(new Vector2Int(x0, hyBot)); AddCellUnique(cells, x0, hyBot); }
                        break;
                    }
                case DiscoveryNodeType.AncientSignalCache:
                    {
                        // seal 门 + 其后文明奖励格（文明 tile 非 vein；全视为结构，一次性落）
                        int cx = x0 + w / 2, midY = y0 + h / 2;
                        if (sealTile != null) { cells.Add(new Vector2Int(cx, midY)); AddCellUnique(cells, cx, midY); }
                        if (h >= 3)
                        {
                            if (ancientDataTile != null) { cells.Add(new Vector2Int(cx, midY - 1)); AddCellUnique(cells, cx, midY - 1); }
                            if (ancientAlloyTile != null) { cells.Add(new Vector2Int(cx, midY + 1)); AddCellUnique(cells, cx, midY + 1); }
                        }
                        break;
                    }
                case DiscoveryNodeType.CollapsedResourcePocket:
                    {
                        // 中央柱：SupportRock（承重）+ 其上 LooseRock（可坍塌）；奖励区在底部
                        int cx = x0 + w / 2;
                        int supY = y0 + h - 2;          // 承重在奖励区上方
                        if (supY >= y0) { cells.Add(new Vector2Int(cx, supY)); AddCellUnique(cells, cx, supY); }
                        for (int yy = supY - 1; yy >= y0; yy--) { cells.Add(new Vector2Int(cx, yy)); AddCellUnique(cells, cx, yy); }
                        break;
                    }
            }
            return cells;
        }

        static void AddCellUnique(List<Vector2Int> list, int x, int y)
        {
            var v = new Vector2Int(x, y);
            for (int i = 0; i < list.Count; i++) if (list[i] == v) return;
            list.Add(v);
        }

        /// <summary>候选 footprint 是否与任一保留区（地表带 / Ruin bounds 外扩）相交。</summary>
        bool OverlapsAny(int x0, int y0, int w, int h, List<RectInt> protect)
        {
            var r = new RectInt(x0, y0, w, h);
            for (int i = 0; i < protect.Count; i++)
            {
                if (RectsOverlap(r, protect[i])) return true;
            }
            return false;
        }

        static bool RectsOverlap(RectInt a, RectInt b)
            => a.xMin < b.xMax && a.xMax > b.xMin && a.yMin < b.yMax && a.yMax > b.yMin;

        static DepthRegionDefinition FindRegion(string regionId)
        {
            foreach (var r in DepthRegionLayout.All)
                if (r.regionId == regionId) return r;
            return null;
        }

        /// <summary>按类型把节点「结构 + 奖励矿」规划并原子提交到 grid。</summary>
        bool TryCommit(DiscoveryNodeSpec spec, DiscoveryNodeInstance inst, int x0, int y0, System.Random rng, HashSet<Vector2Int> placedCells)
        {
            int w = spec.footprintWidth, h = spec.footprintHeight;

            // 1. 计算结构格与奖励区（先于写）
            var structCells = CollectStructureCells(spec, x0, y0, w, h);
            RectInt rewardRect = RewardRect(spec, x0, y0, w, h);

            // 2. 预校验结构格（纯地层、未被占用）
            for (int i = 0; i < structCells.Count; i++)
            {
                var c = structCells[i];
                if (!grid.InBounds(c.x, c.y)) return false;
                if (placedCells.Contains(c)) return false;
                var t = grid.GetTile(c.x, c.y);
                if (t == null || !t.isSolid || t.blockType != BlockType.Normal || t.value > 0) return false;
            }

            // 3. 记录结构格原 tile（回滚用）
            var originals = new Dictionary<Vector2Int, TileDefinition>();
            for (int i = 0; i < structCells.Count; i++)
            {
                var c = structCells[i];
                if (!originals.ContainsKey(c)) originals[c] = grid.GetTile(c.x, c.y);
            }

            // 4. 落结构（SetTile）+ 按 def 归类 seal/risk/reward（C 的文明奖励格直接分类）
            for (int i = 0; i < structCells.Count; i++)
            {
                var c = structCells[i];
                TileDefinition def = StructDefFor(spec, c, x0, y0, w, h);
                if (def == null) { RollbackStructures(originals); return false; }
                grid.SetTile(c.x, c.y, def);
                inst.cells.Add(c);
                if (spec.type == DiscoveryNodeType.AncientSignalCache && def == sealTile)
                {
                    inst.sealCell = c; inst.riskCells.Add(c);
                }
                else if (spec.type == DiscoveryNodeType.AncientSignalCache &&
                         (def == ancientDataTile || def == ancientAlloyTile))
                {
                    inst.rewardCells.Add(c);
                }
                else
                {
                    inst.riskCells.Add(c);
                }
            }

            // 5. A/B/D：经 OreVeinGenerator 在奖励区种真实矿脉并登记。C 的文明奖励已随结构在步骤 4 归类。
            if (spec.type == DiscoveryNodeType.AncientSignalCache)
                return inst.sealCell != Vector2Int.zero && inst.rewardCells.Count > 0;

            if (!CommitVeinReward(spec, inst, rewardRect, rng))
            {
                RollbackStructures(originals);
                return false;
            }
            return true;
        }

        /// <summary>某节点类型对应的奖励区（footprint 内、结构带下方/后方的独立矩形）。A/D 取承重柱下方中央段，保证奖励可 4 邻到风险。</summary>
        static RectInt RewardRect(DiscoveryNodeSpec spec, int x0, int y0, int w, int h)
        {
            int cx = x0 + w / 2;
            switch (spec.type)
            {
                case DiscoveryNodeType.AbandonedMiningPocket:
                    return new RectInt(cx - 1, y0 + h - 1, 3, 1);   // 承重梁下方中央 3 格（含支撑正下格）
                case DiscoveryNodeType.ThermalVentChamber:
                    return new RectInt(x0 + 1, y0 + h / 2, w - 1, 1); // 右/中行（避开左热列）
                case DiscoveryNodeType.CollapsedResourcePocket:
                    return new RectInt(cx - 1, y0 + h - 1, 3, 1);   // 承重柱下方中央 3 格
                default:
                    return new RectInt(x0, y0, 1, 1);
            }
        }

        /// <summary>
        /// A/B/D 奖励：经现有 OreVeinGenerator 生产并登记，锚定在奖励区中央（紧贴结构/风险格正下方，
        /// 保证 A/D 奖励与风险 4 邻可决策）。锚点若是已有 vein 矿格 → 直接登记（组合现有 vein，不重写）；
        /// 否则在该点经 PlantRewardVein 种一条真实矿脉并登记（账实/连通/统计同构）。两条路都走现有
        /// vein 系统（无第二个手工矿源）。无法获得 ≥1 奖励格 → 返回 false（整节点回滚）。
        /// </summary>
        bool CommitVeinReward(DiscoveryNodeSpec spec, DiscoveryNodeInstance inst, RectInt rewardRect, System.Random rng)
        {
            if (oreVeinSource == null) return false;   // A/B/D 需要 vein 源产奖励；无则视为不可提交（回滚）
            var ore = PickRewardOreForRegion(spec.allowedRegionId);
            if (ore == null) return false;

            int anchorX = rewardRect.xMin + rewardRect.width / 2;
            int anchorY = rewardRect.yMin + rewardRect.height / 2;
            var anchor = new Vector2Int(anchorX, anchorY);
            if (!grid.InBounds(anchorX, anchorY)) return false;

            var anchorDef = grid.GetTile(anchorX, anchorY);
            bool existingOre = anchorDef != null && anchorDef.isSolid && anchorDef.value > 0;
            List<Vector2Int> rewardCells = null;

            if (existingOre)
            {
                // 锚点已有 vein 矿 → 组合现有 vein：登记 rewardRect 内所有现成 ore 格（不重写、不重复登记）
                rewardCells = new List<Vector2Int>();
                for (int y = rewardRect.yMin; y < rewardRect.yMax; y++)
                    for (int x = rewardRect.xMin; x < rewardRect.xMax; x++)
                    {
                        if (!grid.InBounds(x, y)) continue;
                        var def = grid.GetTile(x, y);
                        if (def != null && def.isSolid && def.value > 0) rewardCells.Add(new Vector2Int(x, y));
                    }
                if (rewardCells.Count == 0) return false;
            }
            else
            {
                // 锚点空 → 经 OreVeinGenerator 种真实矿脉（含锚点）并登记
                var vein = oreVeinSource.PlantRewardVein(spec.allowedRegionId, ore, anchor, 2, 3, rng, rewardRect);
                if (vein == null) return false;
                rewardCells = vein.cells;
            }

            for (int i = 0; i < rewardCells.Count; i++)
            {
                var c = rewardCells[i];
                inst.rewardCells.Add(c);
                if (!inst.cells.Contains(c)) inst.cells.Add(c);
            }
            return true;
        }

        /// <summary>按 region 从 vein 源选价值最高的矿种（复用 band 真实矿池，非新 tile）。</summary>
        TileDefinition PickRewardOreForRegion(string regionId)
        {
            if (oreVeinSource == null || oreVeinSource.bands == null) return null;
            TileDefinition best = null; int bestVal = -1;
            for (int b = 0; b < oreVeinSource.bands.Length; b++)
            {
                var band = oreVeinSource.bands[b];
                if (band == null || band.ores == null) continue;
                if (!BandNameMatchesRegion(band.bandName, regionId)) continue;
                for (int o = 0; o < band.ores.Length; o++)
                {
                    var t = band.ores[o];
                    if (t == null) continue;
                    if (t.value > bestVal) { bestVal = t.value; best = t; }
                }
            }
            return best;
        }

        static bool BandNameMatchesRegion(string bandName, string regionId)
        {
            if (string.IsNullOrEmpty(bandName)) return true;
            return bandName == regionId;
        }

        TileDefinition StructDefFor(DiscoveryNodeSpec spec, Vector2Int c, int x0, int y0, int w, int h)
        {
            switch (spec.type)
            {
                case DiscoveryNodeType.AbandonedMiningPocket:
                    {
                        int ry = y0 + h - 1;
                        if (c.y == ry - 1) return supportRockTile;      // 承重梁
                        if (c.y == ry - 2) return looseRockTile;        // 梁上松散
                        return null;
                    }
                case DiscoveryNodeType.ThermalVentChamber:
                    return hotRockTile;                                  // 左热列
                case DiscoveryNodeType.AncientSignalCache:
                    {
                        int cx = x0 + w / 2, midY = y0 + h / 2;
                        if (c.y == midY) return sealTile;                // 门
                        if (c.y == midY - 1) return ancientDataTile;     // 门后文明数据
                        if (c.y == midY + 1) return ancientAlloyTile;    // 门后文明合金
                        return null;
                    }
                case DiscoveryNodeType.CollapsedResourcePocket:
                    {
                        int supY = y0 + h - 2;
                        if (c.y == supY) return supportRockTile;         // 承重柱
                        return looseRockTile;                            // 柱上松散（可坍塌）
                    }
                default:
                    return null;
            }
        }

        void RollbackStructures(Dictionary<Vector2Int, TileDefinition> originals)
        {
            foreach (var kv in originals)
                if (grid != null && grid.InBounds(kv.Key.x, kv.Key.y))
                    grid.SetTile(kv.Key.x, kv.Key.y, kv.Value);
        }
    }
}
