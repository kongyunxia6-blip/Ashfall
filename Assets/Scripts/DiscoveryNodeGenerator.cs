using System.Collections.Generic;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-014：地下发现节点生成器（Issue #30 §Architecture Boundary）—— 一个【组合现有系统的
    /// 轻量 Composer + placement pass】。
    ///
    /// 职责：
    ///  - 依附现有 DigGrid，只经 DigGrid.SetTile(x,y,tile,durability) 落图（不建第二套 Grid）；
    ///  - 在 DigGrid.Generate 的 Ruin pass 之后、RefreshAll 之前由 DigGrid 调用（生成顺序集中定义，
    ///    不推翻 Base→Space→Vein→Ruin）；
    ///  - 确定性：seed = seedOverride>=0 ? seedOverride : grid.seed，用 new System.Random(EffectiveSeed)，
    ///    绝不碰 UnityEngine.Random（保证同 seed 反复生成逐格一致）；
    ///  - 保留避让：自己的 reservedRects + 若 grid.ruinGenerator 存在，把每个 RuinInstance.bounds 外扩
    ///    1 格转保留区 → 不覆盖/破坏 AncientRelayRoom；
    ///  - 原子提交：每个节点先「规划全部格 → 全格合法性校验」→ 全过才一次性 SetTile 提交并登记
    ///    DiscoveryNodeInstance；任一步非法则整节点丢弃（rollback，绝不写半个节点）；
    ///  - 四类节点只组合现有系统反应：A/D 走 BlockCollapseSystem（SupportRock→LooseRock）、B 走
    ///    HotRockSystem + HasCapability(Cooling)、C 走 RuinSealSystem + HasCapability(RuinAccess) +
    ///    InventoryGrid/HandleTileDug。本类不持有第二套 collapse/heat/capability/ruin/奖励系统。
    ///
    /// 挂载：任意物体（常与 DigGrid 同物体）。由场景 Builder 注入 tile 引用与 reservedRects，
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

        // ---- 节点用到的 tile（Builder 注入真实 .asset；null 时对应节点跳过） ----

        [Header("结构 / 环境 tile")]
        [Tooltip("承重岩（A/D）。null → 对应节点不铺支撑结构。")]
        public TileDefinition supportRockTile;
        [Tooltip("松散岩（A/D）。null → 对应节点不铺松散段。")]
        public TileDefinition looseRockTile;
        [Tooltip("高温岩（B）。null → B 节点不铺热走廊。")]
        public TileDefinition hotRockTile;
        [Tooltip("遗迹封印门（C）。null → C 节点不铺。")]
        public TileDefinition sealTile;

        [Header("奖励 tile（按区域注入；null → 对应区域节点奖励跳过）")]
        [Tooltip("Shallow 高值矿（A/D 奖励）。")]
        public TileDefinition rewardShallow;
        [Tooltip("Mid 高值矿（B/D 奖励）。")]
        public TileDefinition rewardMid;
        [Tooltip("Deep 高值矿（A/B 奖励）。")]
        public TileDefinition rewardDeep;
        [Tooltip("文明数据碎片（C 奖励）。")]
        public TileDefinition ancientDataTile;
        [Tooltip("古代合金（C 奖励）。")]
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
                // 注意：FootprintNotPlainRock 实测返回 true == 该 footprint 全部为可覆盖的
                // 实心普通岩（value==0、非保留/未占用），即「可落点」。故此处取反：非纯岩才跳过。
                if (!FootprintNotPlainRock(x0, y0, w, h, placedCells)) continue;

                // 整节点原子提交：先规划（该类型的 layout），再校验与写。
                var inst = new DiscoveryNodeInstance
                {
                    type = spec.type,
                    instanceId = $"{DiscoveryNodeTypeInfo.StableId(spec.type)}_x{x0}_y{y0}",
                    usedSeed = EffectiveSeed,
                };
                inst.bounds = new RectInt(x0, y0, w, h);

                bool committed = TryCommit(spec, inst, x0, y0, placedCells);
                if (!committed) continue;

                for (int yy = 0; yy < inst.cells.Count; yy++) placedCells.Add(inst.cells[yy]);
                Nodes.Add(inst);
                placed++;
            }
            return placed;
        }

        /// <summary>footprint 内所有格：当前是「普通实心岩（Normal/value0）」且未被已提交节点占用、不在保留区 → 可作为覆盖目标。</summary>
        bool FootprintNotPlainRock(int x0, int y0, int w, int h, HashSet<Vector2Int> placedCells)
        {
            for (int dy = 0; dy < h; dy++)
                for (int dx = 0; dx < w; dx++)
                {
                    int x = x0 + dx, y = y0 + dy;
                    if (!grid.InBounds(x, y)) return false;
                    var t = grid.GetTile(x, y);
                    // 覆盖目标必须是实心普通岩且无价值（不是矿脉/空格/特殊块）；且未被本 pass 已占用
                    if (t == null || !t.isSolid || t.blockType != BlockType.Normal || t.value > 0) return false;
                    if (placedCells.Contains(new Vector2Int(x, y))) return false;
                }
            return true;
        }

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

        /// <summary>按类型把节点布局「规划并一次性提交」到 grid。</summary>
        bool TryCommit(DiscoveryNodeSpec spec, DiscoveryNodeInstance inst, int x0, int y0, HashSet<Vector2Int> placedCells)
        {
            var plan = new List<(int x, int y, TileDefinition def)>();
            int w = spec.footprintWidth, h = spec.footprintHeight;

            switch (spec.type)
            {
                case DiscoveryNodeType.AbandonedMiningPocket:
                    PlanAbandonedPocket(inst, plan, x0, y0, w, h);
                    break;
                case DiscoveryNodeType.ThermalVentChamber:
                    PlanThermalVent(inst, plan, x0, y0, w, h);
                    break;
                case DiscoveryNodeType.AncientSignalCache:
                    PlanAncientCache(inst, plan, x0, y0, w, h);
                    break;
                case DiscoveryNodeType.CollapsedResourcePocket:
                    PlanCollapsedPocket(inst, plan, x0, y0, w, h);
                    break;
                default:
                    return false;
            }

            // 全格校验：都在界内、非保留区、未占用
            if (plan.Count == 0) return false;
            var local = new HashSet<Vector2Int>();
            for (int i = 0; i < plan.Count; i++)
            {
                int x = plan[i].x, y = plan[i].y;
                if (!grid.InBounds(x, y)) return false;
                if (placedCells.Contains(new Vector2Int(x, y))) return false;
                if (!local.Add(new Vector2Int(x, y))) return false;   // 同一节点内重复 → 非法
            }

            // 全过 → 一次性提交
            for (int i = 0; i < plan.Count; i++)
            {
                var c = plan[i];
                grid.SetTile(c.x, c.y, c.def);
                inst.cells.Add(new Vector2Int(c.x, c.y));
            }
            return true;
        }

        void PlanAbandonedPocket(DiscoveryNodeInstance inst, List<(int x, int y, TileDefinition def)> plan,
            int x0, int y0, int w, int h)
        {
            // A 废弃采矿点：底部铺 2 块高值矿奖励；其上放一根 SupportRock 横梁；梁上竖 LooseRock 段。
            // 空间关系：奖励矿在承重岩正下方/前方，风险（拆梁→上方松散坍塌）与奖励共址但不随机散放。
            var reward = RewardForRegion("Shallow");
            // 底部奖励带（y0+h-1 行），取中心两格
            int ry = y0 + h - 1;
            int cx = x0 + w / 2;
            if (reward != null)
            {
                TryAdd(plan, cx - 1, ry, reward); TryAdd(inst.rewardCells, cx - 1, ry);
                TryAdd(plan, cx, ry, reward); TryAdd(inst.rewardCells, cx, ry);
            }
            // 承重横梁（在奖励带上一行，中心列；覆盖其正上方）
            if (supportRockTile != null)
            {
                int sx = cx, sy = ry - 1;
                TryAdd(plan, sx, sy, supportRockTile); TryAdd(inst.riskCells, sx, sy);
                // 梁上 LooseRock 段（若 h 够高）
                if (looseRockTile != null && ry - 2 >= y0)
                {
                    TryAdd(plan, sx, ry - 2, looseRockTile); TryAdd(inst.riskCells, sx, ry - 2);
                }
            }
            // seal 占位（无）
            inst.sealCell = new Vector2Int(-1, -1);
        }

        void PlanThermalVent(DiscoveryNodeInstance inst, List<(int x, int y, TileDefinition def)> plan,
            int x0, int y0, int w, int h)
        {
            // B 热裂隙室：左侧竖一列 HotRock「热走廊」，右侧放高值矿（危险之后才到）。Cooling 使同路径可处理。
            var reward = RewardForRegion("Mid");
            if (hotRockTile != null)
            {
                int hx = x0, hyMid = y0 + h / 2;
                TryAdd(plan, hx, hyMid, hotRockTile); TryAdd(inst.riskCells, hx, hyMid);
                // 热岩#2：取腔室底部行 y0+h-1；当 h/2 == h-1（即 h==2）时与 hyMid 重合，
                // 跳过以免写入重复格 —— TryCommit 会把「节点内重复格」判非法而整节点拒绝。
                int hyBot = y0 + h - 1;
                if (hyBot != hyMid) { TryAdd(plan, hx, hyBot, hotRockTile); TryAdd(inst.riskCells, hx, hyBot); }
            }
            if (reward != null)
            {
                int rx = x0 + w - 2, ry = y0 + h / 2;
                TryAdd(plan, rx, ry, reward); TryAdd(inst.rewardCells, rx, ry);
            }
            inst.sealCell = new Vector2Int(-1, -1);
        }

        void PlanAncientCache(DiscoveryNodeInstance inst, List<(int x, int y, TileDefinition def)> plan,
            int x0, int y0, int w, int h)
        {
            // C 文明信号缓存点：极小 = 1 RuinSeal 门 + 其后 1~2 文明奖励格。不复用 AncientRelayCoreSystem/完整房间。
            int cx = x0 + w / 2, midY = y0 + h / 2;
            if (sealTile != null)
            {
                TryAdd(plan, cx, midY, sealTile);
                inst.sealCell = new Vector2Int(cx, midY);
                inst.riskCells.Add(new Vector2Int(cx, midY));
            }
            if (ancientDataTile != null)
            {
                TryAdd(plan, cx, midY - 1, ancientDataTile); TryAdd(inst.rewardCells, cx, midY - 1);
            }
            if (ancientAlloyTile != null && h >= 3)
            {
                TryAdd(plan, cx, midY + 1, ancientAlloyTile); TryAdd(inst.rewardCells, cx, midY + 1);
            }
        }

        void PlanCollapsedPocket(DiscoveryNodeInstance inst, List<(int x, int y, TileDefinition def)> plan,
            int x0, int y0, int w, int h)
        {
            // D 坍塌资源囊：中央一列 —— 最深=奖励矿，其上=SupportRock 承重，承重再上=连续 LooseRock。
            // 玩家先挖奖励矿（深、安全）→ 到手；若先挖承重 SupportRock → BlockCollapseSystem 检测其
            // 【上方】同列 LooseRock 段并 Unstable→落格（可能砸到奖励带/玩家）。挖掘顺序 → 不同局部后果。
            int cx = x0 + w / 2;
            int ry = y0 + h - 1;            // 最深一行 = 奖励带
            int supY = ry - 1;              // 承重岩在奖励带之上
            var reward = RewardForRegion("Mid");
            if (reward != null)
            {
                TryAdd(plan, cx, ry, reward); TryAdd(inst.rewardCells, cx, ry);
                if (cx - 1 >= x0) { TryAdd(plan, cx - 1, ry, reward); TryAdd(inst.rewardCells, cx - 1, ry); }
            }
            if (supportRockTile != null && supY >= y0)
            {
                TryAdd(plan, cx, supY, supportRockTile); TryAdd(inst.riskCells, cx, supY);
                // 承重岩【上方】连续 LooseRock（向上扫描触发落格链）：y0 .. supY-1
                if (looseRockTile != null)
                    for (int yy = supY - 1; yy >= y0; yy--)
                    {
                        TryAdd(plan, cx, yy, looseRockTile); TryAdd(inst.riskCells, cx, yy);
                    }
            }
            inst.sealCell = new Vector2Int(-1, -1);
        }

        TileDefinition RewardForRegion(string regionId)
        {
            switch (regionId)
            {
                case "Shallow": return rewardShallow;
                case "Mid": return rewardMid;
                case "Deep": return rewardDeep;
                default: return null;
            }
        }

        static void TryAdd(List<(int x, int y, TileDefinition def)> list, int x, int y, TileDefinition def)
        {
            if (def != null) list.Add((x, y, def));
        }

        static void TryAdd(List<Vector2Int> list, int x, int y) => list.Add(new Vector2Int(x, y));
    }
}
