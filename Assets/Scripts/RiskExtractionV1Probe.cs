using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-011 验收回归 S2：Run 生命周期 / 幂等性 / 装备永久成长回归（播放模式、真实场景驱动）。
    /// 挂载：RiskExtractionV1Test.unity（由 DepthRegionV1Builder.BuildRiskExtractionV1 一并挂上）。
    /// 用法：进入 Play 模式即自动运行（autoRun=true），结果写 d11_play_result.txt + Console PASS/FAIL。
    ///
    /// 对齐 Issue #23 §13 D/E/F：
    ///  - D. Run 生命周期：地表初始 inactive → 离地 active → 回地表未卖风险仍在 → Sell 后 secured →
    ///    再次离地开新 Run（RunNumber+1、run 统计 reset）→ 失败 → respawn 不自动重开 → 再离地方开新 Run；
    ///  - E. 幂等性：ResolveFailure 连续两次 / 同帧双结算 → 只扣一次 Cargo、只扣一次 Cash、只生成一次 summary；
    ///  - F. 装备永久成长回归：失败结算前后四条线等级 / owned / equipped / Effective Stats 完全一致。
    ///
    /// 驱动方式：直接驱动 GameManager.IsAtSurface（RunRiskState 判定 Run 开始/结束的单一权威标志，
    /// 真实场景由 SurfaceHubZone trigger 驱动，等价于玩家离开/回到地表据点），不依赖脆弱 TP。
    /// Cargo 由 DrillVehicle.Inventory.AddItem / UnloadCargo 注入（确定性，与真实挖掘入包同一 API）。
    /// </summary>
    public class RiskExtractionV1Probe : MonoBehaviour
    {
        [Header("引用（Builder 注入；缺省自动查找）")]
        public TileDefinition iron;
        public TileDefinition copper;

        [Tooltip("进入 Play 是否自动跑验收序列")]
        public bool autoRun = true;

        [Tooltip("结果文件路径（默认写 %USERPROFILE%/.workbuddy/tools/）")]
        public string resultFile = @"C:/Users/58058/.workbuddy/tools/d11_play_result.txt";

        readonly List<string> logs = new List<string>();
        int passCount, failCount;

        GameManager gm;
        RunRiskState risk;
        DrillVehicle v;
        DepthRegionProgression prog;
        EquipmentProgression equip;

        // ---------- 断言工具 ----------

        void Assert(bool ok, string name, string detail)
        {
            string line = (ok ? "PASS " : "FAIL ") + name + " :: " + detail;
            logs.Add(line);
            if (ok) passCount++; else failCount++;
            if (!ok) Debug.LogWarning("[DEV-011 Probe] " + line);
        }

        string TryFindOre()
        {
#if UNITY_EDITOR
            // Builder 已注入优先；未注入时按资产路径找（仅编辑器 Play 有效）
            if (iron == null || copper == null)
            {
                iron = UnityEditor.AssetDatabase.LoadAssetAtPath<TileDefinition>("Assets/Ashfall/Data/Iron_铁矿.asset");
                copper = UnityEditor.AssetDatabase.LoadAssetAtPath<TileDefinition>("Assets/Ashfall/Data/Copper_铜矿.asset");
            }
#endif
            if (iron == null) return "未找到 Iron 资产";
            if (copper == null) return "未找到 Copper 资产";
            return null;
        }

        void Start()
        {
            if (!autoRun) return;
            StartCoroutine(RunSequence());
        }

        IEnumerator RunSequence()
        {
            // 等场景 Start 接线完成（GameManager/Player/RunRisk/DepthProgression/Equipment）
            float wait = 0f;
            while (wait < 3f)
            {
                gm = GameManager.Instance;
                if (gm != null) { risk = gm.RunRisk; v = gm.Player; }
                if (gm != null && gm.GetComponent<DepthRegionProgression>() != null)
                    prog = gm.GetComponent<DepthRegionProgression>();
                if (gm != null && gm.GetComponent<EquipmentProgression>() != null)
                    equip = gm.GetComponent<EquipmentProgression>();
                if (gm != null && risk != null && v != null && prog != null)
                    break;
                wait += 0.2f;
                yield return new WaitForSeconds(0.2f);
            }

            var sb = new StringBuilder();
            sb.AppendLine("DEV-011 Probe S2: Run lifecycle / idempotency / equipment regression (Play mode)");

            if (gm == null || risk == null || v == null || prog == null || equip == null)
            {
                sb.AppendLine("EXCEPTION: 未找到完整 rig。gm=" + gm + " risk=" + risk + " v=" + v +
                              " prog=" + prog + " equip=" + equip + " 请先点「灰烬之下→搭建 DEV-011 风险撤离测试场景」后重开 Play。");
            }
            else
            {
                string oreErr = TryFindOre();
                sb.AppendLine("Rig OK: gm.IsAtSurface=" + gm.IsAtSurface +
                              " runActive=" + risk.RunActive + " runNumber=" + risk.RunNumber +
                              " | tiles err=" + (oreErr ?? "none"));

                // 关掉 SurfaceHubZone trigger —— 让本探针独占 IsAtSurface 驱动权（该 zone 会按玩家
                // 真实位置持续回写 IsAtSurface，会把探针的受控翻转立刻盖掉。等价「flag 即权威」）。
                DisableSurfaceZoneDrivers();

                if (oreErr == null)
                {
                    sb.AppendLine();
                    // C# 迭代器不允许在带 catch 的 try 块内 yield(CS1626)，故改用手动 MoveNext
                    // 迭代子协程并在外层捕获异常 —— Step 不含 yield，可安全包 try/catch。
                    Step(RunLifecycle(sb), sb, "D");
                    Step(RunIdempotency(sb), sb, "E");
                    // RunEquipmentRegression 是同步方法(无 yield)，可安全在迭代器内直接 try/catch。
                    try { RunEquipmentRegression(sb); }
                    catch (Exception e) { sb.AppendLine("EXCEPTION[F]: " + e); }
                }
                else
                {
                    sb.AppendLine("矿石资产缺失，跳过 D/E/F（" + oreErr + "）");
                }
            }

            sb.AppendLine($"==== 汇总: 断言 FAIL = {failCount} ====");
            try { File.WriteAllText(resultFile, sb.ToString()); } catch (Exception e) { Debug.LogWarning("[DEV-011 Probe] 写文件失败 " + e.Message); }
            Debug.Log("[DEV-011 Probe] done, see " + resultFile + " (FAIL=" + failCount + ")");
            yield break;
        }

        /// <summary>
        /// 手动迭代一个（可能 yield 的）子测试协程，并在外层捕获异常。
        /// 本方法自身不含 yield，故可在 try/catch 内调用，规避 CS1626。
        /// 若子测试抛异常，统一记入 sb 并返回 false，不让整个探针崩溃。
        /// </summary>
        bool Step(IEnumerator e, StringBuilder sb, string tag)
        {
            try
            {
                while (e.MoveNext()) { }
                return true;
            }
            catch (Exception ex)
            {
                sb.AppendLine($"EXCEPTION[{tag}]: {ex}");
                return false;
            }
        }

        /// <summary>
        /// 停用场景里所有会写 GameManager.IsAtSurface 的 trigger（SurfaceHubZone / SurfaceBase 兜底），
        /// 使探针可独占控制 IsAtSurface 以确定性驱动 Run 生命周期判定。
        /// </summary>
        static void DisableSurfaceZoneDrivers()
        {
            var zones = FindObjectsByType<SurfaceHubZone>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var z in zones)
            {
                var c = z.GetComponent<Collider2D>();
                if (c != null) c.enabled = false;
            }
            var landers = FindObjectsByType<SurfaceBase>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var l in landers)
            {
                var c = l.GetComponent<Collider2D>();
                if (c != null && !SurfaceHubZone.AnyExists) c.enabled = false; // 无 SurfaceHubZone 时 SurfaceBase 才兜底写 IsAtSurface
            }
        }

        // ---------- D. Run 生命周期 ----------

        IEnumerator RunLifecycle(StringBuilder sb)
        {
            logs.Clear(); passCount = failCount = 0;
            sb.AppendLine("==== Run 生命周期 ====");

            // 清理：确保从干净状态开始（出厂装备、无 cargo、地表）
            equip.ResetProgression();
            v.Inventory.Clear();
            v.FullRestore();
            gm.IsAtSurface = true;

            // D1. 初始 inactive
            Assert(!risk.RunActive && !risk.RunSecured, "D_InitialInactive",
                $"RunActive={risk.RunActive} RunSecured={risk.RunSecured}");

            // D2. 首次离地 → active
            gm.IsAtSurface = false;
            yield return null;          // 让 RunRiskState.LateUpdate 检测到
            Assert(risk.RunActive, "D_LeaveSurface_Active", $"RunActive={risk.RunActive} RunNumber={risk.RunNumber}");

            // D3. 回地表未卖：风险仍在（RunActive 保持、未 secured）
            gm.IsAtSurface = true;
            yield return null;
            Assert(risk.RunActive && !risk.RunSecured, "D_ReturnUnSettled_RiskStays",
                $"RunActive={risk.RunActive} RunSecured={risk.RunSecured}");

            // D4. 注入 cargo + Sell 结算 → secured
            v.Inventory.AddItem(iron, 2);
            yield return null;
            int earned = v.UnloadCargo();     // 等价 SellTerminal 成功出售
            risk.NotifyCargoSecured(earned);
            Assert(risk.RunSecured && earned >= 0, "D_Sell_Secured",
                $"earned=${earned} RunSecured={risk.RunSecured} cargoAfter={v.CargoValue}");

            // D5. 再次离地 → 新 Run（RunNumber+1）
            int beforeRun = risk.RunNumber;
            gm.IsAtSurface = false;
            yield return null;
            Assert(risk.RunActive && risk.RunNumber == beforeRun + 1, "D_ReLeave_NewRun",
                $"RunNumber {beforeRun} → {risk.RunNumber}");

            Flush(sb);
        }

        // ---------- E. 幂等性 ----------

        IEnumerator RunIdempotency(StringBuilder sb)
        {
            logs.Clear(); passCount = failCount = 0;
            sb.AppendLine("==== 幂等性（ResolveFailure 连续两次必须为 no-op） ====");

            // 复位到「已开 Run + 携货」状态
            if (!risk.RunActive) { gm.IsAtSurface = false; yield return null; } // 若上个用例已结束 Run，先离地开一个
            if (!risk.RunActive) { risk.BeginRun(); yield return null; }
            risk.OnRespawned();                   // 确保 FailureResolved=false，本次允许结算
            v.Inventory.Clear();
            v.Inventory.AddItem(copper, 4);       // 铜×4 → keep=floor(2)=2，损 2
            v.Inventory.AddItem(iron, 3);         // 铁×3 → keep=floor(1.5)=1，损 2
            v.FullRestore();
            yield return null;

            int cashBefore = gm.Cash;

            // ---- 第 1 次 ResolveFailure ----
            risk.ResolveFailure("idem-A", 30);
            int keptCopper1 = CountCargo(v, copper);
            int keptIron1 = CountCargo(v, iron);
            int cash1 = gm.Cash;
            string summary1 = risk.LastFailureSummary;
            Assert(keptCopper1 == 2 && keptIron1 == 1, "E_First_CargoKeep",
                $"第1次: 铜×4→留{keptCopper1} 铁×3→留{keptIron1}（期望 2/1）");
            Assert(cash1 >= 0, "E_First_CashNotNegative", $"cash {cashBefore}→{cash1}");
            Assert(!string.IsNullOrEmpty(summary1), "E_First_SummaryGenerated",
                $"第1次生成 summary='{summary1}'");

            // ---- 第 2 次 ResolveFailure（幂等 guard 必须拦截，全量 no-op） ----
            risk.ResolveFailure("idem-B", 30);
            int keptCopper2 = CountCargo(v, copper);
            int keptIron2 = CountCargo(v, iron);
            int cash2 = gm.Cash;
            string summary2 = risk.LastFailureSummary;

            Assert(keptCopper2 == keptCopper1 && keptIron2 == keptIron1, "E_Second_CargoNoExtraLoss",
                $"第2次不得再损: 铜 {keptCopper1}→{keptCopper2} 铁 {keptIron1}→{keptIron2}");
            Assert(cash2 == cash1, "E_Second_CashNoExtraFee", $"第2次不得二次扣费: cash {cash1}→{cash2}");
            Assert(summary2 == summary1, "E_Second_SummaryUnchanged", $"第2次不得重写 summary");

            // Run 已因失败结束；respawn 不自动重开；再次离地开新 Run 且清空上次 summary
            Assert(!risk.RunActive && risk.FailureResolved, "E_AfterDeath_RunEnded",
                $"RunActive={risk.RunActive} FailureResolved={risk.FailureResolved}");

            int runBefore = risk.RunNumber;
            risk.OnRespawned();                   // 模拟 GameManager.Respawn
            Assert(!risk.RunActive && !risk.FailureResolved, "E_Respawn_NotAutoRestart",
                $"RunActive={risk.RunActive} FailureResolved={risk.FailureResolved}（不得自动开新 Run）");

            gm.IsAtSurface = false;
            yield return null;                    // LateUpdate 检测到离地 → BeginRun（新 Run）
            Assert(risk.RunActive && risk.RunNumber == runBefore + 1, "E_ReLeave_NewRunIndep",
                $"RunNumber {runBefore} → {risk.RunNumber}");
            Assert(risk.LastFailureSummary.Length == 0, "E_NewRun_SummaryCleared",
                $"BeginRun 后上次 summary 应清空，当前='{risk.LastFailureSummary}'");

            Flush(sb);
        }

        // ---------- F. 装备永久成长回归 ----------

        void RunEquipmentRegression(StringBuilder sb)
        {
            logs.Clear(); passCount = failCount = 0;
            sb.AppendLine("==== 装备永久成长回归（失败前后一致） ====");

            // 先造一个非出厂状态（升级两条线 + 购一模块 + 装一模块）——但升级/购买需 Workbench 门控。
            // 为验证「失败不触碰装备」，直接用 public 字段快照比对（RunRisk 结算根本不写装备）。
            equip.ResetProgression();
            // 用 public 字段直接设一个已知非零态（验收目标是「失败结算前后不变」，来源是否为出厂无关紧要）
            equip.drillLevel = 2;
            equip.fuelTankLevel = 1;
            equip.ownedModules[0] = true;
            equip.equipped[0] = (EquipmentModule)0;

            // 快照
            int d0 = equip.drillLevel, f0 = equip.fuelTankLevel,
                c0 = equip.cargoHoldLevel, m0 = equip.mobilityLevel;
            bool own0 = equip.ownedModules[0];
            bool eq0 = equip.IsEquipped((EquipmentModule)0);
            float dig0 = equip.EffectiveDigSpeedMultiplier, fuel0 = equip.EffectiveMaxFuel,
                  carry0 = equip.EffectiveMaxCarryWeight, spd0 = equip.EffectiveMoveSpeed;

            // 触发一次失败结算
            v.Inventory.Clear();
            v.Inventory.AddItem(copper, 2);
            v.FullRestore();
            risk.BeginRun();      // 确保可结算
            risk.ResolveFailure("equip-regress", 25);

            // 比对：四条线 / owned / equipped / effective 全一致
            Assert(equip.drillLevel == d0 && equip.fuelTankLevel == f0 &&
                   equip.cargoHoldLevel == c0 && equip.mobilityLevel == m0,
                "F_LevelsPreserved", $"drill {d0}→{equip.drillLevel} fuel {f0}→{equip.fuelTankLevel} " +
                                     $"cargo {c0}→{equip.cargoHoldLevel} mob {m0}→{equip.mobilityLevel}");
            Assert(equip.ownedModules[0] == own0, "F_OwnedPreserved", $"owned[0] {own0}→{equip.ownedModules[0]}");
            Assert(equip.IsEquipped((EquipmentModule)0) == eq0, "F_EquippedPreserved", $"equipped {eq0}→{equip.IsEquipped((EquipmentModule)0)}");
            bool statsSame = Mathf.Abs(equip.EffectiveDigSpeedMultiplier - dig0) < 0.0001f &&
                             Mathf.Abs(equip.EffectiveMaxFuel - fuel0) < 0.0001f &&
                             Mathf.Abs(equip.EffectiveMaxCarryWeight - carry0) < 0.0001f &&
                             Mathf.Abs(equip.EffectiveMoveSpeed - spd0) < 0.0001f;
            Assert(statsSame, "F_EffectiveStatsStable",
                $"dig {dig0:F3}/{equip.EffectiveDigSpeedMultiplier:F3} fuel {fuel0:F0}/{equip.EffectiveMaxFuel:F0} " +
                $"carry {carry0:F0}/{equip.EffectiveMaxCarryWeight:F0} spd {spd0:F2}/{equip.EffectiveMoveSpeed:F2}");

            Flush(sb);
        }

        // ---------- 工具 ----------

        static int CountCargo(DrillVehicle vehicle, TileDefinition def)
        {
            if (vehicle == null || def == null) return 0;
            int n = 0;
            var inv = vehicle.Inventory;
            for (int i = 0; i < inv.Capacity; i++)
            {
                var s = inv.GetSlot(i);
                if (s != null && s.isPrimary && s.def == def) n += s.count;
            }
            return n;
        }

        void Flush(StringBuilder sb)
        {
            foreach (var l in logs) sb.AppendLine("  " + l);
            sb.AppendLine($"  => PASS {passCount} / FAIL {failCount}");
            sb.AppendLine();
        }
    }
}
