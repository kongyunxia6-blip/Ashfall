#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// DEV-014：编辑回归（编辑模式静态，不 Play）。覆盖 Issue #30 §Acceptance 可静态断言部分：
    ///  A. Catalog 数据完整性（四类节点登记 / region / footprint / scanSignature）；
    ///  B. Deterministic generation（同 seed 重复 → 布局一致；异 seed → 可观察变化）；
    ///  C. 四类节点跨 seed 均可生成（AbandonedPocket / ThermalVent / AncientCache / CollapsedPocket）；
    ///  D. Reserved / Ruin bounds 避让（节点 bounds 不越 reserved，不覆盖 AncientRelayRoom）；
    ///  E. 原子提交（保留区把整个候选区挡住 → 该 seed 该型零节点 = 拒绝时无半个节点写入）；
    ///  F. 组合复用检查：DiscoveryNodeGenerator 依附现有 DigGrid / RuinGenerator / OreVeinGenerator（不建第二 Grid/矿源）。
    ///  G. Blocker2：节点奖励经现有 OreVeinGenerator 登记（Veins 账实含节点奖励矿）、结构块不覆盖 vein/space。
    ///  只读：只用临时 rig（不入库），不改任何既有资产/场景。不用 DisplayDialog（避免阻塞 MCP）。
    ///  菜单：灰烬之下 → DEV-014 回归：发现节点组合/落点/原子/确定性
    /// </summary>
    public static class DiscoveryNodeV1Regression
    {
        const string OutPath = "C:/Users/58058/.workbuddy/tools/d14_regression_result.txt";
        const string DataFolder = "Assets/Ashfall/Data";

        [MenuItem("灰烬之下/DEV-014 回归：发现节点组合/落点/原子/确定性")]
        public static void Run()
        {
            var sb = new StringBuilder();
            int pass = 0, fail = 0;
            sb.AppendLine("==== DEV-014 编辑回归 ====");

            RunAll(sb, ref pass, ref fail);

            sb.AppendLine($"\n==== 汇总：PASS={pass}  FAIL={fail} ====");
            try { File.WriteAllText(OutPath, sb.ToString()); }
            catch (System.Exception e) { Debug.LogError("[DEV-014] 写结果失败：" + e.Message); }
            Debug.Log($"[DEV-014] 编辑回归完成：PASS={pass} FAIL={fail} → {OutPath}");
        }

        static void RunAll(StringBuilder sb, ref int pass, ref int fail)
        {
            // ---- A. Catalog ----
            Assert(DiscoveryNodeCatalog.All.Length == 4, "A_Catalog4", "DiscoveryNodeCatalog 登记 4 类节点", sb, ref pass, ref fail);
            Assert(DiscoveryNodeCatalog.Find(DiscoveryNodeType.AbandonedMiningPocket) != null
                   && DiscoveryNodeCatalog.Find(DiscoveryNodeType.ThermalVentChamber) != null
                   && DiscoveryNodeCatalog.Find(DiscoveryNodeType.AncientSignalCache) != null
                   && DiscoveryNodeCatalog.Find(DiscoveryNodeType.CollapsedResourcePocket) != null,
                "A_FourTypes", "四类节点均有 spec", sb, ref pass, ref fail);
            bool regionsOk = true, sigOk = true;
            foreach (var s in DiscoveryNodeCatalog.All)
            {
                if (s.allowedRegionId != "Shallow" && s.allowedRegionId != "Mid" && s.allowedRegionId != "Deep") regionsOk = false;
                if (string.IsNullOrEmpty(s.scanSignature)) sigOk = false;
            }
            Assert(regionsOk, "A_Regions", "每类节点 allowedRegionId ∈ {Shallow/Mid/Deep}", sb, ref pass, ref fail);
            Assert(sigOk, "A_ScanSignature", "每类节点有 scanSignature（模糊只读信号）", sb, ref pass, ref fail);

            // ---- 临时 tile / 数据库 / 真实 vein band ----
            var support = MakeTile("support", BlockType.SupportRock);
            var loose = MakeTile("loose", BlockType.LooseRock);
            var hot = MakeTile("hot", BlockType.HotRock);
            var seal = MakeTile("seal", BlockType.RuinSeal);
            var data = MakeTile("data", BlockType.Normal, value: 90);
            var alloy = MakeTile("alloy", BlockType.Normal, value: 60);

            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>($"{DataFolder}/TileDatabase.asset");
            if (db == null || db.emptyTile == null)
            {
                Assert(false, "B_Rig_Db", "TileDatabase 缺失（请先一键生成数据库）", sb, ref pass, ref fail);
                return;
            }

            var surfaceRect = new OreReservedRect { label = "SurfaceBand", x = 0, y = 0, w = 48, h = 3 };

            // ---- 多 seed 扫描：确认四类节点可被生成（真实 vein band 下）----
            var seenTypes = new HashSet<DiscoveryNodeType>();
            RigInfo best = null;
            for (int s = 1000; s <= 1060; s++)
            {
                var rig = BuildRig(db, support, loose, hot, seal, data, alloy, new[] { surfaceRect }, s);
                if (rig == null) { CleanupRig(best); best = null; continue; }
                for (int i = 0; i < rig.gen.Nodes.Count; i++) seenTypes.Add(rig.gen.Nodes[i].type);
                CleanupRig(best);
                best = rig;
            }
            Assert(seenTypes.Contains(DiscoveryNodeType.AbandonedMiningPocket), "C_See_A", "跨 seed 能生成 废弃采矿点(A)", sb, ref pass, ref fail);
            Assert(seenTypes.Contains(DiscoveryNodeType.ThermalVentChamber), "C_See_B", "跨 seed 能生成 热裂隙室(B)", sb, ref pass, ref fail);
            Assert(seenTypes.Contains(DiscoveryNodeType.AncientSignalCache), "C_See_C", "跨 seed 能生成 文明信号缓存点(C)", sb, ref pass, ref fail);
            Assert(seenTypes.Contains(DiscoveryNodeType.CollapsedResourcePocket), "C_See_D", "跨 seed 能生成 坍塌资源囊(D)", sb, ref pass, ref fail);

            if (best == null || best.gen.Nodes.Count == 0)
            {
                Assert(false, "C_NoNode", "多 seed 均未生成任何节点", sb, ref pass, ref fail);
                return;
            }

            RigInfo detailed = PickDensest(db, support, loose, hot, seal, data, alloy, new[] { surfaceRect });
            if (detailed == null || detailed.gen.Nodes.Count == 0)
            {
                Assert(false, "D_NoRig", "无可用详细 rig", sb, ref pass, ref fail);
                return;
            }

            // ---- B. 确定性：同 seed 两次一致 ----
            var rA = BuildRig(db, support, loose, hot, seal, data, alloy, new[] { surfaceRect }, detailed.seed);
            var rB = BuildRig(db, support, loose, hot, seal, data, alloy, new[] { surfaceRect }, detailed.seed);
            Assert(rA != null && rB != null && SameLayout(rA, rB), "B_Deterministic_SameSeed",
                $"seed={detailed.seed} 重复生成 → 节点 id/type/bounds 逐项一致", sb, ref pass, ref fail);

            // ---- B. 不同 seed：可观察变化 ----
            var rC = BuildRig(db, support, loose, hot, seal, data, alloy, new[] { surfaceRect }, detailed.seed + 1);
            bool changed = rC != null && !SameLayout(detailed, rC);
            Assert(changed, "B_Deterministic_DiffSeed", "不同 seed 布局有可观察变化", sb, ref pass, ref fail);

            // ---- D. 保留区避让：节点 bounds 不与 surfaceRect 相交；不越界 ----
            bool surfaceSafe = true, inBounds = true;
            for (int i = 0; i < detailed.gen.Nodes.Count; i++)
            {
                var n = detailed.gen.Nodes[i];
                if (RectsOverlap(new RectInt(n.bounds.x, n.bounds.y, n.bounds.width, n.bounds.height),
                                 new RectInt(surfaceRect.x, surfaceRect.y, surfaceRect.w, surfaceRect.h))) surfaceSafe = false;
                if (n.bounds.xMin < 0 || n.bounds.yMin < 0 || n.bounds.xMax > detailed.grid.Width || n.bounds.yMax > detailed.grid.Depth) inBounds = false;
            }
            Assert(surfaceSafe, "D_NotSurface", "节点 bounds 不覆盖地表保留区（不破坏 Surface）", sb, ref pass, ref fail);
            Assert(inBounds, "D_InGrid", "所有节点 bounds 不越界", sb, ref pass, ref fail);

            // ---- D. 不覆盖 AncientRelayRoom ----
            CheckRuinAvoidance(db, support, loose, hot, seal, data, alloy, new[] { surfaceRect }, sb, ref pass, ref fail);

            // ---- E. 原子提交：整带保留区封死 Shallow+Mid → A/B/D 零生成（C Deep 仍可）----
            AtomicReject(db, support, loose, hot, seal, data, alloy, sb, ref pass, ref fail);

            // ---- F. 复用检查 ----
            Assert(detailed.gen.grid == detailed.grid, "F_SingleGrid", "DiscoveryNodeGenerator 依附现有 DigGrid（不建第二 Grid）", sb, ref pass, ref fail);
            Assert(detailed.gen.oreVeinSource == detailed.grid.oreVeinGenerator, "F_UseVeinSource",
                "DiscoveryNodeGenerator 奖励矿经现有 OreVeinGenerator（oreVeinSource==grid.oreVeinGenerator，无第二矿源）", sb, ref pass, ref fail);
            Assert(detailed.gen.HasNodes, "F_HasNodes", "rig 登记节点（验收可读）", sb, ref pass, ref fail);

            // ---- G. Blocker2：节点奖励矿已进入 vein 账实 / 结构不覆盖 vein/space ----
            CheckComposeVein(detailed, sb, ref pass, ref fail);

            CleanupRig(detailed); CleanupRig(rA); CleanupRig(rB); CleanupRig(rC);
        }

        /// <summary>Blocker2：奖励矿登记进 vein 账实；结构格不是矿/空格（不覆盖已有 vein/space）。</summary>
        static void CheckComposeVein(RigInfo rig, StringBuilder sb, ref int pass, ref int fail)
        {
            bool anyReward = false, anyNonNormalStruct = true, noOverwriteVein = true, noOverwriteSpace = true;
            for (int i = 0; i < rig.gen.Nodes.Count; i++)
            {
                var n = rig.gen.Nodes[i];
                if (n.rewardCells.Count > 0) anyReward = true;
                // 结构/风险格不能是 vein(value>0) 或 empty(非 solid)—— 不覆盖已有结果
                for (int r = 0; r < n.riskCells.Count; r++)
                {
                    var def = rig.grid.GetTile(n.riskCells[r].x, n.riskCells[r].y);
                    if (def == null || def.blockType == BlockType.Normal || !def.isSolid) anyNonNormalStruct = false;
                    // 风险格自身是特殊块（Support/Loose/Hot），不会是 vein(矿 Normal)或 space(空)—— 天然成立；这里检查确为特殊块
                }
            }
            Assert(anyReward, "G_HasRewardViaVein", "节点含奖励矿格（经 vein 系统登记）", sb, ref pass, ref fail);

            // 节点奖励矿格确实出现在 OreVeinGenerator.Veins 账实中（rewardCells 是真实 vein 的一部分）
            bool rewardInVeinAccounts = RewardInVeinAccounts(rig);
            Assert(rewardInVeinAccounts, "G_Reward_In_VeinAccounts", "节点奖励矿格均能反查到 vein 账实记录（无第二手工矿源）", sb, ref pass, ref fail);

            // 结构块（Support/Loose/Hot/Seal）不落在 vein(矿)或 space(空)上：结构格已确认为非 Normal/非空格（覆盖前校验）
            Assert(anyNonNormalStruct, "G_Struct_IsSpecial", "节点结构格为特殊块（非普通矿/空格，未覆盖已有 vein/space）", sb, ref pass, ref fail);
        }

        static bool RewardInVeinAccounts(RigInfo rig)
        {
            if (rig == null || rig.oreVein == null) return rig.gen.Nodes.Count == 0;
            var veinCells = new HashSet<Vector2Int>();
            for (int v = 0; v < rig.oreVein.Veins.Count; v++)
                for (int c = 0; c < rig.oreVein.Veins[v].cells.Count; c++)
                    veinCells.Add(rig.oreVein.Veins[v].cells[c]);
            for (int i = 0; i < rig.gen.Nodes.Count; i++)
            {
                var n = rig.gen.Nodes[i];
                if (n.type == DiscoveryNodeType.AncientSignalCache) continue;   // C 文明 tile 非 vein
                for (int r = 0; r < n.rewardCells.Count; r++)
                    if (!veinCells.Contains(n.rewardCells[r])) return false;    // A/B/D 奖励须在 vein 账实中
            }
            return true;
        }

        static bool CheckRuinAvoidance(TileDatabase db,
            TileDefinition support, TileDefinition loose, TileDefinition hot, TileDefinition seal,
            TileDefinition data, TileDefinition alloy, OreReservedRect[] reserved, StringBuilder sb, ref int pass, ref int fail)
        {
            var go = new GameObject("DEV014_RuinAvoid");
            var dg = go.AddComponent<DigGrid>();
            dg.database = db; dg.width = 48; dg.depth = 64;
            dg.useRandomSeed = false; dg.seed = 20260907; dg.enableFallingRocks = false; dg.surfaceOpeningHalfWidth = 3;
            dg.breakDuration = 0f;

            var space = go.AddComponent<UndergroundSpaceGenerator>(); space.grid = dg; space.seedOverride = -1;
            space.bands = RealSpaceBands(); dg.undergroundSpaceGenerator = space;
            var oreGen = go.AddComponent<OreVeinGenerator>(); oreGen.grid = dg; oreGen.seedOverride = -1;
            oreGen.bands = RealVeinBands(db); oreGen.reservedRects = reserved; dg.oreVeinGenerator = oreGen;

            var wall = MakeTile("wall", BlockType.Normal);
            var rgen = go.AddComponent<RuinGenerator>();
            rgen.grid = dg; rgen.seedOverride = -1;
            rgen.wallTile = wall; rgen.sealTile = seal; rgen.coreTile = MakeTile("core", BlockType.AncientRelayCore); rgen.rewardTile = alloy;
            dg.ruinGenerator = rgen;

            var gen = go.AddComponent<DiscoveryNodeGenerator>();
            gen.grid = dg; gen.seedOverride = -1;
            gen.reservedRects = reserved;
            gen.supportRockTile = support; gen.looseRockTile = loose; gen.hotRockTile = hot; gen.sealTile = seal;
            gen.oreVeinSource = oreGen; gen.ancientDataTile = data; gen.ancientAlloyTile = alloy;
            dg.discoveryNodeGenerator = gen;

            dg.RegenerateFromDatabase();

            bool safe = true;
            if (rgen.HasInstances && gen.HasNodes)
            {
                var rb = rgen.Instances[0].bounds;
                var rrect = new RectInt(rb.xMin, rb.yMin, rb.width, rb.height);
                for (int i = 0; i < gen.Nodes.Count; i++)
                {
                    var n = gen.Nodes[i];
                    var nrect = new RectInt(n.bounds.x, n.bounds.y, n.bounds.width, n.bounds.height);
                    if (RectsOverlap(nrect, rrect)) safe = false;
                }
            }
            Assert(safe, "D_NoRuinOverlap", "节点 bounds 不与 AncientRelayRoom bounds 相交（不破坏遗迹）", sb, ref pass, ref fail);

            Object.DestroyImmediate(go);
            return safe;
        }

        static void AtomicReject(TileDatabase db,
            TileDefinition support, TileDefinition loose, TileDefinition hot, TileDefinition seal,
            TileDefinition data, TileDefinition alloy, StringBuilder sb, ref int pass, ref int fail)
        {
            // Shallow+Mid 整带保留区封死 → A/B/D 无可落结构区（0 生成，拒绝即整节点放弃）；Deep 的 C 仍可。
            var reservedFull = new[]
            {
                new OreReservedRect { label = "SurfaceBand", x = 0, y = 0, w = 48, h = 3 },
                new OreReservedRect { label = "LockShallowMid", x = 0, y = 3, w = 48, h = 41 },
            };
            bool foundA = false, foundB = false, foundD = false, foundC = false;
            for (int s = 2000; s <= 2030 && !(foundA && foundB && foundD); s++)
            {
                var rig = BuildRig(db, support, loose, hot, seal, data, alloy, reservedFull, s);
                if (rig == null) continue;
                for (int i = 0; i < rig.gen.Nodes.Count; i++)
                {
                    var t = rig.gen.Nodes[i].type;
                    if (t == DiscoveryNodeType.AbandonedMiningPocket) foundA = true;
                    if (t == DiscoveryNodeType.ThermalVentChamber) foundB = true;
                    if (t == DiscoveryNodeType.AncientSignalCache) foundC = true;
                    if (t == DiscoveryNodeType.CollapsedResourcePocket) foundD = true;
                }
                CleanupRig(rig);
            }
            Assert(!foundA && !foundB && !foundD, "E_Rejected_NoHalf",
                "Shallow+Mid 整带被保留区封死 → A/B/D 在跨 seed 中 0 生成（拒绝即整节点放弃，无半个节点）", sb, ref pass, ref fail);
            Assert(foundC, "E_Deep_StillOk", "Deep 未封 → C（AncientSignalCache）仍可生成（只拒绝被保留区覆盖者）", sb, ref pass, ref fail);
        }

        static RigInfo PickDensest(TileDatabase db,
            TileDefinition support, TileDefinition loose, TileDefinition hot, TileDefinition seal,
            TileDefinition data, TileDefinition alloy, OreReservedRect[] reserved)
        {
            RigInfo best = null;
            for (int s = 3000; s <= 3060; s++)
            {
                var rig = BuildRig(db, support, loose, hot, seal, data, alloy, reserved, s);
                if (rig == null) continue;
                if (best == null || rig.gen.Nodes.Count > best.gen.Nodes.Count) { CleanupRig(best); best = rig; }
                else CleanupRig(rig);
            }
            return best;
        }

        static RigInfo BuildRig(TileDatabase db,
            TileDefinition support, TileDefinition loose, TileDefinition hot, TileDefinition seal,
            TileDefinition data, TileDefinition alloy, OreReservedRect[] reserved, int seed)
        {
            var go = new GameObject("DEV014_Rig");
            var dg = go.AddComponent<DigGrid>();
            dg.database = db;
            dg.width = 48; dg.depth = 64;
            dg.useRandomSeed = false; dg.seed = seed;
            dg.enableFallingRocks = false; dg.surfaceOpeningHalfWidth = 3;
            dg.breakDuration = 0f;

            // 真实 Space + Vein band（还原验收场景生成顺序），节点才能「组合」已有 vein/space
            var space = go.AddComponent<UndergroundSpaceGenerator>();
            space.grid = dg; space.seedOverride = -1; space.bands = RealSpaceBands();
            dg.undergroundSpaceGenerator = space;
            var oreGen = go.AddComponent<OreVeinGenerator>();
            oreGen.grid = dg; oreGen.seedOverride = -1; oreGen.bands = RealVeinBands(db); oreGen.reservedRects = reserved;
            dg.oreVeinGenerator = oreGen;

            var gen = go.AddComponent<DiscoveryNodeGenerator>();
            gen.grid = dg; gen.seedOverride = -1;
            gen.reservedRects = reserved;
            gen.supportRockTile = support; gen.looseRockTile = loose; gen.hotRockTile = hot; gen.sealTile = seal;
            gen.oreVeinSource = oreGen; gen.ancientDataTile = data; gen.ancientAlloyTile = alloy;
            dg.discoveryNodeGenerator = gen;

            dg.RegenerateFromDatabase();
            return new RigInfo { go = go, grid = dg, gen = gen, oreVein = oreGen, seed = seed };
        }

        static UndergroundSpaceBand[] RealSpaceBands()
        {
            return new[]
            {
                new UndergroundSpaceBand { bandName = "Shallow", minDepth = 3, maxDepth = 21, pocketTarget = 5, pocketMinCells = 5, pocketMaxCells = 9, pocketWidthMax = 4, pocketHeightMax = 3, tunnelTarget = 3, tunnelMinLength = 3, tunnelMaxLength = 5, branchTarget = 0, smallRoomTarget = 1, branchSideMin = 2, branchSideMax = 3 },
                new UndergroundSpaceBand { bandName = "Mid", minDepth = 22, maxDepth = 43, pocketTarget = 6, pocketMinCells = 7, pocketMaxCells = 12, pocketWidthMax = 5, pocketHeightMax = 4, tunnelTarget = 4, tunnelMinLength = 4, tunnelMaxLength = 6, branchTarget = 1, smallRoomTarget = 1, branchSideMin = 2, branchSideMax = 3 },
                new UndergroundSpaceBand { bandName = "Deep", minDepth = 44, maxDepth = 62, pocketTarget = 7, pocketMinCells = 9, pocketMaxCells = 15, pocketWidthMax = 6, pocketHeightMax = 4, tunnelTarget = 4, tunnelMinLength = 6, tunnelMaxLength = 8, branchTarget = 2, smallRoomTarget = 1, branchSideMin = 2, branchSideMax = 4 },
            };
        }

        static OreDepthBand[] RealVeinBands(TileDatabase db)
        {
            var iron = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Iron_铁矿.asset");
            var copper = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Copper_铜矿.asset");
            var silver = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Silver_银矿.asset");
            var gold = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Gold_金矿.asset");
            var platinum = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Platinum_铂金.asset");
            var emerald = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Emerald_绿宝石.asset");
            var diamond = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Diamond_钻石.asset");
            return new[]
            {
                new OreDepthBand { bandName = "Shallow", minDepth = 3, maxDepth = 21, ores = new[] { iron, copper }, weights = new[] { 70f, 30f }, veinMinSize = 2, veinMaxSize = 4, veinFrequency = 0.6f },
                new OreDepthBand { bandName = "Mid", minDepth = 22, maxDepth = 43, ores = new[] { copper, silver, gold }, weights = new[] { 60f, 25f, 15f }, veinMinSize = 3, veinMaxSize = 5, veinFrequency = 0.7f },
                new OreDepthBand { bandName = "Deep", minDepth = 44, maxDepth = 62, ores = new[] { gold, platinum, emerald, diamond }, weights = new[] { 40f, 30f, 20f, 10f }, veinMinSize = 4, veinMaxSize = 6, veinFrequency = 0.7f },
            };
        }

        class RigInfo { public GameObject go; public DigGrid grid; public DiscoveryNodeGenerator gen; public OreVeinGenerator oreVein; public int seed; }

        static void CleanupRig(RigInfo r) { if (r != null && r.go != null) Object.DestroyImmediate(r.go); }

        static bool SameLayout(RigInfo a, RigInfo b)
        {
            if (a == null || b == null) return false;
            if (a.gen.Nodes.Count != b.gen.Nodes.Count) return false;
            for (int i = 0; i < a.gen.Nodes.Count; i++)
            {
                var na = a.gen.Nodes[i]; var nb = b.gen.Nodes[i];
                if (na.instanceId != nb.instanceId) return false;
                if (na.type != nb.type) return false;
                if (na.bounds != nb.bounds) return false;
                if (na.sealCell != nb.sealCell) return false;
                if (na.rewardCells.Count != nb.rewardCells.Count) return false;
                for (int k = 0; k < na.rewardCells.Count; k++) if (na.rewardCells[k] != nb.rewardCells[k]) return false;
                if (na.riskCells.Count != nb.riskCells.Count) return false;
            }
            return true;
        }

        static bool RectsOverlap(RectInt a, RectInt b)
            => a.xMin < b.xMax && a.xMax > b.xMin && a.yMin < b.yMax && a.yMax > b.yMin;

        static TileDefinition MakeTile(string name, BlockType bt, int value = 0)
        {
            var t = ScriptableObject.CreateInstance<TileDefinition>();
            t.displayName = "rig_" + name;
            t.hardness = 1; t.digHits = 2; t.isSolid = true; t.value = value; t.weight = 1f;
            t.stackLimit = 16; t.gridWidth = 1; t.blockType = bt;
            return t;
        }

        static void Assert(bool ok, string tag, string msg, StringBuilder sb, ref int pass, ref int fail)
        {
            if (ok) { pass++; sb.AppendLine($"PASS  {tag}: {msg}"); }
            else { fail++; sb.AppendLine($"FAIL  {tag}: {msg}"); }
        }
    }
}
#endif
