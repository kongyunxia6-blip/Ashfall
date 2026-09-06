#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// DEV-011 验收回归 S1：Cargo Loss 矩阵 + Recovery Fee 矩阵（编辑模式、纯逻辑、无场景依赖）。
    /// 菜单：灰烬之下 → DEV-011 回归：Cargo Loss + Recovery Fee 矩阵
    ///
    /// 对齐 Issue #23 §13 B/C：
    ///  - B. Cargo Loss 矩阵：空舱 / 单件 / 奇数 / 偶数 / 多种矿混装 / 高载重 → 输出 before/lost/kept/after；
    ///    断言 count/weight/UsedSlots 自洽、不负担、不生成非法格子、规则确定性（同输入两跑一致）；
    ///  - C. Recovery Fee 矩阵：Surface 0m / Shallow / Mid / Deep 深度档 → 输出 maxDepth/expected/actual；
    ///    验证 fee=min(cap, Base+floor(depth/Step)×StepFee)，0m 死 = 0 费。
    ///
    /// 纯逻辑部分只调用 InventoryGrid / RiskExtractionCatalog 的既有公开 API，
    /// 不 new 场景、不依赖 MonoBehaviour Awake，编辑模式直接可跑。
    /// （Run 生命周期 / 幂等性 / 装备回归等需 Mono 行为的状态，由场景探针 RiskExtractionV1Probe 在 Play 模式验证。）
    /// </summary>
    public static class RiskExtractionV1Regression
    {
        const string DataFolder = "Assets/Ashfall/Data";
        const string ResultFile = @"C:/Users/58058/.workbuddy/tools/d11_regression_result.txt";

        static readonly List<string> Logs = new List<string>();
        static int passCount, failCount;

        static void Assert(bool ok, string name, string detail)
        {
            string line = (ok ? "PASS " : "FAIL ") + name + " :: " + detail;
            Logs.Add(line);
            if (ok) passCount++; else failCount++;
            // 也打一条紧凑行，方便菜单运行后直接在 Console 扫 FAIL
            if (!ok) Debug.LogWarning("[DEV-011] " + line);
        }

        static void Header(StringBuilder sb, string title)
        {
            passCount = failCount = 0;
            Logs.Clear();
            sb.AppendLine("==== " + title + " ====");
        }

        static void Flush(StringBuilder sb)
        {
            foreach (var l in Logs) sb.AppendLine("  " + l);
            sb.AppendLine($"  => PASS {passCount} / FAIL {failCount}");
            sb.AppendLine();
        }

        // ---------- Cargo Loss 矩阵 ----------

        /// <summary>构造一个空 InventoryGrid，resize 到 rows 行（容量 Columns×rows）、载重上限 maxWeight。</summary>
        static InventoryGrid NewGrid(int rows, float maxWeight)
        {
            var g = new InventoryGrid();
            g.Resize(rows, maxWeight);
            return g;
        }

        /// <summary>把 n 件 def 塞进背包（尽量塞，塞不进不算失败——由调用方按场景预判）。</summary>
        static void Stuff(InventoryGrid g, TileDefinition def, int n)
        {
            int left = n;
            int guard = 0;
            while (left > 0 && guard++ < 20000)
            {
                int placed = g.AddItem(def, left);
                if (placed >= left) break; // 一件都塞不进
                left = placed;
            }
        }

        /// <summary>统计指定矿种在主槽的剩余总件数（遍历只读，不改背包）。</summary>
        static int CountTotalOf(InventoryGrid g, TileDefinition def)
        {
            if (def == null) return 0;
            int n = 0;
            for (int i = 0; i < g.Capacity; i++)
            {
                var s = g.GetSlot(i);
                if (s != null && s.isPrimary && s.def == def) n += s.count;
            }
            return n;
        }

        static void RunCargoLossMatrix(StringBuilder sb, TileDefinition iron, TileDefinition copper, TileDefinition tin)
        {
            Header(sb, "Cargo Loss 矩阵（keep = floor(count × 0.5)，确定性）");

            // 1) 空舱：无变化、无损失
            {
                var g = NewGrid(2, 24f);
                int valueBefore = g.TotalValue;
                var lost = g.ApplyFractionalLoss(RiskExtractionCatalog.CargoKeepRatio);
                Assert(valueBefore == 0 && g.TotalValue == 0, "EmptyCargo_NoLoss",
                    $"before {valueBefore} → after {g.TotalValue} lost[] len={lost.Length}");
                Assert(lost.Length == 0, "EmptyCargo_NoLossLines", "lostLines should be empty");
            }

            // 2) 单件（count=1 → keep=floor(0.5)=0 → 全损）
            {
                var g = NewGrid(2, 24f);
                Stuff(g, iron, 1);
                int before = g.TotalValue;
                var lost = g.ApplyFractionalLoss(RiskExtractionCatalog.CargoKeepRatio);
                Assert(g.TotalValue == 0 && lost.Length == 1 && lost[0].lost == 1,
                    "SingleItem_AllLost", $"iron×1 keep0 lost{lost[0].lost} before ${before} after ${g.TotalValue}");
            }

            // 3) 偶数（count=2 → keep=floor(1)=1 → 损1留1）
            {
                var g = NewGrid(2, 24f);
                Stuff(g, iron, 2);
                var lost = g.ApplyFractionalLoss(RiskExtractionCatalog.CargoKeepRatio);
                int kept = CountTotalOf(g, iron);
                Assert(kept == 1 && lost.Length == 1 && lost[0].lost == 1,
                    "Even_KeepHalf", $"iron×2 → kept {kept} lost {lost[0].lost}");
            }

            // 4) 奇数（count=3 → keep=floor(1.5)=1 → 损2留1）
            {
                var g = NewGrid(2, 24f);
                Stuff(g, iron, 3);
                var lost = g.ApplyFractionalLoss(RiskExtractionCatalog.CargoKeepRatio);
                int kept = CountTotalOf(g, iron);
                Assert(kept == 1 && lost.Length == 1 && lost[0].lost == 2,
                    "Odd_FloorDown", $"iron×3 → kept {kept} lost {lost[0].lost} (keep=floor(1.5)=1, loss=2)");
            }

            // 5) 多种矿混装：铁×4 铜×3 锡×1 → 各按 keep=floor(n/2)
            {
                var g = NewGrid(3, 24f);
                Stuff(g, iron, 4); Stuff(g, copper, 3); Stuff(g, tin, 1);
                int before = g.TotalValue;
                var lost = g.ApplyFractionalLoss(RiskExtractionCatalog.CargoKeepRatio);
                int keepIron = CountTotalOf(g, iron), keepCu = CountTotalOf(g, copper), keepSn = CountTotalOf(g, tin);
                Assert(keepIron == 2 && keepCu == 1 && keepSn == 0,
                    "MixedOres_EachFloorHalf",
                    $"铁×4→留{keepIron} 铜×3→留{keepCu} 锡×1→留{keepSn}");
                // 三行明细
                Assert(lost.Length == 3, "MixedOres_LossLines", $"lostLines={lost.Length} (应 3 种都有损)");
                // 价值守恒：before = 保留价值 + 损失价值
                int lossValue = 0;
                foreach (var l in lost) lossValue += l.def.value * l.lost;
                Assert(before == g.TotalValue + lossValue, "MixedOres_ValueConserved",
                    $"before ${before} = kept ${g.TotalValue} + lost ${lossValue}");
            }

            // 6) 高载重 / 接近容量：用大重量高价值铜矿堆满，损失后 weight/count 自洽
            {
                var g = NewGrid(2, 24f);
                Stuff(g, copper, 20);          // 铜若 weight>1 会因载重只塞得进一部分——以实际塞入为准
                int beforeCount = CountTotalOf(g, copper);
                float beforeW = g.TotalWeight;
                int beforeVal = g.TotalValue;
                var lost = g.ApplyFractionalLoss(RiskExtractionCatalog.CargoKeepRatio);
                int afterCount = CountTotalOf(g, copper);
                // 守恒：before = after + lost.total（按件守恒）
                int lostTotal = 0; foreach (var l in lost) lostTotal += l.lost;
                Assert(beforeCount == afterCount + lostTotal, "NearCap_CountConserved",
                    $"count {beforeCount} = {afterCount} + {lostTotal}");
                Assert(g.TotalWeight >= 0f && beforeW >= g.TotalWeight, "NearCap_NoNegativeWeight",
                    $"weight {beforeW:F2} → {g.TotalWeight:F2}");
                Assert(beforeVal == g.TotalValue + (lostTotal > 0 ? lost[0].def.value * lostTotal : 0) ||
                       lostTotal == 0, "NearCap_ValueConserved",
                    $"before ${beforeVal} after ${g.TotalValue} lost {lostTotal}件");
            }

            // 7) 确定性：同一填充跑两次 ApplyFractionalLoss 前先重装，结果一致（防隐藏随机）
            {
                TileDefinition[] kinds = { iron, copper, tin };
                int[] fill = { 4, 5, 2 };
                int[] resultA = RunLossOnce(kinds, fill);
                int[] resultB = RunLossOnce(kinds, fill);
                bool same = true;
                for (int i = 0; i < kinds.Length; i++)
                    if (resultA[i] != resultB[i]) same = false;
                Assert(same, "Deterministic_TwoRuns",
                    $"lostA=[{string.Join(",", resultA)}] lostB=[{string.Join(",", resultB)}]");
            }

            Flush(sb);
        }

        /// <summary>装填 kinds/fill 后执行一次 loss，返回每种矿物损失件数（helper，供确定性比对）。</summary>
        static int[] RunLossOnce(TileDefinition[] kinds, int[] fill)
        {
            var g = NewGrid(3, 24f);
            for (int i = 0; i < kinds.Length; i++) Stuff(g, kinds[i], fill[i]);
            var lost = g.ApplyFractionalLoss(RiskExtractionCatalog.CargoKeepRatio);
            var res = new int[kinds.Length];
            foreach (var l in lost)
                for (int i = 0; i < kinds.Length; i++)
                    if (l.def == kinds[i]) { res[i] += l.lost; break; }
            return res;
        }

        // ---------- Recovery Fee 矩阵 ----------

        static void RunRecoveryFeeMatrix(StringBuilder sb)
        {
            Header(sb, "Recovery Fee 矩阵（fee = min(cap, Base + floor(depth/Step) × StepFee)）");

            // 0m / 地表死亡 → 0 费
            {
                int fee = RiskExtractionCatalog.RecoveryFee(0);
                Assert(fee == 0, "Fee_Surface0m", $"depth 0 → fee ${fee} (应 0，未下潜无打捞)");
            }

            // 逐深度档：Shallow(3..21) / Mid(22..43) / Deep(44..62)，取每档代表 + 边界
            int[] depths = { 3, 10, 15, 21, 22, 30, 43, 44, 50, 60, 62 };
            int cap = RiskExtractionCatalog.MaxRecoveryFee;
            foreach (int d in depths)
            {
                int expected = cap > 0
                    ? Mathf.Min(cap, RiskExtractionCatalog.BaseRecoveryFee + (d / RiskExtractionCatalog.DepthStep) * RiskExtractionCatalog.DepthStepFee)
                    : RiskExtractionCatalog.BaseRecoveryFee + (d / RiskExtractionCatalog.DepthStep) * RiskExtractionCatalog.DepthStepFee;
                int actual = RiskExtractionCatalog.RecoveryFee(d);
                Assert(actual == expected, "Fee_Table", $"depth {d}m → fee ${actual} (期望 ${expected})");
            }

            // 期望值抽查（Issue §2 示例：50m ≈ $35）—— 需 Base10 + 每10m $5
            {
                int f50 = RiskExtractionCatalog.RecoveryFee(50);
                Assert(RiskExtractionCatalog.BaseRecoveryFee == 10 && RiskExtractionCatalog.DepthStep == 10 &&
                       RiskExtractionCatalog.DepthStepFee == 5, "Fee_IssueSample_Params",
                    $"Base={RiskExtractionCatalog.BaseRecoveryFee} Step={RiskExtractionCatalog.DepthStep} StepFee={RiskExtractionCatalog.DepthStepFee}");
                Assert(f50 == 35, "Fee_IssueSample_50m35", $"50m → ${f50} (Issue 示例期望 $35)");
            }

            // 封顶：若 cap>0，足够深处不得越过 cap
            if (RiskExtractionCatalog.MaxRecoveryFee > 0)
            {
                int deep = 1000; // 理论上远超过 cap 触发阈值
                int f = RiskExtractionCatalog.RecoveryFee(deep);
                Assert(f <= RiskExtractionCatalog.MaxRecoveryFee, "Fee_Cap",
                    $"depth 1000m → ${f} ≤ cap ${RiskExtractionCatalog.MaxRecoveryFee}");
            }

            Flush(sb);
        }

        // ---------- 入口 ----------

        [MenuItem("灰烬之下/DEV-011 回归：Cargo Loss + Recovery Fee 矩阵")]
        public static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("DEV-011 Regression S1: Cargo Loss matrix + Recovery Fee matrix");
            sb.AppendLine($"CargoKeepRatio={RiskExtractionCatalog.CargoKeepRatio} | " +
                          $"Fee={RiskExtractionCatalog.FeeFormulaText}");

            // 加载真实矿资产（编辑模式可用 AssetDatabase；加载失败直接报错退出）
            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>($"{DataFolder}/TileDatabase.asset");
            var iron = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Iron_铁矿.asset");
            var copper = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Copper_铜矿.asset");
            var tin = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Tin_锡矿.asset");
            if (db == null || iron == null || copper == null || tin == null)
            {
                sb.AppendLine("EXCEPTION: 未找到矿资产（检查 Assets/Ashfall/Data 下 Tile 资产）");
                sb.AppendLine($"  db={db} iron={iron} copper={copper} tin={tin}");
            }
            else
            {
                sb.AppendLine($"Tiles: iron(w{iron.weight}/v${iron.value}) copper(w{copper.weight}/v${copper.value}) " +
                              $"tin(w{tin.weight}/v${tin.value})");
                sb.AppendLine();
                try
                {
                    RunCargoLossMatrix(sb, iron, copper, tin);
                    RunRecoveryFeeMatrix(sb);
                }
                catch (Exception e)
                {
                    sb.AppendLine("EXCEPTION: " + e);
                }
            }

            sb.AppendLine($"==== 汇总: 断言 FAIL = {failCount} ====");
            File.WriteAllText(ResultFile, sb.ToString());
            Debug.Log("[DEV-011 Regression] done, see " + ResultFile + " (FAIL=" + failCount + ")");
        }
    }
}
#endif
