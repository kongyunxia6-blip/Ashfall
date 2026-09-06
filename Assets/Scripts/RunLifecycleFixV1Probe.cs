using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-011 fix：Run 生命周期真实帧验证（Sell 成功结束本 Run → 下次离地 BeginRun → Run#2）。
    ///
    /// 背景：现有 RiskExtractionV1Probe 的 Step(IEnumerator) 用 while(MoveNext) 同步推进子协程，
    /// 把 `yield return null` 变成零帧等待 —— RunRiskState.LateUpdate 得不到真实帧机会去触发
    /// BeginRun，导致 Run#1→Sell→Run#2 无法被真实验证。
    ///
    /// 本探针改用标准 StartCoroutine + WaitForEndOfFrame（真实跨帧），在播放模式逐帧驱动
    /// GameManager.IsAtSurface，验证完整 Run 生命周期：
    ///   Run#1 下矿(离地) → BeginRun(RunNumber=1, MaxDepthThisRun reset) → 下潜记深度
    ///   → 返航回地表 → Sell 成功 → 本 Run 结束(RunActive=false, RunSecured=true)
    ///   → 再下矿(再次离地) → BeginRun(RunNumber=2, MaxDepthThisRun/DeepestRegion/first-enter reset)
    ///   → 且 MaxDepthEver 永不 reset。
    ///
    /// 挂载：场景内任意物体（autoRun=true 自动跑），结果写 d11_runlifecycle_fix.txt。
    /// </summary>
    public class RunLifecycleFixV1Probe : MonoBehaviour
    {
        [Header("引用（缺省自动查找；iron/copper 仅 multi-stack 交叉用，可选）")]
        public TileDefinition iron;
        public TileDefinition copper;

        [Tooltip("进入 Play 是否自动跑")]
        public bool autoRun = true;

        [Tooltip("结果文件")]
        public string resultFile = @"C:/Users/58058/.workbuddy/tools/d11_runlifecycle_fix.txt";

        readonly List<string> logs = new List<string>();
        int passCount, failCount;

        void Assert(bool ok, string name, string detail)
        {
            string line = (ok ? "PASS " : "FAIL ") + name + " :: " + detail;
            logs.Add(line);
            if (ok) passCount++; else failCount++;
            if (!ok) Debug.LogWarning("[DEV-011 fix] " + line);
        }

        void Start()
        {
            if (!autoRun) return;
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("DEV-011 fix probe: Run lifecycle Sell→结束→离地 BeginRun→Run#2 (真实帧)");

            // 等 rig 就绪
            GameManager gm = null; RunRiskState risk = null; DrillVehicle v = null; DepthRegionProgression prog = null;
            float wait = 0f;
            while (wait < 4f)
            {
                gm = GameManager.Instance;
                if (gm != null) { risk = gm.RunRisk; v = gm.Player; prog = gm.GetComponent<DepthRegionProgression>(); }
                if (gm != null && risk != null && v != null && prog != null) break;
                wait += 0.2f;
                yield return new WaitForSeconds(0.2f);
            }
            if (gm == null || risk == null || v == null || prog == null)
            {
                sb.AppendLine("EXCEPTION: rig 不完整 gm=" + gm + " risk=" + risk + " v=" + v + " prog=" + prog);
                Flush(sb);
                yield break;
            }

            // 关闭会写 IsAtSurface 的 trigger，让本探针独占驱动
            foreach (var z in FindObjectsByType<SurfaceHubZone>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (z.GetComponent<Collider2D>() != null) z.GetComponent<Collider2D>().enabled = false;

            // 干净起点：地表、空舱、满状态
            v.Inventory.Clear();
            v.FullRestore();
            float surfaceY = v.transform.position.y;   // 记录地表行 Y，返航时移回
            gm.IsAtSurface = true;
            yield return new WaitForEndOfFrame();   // 让 LateUpdate 归位一次

            int everBefore = prog.MaxDepthEver;      // 历史最深（不应 reset）

            // ---- Run#1 下矿 ----
            gm.IsAtSurface = false;
            yield return new WaitForEndOfFrame();    // RunRiskState.LateUpdate 触发 BeginRun
            Assert(risk.RunActive, "R1_Leave_BeginRun", $"RunActive={risk.RunActive} RunNumber={risk.RunNumber} (期望 1)");
            Assert(risk.RunNumber == 1, "R1_RunNumber", $"RunNumber={risk.RunNumber} (期望 1)");
            Assert(risk.RunSecured == false, "R1_NotSecuredYet", "下矿时不应 secured");

            // 下潜数格，制造 MaxDepthThisRun（走真实的 Grid 竖直向下通道，避免横向穿墙）
            int startRunNumber = risk.RunNumber;
            int runDepth = 3;
            for (int i = 0; i < runDepth; i++)
            {
                var p = v.transform.position;
                v.transform.position = new Vector3(p.x, p.y - 1f, p.z);
                yield return new WaitForEndOfFrame();
            }
            Assert(prog.MaxDepthThisRun >= runDepth, "R1_DepthRecorded",
                $"MaxDepthThisRun={prog.MaxDepthThisRun} (下潜 {runDepth} 格后应 ≥{runDepth})");

            // ---- 返航回地表（未 Sell：Run 仍在进行）----
            v.transform.position = new Vector3(v.transform.position.x, surfaceY, v.transform.position.z);
            yield return new WaitForEndOfFrame();
            gm.IsAtSurface = true;
            yield return new WaitForEndOfFrame();
            Assert(risk.RunActive && !risk.RunSecured, "R1_BackUnsettled_RiskStays",
                $"RunActive={risk.RunActive} RunSecured={risk.RunSecured}");

            // ---- Sell 成功：本 Run 结束 ----
            v.Inventory.AddItem(iron, 2);            // 2×$12=$24
            yield return new WaitForEndOfFrame();
            int earned = v.UnloadCargo();
            risk.NotifyCargoSecured(earned);
            yield return new WaitForEndOfFrame();
            Assert(earned == 24, "R1_Sell_Earned", $"earned=${earned} (期望 $24)");
            Assert(!risk.RunActive, "R1_Sell_EndsRun", $"Sell 后 RunActive 应为 false（本 Run 结束），实际={risk.RunActive}");
            Assert(risk.RunSecured, "R1_Sell_Secured", $"Sell 后 RunSecured 应为 true，实际={risk.RunSecured}");

            // 地表停留：不得自动开新 Run
            yield return new WaitForEndOfFrame();
            Assert(!risk.RunActive && risk.RunNumber == startRunNumber, "R1_StaySurface_NoNewRun",
                $"停留地表不自动开新 Run: RunActive={risk.RunActive} RunNumber={risk.RunNumber}");

            // ---- 再下矿：Run#2 BeginRun，统计 reset ----
            int depthBeforeR2 = prog.MaxDepthThisRun;   // Run#1 留下的深度
            int everBeforeR2 = prog.MaxDepthEver;
            gm.IsAtSurface = false;
            yield return new WaitForEndOfFrame();
            Assert(risk.RunActive && risk.RunNumber == startRunNumber + 1, "R2_Leave_NewRun",
                $"再下矿应开 Run#2: RunNumber {startRunNumber} → {risk.RunNumber}");
            // reset：刚离地时刻 MaxDepthThisRun 回到当前下潜起算（≤1 格语义；关键是 < Run#1 遗留值）
            Assert(prog.MaxDepthThisRun < depthBeforeR2, "R2_MaxDepthReset",
                $"MaxDepthThisRun reset {depthBeforeR2} → {prog.MaxDepthThisRun}");
            Assert(prog.MaxDepthEver == everBeforeR2, "R2_MaxDepthEverKept",
                $"MaxDepthEver 不得 reset: {everBeforeR2} → {prog.MaxDepthEver}");

            Flush(sb);
            try { File.WriteAllText(resultFile, sb.ToString()); }
            catch (Exception e) { Debug.LogWarning("[DEV-011 fix] 写文件失败 " + e.Message); }
            Debug.Log("[DEV-011 fix] done, see " + resultFile + " (FAIL=" + failCount + ")");
            yield break;
        }

        void Flush(StringBuilder sb)
        {
            foreach (var l in logs) sb.AppendLine("  " + l);
            sb.AppendLine($"  => PASS {passCount} / FAIL {failCount}");
        }
    }
}
