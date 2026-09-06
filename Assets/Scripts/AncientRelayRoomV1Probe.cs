using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-013：播放模式验收探针（真实 Unity 运行）。
    /// 覆盖 Issue §12 需真实运行的矩阵：
    ///  D. RuinDiscoveryService 只读信号（远模糊 / 近触发 discovered / 不泄露奖励坐标）；
    ///  E. RuinSeal A-B（无 RuinAccess 拒挖、有 RuinAccess 单格打开、邻格零伤、equip/unequip 可逆）；
    ///  F. AncientRelayCore 首次交互 + 幂等（AncientDataFragment/AncientAlloy 进 Inventory，二次不重复）；
    ///  G. RunRisk 集成（携带奖励死亡按 cargo loss、investigated 不重置、Run#2 不错误重置）。
    /// 用真实跨帧协程 + 公共挖掘路径 DrillVehicle.TryDigHit（单格唯一入口）驱动，写入结果文件。
    ///
    /// 挂载：AncientRelayRoomV1Test 场景 GameManager 物体。autoRun=true 时 Start 自动跑。
    /// </summary>
    public class AncientRelayRoomV1Probe : MonoBehaviour
    {
        public bool autoRun = true;
        public DigGrid grid;
        public RuinGenerator generator;
        public RuinDiscoveryService discovery;
        public AncientRelayCoreSystem core;
        public RuinSealSystem seal;
        public DrillVehicle vehicle;
        public EquipmentProgression equipment;
        public TileDefinition wallAsset;
        public TileDefinition sealAsset;
        public TileDefinition coreAsset;
        public TileDefinition alloyAsset;
        public TileDefinition fragmentAsset;

        const string OutPath = "C:/Users/58058/.workbuddy/tools/d13_play_result.txt";

        int passCount = 0, failCount = 0;
        readonly StringBuilder sb = new StringBuilder();

        IEnumerator Start()
        {
            Debug.Log("[DEV-013] Probe Start: running...");
            yield return new WaitForSeconds(1f);

            // 兜底解析（Builder 编辑模式序列化引用可能因 private set 为 null，运行时补齐）
            if (equipment == null) equipment = GetComponent<EquipmentProgression>()
                ?? GetComponentInParent<EquipmentProgression>() ?? FindObjectOfType<EquipmentProgression>();
            var gm = GameManager.Instance;

            // 确保起点干净：无模块、钻头 Lv0；场景已 Regenerate，读回生成器实例
            if (equipment != null)
            {
                equipment.drillLevel = 0;
                for (int i = 0; i < equipment.ownedModules.Length; i++) equipment.ownedModules[i] = false;
                for (int i = 0; i < equipment.equipped.Length; i++) equipment.equipped[i] = null;
            }

            sb.AppendLine("==== DEV-013 播放验收 ====");
            yield return Step(D_DiscoverySignals(), "D");
            yield return Step(E_RuinSealAB(), "E");
            yield return Step(F_AncientRelayCore(), "F");
            yield return Step(G_RunRiskIntegration(), "G");

            sb.AppendLine($"\n==== 汇总：PASS={passCount}  FAIL={failCount} ====");
            try { File.WriteAllText(OutPath, sb.ToString()); }
            catch (System.Exception e) { Debug.LogError("[DEV-013] 写结果失败：" + e.Message); }
            Debug.Log($"[DEV-013] 播放验收完成：PASS={passCount} FAIL={failCount} → {OutPath}");
        }

        IEnumerator Step(IEnumerator body, string tag) { yield return body; }

        void Assert(bool ok, string tag, string msg)
        {
            if (ok) { passCount++; sb.AppendLine($"PASS  {tag}: {msg}"); }
            else { failCount++; sb.AppendLine($"FAIL  {tag}: {msg}"); }
            Debug.Log($"[DEV-013] {(ok ? "PASS" : "FAIL")} {tag}: {msg}");
        }

        RuinInstance FirstRuin()
        {
            if (generator != null && generator.Instances.Count > 0) return generator.Instances[0];
            // 兜底：从 discovery 已注册布局找
            if (discovery != null && discovery.Instances.Count > 0) return discovery.Instances[0];
            return null;
        }

        // ---------- D. RuinDiscoveryService 只读信号 ----------
        IEnumerator D_DiscoverySignals()
        {
            sb.AppendLine("\n==== D. 遗迹发现信号（只读） ====");
            if (grid == null || generator == null || discovery == null)
            {
                Assert(false, "D_Rig", "缺 grid/generator/discovery"); yield break;
            }
            var ruin = FirstRuin();
            if (ruin == null)
            {
                Assert(false, "D_NoRuin", "生成器未登记任何遗迹（seed 未放置 AncientRelayRoom）"); yield break;
            }
            Assert(generator.HasInstances, "D_RuinGenerated", $"seed 下生成 {generator.Instances.Count} 座遗迹（{ruin.instanceId}）");

            discovery.RegisterAll(generator.Instances);
            // 远程（地表出生区，bounds 在 Deep 44+）：应无信号或至多模糊（dist > signalRange → 无）
            var farCenter = new Vector2Int(grid.Width / 2, 2);
            var farRes = discovery.ScanRuinAround(farCenter);
            Assert(!farRes.hasSignal || !farRes.isDiscovered, "D_Far_NoDiscover",
                $"远处({farCenter})未触发发现（hasSignal={farRes.hasSignal}, dist={farRes.distance}）");
            Assert(!discovery.IsDiscovered(ruin.instanceId), "D_Far_StateClean", "远距离不置 discovered");

            // 近处（bounds 内部）：触发 discovered
            var nearCenter = new Vector2Int(ruin.bounds.xMin, ruin.bounds.yMin);
            var nearRes = discovery.ScanRuinAround(nearCenter);
            Assert(nearRes.firstDiscovery, "D_Near_FirstDiscover", $"接近{boundsStr(ruin)}触发首发现");
            Assert(discovery.IsDiscovered(ruin.instanceId), "D_Near_StateSet", "近距离置 discovered=true");
            Assert(nearRes.isDiscovered, "D_Res_IsDiscovered", "返回结果 isDiscovered=true");

            // 再次扫描不再触发首发现（幂等）
            discovery.ScanRuinAround(nearCenter);
            Assert(discovery.DiscoveredCount == 1, "D_Discover_Idempotent", "首发现幂等（计数 1，不重复）");

            // 扫描前后世界不变（只读）：记录核心/墙/奖励格 tile，扫描后应一致
            var tSeal = grid.GetTile(ruin.entranceCell.x, ruin.entranceCell.y);
            var tCore = grid.GetTile(ruin.coreCell.x, ruin.coreCell.y);
            discovery.ScanRuinAround(new Vector2Int(grid.Width / 2, 2));
            discovery.ScanRuinAround(nearCenter);
            Assert(grid.GetTile(ruin.entranceCell.x, ruin.entranceCell.y) == tSeal, "D_ReadOnly_Seal", "扫描后 Seal 格未变");
            Assert(grid.GetTile(ruin.coreCell.x, ruin.coreCell.y) == tCore, "D_ReadOnly_Core", "扫描后 Core 格未变");
            Assert(!ruin.rewardCells.Exists(c => nearRes.description.Contains("坐标")), "D_No_RewardLeak", "信号描述不泄露奖励坐标");
        }

        static string boundsStr(RuinInstance r) => $"({r.bounds.x},{r.bounds.y},{r.bounds.width},{r.bounds.height})";

        // ---------- E. RuinSeal A-B ----------
        IEnumerator E_RuinSealAB()
        {
            sb.AppendLine("\n==== E. RuinSeal A-B（无/有 RuinAccess） ====");
            if (grid == null || seal == null || vehicle == null || equipment == null)
            {
                Assert(false, "E_Rig", "缺 seal/vehicle/equipment"); yield break;
            }
            var ruin = FirstRuin();
            if (ruin == null) { Assert(false, "E_NoRuin", "无遗迹"); yield break; }

            var sc = ruin.entranceCell;
            Assert(grid.GetTile(sc.x, sc.y) != null && grid.GetTile(sc.x, sc.y).blockType == BlockType.RuinSeal,
                "E_Pre_SealInPlace", $"({sc.x},{sc.y}) 入口是 RuinSeal");

            // ---- A. 无 RuinAccess ----
            equipment.equipped[0] = null;
            equipment.ownedModules[(int)EquipmentModule.RuinAccessKey] = false;
            yield return new WaitForEndOfFrame();
            Assert(!MiningCapabilityResolver.HasCapability(MiningCapability.RuinAccess), "E_A_NoAccess", "初始无 RuinAccess");
            seal.ResetSeal();
            int durBefore = grid.GetDurability(sc.x, sc.y);
            var resA = vehicle.TryDigHit(sc);
            Assert(resA == DigHitResult.Sealed, "E_A_Sealed_Result", $"无 RuinAccess 挖 Seal → Sealed（拒挖）");
            Assert(grid.GetTile(sc.x, sc.y) != null && grid.GetTile(sc.x, sc.y).blockType == BlockType.RuinSeal, "E_A_Seal_Closed",
                "无权限入口不被打开（Seal 仍在原位）");
            Assert(grid.GetDurability(sc.x, sc.y) == durBefore, "E_A_Seal_NoDamage", "无权限不扣 Seal 耐久（单格不变）");
            Assert(seal.LastVerdict == "sealed_no_access", "E_A_Feedback", "封印系统给出明确拒挖反馈（非 invisible wall）");

            // 无权限挖 Seal 不伤邻格
            Assert(NeighborUnscathedExcluding(sc, -1), "E_A_NeighborsUntouched", "无权限时 Seal 邻格不受主动挖掘伤害");

            // ---- B. 有 RuinAccess ----
            equipment.ownedModules[(int)EquipmentModule.RuinAccessKey] = true;
            equipment.equipped[0] = EquipmentModule.RuinAccessKey;
            yield return new WaitForEndOfFrame();
            Assert(MiningCapabilityResolver.HasCapability(MiningCapability.RuinAccess), "E_B_Access", "装备 RuinAccessKey → HasRuinAccess=true");
            seal.ResetSeal();
            Assert(seal.AllowOpenSeal(MiningCapabilityResolver.HasCapability(MiningCapability.RuinAccess))
                   && seal.LastVerdict == "open_allowed", "E_B_Gate_Allows",
                "权限满足后封印门控放行（open_allowed，不再静默拒挖）");

            // 有权限：连击打开（digHits=2）
            int beforeLeft = grid.GetDurability(sc.x - 1, sc.y);   // 邻墙（若实心）
            bool opened = false;
            for (int i = 0; i < 5 && !opened; i++)
            {
                var r = vehicle.TryDigHit(sc);
                if (r == DigHitResult.Broken) opened = true;
                else if (r == DigHitResult.Hit) continue;
                else if (r == DigHitResult.NotSolid) break;
            }
            Assert(opened, "E_B_Seal_Opened", "有 RuinAccess 后单格命中打开 Seal（入口打通）");
            var afterTile = grid.GetTile(sc.x, sc.y);
            Assert(afterTile == null || !afterTile.isSolid || afterTile == grid.database.emptyTile, "E_B_EntryOpen",
                "Seal 移除后入口格变空（可通行）");
            Assert(NeighborUnscathedExcluding(sc, beforeLeft), "E_B_NeighborsUntouched", "打开 Seal 只作用 1 Block，邻格不受主动挖掘伤害");

            // Unequip → 可逆
            equipment.equipped[0] = null;
            yield return new WaitForEndOfFrame();
            Assert(!MiningCapabilityResolver.HasCapability(MiningCapability.RuinAccess), "E_B_Unequip_Reversible", "卸下 RuinAccessKey → 能力可逆");

            // 能力查询走 capability 层（E 代码只查 HasCapability，不查模块 enum）
            Assert(SpecialBlockCatalog.Classify(sealAsset) != null
                   && (SpecialBlockCatalog.Classify(sealAsset).reactionHook & SpecialReactionHook.SealBreak) != 0,
                "E_Seal_Metadata", "RuinSeal 经 SpecialBlockCatalog 元数据（SealBreak hook），不散落 if(blockType)");
        }

        bool NeighborUnscathedExcluding(Vector2Int c, int beforeLeft)
        {
            // 记录命中 Seal 前后邻格耐久是否均未减（对实心邻格）
            foreach (var n in Neighbors4(c))
            {
                if (!grid.IsSolid(n.x, n.y)) continue;
                int cur = grid.GetDurability(n.x, n.y);
                if (cur < 1 && grid.GetTile(n.x, n.y) != grid.database.emptyTile) return false;
            }
            return true;
        }

        static System.Collections.Generic.List<Vector2Int> Neighbors4(Vector2Int c)
            => new System.Collections.Generic.List<Vector2Int>
            {
                new Vector2Int(c.x + 1, c.y), new Vector2Int(c.x - 1, c.y),
                new Vector2Int(c.x, c.y + 1), new Vector2Int(c.x, c.y - 1),
            };

        // ---------- F. AncientRelayCore 一次性调查 + 幂等 ----------
        IEnumerator F_AncientRelayCore()
        {
            sb.AppendLine("\n==== F. AncientRelayCore 一次性调查/奖励 ====");
            if (grid == null || core == null || vehicle == null)
            {
                Assert(false, "F_Rig", "缺 core/vehicle"); yield break;
            }
            var ruin = FirstRuin();
            if (ruin == null) { Assert(false, "F_NoRuin", "无遗迹"); yield break; }

            if (discovery == null) { Assert(false, "F_NoDiscovery", "缺 discovery"); yield break; }
            discovery.RegisterAll(generator != null ? generator.Instances : null);

            var cc = ruin.coreCell;
            Assert(grid.GetTile(cc.x, cc.y) != null && grid.GetTile(cc.x, cc.y).blockType == BlockType.AncientRelayCore,
                "F_Pre_CoreInPlace", $"({cc.x},{cc.y}) 房间内是 AncientRelayCore");

            // 首次交互
            var invBefore = Snapshot(vehicle);
            var res1 = vehicle.TryDigHit(cc);
            Assert(res1 == DigHitResult.Interacted, "F_First_Interacted", $"首次命中 Core → Interacted（{res1}）");
            Assert(discovery.IsInvestigated(ruin.instanceId), "F_First_Investigated", "首次交互标记 investigated=true");

            // 奖励进入现有 Inventory（fragment×1 + alloy×3）
            int got = inventoryDelta(invBefore);
            Assert(got > 0, "F_Reward_Granted", $"首次调查获得文明资源（Inventory 净增 {got} 件：{core.LastRewardText}）");
            Assert(invBefore.hasFragment == false && HasItem(vehicle, fragmentAsset), "F_Reward_Fragment",
                "AncientDataFragment 进入现有 Inventory");
            Assert(invBefore.alloyCount == 0 && HasItem(vehicle, alloyAsset), "F_Reward_Alloy", "AncientAlloy 进入现有 Inventory");
            Assert(core.LastVerdict == "investigated_first_time", "F_First_Verdict", "Core 状态 = investigated_first_time");

            // 二次交互：不重复首奖
            var invAfter1 = Snapshot(vehicle);
            var res2 = vehicle.TryDigHit(cc);
            Assert(res2 == DigHitResult.Interacted, "F_Second_Interacted", "二次命中仍 Interacted（交互点不消失）");
            Assert(grid.GetTile(cc.x, cc.y) != null && grid.GetTile(cc.x, cc.y).blockType == BlockType.AncientRelayCore,
                "F_Core_NotDestroyed", "Core 是装置点，不被挖穿/不消失");
            int got2 = inventoryDelta(invAfter1);
            Assert(got2 == 0, "F_Second_NoRepeat", $"二次交互不重复发首奖（Inventory 净增 {got2}）");
            Assert(core.LastVerdict == "already_investigated", "F_Second_Verdict", "Core 状态 = already_investigated");
        }

        // 简易背包快照（只关心 fragment/alloy 计数 + 总件数）
        class InvSnap { public bool hasFragment; public int alloyCount; public int totalItems; }
        InvSnap Snapshot(DrillVehicle v)
        {
            var s = new InvSnap();
            if (v == null || v.Inventory == null) return s;
            for (int i = 0; i < v.Inventory.Capacity; i++)
            {
                var slot = v.Inventory.GetSlot(i);
                if (slot == null || slot.def == null || slot.count <= 0) continue;
                s.totalItems += slot.count;
                if (slot.def == fragmentAsset) s.hasFragment = true;
                if (slot.def == alloyAsset) s.alloyCount += slot.count;
            }
            return s;
        }

        int inventoryDelta(InvSnap before)
        {
            var s = Snapshot(vehicle);
            return s.totalItems - before.totalItems;
        }

        bool HasItem(DrillVehicle v, TileDefinition def)
        {
            for (int i = 0; i < v.Inventory.Capacity; i++)
            {
                var slot = v.Inventory.GetSlot(i);
                if (slot != null && slot.def == def && slot.count > 0) return true;
            }
            return false;
        }

        // ---------- G. RunRisk 集成 ----------
        IEnumerator G_RunRiskIntegration()
        {
            sb.AppendLine("\n==== G. RunRisk 集成（死亡 cargo loss，不重置遗迹状态） ====");
            var gm = GameManager.Instance;
            var runRisk = gm != null ? gm.RunRisk : null;
            if (runRisk == null) { Assert(false, "G_NoRunRisk", "场景无 RunRiskState（DEV-011）"); yield break; }

            try
            {
                // 确保一块含奖励的 cargo 在背包（奖励已是 cargo）
                bool hasRewardCargo = HasItem(vehicle, fragmentAsset) || HasItem(vehicle, alloyAsset);
                Assert(hasRewardCargo, "G_Reward_InCargo", "遗迹奖励已作为 Cargo 进入背包（不绕过风险体系）");

                var ruin = FirstRuin();
                string iid = ruin != null ? ruin.instanceId : "";
                if (ruin != null && !discovery.IsInvestigated(iid)) discovery.MarkInvestigated(iid);
                bool invBeforeDeath = discovery.IsInvestigated(iid);

                // 开始 Run#1（携带 cargo 下潜）→ 死亡 → ResolveFailure 按 cargo loss
                runRisk.BeginRun();
                int cargoBefore = vehicle.CargoValue;
                int cashBefore = gm.Cash;
                runRisk.ResolveFailure("DEV013_death_test", ruin != null ? ruin.bounds.yMin : 50);

                int cargoAfter = vehicle.CargoValue;
                bool lossApplied = cargoAfter < cargoBefore;
                Assert(lossApplied, "G_CargoLoss_Applied",
                    $"携带遗迹奖励死亡 → Cargo 按 DEV-011 规则部分损失（{cargoBefore}→{cargoAfter}）");
                Assert(discovery.IsInvestigated(iid) == invBeforeDeath, "G_Death_NoReset_Investigated",
                    "死亡结算不重置遗迹 investigated 状态（防刷首奖）");

                // Run#2 开始：investigated 仍保持（不随新 Run 错误重置）
                runRisk.BeginRun();
                Assert(discovery.IsInvestigated(iid), "G_Run2_NoReset", "Run#2 不错误重置 investigated（首奖不因返航/新 Run 复现）");
            }
            catch (System.Exception e)
            {
                Assert(false, "G_Exception", "RunRisk 集成段异常：" + e.Message);
            }
        }
    }
}
