#if UNITY_EDITOR
// DEV-015 确定性经济模拟 v2（Editor-only）。模型贴近真实现：
//  - 每趟沿竖井下潜 depthD 格（沿途挖矿）→ 每格以带频率/权重生成 vein → 逐【块】装入（HandleTileDug 语义：
//    满则按 valuePerWeight 丢最差 1 块再放新块）→ 保留多样混合货，不出现"全被单一种密度最高矿替换"退化。
//  - 燃料：移动耗油按每格穿越 + 挖掘耗油按 digTime/digMult 计入；返航上行按载重惩罚。
// 输出各 Region × 装备配置的 cargo 价值/重量/v-w/燃料余量/首升趟数。
//
// DEV-015 Blocker2 根治：矿带权重 / vein 尺寸 / 频率【只从 OreRegionPreset 权威资产读取】，
// 不再在模拟器内部硬编码第二份世界参数；runs-to-next-upgrade 按 EquipmentCatalog.CostOf 计算（不再硬编码 $60）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ashfall.EditorTools
{
    public static class EconomyBalanceV1Sim
    {
        const string OutPath = "C:/Users/58058/.workbuddy/tools/d15_economy_sim.txt";
        const string DataFolder = "Assets/Ashfall/Data";
        const string PresetPath = DataFolder + "/OreRegionPreset.asset";
        static TileDefinition Load(string f) => AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/{f}.asset");

        sealed class Out
        {
            public string region, cfg;
            public int cargoValue; public float weight, cap, vpw, downFuel, upFuel, margin; public int runs;
        }

        [MenuItem("灰烬之下/DEV-015 经济模拟：Shallow-Mid-Deep × 装备配置")]
        public static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== DEV-015 经济模拟 v3（读取 OreRegionPreset 权威带 + 真实燃料 + CostOf）====");

            // ---- 权威带：从 OreRegionPreset 资产读取（若资产缺失则报错，不再内部硬编码）----
            var preset = AssetDatabase.LoadAssetAtPath<OreRegionPreset>(PresetPath);
            if (preset == null || preset.bands == null || preset.bands.Length == 0)
            {
                Debug.LogError("[DEV-015] 缺少权威配置 " + PresetPath + "。请先运行菜单「灰烬之下/搭建 DEV-015 经济权威资产与矿种」。");
                sb.AppendLine("\n==== 汇总: 中止（缺少 OreRegionPreset 权威资产）====");
                try { File.WriteAllText(OutPath, sb.ToString()); } catch (Exception e) { Debug.LogError(e.Message); }
                return;
            }

            // 资源表（真实 .asset）
            sb.AppendLine("---- 资源表（真实 .asset）----");
            var allKnown = new[]
            {
                "Iron_铁矿","Tin_锡矿","Copper_铜矿","Silver_银矿","Gold_金矿",
                "Emerald_绿宝石","Platinum_铂金","Ruby_红宝石","Diamond_钻石",
                "Coal_煤","Lead_铅矿","Amethyst_紫水晶","Sapphire_蓝宝石",
                "Uranium_铀矿","EnergyCrystal_能量水晶","AnomalousCrystal_异常水晶","UnknownMineral_未知矿物",
            };
            foreach (var n in allKnown)
            {
                var o = Load(n);
                if (o == null) { sb.AppendLine($"  {n}  <MISSING>"); continue; }
                sb.AppendLine($"  {o.name,-10} val={o.value,-6} w={o.weight,4} v/w={o.value / Mathf.Max(.0001f, o.weight),6:0.0} hard={o.hardness}");
            }

            sb.AppendLine("\n---- 权威带（OreRegionPreset.bands，非硬编码）----");
            foreach (var b in preset.bands)
            {
                if (b == null || b.ores == null) continue;
                var names = new StringBuilder();
                for (int i = 0; i < b.ores.Length; i++)
                    if (b.ores[i] != null)
                        names.Append(b.ores[i].name).Append(i < b.weights.Length ? $"({b.weights[i]:0})" : "").Append(" ");
                sb.AppendLine($"  {b.bandName} y{b.minDepth}..{b.maxDepth}: {names} | vein {b.veinMinSize}~{b.veinMaxSize} freq {b.veinFrequency:0.00}");
            }

            var cfg = new[]{
                new object[]{"Lv0",0,0,0,false,false},
                new object[]{"Lv1+电机",1,1,1,true,false},
                new object[]{"Lv2+电机货架",2,2,2,true,true},
            };

            var outs = new List<Out>();
            var rng = new System.Random(20260915);
            sb.AppendLine("\n---- Region × 配置 模拟 ----");
            foreach (var b in preset.bands)
            {
                if (b == null) continue;
                string rn = b.bandName;
                int d = rn == "Shallow" ? 18 : (rn == "Mid" ? 40 : 58);
                foreach (var c in cfg)
                {
                    var o = Sim(b, d, (string)c[0], (int)c[1], (int)c[2], (int)c[3], (bool)c[4], (bool)c[5], rng);
                    outs.Add(o);
                    sb.AppendLine($"  [{b.bandName}|{o.cfg}] cargo ${o.cargoValue} (w {o.weight,4:0}/{o.cap,3:0}) v/w {o.vpw,5:0.0} fuel用{Math.Max(o.downFuel, o.upFuel),4:0}/max{(int)EquipmentCatalog.MaxFuel((int)c[2])} 返航余量{o.margin,6:0.0} 首升≈{o.runs}趟");
                }
            }
            try { File.WriteAllText(OutPath, sb.ToString()); } catch (Exception e) { Debug.LogError("[DEV-015] " + e.Message); }
            Debug.Log("[DEV-015] 模拟完成 → " + OutPath);
        }

        static Out Sim(OreDepthBand b, int depthD, string label, int dl, int fl, int cl, bool motor, bool rack, System.Random rng)
        {
            var o = new Out { region = b.bandName, cfg = label };
            float cap = EquipmentCatalog.MaxCarryWeight(cl) + (rack ? EquipmentCatalog.CargoRackCarryBonus : 0);
            o.cap = cap;
            float moveMult = motor ? EquipmentCatalog.EfficientMotorFuelMult : 1f;
            float freq = Mathf.Max(0.02f, b.veinFrequency);
            int vMin = Mathf.Max(1, b.veinMinSize), vMax = Mathf.Max(vMin, b.veinMaxSize);

            var list = new List<(float vpw, float w, int v)>();
            float weight = 0; int value = 0;
            int guard = 0;
            while (weight < cap - 0.001f && guard++ < depthD * 12)
            {
                if ((float)rng.NextDouble() > freq) continue;
                TileDefinition def = PickWeighted(b, rng);
                if (def == null) continue;
                float vpw = def.value / Mathf.Max(.0001f, def.weight);
                int vein = rng.Next(vMin, vMax + 1);
                for (int k = 0; k < vein && weight < cap - 0.001f; k++)
                {
                    if (weight + def.weight <= cap + 0.0001f)
                    {
                        weight += def.weight; value += def.value; list.Add((vpw, def.weight, def.value));
                    }
                    else
                    {
                        int worst = -1; float wmin = float.MaxValue;
                        for (int i = 0; i < list.Count; i++)
                            if (list[i].vpw < vpw && list[i].vpw < wmin) { wmin = list[i].vpw; worst = i; }
                        if (worst < 0) break;
                        weight -= list[worst].w; value -= list[worst].v; list.RemoveAt(worst);
                        if (weight + def.weight <= cap + 0.0001f)
                        {
                            weight += def.weight; value += def.value; list.Add((vpw, def.weight, def.value));
                        }
                    }
                }
            }
            o.cargoValue = value; o.weight = weight; o.vpw = weight > 0.001f ? value / weight : 0;

            float digPerCell = 0.35f / EquipmentCatalog.DigSpeedMultiplier(dl);
            float movePerCell = 1f / 4.0f;
            float downPerCell = movePerCell * 1.1f * moveMult + digPerCell * 2.0f;
            o.downFuel = depthD * downPerCell;
            float load = Mathf.Clamp01(weight / cap);
            o.upFuel = depthD * 1.1f * moveMult * 2.2f * (1f + load * 0.35f);
            float maxF = EquipmentCatalog.MaxFuel(fl);
            o.margin = maxF - o.downFuel - o.upFuel;
            // DEV-015：runs 改为「攒够首升钻头(Drill Lv1 CostOf)需几趟」，不再硬编码 $60
            int drillLv1Cost = EquipmentCatalog.CostOf(EquipmentLine.Drill, 1);
            o.runs = Mathf.Max(0, (int)Mathf.Ceil(drillLv1Cost / Mathf.Max(1, value)));
            return o;
        }

        static TileDefinition PickWeighted(OreDepthBand b, System.Random rng)
        {
            if (b == null || b.ores == null || b.ores.Length == 0) return null;
            float total = 0f;
            for (int i = 0; i < b.ores.Length; i++)
            {
                if (b.ores[i] == null) continue;
                float w = (b.weights != null && i < b.weights.Length) ? Mathf.Max(0f, b.weights[i]) : 1f;
                total += w;
            }
            if (total <= 0f) return b.ores[0];
            double roll = rng.NextDouble() * total;
            for (int i = 0; i < b.ores.Length; i++)
            {
                if (b.ores[i] == null) continue;
                float w = (b.weights != null && i < b.weights.Length) ? Mathf.Max(0f, b.weights[i]) : 1f;
                roll -= w;
                if (roll <= 0d) return b.ores[i];
            }
            return b.ores[b.ores.Length - 1];
        }
    }
}
#endif
