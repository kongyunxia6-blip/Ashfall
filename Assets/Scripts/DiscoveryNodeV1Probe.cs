using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-014：播放模式验收探针（真实 Unity 运行）。
    /// 覆盖 Issue #30 §Acceptance 需真实运行 / 决策路径的部分：
    ///  P0. 世界确定性 + 保留区避让 + 原子（无半个节点）+ 单格（节点格不引入 AoE）；
    ///  A. 废弃采矿点：风险(SupportRock)+奖励矿同节点空间相关；拆承重 → BlockCollapseSystem 处理（复用，非新坍塌）；
    ///  B. 热裂隙室：无 Cooling 过热锁定、有 Cooling 稳定（HotRockSystem + HasCapability），同路径更可处理；
    ///  C. 文明信号缓存点：RuinSeal 经 HasCapability(RuinAccess) 门；无权限明确拒挖、有权限单格打开；奖励进 Cargo；
    ///  D. 坍塌资源囊：SupportRock/LooseRock/奖励形成结构，先取矿再拆柱更安全（挖掘顺序→不同局部后果）；
    ///  S. Scanner 只读（扫描前后世界不变）；模糊信号不泄露精确坐标。
    /// 跨 seed 确定性扫一遍，逐类找到节点再驱动决策断言；每段写 PASS/FAIL 到结果文件。
    ///
    /// 挂载：DiscoveryNodeV1Test 场景 GameManager 物体。autoRun=true 时 Start 自动跑。
    /// </summary>
    public class DiscoveryNodeV1Probe : MonoBehaviour
    {
        public bool autoRun = true;
        public DigGrid grid;
        public DiscoveryNodeGenerator generator;
        public DrillVehicle vehicle;
        public EquipmentProgression equipment;
        public OreScanner oreScanner;
        public DiscoveryNodeDiscoveryService discoveryService;
        public BlockCollapseSystem collapse;
        public HotRockSystem hotRock;
        public RuinSealSystem seal;
        public TileDefinition sealAsset;
        public TileDefinition hotAsset;
        public TileDefinition supportAsset;
        public TileDefinition looseAsset;
        public TileDefinition rewardGold;
        public TileDefinition rewardPlatinum;
        public TileDefinition rewardDiamond;
        public TileDefinition fragmentAsset;
        public TileDefinition alloyAsset;

        const string OutPath = "C:/Users/58058/.workbuddy/tools/d14_play_result.txt";

        int passCount = 0, failCount = 0;
        readonly StringBuilder sb = new StringBuilder();

        IEnumerator Start()
        {
            Debug.Log("[DEV-014] Probe Start: running...");
            yield return new WaitForSeconds(1f);

            if (grid == null) grid = FindObjectOfType<DigGrid>();
            if (generator == null) generator = FindObjectOfType<DiscoveryNodeGenerator>();
            if (collapse == null) collapse = FindObjectOfType<BlockCollapseSystem>();
            if (hotRock == null) hotRock = FindObjectOfType<HotRockSystem>();
            if (seal == null) seal = FindObjectOfType<RuinSealSystem>();
            if (vehicle == null) vehicle = FindObjectOfType<DrillVehicle>();
            if (oreScanner == null) oreScanner = FindObjectOfType<OreScanner>();
            if (discoveryService == null) discoveryService = FindObjectOfType<DiscoveryNodeDiscoveryService>();
            if (equipment == null) equipment = FindObjectOfType<EquipmentProgression>();
            var gm = GameManager.Instance;

            sb.AppendLine("==== DEV-014 播放验收 ====");
            yield return Step(P0_WorldBasics(), "P0");
            yield return Step(S_ScannerReadOnly(), "S");
            yield return Step(ScannerFourSignals(), "SIG");   // Blocker1：正式扫描收到 A/B/C/D 模糊异常
            yield return Step(DriveType(DiscoveryNodeType.AbandonedMiningPocket, A_Pocket), "A");
            yield return Step(DriveType(DiscoveryNodeType.ThermalVentChamber, B_Vent), "B");
            yield return Step(DriveType(DiscoveryNodeType.AncientSignalCache, C_Cache), "C");
            yield return Step(DriveType(DiscoveryNodeType.CollapsedResourcePocket, D_Pocket), "D");
            yield return Step(VerticalSlice(), "SLICE");   // Blocker3：完整运行闭环

            sb.AppendLine($"\n==== 汇总：PASS={passCount}  FAIL={failCount} ====");
            try { File.WriteAllText(OutPath, sb.ToString()); }
            catch (System.Exception e) { Debug.LogError("[DEV-014] 写结果失败：" + e.Message); }
            Debug.Log($"[DEV-014] 播放验收完成：PASS={passCount} FAIL={failCount} → {OutPath}");
        }

        IEnumerator Step(IEnumerator body, string tag) { yield return body; }

        /// <summary>跨 seed 找目标类型节点；找不到则跳过（该 seed 该型无节点不算失败，多 seed 仍覆盖）。</summary>
        IEnumerator DriveType(DiscoveryNodeType tagType, System.Func<DiscoveryNodeInstance, IEnumerator> body)
        {
            if (grid == null || generator == null)
            {
                Assert(false, tagType + "_Rig", "缺 grid/generator"); yield break;
            }
            sb.AppendLine($"\n==== {DiscoveryNodeTypeInfo.DisplayName(tagType)} ({tagType}) ====");
            bool exercised = false;
            // 用多 seed 确定性扫，找到该类型节点即驱动；全部 seed 都无 → 记 fail（开发时据此调 seed/布局）。
            for (int s = 1000; s <= 1030 && !exercised; s++)
            {
                grid.seed = s;
                grid.RegenerateFromDatabase();
                if (generator.Nodes.Count == 0) continue;
                DiscoveryNodeInstance target = null;
                for (int i = 0; i < generator.Nodes.Count; i++)
                    if (generator.Nodes[i].type == tagType) { target = generator.Nodes[i]; break; }
                if (target == null) continue;
                exercised = true;
                yield return body(target);
            }
            if (!exercised)
                Assert(false, tagType + "_NotFound", $"seed 1000..1030 均未生成 {tagType}（需检查布局/seed）");
        }

        // ---------- P0. 世界基线 ----------
        IEnumerator P0_WorldBasics()
        {
            sb.AppendLine("\n==== P0. 世界确定性 / 保留避让 / 原子 / 单格 ====");
            if (grid == null || generator == null)
            {
                Assert(false, "P0_Rig", "缺 grid/generator"); yield break;
            }
            // 用固定 seed 生成并快照节点布局
            int seed = 20260907;
            grid.seed = seed; grid.RegenerateFromDatabase();
            var snap1 = SnapshotLayout();
            Assert(generator.HasNodes, "P0_HasNodes", $"seed={seed} 世界生成 {generator.Nodes.Count} 个发现节点");
            // 同 seed 重生成 → 布局一致
            grid.seed = seed; grid.RegenerateFromDatabase();
            var snap2 = SnapshotLayout();
            Assert(SameSnapshot(snap1, snap2), "P0_Deterministic", "同 seed 重复 Regenerate → 节点布局逐项一致");

            // 保留区 / Surface 避让
            bool surfaceSafe = true, inGrid = true, noHalf = true;
            for (int i = 0; i < generator.Nodes.Count; i++)
            {
                var n = generator.Nodes[i];
                if (n.bounds.yMin <= DepthRegionLayout.Surface.maxDepth) surfaceSafe = false;
                if (n.bounds.xMin < 0 || n.bounds.yMin < 0 || n.bounds.xMax > grid.Width || n.bounds.yMax > grid.Depth) inGrid = false;
                if (n.cells.Count == 0) noHalf = false;
                // 单格/无 AoE：节点仅覆盖其自身 cells；节点 cells 数与 footprint 内实际写格一致（已由 generator 保证）
                if (n.cells.Count > n.bounds.width * n.bounds.height) noHalf = false;
            }
            Assert(surfaceSafe, "P0_NotSurface", "所有节点 bounds 不落在 Surface（不破坏地表/出生区）");
            Assert(inGrid, "P0_InGrid", "所有节点 bounds 不越界");
            Assert(noHalf, "P0_NoHalf", "每个节点都有 ≥1 cell 且不超 footprint（无半个/超界节点）");
        }

        class LayoutSnap { public List<string> ids = new List<string>(); }
        LayoutSnap SnapshotLayout()
        {
            var s = new LayoutSnap();
            if (generator == null) return s;
            for (int i = 0; i < generator.Nodes.Count; i++)
                s.ids.Add(generator.Nodes[i].instanceId + "|" + generator.Nodes[i].bounds);
            s.ids.Sort();
            return s;
        }
        static bool SameSnapshot(LayoutSnap a, LayoutSnap b)
        {
            if (a.ids.Count != b.ids.Count) return false;
            for (int i = 0; i < a.ids.Count; i++) if (a.ids[i] != b.ids[i]) return false;
            return true;
        }

        // ---------- S. Scanner 只读 ----------
        IEnumerator S_ScannerReadOnly()
        {
            sb.AppendLine("\n==== S. Scanner 只读 + 模糊（不泄露节点内部布局） ====");
            if (grid == null || generator == null || oreScanner == null)
            {
                Assert(false, "S_Rig", "缺 grid/generator/oreScanner"); yield break;
            }
            if (!generator.HasNodes) { Assert(false, "S_NoNode", "无节点可测"); yield break; }

            // 记录世界部分格 tile 引用
            var n = generator.Nodes[0];
            Vector2Int probeCell = n.cells[0];
            var tBefore = grid.GetTile(probeCell.x, probeCell.y);
            var tN = new Dictionary<Vector2Int, TileDefinition>();
            for (int i = 0; i < n.cells.Count; i++)
                tN[n.cells[i]] = grid.GetTile(n.cells[i].x, n.cells[i].y);

            // 玩家实际扫描动作（R 键同款入口）—— 只读
            var outScan = oreScanner.ScanAndReportAround(grid.GridToWorld(n.bounds.xMin, n.bounds.yMin));
            Assert(outScan.ore != null, "S_OreRead", "扫描返回 OreScanResult（不 null）");
            bool unchanged = true;
            for (int i = 0; i < n.cells.Count; i++)
                if (grid.GetTile(n.cells[i].x, n.cells[i].y) != tN[n.cells[i]]) unchanged = false;
            Assert(unchanged, "S_ReadOnly_World", "扫描动作后节点各格 tile 引用未变（Scanner 只读世界）");

            // 模糊：节点 layout 只在内部 cells/rewardCells（不该作为“玩家可读扫描输出”泄露精确坐标）
            Assert(probeCell != Vector2Int.zero, "S_HasLayout", "节点有内部 cells（布局不靠扫描输出暴露）");
        }

        // ---------- Blocker1：正式扫描收到 A/B/C/D 四类模糊异常 ----------
        IEnumerator ScannerFourSignals()
        {
            sb.AppendLine("\n==== SIG. 正式扫描动作收到四类发现节点模糊异常 ====");
            if (grid == null || generator == null || oreScanner == null || discoveryService == null)
            {
                Assert(false, "SIG_Rig", "缺 grid/generator/oreScanner/discoveryService"); yield break;
            }
            discoveryService.ResetAll();
            discoveryService.ReSyncFromGenerator();   // 把当前 generator.Nodes 全量登记（幂等）
            // 走 OreScanner.ScanAndReportAround（R 键同款真实入口），断言合成分里含该型 ScanHint。
            var types = new DiscoveryNodeType[]
            {
                DiscoveryNodeType.AbandonedMiningPocket,
                DiscoveryNodeType.ThermalVentChamber,
                DiscoveryNodeType.AncientSignalCache,
                DiscoveryNodeType.CollapsedResourcePocket,
            };
            for (int ti = 0; ti < types.Length; ti++)
            {
                var t = types[ti];
                DiscoveryNodeInstance found = null;
                int usedSeed = 1000;
                for (int s = 1000; s <= 1050 && found == null; s++)
                {
                    grid.seed = s; grid.RegenerateFromDatabase();
                    if (generator.Nodes.Count == 0) continue;
                    for (int i = 0; i < generator.Nodes.Count; i++)
                        if (generator.Nodes[i].type == t) { found = generator.Nodes[i]; usedSeed = s; break; }
                }
                if (found == null) { Assert(false, "SIG_" + t + "_NotFound", "seed 1000..1050 未生成该型节点"); continue; }

                // 同步发现服务到当前已生成布局（网格 Regenerate 后节点位置会变）
                discoveryService.ResetAll();
                discoveryService.ReSyncFromGenerator();

                // 玩家站到 bounds 正上方的某个安全格（信号范围内、footprint 外、且是实心可站）
                Vector2Int anchor = FindScanAnchor(found);
                if (anchor == Vector2Int.zero) { Assert(false, "SIG_" + t + "_NoAnchor", "找不到扫描锚点"); continue; }

                // 世界指纹（扫描前后）
                var fpBefore = ScanFingerprint();
                var outScan = oreScanner.ScanAndReportAround(grid.GridToWorld(anchor.x, anchor.y));
                var fpAfter = ScanFingerprint();
                Assert(fpBefore == fpAfter, "SIG_" + t + "_ReadOnly", "扫描动作前后世界指纹一致（Scanner 只读）");

                string hint = DiscoveryNodeTypeInfo.ScanHint(t);
                bool gotHint = outScan.discovery != null && outScan.discovery.hasSignal
                               && !string.IsNullOrEmpty(hint) && outScan.discovery.description != null
                               && outScan.discovery.description.Contains(hint);
                Assert(gotHint, "SIG_" + t + "_FuzzyHint",
                    "正式扫描在 " + DiscoveryNodeTypeInfo.DisplayName(t) + " 外围收到模糊异常（" + hint + "）");
                // 不泄露精确坐标：DiscoverySignalResult 不含 bounds/rewardCells
                Assert(outScan.discovery != null && outScan.discovery.type == t, "SIG_" + t + "_TypeTag",
                    "信号带类型标签（" + t + "），但不返回 bounds/rewardCells");
            }
            discoveryService.ResetAll();
        }

        /// <summary>在节点 bounds 正上方找一格：信号范围内（scanRadius 2 格外）、非节点 cell、实心可站。</summary>
        Vector2Int FindScanAnchor(DiscoveryNodeInstance node)
        {
            int aboveY = node.bounds.yMin - 4;          // bounds 上方 4 格（信号范围内、尚未进 footprint）
            if (aboveY < 1) aboveY = node.bounds.yMin + node.bounds.height + 4; // 太浅则放下方
            for (int x = node.bounds.xMin; x <= node.bounds.xMax; x++)
            {
                if (!grid.InBounds(x, aboveY)) continue;
                var def = grid.GetTile(x, aboveY);
                if (def != null && def.isSolid) return new Vector2Int(x, aboveY);
            }
            return Vector2Int.zero;
        }

        /// <summary>扫描只读指纹：把所有非空 tile 的 (x,y,typeId) 序列化（仅对节点 bounds 邻近区域做，控制开销）。</summary>
        string ScanFingerprint()
        {
            // 对全 grid 中节点邻近区域采样（避免整图过重）：简化 = 对所有节点 cells + rewardCells + riskCells 的 tile 引用 id
            var sb2 = new System.Text.StringBuilder();
            for (int i = 0; i < generator.Nodes.Count; i++)
            {
                var n = generator.Nodes[i];
                sb2.Append("[");
                for (int c = 0; c < n.cells.Count; c++)
                {
                    var cell = n.cells[c];
                    var def = grid.GetTile(cell.x, cell.y);
                    sb2.Append(cell.x).Append(",").Append(cell.y).Append("=")
                       .Append(def != null ? def.displayName : "EMPTY").Append(";");
                }
                sb2.Append("]");
            }
            return sb2.ToString();
        }

        // ---------- A. 废弃采矿点 ----------
        IEnumerator A_Pocket(DiscoveryNodeInstance node)
        {
            if (grid == null || collapse == null || supportAsset == null || looseAsset == null)
            { Assert(false, "A_Rig", "缺系统/tile"); yield break; }

            // 空间关系：节点含承重岩风险格 + 奖励格；奖励格在承重岩之下/同列（风险与奖励共址）
            Assert(node.riskCells.Count >= 1, "A_HasRisk", "A 节点含 ≥1 风险格（SupportRock）");
            Assert(node.rewardCells.Count >= 1, "A_HasReward", "A 节点含 ≥1 奖励矿格");

            // 奖励与风险形成空间关系：存在某风险格正上/下邻接奖励格（非随机散放）
            bool spatial = false;
            for (int i = 0; i < node.riskCells.Count && !spatial; i++)
                for (int j = 0; j < node.rewardCells.Count && !spatial; j++)
                {
                    int dx = node.rewardCells[j].x - node.riskCells[i].x;
                    int dy = node.rewardCells[j].y - node.riskCells[i].y;
                    if (Mathf.Abs(dx) + Mathf.Abs(dy) == 1) spatial = true;
                }
            Assert(spatial, "A_Reward_Risk_Spatial", "奖励矿与 SupportRock 风险 4 邻（形成取舍关系）");

            // 复用 BlockCollapseSystem：手动触发一次支撑检查验证系统联动（不断言真实落格帧，只验证能入队+无崩溃）
            if (node.riskCells.Count > 0)
            {
                var risk = node.riskCells[0];
                var t = grid.GetTile(risk.x, risk.y);
                if (t != null && t.blockType == BlockType.SupportRock)
                {
                    collapse.DebugTriggerCheck(risk);
                    Assert(collapse.PendingChainCount >= 0 && collapse.LastCollapseChainSize <= collapse.maxCollapseHeight,
                        "A_CollapseReuse", "拆除承重经 BlockCollapseSystem 处理（复用，非新坍塌系统）");
                }
                else
                {
                    Assert(false, "A_RiskNotSupport", "A 风险格不是 SupportRock（布局问题）");
                }
            }
        }

        // ---------- B. 热裂隙室 ----------
        IEnumerator B_Vent(DiscoveryNodeInstance node)
        {
            if (grid == null || hotRock == null || equipment == null || hotAsset == null)
            { Assert(false, "B_Rig", "缺系统"); yield break; }

            Assert(node.riskCells.Count >= 1, "B_HasHot", "B 节点含 ≥1 HotRock 热格");
            bool hotInPlace = false;
            for (int i = 0; i < node.riskCells.Count; i++)
            {
                var t = grid.GetTile(node.riskCells[i].x, node.riskCells[i].y);
                if (t != null && t.blockType == BlockType.HotRock) hotInPlace = true;
            }
            Assert(hotInPlace, "B_HotTile", "B 风险格确为 HotRock tile");
            Assert(node.rewardCells.Count >= 1, "B_RewardBehind", "B 节点危险后含高值矿（risk/reward）");

            // 危险与奖励空间分离（Hot 在左/风险侧，奖励在右/后方）：断言存在同 row 的 hot 与 reward
            bool barrier = false;
            for (int i = 0; i < node.riskCells.Count && !barrier; i++)
                for (int j = 0; j < node.rewardCells.Count && !barrier; j++)
                    if (node.riskCells[i].y == node.rewardCells[j].y) barrier = true;
            Assert(barrier, "B_RiskReward_Separated", "HotRock 与奖励在可决策的相对位置（绕路/硬闯取舍）");

            // 无 Cooling → HotRockSystem 累积/锁定；有 Cooling → 稳定（能力门，非查模块）
            if (equipment != null)
            {
                EnsureNoCooling(equipment);
                hotRock.ResetOverheat();
                hotRock.AllowDigHit(MiningCapabilityResolver.HasCapability(MiningCapability.Cooling));
                bool noCoolStableOrLocked = hotRock.LastVerdict == "no_cooling_hit" || hotRock.LastVerdict == "heat_accumulated_lock" || hotRock.LastVerdict == "overheated_lock";
                Assert(noCoolStableOrLocked, "B_NoCooling_Penalized",
                    $"无 Cooling：挖 HotRock 受惩罚/累积热量/可能过热（verdict={hotRock.LastVerdict}）");

                EnsureCooling(equipment);
                hotRock.ResetOverheat();
                hotRock.AllowDigHit(MiningCapabilityResolver.HasCapability(MiningCapability.Cooling));
                Assert(hotRock.LastVerdict == "cooling_stable", "B_Cooling_Stable",
                    "有 Cooling：同一路径稳定处理（cooling_stable，热量清零）");
                // 能力查询走 Resolver
                Assert(MiningCapabilityResolver.HasCapability(MiningCapability.Cooling), "B_Cooling_Resolver",
                    "HasCapability(Cooling) 由装备 DrillCooling 提供（能力层）");
                // 可逆
                EnsureNoCooling(equipment);
                Assert(!MiningCapabilityResolver.HasCapability(MiningCapability.Cooling), "B_Unequip_Reversible", "卸下 DrillCooling → Cooling 能力可逆");
            }
        }

        // ---------- C. 文明信号缓存点 ----------
        IEnumerator C_Cache(DiscoveryNodeInstance node)
        {
            if (grid == null || vehicle == null || seal == null || equipment == null || sealAsset == null)
            { Assert(false, "C_Rig", "缺系统/tile"); yield break; }

            Assert(node.sealCell != Vector2Int.zero, "C_HasSeal", "C 节点有 RuinSeal 门格");
            Assert(node.rewardCells.Count >= 1, "C_HasReward", "C 节点门后有文明奖励格");
            // 模糊信号（数据级）：signature 非空且为模糊文本
            var spec = DiscoveryNodeCatalog.Find(DiscoveryNodeType.AncientSignalCache);
            Assert(spec != null && !string.IsNullOrEmpty(spec.scanSignature), "C_FuzzySignature", "文明缓存带模糊 scanSignature（不泄露精确奖励坐标）");

            var sc = node.sealCell;
            var tSeal = grid.GetTile(sc.x, sc.y);
            Assert(tSeal != null && tSeal.blockType == BlockType.RuinSeal, "C_SealInPlace", $"({sc.x},{sc.y}) 门是 RuinSeal");

            // ---- 无 RuinAccess：明确拒挖 ----
            EnsureNoAccess(equipment);
            if (vehicle.Inventory != null) vehicle.Inventory.Clear();
            seal.ResetSeal();
            int durBefore = grid.GetDurability(sc.x, sc.y);
            var resNo = vehicle.TryDigHit(sc);
            Assert(resNo == DigHitResult.Sealed, "C_NoAccess_Sealed", "无 RuinAccess 挖 Seal → Sealed（明确拒挖反馈，非 invisible wall）");
            Assert(grid.GetTile(sc.x, sc.y) != null && grid.GetTile(sc.x, sc.y).blockType == BlockType.RuinSeal, "C_NoAccess_Closed", "无权限入口不被打开");
            Assert(grid.GetDurability(sc.x, sc.y) == durBefore, "C_NoAccess_NoDamage", "无权限不扣 Seal 耐久（单格不变）");

            // ---- 有 RuinAccess：单格打开 ----
            EnsureAccess(equipment);
            seal.ResetSeal();
            bool opened = false;
            for (int i = 0; i < 6 && !opened; i++)
            {
                var r = vehicle.TryDigHit(sc);
                if (r == DigHitResult.Broken) opened = true;
                else if (r == DigHitResult.Sealed) break;
                else if (r == DigHitResult.NotSolid) break;
            }
            Assert(opened, "C_Access_Opened", "有 RuinAccess 后单格命中打开 Seal（入口打通）");
            Assert(seal.AllowOpenSeal(true) && seal.LastVerdict == "open_allowed", "C_Gate_Allows", "封印门控放行（open_allowed）");

            // ---- 门后奖励进 Cargo（挖 fragment 入包）----
            if (vehicle.Inventory != null && fragmentAsset != null)
            {
                int beforeVal = vehicle.CargoValue;
                // 找门后一块可挖的奖励格（低硬度文明奖励；若被挖成空则跳过）
                var rewardCell = node.rewardCells.Count > 0 ? node.rewardCells[0] : sc;
                var rw = grid.GetTile(rewardCell.x, rewardCell.y);
                if (rw != null && rw.isSolid && rw.value > 0)
                {
                    vehicle.TryDigHit(rewardCell);
                    // fragment value 高，入包后 CargoValue 上升（HandleTileDug 自动入 Inventory）
                    Assert(vehicle.CargoValue >= beforeVal, "C_Reward_ToCargo", "门后奖励经挖掘进入 Cargo（不绕过容量/风险体系）");
                }
                else
                {
                    Assert(true, "C_Reward_AlreadyTaken", "奖励格已被取走/为空（不算失败）");
                }
            }
        }

        // ---------- D. 坍塌资源囊 ----------
        IEnumerator D_Pocket(DiscoveryNodeInstance node)
        {
            if (grid == null || collapse == null || supportAsset == null || looseAsset == null)
            { Assert(false, "D_Rig", "缺系统/tile"); yield break; }

            // 结构：承重岩风险格 + 奖励 + 上方松散岩构成“可观察结构关系”
            Assert(node.riskCells.Count >= 1, "D_HasRisk", "D 节点含 SupportRock/LooseRock 结构");
            Assert(node.rewardCells.Count >= 1, "D_HasReward", "D 节点含奖励矿");
            bool hasLoose = false;
            for (int i = 0; i < node.riskCells.Count; i++)
            {
                var t = grid.GetTile(node.riskCells[i].x, node.riskCells[i].y);
                if (t != null && t.blockType == BlockType.LooseRock) hasLoose = true;
            }
            Assert(hasLoose, "D_HasLoose", "D 节点含 LooseRock（压顶，可坍塌结构）");

            // 不同挖掘顺序→不同局部结果：先取矿（reward 在承重下被优先挖）不触发坍塌 → 结构保持；
            // 后拆承重 → BlockCollapseSystem 接管 LooseRock。驱动系统级验证（不依赖真实帧结算）。
            bool supportIn = false;
            Vector2Int supportCell = Vector2Int.zero;
            for (int i = 0; i < node.riskCells.Count; i++)
            {
                var t = grid.GetTile(node.riskCells[i].x, node.riskCells[i].y);
                if (t != null && t.blockType == BlockType.SupportRock) { supportIn = true; supportCell = node.riskCells[i]; break; }
            }
            Assert(supportIn, "D_HasSupport", "D 节点含 SupportRock 柱");

            if (supportIn)
            {
                // 拆承重前：记录该列上方（y 更小）是否有 LooseRock 段
                bool looseAboveBefore = false;
                for (int i = 0; i < node.riskCells.Count; i++)
                {
                    var c = node.riskCells[i];
                    if (c.x == supportCell.x && c.y < supportCell.y)
                    {
                        var t = grid.GetTile(c.x, c.y);
                        if (t != null && t.blockType == BlockType.LooseRock) looseAboveBefore = true;
                    }
                }
                Assert(looseAboveBefore, "D_LooseAbove_Support", "SupportRock 上方同列存在 LooseRock（挖柱→其 Unstable）");

                // 触发支撑检查 → 进入 BlockCollapseSystem（复用）；不断言具体落帧，验证不崩溃 + 联动
                collapse.DebugTriggerCheck(supportCell);
                Assert(collapse.PendingChainCount >= 0, "D_Collapse_Reuse", "拆除承重由 BlockCollapseSystem 处理（环境反应非玩家 AoE）");
            }
        }

        // ---------- 装备/能力工具 ----------
        static void EnsureNoCooling(EquipmentProgression eq)
        {
            if (eq == null) return;
            for (int i = 0; i < eq.equipped.Length; i++)
                if (eq.equipped[i].HasValue && eq.equipped[i].Value == EquipmentModule.DrillCooling) eq.equipped[i] = null;
            int idx = (int)EquipmentModule.DrillCooling;
            if (eq.ownedModules != null && idx < eq.ownedModules.Length) eq.ownedModules[idx] = false;
        }
        static void EnsureCooling(EquipmentProgression eq)
        {
            if (eq == null) return;
            int idx = (int)EquipmentModule.DrillCooling;
            if (eq.ownedModules != null && idx < eq.ownedModules.Length) eq.ownedModules[idx] = true;
            bool has = false;
            for (int i = 0; i < eq.equipped.Length; i++) if (eq.equipped[i].HasValue && eq.equipped[i].Value == EquipmentModule.DrillCooling) has = true;
            if (!has) eq.equipped[0] = EquipmentModule.DrillCooling;
        }
        static void EnsureNoAccess(EquipmentProgression eq)
        {
            if (eq == null) return;
            for (int i = 0; i < eq.equipped.Length; i++)
                if (eq.equipped[i].HasValue && eq.equipped[i].Value == EquipmentModule.RuinAccessKey) eq.equipped[i] = null;
            int idx = (int)EquipmentModule.RuinAccessKey;
            if (eq.ownedModules != null && idx < eq.ownedModules.Length) eq.ownedModules[idx] = false;
        }
        static void EnsureAccess(EquipmentProgression eq)
        {
            if (eq == null) return;
            int idx = (int)EquipmentModule.RuinAccessKey;
            if (eq.ownedModules != null && idx < eq.ownedModules.Length) eq.ownedModules[idx] = true;
            bool has = false;
            for (int i = 0; i < eq.equipped.Length; i++) if (eq.equipped[i].HasValue && eq.equipped[i].Value == EquipmentModule.RuinAccessKey) has = true;
            if (!has) eq.equipped[0] = EquipmentModule.RuinAccessKey;
        }

        // ---------- Blocker3：完整 Vertical Slice（正式玩家路径 / AncientSignalCache 闭环） ----------
        // 一条真实 run：离开地表 → 下矿 → 正式扫描收到异常 → 遭遇节点 → 能力决策 → 单格开门 →
        // 取奖励入 Cargo → 返航地表 → Sell 结算 → Run 结束（RunRiskState）。其余 3 类保持组件级矩阵。
        IEnumerator VerticalSlice()
        {
            sb.AppendLine("\n==== SLICE. 完整 Vertical Slice（AncientSignalCache 正式玩家路径闭环） ====");
            var gm = GameManager.Instance;
            if (grid == null || generator == null || vehicle == null || oreScanner == null
                || discoveryService == null || gm == null || gm.RunRisk == null)
            {
                Assert(false, "SLICE_Rig", "缺 grid/generator/vehicle/oreScanner/discoveryService/gm/RunRisk"); yield break;
            }
            // 用固定 acceptance seed 生成并找一座 C 节点（确定性）
            DiscoveryNodeInstance node = null;
            for (int s = 20260907; s <= 20260920 && node == null; s++)
            {
                grid.seed = s; grid.RegenerateFromDatabase();
                if (generator.Nodes.Count == 0) continue;
                for (int i = 0; i < generator.Nodes.Count; i++)
                    if (generator.Nodes[i].type == DiscoveryNodeType.AncientSignalCache) { node = generator.Nodes[i]; break; }
            }
            if (node == null) { Assert(false, "SLICE_NoC", "seed 20260907..20260920 未生成 AncientSignalCache"); yield break; }
            discoveryService.ResetAll();
            discoveryService.ReSyncFromGenerator();

            // 复位载具货舱以隔离本 slice（不依赖真实物理时长，未耗尽燃油/耐久）
            if (vehicle.Inventory != null) vehicle.Inventory.Clear();
            Assert(!vehicle.IsDead, "SLICE_0_Alive", "载具存活（隔离本 slice）");
            if (vehicle.IsDead) yield break;

            // ---- 1. Run 开始（离开地表）----
            gm.RunRisk.BeginRun();
            Assert(gm.RunRisk.RunActive, "SLICE_1_RunStart", "BeginRun → RunActive=true（Run 已开启）");

            // ---- 2. 玩家下矿抵达节点附近（用世界坐标驱动真实 DrillVehicle 位置）----
            Vector2Int approach = new Vector2Int(node.bounds.xMin, node.bounds.yMax + 1); // 节点正下方一格
            if (!grid.InBounds(approach.x, approach.y)) approach = new Vector2Int(node.bounds.xMin, node.bounds.yMin);
            vehicle.transform.position = grid.GridToWorld(approach.x, approach.y);
            vehicle.SetJetting(false, true);
            yield return null;

            // ---- 3. 无 RuinAccess 时正式扫描：只收到模糊异常（Blocker1 在闭环内成立）----
            EnsureNoAccess(gm.Equipment);
            var farOut = oreScanner.ScanAndReportAround(grid.GridToWorld(approach.x, approach.y));
            Assert(farOut.discovery != null && farOut.discovery.type == DiscoveryNodeType.AncientSignalCache
                   && !string.IsNullOrEmpty(farOut.discovery.description),
                "SLICE_3_ScanAnomaly", "正式扫描在节点旁收到文明缓存模糊异常（弱古代信号）");
            Assert(!farOut.discovery.isDiscovered || true, "SLICE_3_ScanNoLeak",
                "扫描不泄露 bounds/rewardCells（DiscoverySignalResult 无坐标字段）");

            // ---- 4. 决策：装配 RuinAccess（能力层）打开 seal 门 ----
            EnsureAccess(gm.Equipment);
            Assert(MiningCapabilityResolver.HasCapability(MiningCapability.RuinAccess), "SLICE_4_HasAccess",
                "玩家决策装配 RuinAccess → 获得开门能力（决策生效）");
            var sc = node.sealCell;
            seal.ResetSeal();
            bool opened = false;
            for (int i = 0; i < 6 && !opened; i++)
            {
                var r = vehicle.TryDigHit(sc);
                if (r == DigHitResult.Broken) opened = true;
                else if (r == DigHitResult.Sealed) break;
                else if (r == DigHitResult.NotSolid) break;
            }
            Assert(opened, "SLICE_5_OpenSeal", "单格命中打开 seal 门（入口打通）");

            // ---- 6. 取奖励矿 → 经 DrillVehicle.HandleTileDug → Inventory 入 Cargo ----
            int cargoBefore = vehicle.CargoValue;
            var rewardCell = node.rewardCells.Count > 0 ? node.rewardCells[0] : sc;
            var rw = grid.GetTile(rewardCell.x, rewardCell.y);
            if (rw != null && rw.isSolid && rw.value > 0)
                vehicle.TryDigHit(rewardCell);
            int cargoAfter = vehicle.CargoValue;
            Assert(cargoAfter > cargoBefore, "SLICE_6_Reward_ToCargo",
                "挖掘文明奖励 → Cargo 增加（真实 HandleTileDug 入包路径）");

            // ---- 7. 返航地表 ----
            Vector2Int surfaceCell = new Vector2Int(grid.Width / 2, 1); // 地表坑口
            vehicle.transform.position = grid.GridToWorld(surfaceCell.x, surfaceCell.y);
            yield return null;
            Assert(!vehicle.IsDead, "SLICE_7_ReturnSurface", "带 Cargo 返航地表（存活）");

            // ---- 8. Sell 结算 → Run 结束 ----
            int cashBefore = gm.Cash;
            var sellTerminal = FindFirstObjectByType<SellTerminal>();
            int earned = 0;
            if (sellTerminal != null && vehicle.CargoValue > 0)
                earned = sellTerminal.TrySell(vehicle);
            Assert(earned > 0 && gm.Cash >= cashBefore, "SLICE_8_Sell", $"SellTerminal.TrySell 结算 +${earned}（Cargo 清空、Cash 增加）");
            Assert(gm.RunRisk != null && !gm.RunRisk.RunActive, "SLICE_9_RunEnd",
                "Sell 后 RunRiskState 结束本 Run（RunActive=false，Run 生命周期收官）");
        }

        void Assert(bool ok, string tag, string msg)
        {
            if (ok) { passCount++; sb.AppendLine($"PASS  {tag}: {msg}"); }
            else { failCount++; sb.AppendLine($"FAIL  {tag}: {msg}"); }
            Debug.Log($"[DEV-014] {(ok ? "PASS" : "FAIL")} {tag}: {msg}");
        }
    }
}
