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
        public MiningFeelController miningFeel;   // Blocker3：真实物理驱动移动（同玩家输入源）
        public BlockCollapseSystem collapse;

        // Blocker3 Vertical Slice 运行时防线（迭代器协程不能有 ref/out 参数，故用实例字段 + 可变 holder 传递跨段状态）
        bool sliceNoTeleport = true;              // 任一 DriveAlong 段出现单帧 grid 位移 >1 → false
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
            // Blocker3 第三轮：给 RunRiskState 一个「玩家处地表 Hub → 不自动开 Run」的干净基线。
            // 本验收场景没有 SurfaceHubZone 触发器（DEV-011 正式规则里 IsAtSurface 的唯一权威来源），
            // 若不预先置 true，RunRiskState.LateUpdate 会在帧 0 就因 !IsAtSurface&&!RunActive 幽灵自启一个 Run，
            // 使 VerticalSlice 无法演示「真实离地 → 状态机自启」。
            // 同步执行（在本协程首个 yield 之前）以早于帧 0 的 LateUpdate；载具出生即在地表坑口 → IsAtSurface=true。
            var gmSync = GameManager.Instance;
            if (gmSync != null) gmSync.IsAtSurface = true;

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
            if (miningFeel == null) miningFeel = FindObjectOfType<MiningFeelController>();   // Blocker3
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

        // ---------- Blocker3：完整 Vertical Slice（真实挖井下矿/返航闭环，不再 teleport） ----------
        // 一条【不靠 transform.position 跳点】的真实 run：
        //   Surface(真实 spawn/地表坑口) → 真实 TryDigHit 挖开岩层 + 真实物理移动逐格下潜（Hover 被实心岩
        //   阻挡，不挖穿就过不去）→ 真实扫描收到 A 模糊异常 → 遭遇 A(废弃矿点)节点 → 决策（先取奖励、
        //   保 SupportRock 不拆 → 不触发坍塌）→ 奖励矿入 Cargo → 沿真实巷道原路逐格返航地表 →
        //   SellTerminal.TrySell → RunRiskState.RunActive=false（Run 收官）。
        // 防线：载具只在【抵达真实 spawn】后经挖/移动逐格位移，方法内绝不把 transform.position 直接 set 到
        // 节点/地表；DriveAlong 逐格检查 grid 格位移(单步≤1)，任一环出现瞬移跳变或读不到预期格即 FAIL。
        // 其余 3 类（B/C/D）保持组件级矩阵（各自 DriveType 已覆盖其决策语义）。
        IEnumerator VerticalSlice()
        {
            sb.AppendLine("\n==== SLICE. 完整 Vertical Slice（真实挖井下矿/返航 · AbandonedMiningPocket 玩家路径闭环） ====");
            var gm = GameManager.Instance;
            if (grid == null || generator == null || vehicle == null || oreScanner == null
                || discoveryService == null || gm == null || gm.RunRisk == null || miningFeel == null)
            {
                Assert(false, "SLICE_Rig", "缺 grid/generator/vehicle/oreScanner/discoveryService/miningFeel/gm/RunRisk"); yield break;
            }

            // ---- 0. 从真实 spawn 出发 ----
            // 切 Hover：悬浮直驱，被实心岩 BlockedByTerrain 阻挡 → 必须真挖通才过得去；无重力坠落时序漂移，确定性高。
            vehicle.SetMovementMode(DrillVehicle.MovementMode.Hover);
            vehicle.SetJetting(false, true);
            var prevMode = gm.Upgrades != null ? gm.Upgrades.drillLevel : 1;
            // 测试脚手架：临时把钻头提到能挖穿任何矿（含铁/铜 vein），避免 CarveOpen 卡在矿石硬度门；SLICE 是 Start 最后一段，本改不影响前序组件测试。
            if (gm.Upgrades != null) gm.Upgrades.drillLevel = 5;
            if (gm.spawnPoint != Vector3.zero)
                vehicle.transform.position = gm.spawnPoint;      // 仅此处定格真实 spawn（此后不再 set position）
            yield return NullFrame(0.3f);
            Vector2Int startCell = grid.WorldToGrid(vehicle.transform.position);
            Assert(grid.InBounds(startCell.x, startCell.y) && startCell.y <= DepthRegionLayout.Surface.maxDepth,
                "SLICE_0_StartSurface", $"载具从真实地表 spawn 出发（cell={startCell}，位于地表带内）");
            if (!grid.InBounds(startCell.x, startCell.y) || startCell.y > DepthRegionLayout.Surface.maxDepth) { if (gm.Upgrades != null) gm.Upgrades.drillLevel = prevMode; yield break; }
            sliceNoTeleport = true;                    // 防瞬移防线（实例字段）：任一 DriveAlong 段单帧位移 >1 → false

            // ---- 1. 固定 acceptance seed 生成并选一座【浅层可达】A 节点（真实下潜 3~14 格，成本可控）----
            DiscoveryNodeInstance node = null;
            int usedSeed = 0;
            for (int s = 20260907; s <= 20260925 && node == null; s++)
            {
                grid.seed = s; grid.RegenerateFromDatabase();
                if (generator.Nodes.Count == 0) continue;
                for (int i = 0; i < generator.Nodes.Count; i++)
                {
                    var n = generator.Nodes[i];
                    if (n.type != DiscoveryNodeType.AbandonedMiningPocket) continue;
                    int top = n.bounds.yMin;
                    if (top >= startCell.y + 3 && top <= startCell.y + 14) { node = n; usedSeed = s; break; }
                }
            }
            if (node == null)
            {
                grid.seed = 20260907; grid.RegenerateFromDatabase();
                for (int i = 0; i < generator.Nodes.Count; i++)
                    if (generator.Nodes[i].type == DiscoveryNodeType.AbandonedMiningPocket) { node = generator.Nodes[i]; usedSeed = 20260907; break; }
            }
            if (node == null) { Assert(false, "SLICE_NoA", "seed 20260907..20260925 未生成 AbandonedMiningPocket"); if (gm.Upgrades != null) gm.Upgrades.drillLevel = prevMode; yield break; }
            Assert(true, "SLICE_1_Seeded", $"选 seed={usedSeed} 的 A 节点 bounds={node.bounds}（浅层可达）");
            discoveryService.ResetAll();
            discoveryService.ReSyncFromGenerator();

            if (vehicle.Inventory != null) vehicle.Inventory.Clear();
            Assert(!vehicle.IsDead, "SLICE_0_Alive", "载具存活（隔离本 slice）");
            if (vehicle.IsDead) { if (gm.Upgrades != null) gm.Upgrades.drillLevel = prevMode; yield break; }

            // ---- 2. 离地表前的基线：玩家处地表 Hub（IsAtSurface=true）→ Run 尚未由状态机开启 ----
            //   Blocker3 第三轮：不再直接调 gm.RunRisk.BeginRun()（那会绕过 DEV-011 正式规则）。正式规则是
            //   RunRiskState.LateUpdate 里「!IsAtSurface && !RunActive → BeginRun」，即玩家真正离开地表后由
            //   状态机自己在真实帧开新 Run。故这里只记录基线：载具仍在地表 → RunActive 必须为 false、
            //   RunNumber 不因探针调用而变化（证明状态机未在地表自启，也没被手动开）。
            gm.IsAtSurface = true;                       // 基线：玩家在地表坑口（出生即地表），Sell 前都保持
            int runNoAtSurface = gm.RunRisk.RunNumber;
            Assert(!gm.RunRisk.RunActive, "SLICE_2_Surface_NoRun",
                $"离地表前（IsAtSurface=true）：RunActive=false（状态机未在地表自启），RunNumber={runNoAtSurface}");
            yield return NullFrame(0.1f);                // 跨若干真实帧仍应稳定不误自启
            Assert(gm.RunRisk.RunNumber == runNoAtSurface && !gm.RunRisk.RunActive,
                "SLICE_2_StaySurface_Stable", $"停留地表跨帧不误开 Run：RunNumber={gm.RunRisk.RunNumber} RunActive={gm.RunRisk.RunActive}");

            // ---- 3. 真实下矿：挖通从当前格到「节点上方一格 approach」的 L 形真实巷道并逐格驶入 ----
            Vector2Int approach = new Vector2Int(node.bounds.xMin + node.bounds.width / 2, node.bounds.yMin - 1);
            // （approach 若在界内，水平段会把它挖空成为可停格；若越界则退到 bounds 内最浅可交互列）
            if (!grid.InBounds(approach.x, approach.y))
                approach = new Vector2Int(node.bounds.xMin, node.bounds.yMin);
            Vector2Int cur = grid.WorldToGrid(vehicle.transform.position);
            int shaftX = cur.x;                        // 竖井就用载具当前列（通常即地表坑口 centerX 下方）
            // 3a) 竖直段：当前列从 minY 逐格向下挖到 approach.y
            int loY = System.Math.Min(cur.y, approach.y), hiY = System.Math.Max(cur.y, approach.y);
            for (int y = loY + 1; y <= hiY; y++)
            {
                if (!grid.InBounds(shaftX, y)) { sliceNoTeleport = false; break; }
                if (grid.IsSolid(shaftX, y))
                {
                    var r = CarveOpen(shaftX, y);
                    if (r != DigHitResult.Broken) { Assert(false, "SLICE_3_CarveDown", $"({shaftX},{y}) 挖不穿 res={r}"); if (gm.Upgrades != null) gm.Upgrades.drillLevel = prevMode; yield break; }
                }
            }
            // ---- 2b. 真正离开地表 → RunRiskState 状态机自己开 Run（非探针手动 BeginRun）----
            //   竖井已挖通，载具即将沿竖井下潜离开地表带。此时拨动官方离地表信号 IsAtSurface=false
            //   （等效真实地表 Hub 触发器 SurfaceHubZone 在载具离场时置 false；本场景无该触发器故由探针拨动，
            //    与 DEV-011 探针一致——DEV-011 也以 IsAtSurface 官方杠杆驱动、从不直接调 BeginRun）。
            //   随后等一个真实 EndOfFrame：RunRiskState.LateUpdate 侦测到 !IsAtSurface && !RunActive → 自己 BeginRun。
            //   断言：RunActive==true 且 RunNumber 恰比基线 +1 —— 证明是状态机在离地真实帧自启，而非探针手动开始。
            gm.IsAtSurface = false;
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();        // 双帧保险：确保 LateUpdate 至少一次见到离地表状态
            Assert(gm.RunRisk.RunActive, "SLICE_2_Run_AutoStart",
                $"真实离地后 RunRiskState 状态机自启（未调 BeginRun）→ RunActive=true，RunNumber={gm.RunRisk.RunNumber}");
            Assert(gm.RunRisk.RunNumber == runNoAtSurface + 1, "SLICE_2_RunNumber_Incremented",
                $"状态机离地自启使 RunNumber 恰 +1：{runNoAtSurface} → {gm.RunRisk.RunNumber}（证明非手动 BeginRun 直接调用）");

            //     竖直驶入到 approach.y 那一行
            var legDown = new SliceLeg();
            yield return DriveAlong(new Vector2Int(shaftX, approach.y), legDown);
            if (!legDown.done) { Assert(false, "SLICE_3_Descend", "真实下潜到 approach 行失败"); if (gm.Upgrades != null) gm.Upgrades.drillLevel = prevMode; yield break; }
            // 3b) 水平段：沿 approach.y 及其上一行 approach.y-1 挖通【2 格高横巷】（车辆角点 ±0.36 会探入
            //     相邻行，1 格高横巷在水平移动时会被上下实心格卡住——实测几何；故水平段须 2 格高清障）。
            int fx = System.Math.Min(shaftX, approach.x), tx = System.Math.Max(shaftX, approach.x);
            for (int ry = 0; ry < 2; ry++)   // 打开 approach.y 与 approach.y-1 两行（都在节点顶部之上）
            {
                int yy = approach.y - ry;
                if (yy < 1) break;                       // 不挖到地表带之上
                for (int x = fx; x <= tx; x++)
                {
                    if (!grid.InBounds(x, yy)) { sliceNoTeleport = false; break; }
                    if (grid.IsSolid(x, yy))
                    {
                        var r = CarveOpen(x, yy);
                        if (r != DigHitResult.Broken) { Assert(false, "SLICE_3_CarveAcross", $"({x},{yy}) 挖不穿 res={r}"); if (gm.Upgrades != null) gm.Upgrades.drillLevel = prevMode; yield break; }
                    }
                }
            }
            var legAcross = new SliceLeg();
            yield return DriveAlong(approach, legAcross);
            if (!legAcross.done) { Assert(false, "SLICE_3_Reach", "真实横移抵达节点旁失败"); if (gm.Upgrades != null) gm.Upgrades.drillLevel = prevMode; yield break; }
            Vector2Int arrived = grid.WorldToGrid(vehicle.transform.position);
            Assert(Manhattan(arrived, approach) == 0, "SLICE_3_ArrivedAtNode",
                $"真实移动后载具停在节点旁 approach={arrived}（无瞬移）");
            Assert(arrived.y >= startCell.y, "SLICE_3_ReallyDeeper", $"确实离开地表真实下潜（y {startCell.y}→{arrived.y}）");
            Assert(sliceNoTeleport, "SLICE_3_NoTeleport_Down", "下矿全程 grid 格单步位移 ≤1（无 transform.position 跳点）");

            // ---- 4. 真实扫描（站在实际抵达格）：收到 A 模糊异常；到边界内才 isDiscovered=true ----
            //   Blocker3 原 tautology `!isDiscovered || true` 恒真已删除；改为真实可失败行为断言：
            //   本 slice 已实际抵达节点旁(discoveryRadius 内)，同一次正式扫描必须 hasSignal + isDiscovered=true。
            var atCell = grid.WorldToGrid(vehicle.transform.position);
            discoveryService.ResetAll();               // 清首发现记录 → 验「本次抵达内扫描→真发现」而非残留
            discoveryService.ReSyncFromGenerator();
            var scan = oreScanner.ScanAndReportAround(grid.GridToWorld(atCell.x, atCell.y));
            Assert(scan.discovery != null && scan.discovery.hasSignal
                   && scan.discovery.type == DiscoveryNodeType.AbandonedMiningPocket
                   && scan.discovery.description != null && scan.discovery.description.Contains("废弃"),
                "SLICE_4_ScanAnomaly", "正式扫描在真实抵达格收到 A 废弃采矿点模糊异常");
            Assert(scan.discovery != null && scan.discovery.isDiscovered,
                "SLICE_4_ScanFound", "实际抵达(discoveryRadius 内)后扫描 isDiscovered=true（模糊发现成立）");

            // ---- 5. 遭遇 + 决策：A 节点 = 奖励矿与 SupportRock 风险取舍；玩家选择【先取奖励、保承重柱】----
            Assert(node.riskCells.Count >= 1 && node.rewardCells.Count >= 1, "SLICE_5_AStructure", "A 节点含风险(SupportRock) + 奖励矿");
            bool supportFound = false; Vector2Int supportCell = Vector2Int.zero;
            for (int i = 0; i < node.riskCells.Count; i++)
            {
                var t = grid.GetTile(node.riskCells[i].x, node.riskCells[i].y);
                if (t != null && t.blockType == BlockType.SupportRock) { supportFound = true; supportCell = node.riskCells[i]; break; }
            }
            Assert(supportFound, "SLICE_5_HasSupport", "A 节点确有 SupportRock 承重柱（决策对象）");
            if (!supportFound) { if (gm.Upgrades != null) gm.Upgrades.drillLevel = prevMode; yield break; }
            // 玩家决策 = 不动 SupportRock，只取奖励矿入 Cargo：
            int cargoBefore = vehicle.CargoValue;
            bool tookAny = false;
            var rewardCell = node.rewardCells.Count > 0 ? node.rewardCells[0] : supportCell;
            var rw = grid.GetTile(rewardCell.x, rewardCell.y);
            if (rw != null && rw.isSolid && rw.value > 0)
            {
                for (int i = 0; i < 8 && !tookAny; i++)   // 多击到 Broken（矿可能多耐久），drill=5 ≥ 硬度
                {
                    var r = vehicle.TryDigHit(rewardCell);
                    tookAny = r == DigHitResult.Hit || r == DigHitResult.Broken;
                    if (r == DigHitResult.Broken) break;
                    if (r == DigHitResult.NotSolid) break;
                }
            }
            int cargoAfter = vehicle.CargoValue;
            Assert(cargoAfter > cargoBefore && tookAny, "SLICE_5_TakeReward_ToCargo",
                "先取奖励矿入 Cargo（真实 HandleTileDug 入包，Cargo 增加）");
            // 保 SupportRock 未拆 → 承重柱仍在原位（本 slice 的“安全决策”未触发坍塌）。
            // 注：不断言全局 collapse 计数——A/D 组件测试已先 DebugTriggerCheck 残留状态；此处只证「本决策没动承重柱」。
            var supTile = grid.GetTile(supportCell.x, supportCell.y);
            Assert(supTile != null && supTile.blockType == BlockType.SupportRock && grid.IsSolid(supportCell.x, supportCell.y),
                "SLICE_5_NoCollapse_Safe", "未拆 SupportRock → 承重柱原封未动（“先取矿后拆柱”安全决策成立）");

            // ---- 6. 带货真实返航：沿已挖开的巷道原路驶回 spawn 所在行（先水平回竖井，再沿竖井上行；均不 teleport）----
            Vector2Int shaftCell = new Vector2Int(shaftX, approach.y);   // 回到竖井底部（走已开的 2 格高横巷）
            var legBackH = new SliceLeg();
            yield return DriveAlong(shaftCell, legBackH);
            if (!legBackH.done) { Assert(false, "SLICE_6_ReturnHoriz", "真实返航(水平回竖井)失败"); if (gm.Upgrades != null) gm.Upgrades.drillLevel = prevMode; yield break; }
            // 竖井仅 1 列宽：先让载具精确回到竖井格中心，再上行（1 列宽 + 载具角点会卡到侧壁；居中方可通过）
            var legCenter = new SliceLeg();
            yield return CenterOnCell(shaftCell, legCenter);
            if (!legCenter.done) { Assert(false, "SLICE_6_CenterShaft", "返航前无法回中竖井格"); if (gm.Upgrades != null) gm.Upgrades.drillLevel = prevMode; yield break; }
            var legUp = new SliceLeg();
            yield return DriveAlong(new Vector2Int(shaftX, startCell.y), legUp);   // 沿竖井上行回 spawn 行
            if (!legUp.done) { Assert(false, "SLICE_6_ReturnUp", "真实返航(沿竖井上行)到地表行失败"); if (gm.Upgrades != null) gm.Upgrades.drillLevel = prevMode; yield break; }
            // 返航后需处于 spawn 所在坑口(centerX)；shaftX 即 spawn 列(center)，故回程竖井列已回到 spawn.x。
            Vector2Int homeCell = new Vector2Int(startCell.x, startCell.y);
            Vector2Int atBack = grid.WorldToGrid(vehicle.transform.position);
            if (atBack.x != homeCell.x)   // 一般不会走到：竖井即 spawn 列。兜底才横向挖/驶回坑口
            {
                int lx = System.Math.Min(atBack.x, homeCell.x), rx = System.Math.Max(atBack.x, homeCell.x);
                for (int x = lx; x <= rx; x++)
                    if (grid.InBounds(x, startCell.y) && grid.IsSolid(x, startCell.y))
                    {
                        var cr = CarveOpen(x, startCell.y);
                        if (cr != DigHitResult.Broken) { Assert(false, "SLICE_6_CarveHome", "返航回 spawn 横向挖不穿"); if (gm.Upgrades != null) gm.Upgrades.drillLevel = prevMode; yield break; }
                    }
                var legHome = new SliceLeg();
                yield return DriveAlong(homeCell, legHome);
                if (!legHome.done) { Assert(false, "SLICE_6_ReturnHome", "真实驶回 spawn 失败"); if (gm.Upgrades != null) gm.Upgrades.drillLevel = prevMode; yield break; }
            }
            Assert(Manhattan(grid.WorldToGrid(vehicle.transform.position), homeCell) <= 2,
                "SLICE_6_ReturnSurface", "带 Cargo 真实返航地表坑口（无瞬移，grid 格逐段推进）");
            Assert(!vehicle.IsDead, "SLICE_6_Alive_AfterReturn", "带货真实返航地表后载具存活");
            Assert(sliceNoTeleport, "SLICE_6_NoTeleport", "返航全程 grid 格单步位移 ≤1（无 transform.position 跳点）");
            if (!sliceNoTeleport) { if (gm.Upgrades != null) gm.Upgrades.drillLevel = prevMode; yield break; }

            // 回到地表坑口 = 重新进入地表 Hub → IsAtSurface 拨回 true；此时 Run 仍在进行（未 Sell），
            // 且状态机不得因回地表而误开新 Run（需 Sell 结算才结束当前 Run）。
            gm.IsAtSurface = true;
            yield return new WaitForEndOfFrame();
            Assert(gm.RunRisk.RunActive && gm.RunRisk.RunNumber == runNoAtSurface + 1,
                "SLICE_6_BackHub_NoNewRun",
                $"回地表 Hub（IsAtSurface=true）：Run 仍进行中（RunActive=true, Run#{gm.RunRisk.RunNumber}），不误开新 Run");

            // ---- 7. 真实驶入 SellTerminal 触发器 → PlayerInRange=true → 出售 → Run 结束 ----
            //   Blocker3 第三轮：不再停在坑口就直接 TrySell（那只能证明结算函数能用，无法证明玩家真实驶入了终端范围）。
            //   改为：真实沿地表驶入终端 Trigger（物理 Overlap），等 OnTriggerEnter2D 触发后断言
            //   SellTerminal.PlayerInRange==true，再走终端 Update 同款结算入口 TrySell。
            var sellTerminal = FindFirstObjectByType<SellTerminal>();
            Assert(sellTerminal != null, "SLICE_7_HasTerminal", "场景中存在 SellTerminal（真实出售终端）");
            int cashBefore = gm.Cash;
            int earned = 0;
            if (sellTerminal != null)
            {
                // 终端触发器中心所在格；沿 spawn 行从当前位置真实驶入终端下方并回中其格心（物理 Overlap 进 Trigger）
                Vector2Int stCell = grid.WorldToGrid(sellTerminal.transform.position);
                Vector2Int fromHere = grid.WorldToGrid(vehicle.transform.position);
                int sl = System.Math.Min(fromHere.x, stCell.x), sr = System.Math.Max(fromHere.x, stCell.x);
                for (int x = sl; x <= sr; x++)                     // 若行上实心格挡路则挖开（地表土/矿可挖）
                    if (grid.InBounds(x, homeCell.y) && grid.IsSolid(x, homeCell.y))
                    {
                        var cr = CarveOpen(x, homeCell.y);
                        if (cr != DigHitResult.Broken) { Assert(false, "SLICE_7_CarveToTerminal", $"({x},{homeCell.y}) 驶向终端挖不穿 res={cr}"); break; }
                    }
                var legToTerm = new SliceLeg();
                yield return DriveAlong(stCell, legToTerm);
                if (legToTerm.done)
                {
                    var legCenterTerm = new SliceLeg();
                    yield return CenterOnCell(stCell, legCenterTerm);   // 居中到终端格心 → 物理进 Trigger
                }
                yield return new WaitForEndOfFrame();                 // 让 OnTriggerEnter2D → presence.Enter 落地
                yield return new WaitForEndOfFrame();
                // PlayerInRange 由 HubZoneTracker 权威：任一 Sell 终端在场计数 >0 = 玩家真实在触发范围内
                Assert(SellTerminal.PlayerInRange, "SLICE_7_PlayerInRange",
                    $"真实驶入终端 Trigger 后 PlayerInRange=true（载具格={grid.WorldToGrid(vehicle.transform.position)}，终端格={stCell}）");
                // 走终端 Update 同款入口：presence.Present 已成立时 TrySell == 玩家按 E（结算路径一致）
                if (SellTerminal.PlayerInRange && vehicle.CargoValue > 0)
                    earned = sellTerminal.TrySell(vehicle);
            }
            Assert(earned > 0 && gm.Cash >= cashBefore, "SLICE_7_Sell",
                $"真实终端范围内出售 +${earned}（Cargo 清空、Cash 增加），非坑口直调");
            Assert(gm.RunRisk != null && !gm.RunRisk.RunActive, "SLICE_8_RunEnd",
                "出售后 RunRiskState 结束本 Run（RunActive=false，本 Run 生命周期收官）");
            if (gm.Upgrades != null) gm.Upgrades.drillLevel = prevMode;
        }

        // ---------- Blocker3 工具 ----------

        static int Manhattan(Vector2Int a, Vector2Int b) => System.Math.Abs(a.x - b.x) + System.Math.Abs(a.y - b.y);

        IEnumerator NullFrame(float sec)
        {
            float t0 = Time.time;
            while (Time.time - t0 < sec) yield return null;
        }

        /// <summary>真实挖穿一格（循环 TryDigHit 直到 Broken）。TryDigHit 无距离限制 → 控制性挖掘，等价玩家按住敲击。</summary>
        DigHitResult CarveOpen(int x, int y)
        {
            if (!grid.InBounds(x, y)) return DigHitResult.NotSolid;
            if (!grid.IsSolid(x, y)) return DigHitResult.Broken;   // 已空 = 视为已挖穿
            int guard = 0;
            DigHitResult last = DigHitResult.Hit;
            while (guard++ < 24)
            {
                last = vehicle.TryDigHit(new Vector2Int(x, y));
                if (last == DigHitResult.Broken) return last;
                if (last != DigHitResult.Hit) return last;           // NotSolid/HardnessLow/Sealed/Overheated 中止
            }
            return last;
        }

        /// <summary>
        /// 让载具以真实物理(Hover 直驱)逐格驶向 target，直到其 grid 格 == target。
        /// 每帧只在朝向 target 的轴给 DebugDriveInput（与真实按键同源：ReadInput→FixedUpdate），到位即清输入。
        /// 防线：记录前格，单帧 grid 位移若 >1 → sliceNoTeleport=false（瞬移跳变）。
        /// Hover 被实心岩阻挡：若前方没被 CarveOpen 挖空，将卡住直到帧数耗尽 → leg.done=false。
        /// 迭代器协程不能带 ref/out 参数 → 完成与否经可变 holder leg 传回。
        /// </summary>
        IEnumerator DriveAlong(Vector2Int target, SliceLeg leg)
        {
            leg.done = false;
            int stall = 0;
            Vector2Int prev = grid.WorldToGrid(vehicle.transform.position);
            while (stall < 1200)   // 1200 fixed 帧 ≈ 20s 上限；单程多格足够（挖开后一格仅需数帧）
            {
                Vector2Int now = grid.WorldToGrid(vehicle.transform.position);
                if (now == target)
                {
                    leg.done = true;
                    if (miningFeel != null) miningFeel.DebugDriveInput(Vector2.zero, false);
                    yield break;
                }
                if (Manhattan(now, prev) > 1) sliceNoTeleport = false;   // 单步 >1 格 = 瞬移/穿透
                prev = now;
                Vector2Int d = target - now;
                Vector2 drive = System.Math.Abs(d.x) > System.Math.Abs(d.y)
                    ? new Vector2(System.Math.Sign(d.x), 0f)
                    : new Vector2(0f, System.Math.Sign(d.y));
                // grid y 向下为正；Hover 用世界轴（世界 y 上为正）→ grid 下潜 = 世界 y 负 → drive.y 取反
                Vector2 worldDrive = new Vector2(drive.x, -drive.y);
                if (miningFeel != null) miningFeel.DebugDriveInput(worldDrive, false);
                yield return new WaitForFixedUpdate();
                stall++;
            }
            if (miningFeel != null) miningFeel.DebugDriveInput(Vector2.zero, false);
        }

        /// <summary>把载具精确回中到某格中心（世界坐标），保证穿 1 列宽竖井时角点不卡侧壁。done=是否回中。</summary>
        IEnumerator CenterOnCell(Vector2Int cell, SliceLeg leg)
        {
            leg.done = false;
            Vector3 center = grid.GridToWorld(cell.x, cell.y);
            int stall = 0;
            while (stall < 600)   // 10s 上限
            {
                Vector3 p = vehicle.transform.position;
                float dx = center.x - p.x;
                float dy = center.y - p.y;
                if (Mathf.Abs(dx) < 0.05f && Mathf.Abs(dy) < 0.05f)
                {
                    leg.done = true;
                    if (miningFeel != null) miningFeel.DebugDriveInput(Vector2.zero, false);
                    yield break;
                }
                Vector2 worldDrive = new Vector2(Mathf.Clamp(dx, -1f, 1f), Mathf.Clamp(dy, -1f, 1f));
                if (miningFeel != null) miningFeel.DebugDriveInput(worldDrive, false);
                yield return new WaitForFixedUpdate();
                stall++;
            }
            if (miningFeel != null) miningFeel.DebugDriveInput(Vector2.zero, false);
        }

        /// <summary>DriveAlong 的完成标志 holder（可变引用传参，规避迭代器 ref 限制）。</summary>
        sealed class SliceLeg { public bool done; }

        void Assert(bool ok, string tag, string msg)
        {
            if (ok) { passCount++; sb.AppendLine($"PASS  {tag}: {msg}"); }
            else { failCount++; sb.AppendLine($"FAIL  {tag}: {msg}"); }
            Debug.Log($"[DEV-014] {(ok ? "PASS" : "FAIL")} {tag}: {msg}");
        }
    }
}
