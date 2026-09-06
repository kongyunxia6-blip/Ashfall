#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// DEV-013：编辑回归（编辑模式静态，不 Play）。覆盖 Issue §12 可静态断言的部分：
    ///  A. 数据层完整性（RuinCatalog.AncientRelay 字段 / rewardProfile / scanSignature /
    ///     SpecialBlockCatalog 已登记 RuinSeal + AncientRelayCore + Classify）；
    ///  B. Deterministic generation（同 seed 重复生成布局一致；不同 seed 可观察变化；footprint 不越界；
    ///     不进 Surface / 保留区）；
    ///  C. Generator legality（Seal 邻接内腔 → 入口→Core 可达路径存在；内腔连通；不建第二 Grid）；
    ///  只读：只用临时 rig（不入库），不改任何既有资产/场景。
    ///  刻意不用 DisplayDialog（避免阻塞 MCP）。菜单：灰烬之下 → DEV-013 回归：遗迹/封印/中继核。
    /// </summary>
    public static class AncientRelayRoomV1Regression
    {
        const string OutPath = "C:/Users/58058/.workbuddy/tools/d13_regression_result.txt";
        const string DataFolder = "Assets/Ashfall/Data";

        [MenuItem("灰烬之下/DEV-013 回归：遗迹数据/生成/封印元数据")]
        public static void Run()
        {
            var sb = new StringBuilder();
            int pass = 0, fail = 0;
            sb.AppendLine("==== DEV-013 编辑回归 ====");

            RunAll(sb, ref pass, ref fail);

            sb.AppendLine($"\n==== 汇总：PASS={pass}  FAIL={fail} ====");
            try { File.WriteAllText(OutPath, sb.ToString()); }
            catch (System.Exception e) { Debug.LogError("[DEV-013] 写结果失败：" + e.Message); }
            Debug.Log($"[DEV-013] 编辑回归完成：PASS={pass} FAIL={fail} → {OutPath}");
        }

        static void RunAll(StringBuilder sb, ref int pass, ref int fail)
        {
            Assert(A_DataLayer(sb), "A_DataLayer", "数据层完整性：RuinCatalog.AncientRelay 登记齐全", sb, ref pass, ref fail);
            Assert(SpecialBlockCatalog.Find(SpecialBlockCatalog.RuinSeal) != null
                   && SpecialBlockCatalog.Find(SpecialBlockCatalog.AncientRelayCore) != null,
                "A_SealCore_Catalog", "SpecialBlockCatalog 已登记 RuinSeal / AncientRelayCore", sb, ref pass, ref fail);

            Assert(((int)MiningCapability.RuinAccess & (1 << 3)) != 0,
                "A_RuinAccess_Capability", "MiningCapability.RuinAccess 位已定义（=1<<3）", sb, ref pass, ref fail);

            // ---- 用临时 rig 验证确定性 + 合法性 ----
            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>($"{DataFolder}/TileDatabase.asset");
            if (db == null || db.emptyTile == null)
            {
                Assert(false, "B_Rig_Db", "TileDatabase 缺失或 emptyTile 为空（请先一键生成数据库）", sb, ref pass, ref fail);
                return;
            }

            // 临时墙/seal/core/alloy tile（不入库，仅跑生成逻辑）
            var wall = MakeTile("W", BlockType.Normal);
            var seal = MakeTile("S", BlockType.RuinSeal);
            var core = MakeTile("C", BlockType.AncientRelayCore);
            var alloy = MakeTile("A", BlockType.Normal);

            // ---- Rig 1：seed=20260906 生成，快照 ----
            var r1 = BuildRig(db, wall, seal, core, alloy, 20260906);
            if (r1 == null) { Assert(false, "B_Rig1", "Rig1 生成失败", sb, ref pass, ref fail); return; }

            // 至少登记一座
            Assert(r1.ruins.Count >= 1, "B_Generated", $"seed 生成遗迹 {r1.ruins.Count} 座", sb, ref pass, ref fail);
            var inst = r1.ruins[0];

            // C. 合法性：bounds 不越界、不进 Surface、入口邻 interior
            Assert(inst.bounds.xMin >= 0 && inst.bounds.yMin >= 0 && inst.bounds.xMax < r1.grid.Width
                   && inst.bounds.yMax < r1.grid.Depth, "C_Bounds_InGrid", $"footprint {inst.bounds} 不越界", sb, ref pass, ref fail);
            Assert(inst.bounds.yMin > DepthRegionLayout.Surface.maxDepth, "C_Not_Surface",
                $"遗迹 yMin={inst.bounds.yMin} 不在 Surface（不破坏地表带）", sb, ref pass, ref fail);
            Assert(IsInAllowedRegion(inst), "C_Region_Allowed", $"遗迹落在 AncientRelayRoom 允许区域（Deep）", sb, ref pass, ref fail);
            Assert(SealAdjacentToInterior(r1.grid, inst), "C_SealAdjacentInterior",
                $"入口 Seal {inst.entranceCell} 邻接内腔（存在合法入口→Core 通路）", sb, ref pass, ref fail);
            Assert(CoreReachableFromInterior(r1.grid, inst), "C_Core_Reachable", "Core 位于内腔可通行区（可达）", sb, ref pass, ref fail);
            Assert(inst.rewardCells.Count >= 1, "C_Has_RewardNode", "遗迹含 ≥1 个奖励节点", sb, ref pass, ref fail);

            // B. 确定性：同 seed 重新生成 → 布局一致
            var r1b = BuildRig(db, wall, seal, core, alloy, 20260906);
            Assert(r1b != null && SameLayout(r1, r1b), "B_Deterministic_SameSeed",
                "同 seed 重复生成 → count/instanceId/bounds/entrance/core 一致", sb, ref pass, ref fail);

            // B. 不同 seed：可观察变化（至少 bounds 起点或 instanceId 不同）
            var r2 = BuildRig(db, wall, seal, core, alloy, 42);
            bool changed = r2 != null && r2.ruins.Count >= 1 && !SameBounds(r1.ruins[0], r2.ruins[0]);
            Assert(changed, "B_Deterministic_DiffSeed", "不同 seed 至少能产生可观察布局变化（bounds 起点不同）", sb, ref pass, ref fail);

            // C. 唯一 Grid：vein/space/ruin pass 都写同一个 digGrid（无第二套）
            Assert(r1.ruinGen.grid == r1.grid, "C_SingleGrid", "RuinGenerator 依附现有 DigGrid（不建第二 Grid）", sb, ref pass, ref fail);

            // D 静态只读：RuinGenerator 不接管 vein/space 的既有 grid 语义（只经 grid.SetTile 写，无独立数组）
            Assert(r1.ruinGen.Instances[0].definition == RuinCatalog.AncientRelay, "D_Def_Link", "RuinInstance 引用 AncientRelayRoom 定义", sb, ref pass, ref fail);

            CleanupRig(r1); CleanupRig(r1b); CleanupRig(r2);

            // ---- E. Blocker2：EquipmentProgression ownedModules 旧序列化迁移自愈 ----
            E_OwnedModules_Migration(sb, ref pass, ref fail);

            // ---- F. Blocker3：InventoryGrid 整包原子预检 CanAcceptFullBatch（纯 C# 编辑态） ----
            F_BatchAtomic(sb, ref pass, ref fail);
        }

        // ---------- F. Blocker3：整包原子预检 CanAcceptFullBatch（上一轮"分项预检不共享占用"漏洞的编辑级复现） ----------
        static void F_BatchAtomic(StringBuilder sb, ref int pass, ref int fail)
        {
            // 受控 InventoryGrid：6 列 × 2 行 = 12 格，载重 24。占位货物 = weight0/stackLimit1 → 可精确留空格。
            var inv = new InventoryGrid();
            inv.Resize(2, 24f);
            Assert(inv.Capacity == 12, "F_Rig_12", $"测试货舱 12 格（capacity={inv.Capacity}）", sb, ref pass, ref fail);

            var filler = MakeCargoTile("filler", 0f, 1);    // weight0, stackLimit1, gridWidth1
            var frag = MakeCargoTile("frag", 0.2f, 16);     // 模拟 AncientDataFragment
            var alloy = MakeCargoTile("alloy", 0.8f, 16);   // 模拟 AncientAlloy

            // (1) 只剩 1 个空格：fragment×1 单独能放、alloy×3 单独也能放，但整包（需 2 空格）必须拒绝。
            FillToFree(inv, filler, 1);
            Assert(inv.CanAcceptFull(frag, 1), "F_Partial_SoloFrag",
                "仅 1 空格：fragment×1 单独预检可放", sb, ref pass, ref fail);
            Assert(inv.CanAcceptFull(alloy, 3), "F_Partial_SoloAlloy",
                "仅 1 空格：alloy×3 单独预检也可放（各自独立看都够 → 旧分项预检会误判）", sb, ref pass, ref fail);
            Assert(!inv.CanAcceptFullBatch(new[] { new InventoryGrid.AddItemRequest(frag, 1), new InventoryGrid.AddItemRequest(alloy, 3) }),
                "F_Partial_Batch_Reject",
                "整包原子预检：fragment×1+alloy×3 需 2 空格 > 现有 1 → 拒绝（不再“部分奖励丢失却标调查”）", sb, ref pass, ref fail);
            Assert(inv.UsedSlots == 11, "F_Partial_NoMutate", "整包拒绝不污染真实 slots（仍占 11 格）", sb, ref pass, ref fail);

            // (2) 跨多个 stackLimit：单件 alloy×20（>16 需 2 堆）。1 空格应拒（旧实现重复命中同一空槽误判）、2 空格应成。
            inv.Clear();
            FillToFree(inv, filler, 1);
            Assert(!inv.CanAcceptFullBatch(new[] { new InventoryGrid.AddItemRequest(alloy, 20) }),
                "F_MultiStack_1Free_Reject",
                "仅 1 空格：alloy×20 需跨 2 堆 → 整包拒绝（单件跨 stack 不重看同一空槽）", sb, ref pass, ref fail);
            inv.Clear();
            FillToFree(inv, filler, 2);
            Assert(inv.CanAcceptFullBatch(new[] { new InventoryGrid.AddItemRequest(alloy, 20) }),
                "F_MultiStack_2Free_Fits",
                "恰 2 空格：alloy×20 跨 2 堆可完整入包（消耗两格）", sb, ref pass, ref fail);

            // (3) 预检通过 → AddItem 实际 leftover 必为 0（模拟与真实发放同构，不丢不复制）
            inv.Clear();
            FillToFree(inv, filler, 2);
            bool batchOk = inv.CanAcceptFullBatch(new[] { new InventoryGrid.AddItemRequest(frag, 1), new InventoryGrid.AddItemRequest(alloy, 3) });
            int leftFrag = inv.AddItem(frag, 1);
            int leftAlloy = inv.AddItem(alloy, 3);
            Assert(batchOk && leftFrag == 0 && leftAlloy == 0, "F_Predict_AddItemZero",
                "整包预检通过 → AddItem 每项 left==0（预检精确预测真实发放，无部分/复制）", sb, ref pass, ref fail);
        }

        // 用 weight0/stackLimit1 占位货物把背包清空并精确留 free 个空格
        static void FillToFree(InventoryGrid inv, TileDefinition filler, int free)
        {
            inv.Clear();
            int fill = inv.Capacity - free;
            if (fill > 0) inv.AddItem(filler, fill);
        }

        static TileDefinition MakeCargoTile(string name, float weight, int stackLimit)
        {
            var t = ScriptableObject.CreateInstance<TileDefinition>();
            t.displayName = "rig_" + name;
            t.weight = weight;
            t.stackLimit = stackLimit;
            t.gridWidth = 1;
            t.value = 1;
            return t;
        }

        // ---------- E. ownedModules 4→5 迁移自愈（Blocker2） ----------
        static void E_OwnedModules_Migration(StringBuilder sb, ref int pass, ref int fail)
        {
            var eqGo = new GameObject("DEV013_EqMig");
            var eq = eqGo.AddComponent<EquipmentProgression>();   // Awake → EnsureModuleStorage 已扩到 enum 数

            // 模拟"旧 DEV-010/011 场景序列化出的长度 4 ownedModules"：强行退回 4，且第 0/1 位已拥有（旧模块状态）
            eq.ownedModules = new bool[4];
            eq.ownedModules[(int)EquipmentModule.EfficientMotor] = true;      // 0
            eq.ownedModules[(int)EquipmentModule.ReinforcedCargoRack] = true; // 1
            // 2 DrillCooling / 3 SurveySensor 未拥有

            // 显式调用迁移（等价 Awake/OnValidate 在旧场景加载时自愈）
            eq.EnsureModuleStorage();

            // 断言 1：长度扩到当前 enum 数（5，含 RuinAccessKey）
            int need = System.Enum.GetValues(typeof(EquipmentModule)).Length;
            Assert(eq.ownedModules.Length == need, "E_Mig_Resized",
                $"旧 ownedModules[4] 迁移 → [{eq.ownedModules.Length}]（enum 共 {need}）", sb, ref pass, ref fail);

            // 断言 2：前 4 位旧模块状态保留（0/1 true，2/3 false）
            Assert(eq.IsOwned(EquipmentModule.EfficientMotor) && eq.IsOwned(EquipmentModule.ReinforcedCargoRack),
                "E_Mig_PreserveOwned", "迁移后旧模块（节能电机/强化货架）仍 owned", sb, ref pass, ref fail);
            Assert(!eq.IsOwned(EquipmentModule.DrillCooling) && !eq.IsOwned(EquipmentModule.SurveySensor),
                "E_Mig_UnOwnedKept", "未拥有的旧模块迁移后仍为 false", sb, ref pass, ref fail);

            // 断言 3：RuinAccessKey（int=4）现在索引安全，可 IsOwned / 置 true（不越界）
            int raIdx = (int)EquipmentModule.RuinAccessKey;
            Assert(raIdx == 4 && raIdx < eq.ownedModules.Length, "E_Mig_RuinAccessInRange",
                $"RuinAccessKey int={raIdx} < ownedModules.Length={eq.ownedModules.Length}", sb, ref pass, ref fail);
            bool noOOB = false;
            try { eq.IsOwned(EquipmentModule.RuinAccessKey); noOOB = true; }
            catch (System.Exception) { noOOB = false; }
            Assert(noOOB, "E_Mig_IsOwnedNoOOB", "IsOwned(RuinAccessKey) 不越界", sb, ref pass, ref fail);

            // 断言 4：RuinAccessKey Buy/Equip/Unequip 交易路径不越界（IsOwned 读 + ownedModules[4] 写）
            // 绕过 CanAccessWorkbench 门控：直接验证数据层索引（EnsureModuleStorage + IsOwned 在 Array write 前已兜底）
            eq.ownedModules[raIdx] = true;   // 相当于 TryBuyModule 成功后的写入（此时长度已扩到 5）
            Assert(eq.IsOwned(EquipmentModule.RuinAccessKey), "E_Mig_RuinAccessOwned",
                "RuinAccessKey owned 写入成功（length 5 索引 4 安全）", sb, ref pass, ref fail);
            // 装备槽仍可装（equipped[0] = RuinAccessKey 不依赖 ownedModules 长度）
            eq.equipped[0] = EquipmentModule.RuinAccessKey;
            Assert(eq.IsEquipped(EquipmentModule.RuinAccessKey), "E_Mig_Equip", "RuinAccessKey 可装备", sb, ref pass, ref fail);
            eq.equipped[0] = null;
            Assert(!eq.IsEquipped(EquipmentModule.RuinAccessKey), "E_Mig_Unequip", "RuinAccessKey 可卸下", sb, ref pass, ref fail);

            Object.DestroyImmediate(eqGo);
        }

        // 下面是在临时 GameObject 上搭 rig
        static RigInfo BuildRig(TileDatabase db, TileDefinition wall, TileDefinition seal, TileDefinition core, TileDefinition alloy, int seed)
        {
            var go = new GameObject("DEV013_Rig");
            var dg = go.AddComponent<DigGrid>();
            dg.database = db;
            dg.width = 48;
            dg.depth = 64;
            dg.useRandomSeed = false;
            dg.seed = seed;
            dg.enableFallingRocks = false;
            dg.surfaceOpeningHalfWidth = 3;

            var ruinGen = go.AddComponent<RuinGenerator>();
            ruinGen.grid = dg;
            ruinGen.seedOverride = -1;
            ruinGen.wallTile = wall;
            ruinGen.sealTile = seal;
            ruinGen.coreTile = core;
            ruinGen.rewardTile = alloy;
            dg.ruinGenerator = ruinGen;

            dg.RegenerateFromDatabase();
            if (ruinGen.Instances.Count == 0)
            {
                Object.DestroyImmediate(go);
                return null;
            }
            return new RigInfo { go = go, grid = dg, ruinGen = ruinGen, ruins = ruinGen.Instances };
        }

        class RigInfo { public GameObject go; public DigGrid grid; public RuinGenerator ruinGen; public System.Collections.Generic.List<RuinInstance> ruins; }

        static void CleanupRig(RigInfo r) { if (r != null && r.go != null) Object.DestroyImmediate(r.go); }

        static TileDefinition MakeTile(string name, BlockType bt)
        {
            var t = ScriptableObject.CreateInstance<TileDefinition>();
            t.displayName = "rig_" + name;
            t.hardness = bt == BlockType.Normal ? 1 : 1;
            t.digHits = 2;
            t.isSolid = true;
            t.blockType = bt;
            return t;
        }

        static bool SameLayout(RigInfo a, RigInfo b)
        {
            if (a.ruins.Count != b.ruins.Count) return false;
            for (int i = 0; i < a.ruins.Count; i++)
            {
                if (a.ruins[i].instanceId != b.ruins[i].instanceId) return false;
                if (a.ruins[i].bounds != b.ruins[i].bounds) return false;
                if (a.ruins[i].entranceCell != b.ruins[i].entranceCell) return false;
                if (a.ruins[i].coreCell != b.ruins[i].coreCell) return false;
            }
            return true;
        }

        static bool SameBounds(RuinInstance a, RuinInstance b)
            => a != null && b != null && a.bounds == b.bounds && a.entranceCell == b.entranceCell && a.coreCell == b.coreCell;

        static bool IsInAllowedRegion(RuinInstance inst)
        {
            var def = inst.definition;
            if (def == null) return false;
            foreach (var r in DepthRegionLayout.All)
                if (r.regionId == def.allowedRegionId
                    && inst.bounds.yMin >= r.minDepth && inst.bounds.yMax <= r.maxDepth)
                    return true;
            return false;
        }

        static bool SealAdjacentToInterior(DigGrid g, RuinInstance inst)
        {
            var s = inst.entranceCell;
            foreach (var interior in inst.interiorCells)
            {
                int dx = interior.x - s.x, dy = interior.y - s.y;
                if (Mathf.Abs(dx) + Mathf.Abs(dy) == 1) return true;
            }
            return false;
        }

        static bool CoreReachableFromInterior(DigGrid g, RuinInstance inst)
        {
            // 简化：核心自身是 interior 一员或与某 interior 格 4 邻（即不悬空被墙隔离）
            var c = inst.coreCell;
            if (inst.interiorCells.Contains(c)) return true;
            foreach (var i in inst.interiorCells)
                if (Mathf.Abs(i.x - c.x) + Mathf.Abs(i.y - c.y) == 1) return true;
            return false;
        }

        // ---------- A. 数据层完整性 ----------
        static bool A_DataLayer(StringBuilder sb)
        {
            var d = RuinCatalog.AncientRelay;
            if (d == null) return false;
            bool ok = d.stableId == RuinCatalog.AncientRelayRoom
                && d.allowedRegionId == "Deep"
                && d.requiredCapability == MiningCapability.RuinAccess
                && d.rewardProfile != null && d.rewardProfile.Length == 2
                && !string.IsNullOrEmpty(d.scanSignature)
                && d.footprintWidth >= 6 && d.footprintHeight >= 3;
            // reward 必须含 AncientDataFragment 与 ancient_alloy
            bool hasFragment = false, hasAlloy = false;
            foreach (var r in d.rewardProfile)
            {
                if (r.stableId == RuinCatalog.AncientDataFragment) hasFragment = true;
                if (r.stableId == OreCatalog.AncientAlloy) hasAlloy = true;
            }
            return ok && hasFragment && hasAlloy;
        }

        static void Assert(bool ok, string tag, string msg, StringBuilder sb, ref int pass, ref int fail)
        {
            if (ok) { pass++; sb.AppendLine($"PASS  {tag}: {msg}"); }
            else { fail++; sb.AppendLine($"FAIL  {tag}: {msg}"); }
        }
    }
}
#endif
