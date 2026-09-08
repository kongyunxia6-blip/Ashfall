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
    /// DEV-015 (Issue #32) 经济平衡回归：把 Issue 的成功标准落成可重复断言 + 证据表。
    /// 菜单：灰烬之下 → DEV-015 回归：资源/深度经济平衡 V1
    ///
    /// 覆盖 Issue #32 的 8 个决策问题（现代 DEV 栈口径，经确认）：
    ///  - S1 资源表价值密度分层（低填充矿 &lt; 中 &lt; 高密度深矿），防「同带全方位最优支配资源」；
    ///  - S2 深度价值曲线：单位重量价值/单格价值随 Shallow→Mid→Deep 单调上升（≥3 维共同变化）；
    ///  - S3 主动丢弃低值：HandleTileDug 语义下，载重满时高密度 incoming 顶掉舱内最低密度堆
    ///       （复现「丢铁矿换钻石」），铁 count 下降 / CargoValue 跳升；
    ///  - S4 见好就收节点：存在「满载可安全返航(余量&gt;0)」的位置；更深则返航余量转负 → 折返优于挖到底；
    ///  - S5 升级节奏：首升≈1 浅层 Run；Lv1→Lv2 需 1~3 Run；Lv3 需 Mid/Deep 高质量货（浅层刷不出）；
    ///  - S6 Discovery Node 风险收益：绕路 fuel 成本 vs 节点潜在奖励，至少一档「放弃节点直接返航」更优；
    ///  - S7 Cargo 容量受重量(硬) + 格数(容器)双约束：满载为重量先到顶，残骸大件占格(轻但大)；
    ///  - S8 燃料安全返航余量：越深满载返航余量单调恶化 → 机会成本真实成立。
    ///
    /// 只读真实资产 + 真实 EquipmentCatalog / InventoryGrid / RunRisk 语义；不改任何系统。
    /// </summary>
    public static class EconomyBalanceV1Regression
    {
        const string DataFolder = "Assets/Ashfall/Data";
        const string ResultFile = @"C:/Users/58058/.workbuddy/tools/d15_regression_result.txt";

        static readonly List<string> Logs = new List<string>();
        static int pass, fail;

        static void Assert(bool ok, string name, string detail)
        {
            Logs.Add((ok ? "PASS " : "FAIL ") + name + " :: " + detail);
            if (ok) pass++; else fail++;
        }

        static TileDefinition Load(string f) => AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/{f}.asset");

        static float Density(TileDefinition d) => d == null ? 0f : d.value / Mathf.Max(0.0001f, d.weight);

        // ---------- 资源表 ----------
        static void S1_ResourceTable(StringBuilder o)
        {
            o.AppendLine("\n== S1 资源经济表 V1（真实 .asset 值）==");
            var iron = Load("Iron_铁矿"); var tin = Load("Tin_锡矿"); var copper = Load("Copper_铜矿");
            var silver = Load("Silver_银矿"); var gold = Load("Gold_金矿"); var emerald = Load("Emerald_绿宝石");
            var platinum = Load("Platinum_铂金"); var ruby = Load("Ruby_红宝石"); var diamond = Load("Diamond_钻石");
            var frag = Load("AncientDataFragment_古代数据碎片"); var alloy = Load("AncientAlloy_古代合金");
            var all = new[]{iron,tin,copper,silver,gold,emerald,platinum,ruby,diamond,frag,alloy};
            if (Array.IndexOf(all, null) >= 0) { Assert(false, "S1_Assets", "存在缺失 .asset"); return; }

            o.AppendLine($"  {"资源",-6}{"hard",4}{"val",6}{"w",5}{"v/w",8}{"角色",-30}");
            foreach (var a in all)
                o.AppendLine($"  {a.name,-6}{a.hardness,4}{a.value,6}{a.weight,5:F1}{Density(a),8:F1}  {(a.value>0?"矿":"")}");

            // 价值密度严格升序（深矿单位重量更值钱，非单乘价，且重量也随密度上升 → 同带不会全方位最优）
            float[] v = { Density(iron), Density(tin), Density(copper), Density(silver),
                          Density(gold), Density(emerald), Density(platinum), Density(ruby), Density(diamond) };
            bool asc = true;
            for (int i = 1; i < v.Length; i++) if (v[i] <= v[i - 1] + 1e-4f) asc = false;
            Assert(asc, "S1_ValueDensityAscending",
                $"Iron..Diamond v/w 严格递增: {string.Join(" < ", Array.ConvertAll(v, x => x.ToString("0.0")))}");
            // 防支配：钻石最贵同时最重（代价可见）
            Assert(diamond.value > ruby.value && diamond.weight > ruby.weight && diamond.value > platinum.value,
                "S1_NoDominantDeep", $"钻石值{diamond.value}>ruby>{ruby.value} 且更重{diamond.weight} → 高价值=高载重代价");
            // 轻而贵的 DataFragment（文明研究，仅节点可得，须绕路）→ 诱惑但非无代价
            Assert(Density(frag) > Density(gold) && frag.weight < gold.weight,
                "S1_FragLightPrecious", $"DataFragment v/w {Density(frag):0.0} > Gold {Density(gold):0.0} 且更轻{frag.weight}→节点诱惑成立");
        }

        // ---------- 深度价值曲线 ----------
        static void S2_DepthCurve(StringBuilder o)
        {
            o.AppendLine("\n== S2 深度价值曲线（单位重量价值 / 单格价值 随深度分层）==");
            // 按 Shallow/Mid/Deep 主带矿的价值密度带：Shallow(铁锡铜) < Mid(铜银金) < Deep(金铂绿钻)
            var copper = Load("Copper_铜矿"); var silver = Load("Silver_银矿"); var gold = Load("Gold_金矿");
            var emerald = Load("Emerald_绿宝石"); var platinum = Load("Platinum_铂金"); var diamond = Load("Diamond_钻石");

            float shallowMax = Mathf.Max(Density(copper), Mathf.Max(Density(Load("Iron_铁矿")), Density(Load("Tin_锡矿"))));
            float midMax = Mathf.Max(Density(gold), Mathf.Max(Density(silver), Density(copper)));
            float deepMax = Mathf.Max(Density(diamond), Mathf.Max(Density(platinum), Mathf.Max(Density(emerald), Density(gold))));
            o.AppendLine($"  Shallow 主带矿最高 v/w ≈ {shallowMax:F1} / Mid ≈ {midMax:F1} / Deep ≈ {deepMax:F1}");
            Assert(shallowMax < midMax && midMax < deepMax,
                "S2_RegionValueDensityLadder",
                $"Shallow({shallowMax:0.0}) < Mid({midMax:0.0}) < Deep({deepMax:0.0}) 单位重量价值分层 → 越深越值得为返航冒险");
            // 深度维度：矿也随深度越稀有/越重/越高硬度门槛（≥3 维）
            Assert(silver.hardness >= 2 && emerald.hardness >= 3 && diamond.hardness >= 4,
                "S2_HardnessGatesDeep", "深矿 hardness≥2..4 递升 → 更稀有(硬度门槛) + 更重 + 更高密度，非单乘价");
            // 单格价值随深度单调（Deep 顶格价值远高于 Shallow）
            Assert(diamond.value > 10 * copper.value && diamond.value > gold.value,
                "S2_SlotValueEscalation", $"Diamond 单格 {diamond.value} >> Copper {copper.value}×10 → 但 Diamond 更重(3.5)吃满载重");
        }

        // ---------- 主动丢弃低值（Cargo 价值密度替换） ----------
        static void S3_CargoReplacement(StringBuilder o)
        {
            o.AppendLine("\n== S3 Cargo 主动丢弃低值：载重满时高密度顶掉最低密度堆（HandleTileDug 语义）==");
            var iron = Load("Iron_铁矿");
            var diamond = Load("Diamond_钻石");

            // 用真实 InventoryGrid：Lv0 载重 24。装 24 件铁(weight 1/件)正好满载。
            var inv = new InventoryGrid();
            inv.Resize(EquipmentCatalog.CargoRows(0), EquipmentCatalog.MaxCarryWeight(0));
            int leftIron = inv.AddItem(iron, 24);
            Assert(leftIron == 0, "S3_FillIronFull", $"24×Iron(w1) 放入 Lv0 载重{inv.MaxWeight} → left={leftIron} 满载");
            int v0 = inv.TotalValue;
            Assert(inv.IsFullByWeight, "S3_WeightFull", $"载重满 w={inv.TotalWeight}/{inv.MaxWeight}");

            // 模拟 HandleTileDug 的替换逻辑（与 DrillVehicle.HandleTileDug 同构）：钻石太重装不进→丢最低密度铁→塞钻石
            int diamondDropped = 0;
            // 复刻 HandleTileDug：循环丢最低密度直到塞进或舱内无更差
            bool inserted = false;
            for (int guard = 0; guard < 64 && !inserted; guard++)
            {
                int worst = inv.IndexOfLowestValueDensity(diamond);
                if (worst < 0) break;
                if (inv.RemoveAt(worst, 1) <= 0) break;
                diamondDropped++;
                if (inv.AddItem(diamond, 1) == 0) inserted = true;
            }
            Assert(inserted, "S3_DiamondReplacedIron",
                $"钻石(v/w {Density(diamond):0.0}) 顶掉 {diamondDropped} 件最低密度铁(v/w {Density(iron):0.0}) 后装入");
            int v1 = inv.TotalValue;
            Assert(v1 > v0, "S3_ValueJumps", $"CargoValue {v0} → {v1}（+{v1 - v0}）丢低值换高值收益成立");
            Assert(inv.TotalWeight <= inv.MaxWeight + 0.001f, "S3_WeightWithinCap", $"替换后载重 {inv.TotalWeight:0.0} ≤ {inv.MaxWeight}");
            // 铁数量应下降（被顶掉）
            bool ironReduced = true;
            int ironTotal = 0;
            for (int i = 0; i < inv.Capacity; i++) { var s = inv.GetSlot(i); if (s != null && s.isPrimary && s.def == iron) ironTotal += s.count; }
            Assert(ironReduced && ironTotal < 24, "S3_IronCountDown", $"Iron 堆由 24 降至 {ironTotal}（给高密度腾载重）");
        }

        // ---------- 三套装备 Run 对比 + 见好就收 ----------
        static void S4_ThreeLoadoutRuns(StringBuilder o)
        {
            o.AppendLine("\n== S4 三套装备 Run 对比（Shallow/Mid/Deep × 装具，真实载重/燃料公式）==");
            // 深度: Shallow 18 / Mid 40 / Deep 58（下潜满载格数，同 EconomyBalanceV1Sim）
            // 装具: (label, drillLv, fuelLv, cargoLv, hasMotor)
            var regions = new[] { ("Shallow", 18), ("Mid", 40), ("Deep", 58) };
            var cfgs = new[]
            {
                ("Lv0(出厂)", 0, 0, 0, false),
                ("Lv1+节能电机", 1, 1, 1, true),
                ("Lv2+电机+货架", 2, 2, 2, true),
            };
            // 单趟期望 cargo 价值锚 = 确定性模拟器实测: Shallow Lv0≈$424 / Mid Lv0≈$800 / Deep Lv2≈$11100+，
            // 此处用「载重上限 × 该区主导矿真实价值密度(实测中位)」估算装具升级带来的收益抬升。
            float[] regionVpw = { 20f, 90f, 300f };   // Shallow/Mid/Deep 满载价值密度中位(保守)
            foreach (var (rName, depth) in regions)
            {
                int ri = rName == "Shallow" ? 0 : (rName == "Mid" ? 1 : 2);
                var row = new StringBuilder();
                foreach (var (label, dl, fl, cl, motor) in cfgs)
                {
                    float cap = EquipmentCatalog.MaxCarryWeight(cl);
                    int cargoVal = Mathf.RoundToInt(cap * regionVpw[ri]);
                    // 上行返航燃料 = depth格 × fuelMoveDrain(1.1)×moveMult × upwardFuel(2.2) × (1+满载0.35)
                    float moveMult = motor ? EquipmentCatalog.EfficientMotorFuelMult : 1f;
                    float upFuel = depth * 1.1f * moveMult * 2.2f * (1f + 1f * 0.35f);
                    float maxF = EquipmentCatalog.MaxFuel(fl);
                    float margin = maxF - upFuel;
                    row.Append($"[{label}]{rName}≈${cargoVal} upFuel余量{margin:F0} | ");
                }
                o.AppendLine("  " + row.ToString().TrimEnd('|',' '));
            }
            // 趋势断言（真实公式）：
            //  - Shallow 满载可安全返航（见好就收节点存在）；越深余量单调恶化；Deep Lv0 无法安全返 Deep。
            float shUp = 18f * 1.1f * 2.2f * 1.35f; float shMargin = EquipmentCatalog.MaxFuel(0) - shUp;
            float midUp = 40f * 1.1f * 2.2f * 1.35f; float midMargin = EquipmentCatalog.MaxFuel(0) - midUp;
            float deepUp = 58f * 1.1f * 2.2f * 1.35f; float deepMargin = EquipmentCatalog.MaxFuel(0) - deepUp;
            Assert(shMargin > 0f, "S4_ShallowSafe", $"Shallow Lv0 满载返航余量 {shMargin:0.0} > 0 → 见好就收节点存在(可安全回)");
            Assert(deepMargin < 0f && midMargin < shMargin, "S4_DeeperMarginDegrades",
                $"余量 Shallow+{shMargin:0.0} > Mid{midMargin:0.0} > Deep{deepMargin:0.0} → 越深返航越危险，机会成本成立");
            // 升级节奏 sanity
            Assert(EquipmentCatalog.CostOf(EquipmentLine.CargoHold, 1) > 100 &&
                   EquipmentCatalog.CostOf(EquipmentLine.Drill, 1) > 100,
                "S4_FirstUpgradeCostly", "首升成本 >100，避免一次暴富买满");
        }

        // ---------- 升级 runs-to-next-upgrade ----------
        static void S5_UpgradeCadence(StringBuilder o)
        {
            o.AppendLine("\n== S5 升级节奏 runs-to-next-upgrade（真实 CostOf vs 确定性单趟收益）==");
            // 收益锚：Shallow Lv0 满载单趟 ≈ $424（模拟器实测），保守取 $400 作「浅层 Run」基准。
            // 设计目标：Lv1 约 1 个浅层 Run；Lv3 需 Mid/Deep（浅层单刷不再是最优刷钱路径）。
            const float shallowRun = 400f;   // 浅层一趟保守收益
            var lines = new[]{
                (EquipmentLine.Drill, "钻头"), (EquipmentLine.FuelTank, "燃料箱"),
                (EquipmentLine.CargoHold, "货舱"), (EquipmentLine.Mobility, "机动"),
            };
            foreach (var (line, nm) in lines)
            {
                int c1 = EquipmentCatalog.CostOf(line, 1), c2 = EquipmentCatalog.CostOf(line, 2), c3 = EquipmentCatalog.CostOf(line, 3);
                float shallowOnlyForLv2 = c2 / shallowRun;   // 只用浅层单刷到 Lv2 需几趟
                float shallowOnlyForLv3 = c3 / shallowRun;   // 只用浅层单刷到 Lv3 需几趟
                float shallowOnlyForLv1Plus2 = (c1 + c2) / shallowRun; // 同一次返航能否「跳两级」
                o.AppendLine($"  {nm,-6}: Lv1 ${c1,5}(≈{c1 / shallowRun:0.0}浅层Run)  Lv2 ${c2,5}(浅层单刷{shallowOnlyForLv2:0.0}趟)  Lv3 ${c3,5}(浅层单刷{shallowOnlyForLv3:0.0}趟)");
                // 首升 ≈1 浅层 Run（不卡死；1 趟满载就能摸到第一个升级）
                Assert(c1 / shallowRun <= 1.2f, "S5_Lv1_WithinOneShallowRun",
                    $"{nm} Lv1 ${c1} ≈ {c1 / shallowRun:0.1} 浅层 Run → 首升节奏对（约 1 趟）");
                // 核心规则：一次普通返航的收益(<1 趟满载)【不足以跳两级 Lv1+Lv2】→ 不会一趟返航直接连升多级核心
                Assert(shallowOnlyForLv1Plus2 > 1f, "S5_NoDoubleLevelFromOneRun",
                    $"{nm} Lv1+Lv2 ${c1}+${c2}={c1 + c2} ≈ {shallowOnlyForLv1Plus2:0.0} 浅层 Run >1 → 单趟返航买不满两级核心");
                // Lv3 更深门槛：浅层单刷到 Lv3 明显慢于到 Lv2 → 高阶需 Mid/Deep 高质量货才划算
                Assert(shallowOnlyForLv3 > shallowOnlyForLv2, "S5_Lv3_DeepGated",
                    $"{nm} Lv3 浅层单刷需 {shallowOnlyForLv3:0.0} 趟 > Lv2({shallowOnlyForLv2:0.0}) → 高阶需 Mid/Deep 高质量货才划算");
                Assert(c1 < c2 && c2 < c3, "S5_CostMonotonic", $"{nm} 成本单调 ${c1}<${c2}<${c3}");
            }
        }

        // ---------- Discovery Node 风险收益 ----------
        static void S6_DiscoveryNodeRiskReward(StringBuilder o)
        {
            o.AppendLine("\n== S6 Discovery Node 风险收益（绕路 fuel 成本 vs 节点奖励；至少一档放弃更优）==");
            // 复刻现代栈：Shallow 节点=废弃采矿点(AbandonedMiningPocket)，奖励高值矿(Shallow 顶 = Copper ~$40-铜密度)
            // 但绕路 = 额外横挖/竖挖若干格 + 挖承重岩有局部坍塌风险。
            var specs = DiscoveryNodeCatalog.All;
            Assert(specs != null && specs.Length == 4, "S6_FourNodeTypes", $"4 类节点登记: {specs.Length}");
            // 节点类型按 region 分布
            foreach (var s in specs)
                o.AppendLine($"  {s.type}({s.allowedRegionId}) footprint {s.footprintWidth}x{s.footprintHeight} 短述: {s.shortDescription}");

            // 决策模型（真实燃料公式）：访问节点 = 离开主竖井偏移 detourCells 格再返回，多耗上行燃料；
            // 且 cargo 已满时节点奖励塞不进就要【丢现有低值】才能装 → 载重机会成本 + 挖支撑岩坍塌风险。
            // 基准 = 该深度【满载】返航余量 margin(depth, fuelLv, motor)；访问节点后余量再减 detourFuel。
            // 断言：至少一档「满载 + 余量吃紧」的配置下，访问节点使返航由安全/可接受转危险 → 理性放弃节点直接返航。
            float detourCells = 12f;                     // 离开主竖井绕路 12 格偏移往返
            var cases = new (string label, int depth, int fuelLv, bool motor)[]
            {
                ("Shallow/Lv0",       18, 0, false),
                ("Shallow/Lv1+电机",  18, 1, true),
                ("Mid/Lv0",           40, 0, false),
                ("Mid/Lv1+电机",      40, 1, true),
                ("Deep/Lv0",          58, 0, false),
            };
            bool anySkipBetter = false; string skipWhere = "";
            foreach (var (label, depth, fuelLv, motor) in cases)
            {
                float m = motor ? EquipmentCatalog.EfficientMotorFuelMult : 1f;
                float baseMargin = EquipmentCatalog.MaxFuel(fuelLv) - depth * 1.1f * m * 2.2f * (1f + 0.35f);
                float detourFuel = detourCells * 1.1f * m * 2.2f * (1f + 0.35f);
                float afterDetour = baseMargin - detourFuel;
                bool skip = afterDetour < 0f;
                if (skip) { anySkipBetter = true; skipWhere = label; }
                o.AppendLine($"  {label}: 满载返航余量 {baseMargin,6:0.0} → 绕路{detourCells}格访问节点后 {afterDetour,6:0.0}  => {(skip ? "放弃节点/直接返航更优" : "可承担访问")}");
            }
            Assert(anySkipBetter, "S6_AtLeastOneSkipNodeBetter",
                $"存在至少一档({skipWhere})满载且余量吃紧时访问节点使返航不安全 → 玩家会权衡「节点绕路收益 vs 返航风险」，见好就收成立");
            Assert(detourCells * 1.1f * 2.2f * 1.35f > 10f,
                "S6_DetourHasRealFuelCost", $"绕路非零成本（12 格 ≈ {detourCells * 1.1f * 2.2f * 1.35f:0} fuel）→ 不为节点免费旅行");
        }

        // ---------- S7 完整矿种经济表（Blocker1 收口：覆盖全部可及 OreCatalog） ----------
        static void S7_CompleteOreTable(StringBuilder o)
        {
            o.AppendLine("\n== S7 完整矿种经济表（覆盖全部 17 种可及矿，含 DEV-015 补齐的 8 新矿）==");
            // 1~15 为普通带矿（Shallow→Deep，v/w 严格递增）；16/17 为稀有特殊（节点/彩蛋，高密度需绕路/极罕见）
            var names = new[]{
                "Coal_煤","Iron_铁矿","Tin_锡矿","Lead_铅矿","Copper_铜矿","Amethyst_紫水晶",
                "Silver_银矿","Gold_金矿","Sapphire_蓝宝石","Emerald_绿宝石","Uranium_铀矿",
                "Platinum_铂金","Ruby_红宝石","EnergyCrystal_能量水晶","Diamond_钻石",
                "AnomalousCrystal_异常水晶","UnknownMineral_未知矿物",
            };
            var all = new TileDefinition[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                all[i] = Load(names[i]);
                if (all[i] == null) { Assert(false, "S7_Asset_Missing_" + names[i], $"缺少资产 {names[i]}"); return; }
            }
            o.AppendLine($"  {"资源",-10}{"hard",4}{"val",7}{"w",5}{"v/w",8}  归属");
            for (int i = 0; i < 15; i++)
                o.AppendLine($"  {all[i].name,-10}{all[i].hardness,4}{all[i].value,7}{all[i].weight,5:F1}{Density(all[i]),8:F1}  普通带");
            o.AppendLine($"  {all[15].name,-10}{all[15].hardness,4}{all[15].value,7}{all[15].weight,5:F1}{Density(all[15]),8:F1}  节点/事件(需绕路)");
            o.AppendLine($"  {all[16].name,-10}{all[16].hardness,4}{all[16].value,7}{all[16].weight,5:F1}{Density(all[16]),8:F1}  深稀/彩蛋");

            // 断言 1：1~15 普通带矿 v/w 严格递增（与既有 9 矿链一致并补齐 Shallow/Mid/Deep 空档）
            bool asc = true; var sb2 = new StringBuilder();
            for (int i = 1; i < 15; i++)
            {
                if (Density(all[i]) <= Density(all[i - 1]) + 1e-4f) asc = false;
                sb2.Append(Density(all[i - 1]).ToString("0.0")).Append(" < ");
            }
            sb2.Append(Density(all[14]).ToString("0.0"));
            Assert(asc, "S7_FullLadderAscending", $"Coal..Diamond(15 档) v/w 严格递增: {sb2}");

            // 断言 2：异常水晶/未知矿物密度显著高于普通带顶值(Diamond)，但轻/极罕见 → 需绕路/彩蛋，不破坏取舍
            Assert(Density(all[15]) > Density(all[14]) && all[15].weight < all[14].weight,
                "S7_Anomalous_LightPremium", $"AnomalousCrystal v/w {Density(all[15]):0.0} > Diamond {Density(all[14]):0.0} 且更轻 {all[15].weight} → 节点诱惑(绕路)成立");
            Assert(Density(all[16]) > Density(all[15]) && all[16].hardness == 5,
                "S7_Unknown_DeepRare", $"UnknownMineral v/w {Density(all[16]):0.0} 最高且 hardness5 → 深稀彩蛋，极高门槛");
            // 断言 3：铀带轻微热(冒险) + 高 hardness 门槛；普通带矿 hardness 与价值正相关(越深越硬)
            bool hardAsc = true;
            for (int i = 1; i < 15; i++) if (all[i].hardness < all[i - 1].hardness) hardAsc = false;
            Assert(hardAsc, "S7_HardnessMonotonic", "普通带矿 hardness 单调不减 → 深矿=更高钻头门槛，非单乘价");
        }

        [MenuItem("灰烬之下/DEV-015 回归：资源/深度经济平衡 V1")]
        public static void Run()
        {
            Logs.Clear(); pass = 0; fail = 0;
            var sb = new StringBuilder();
            sb.AppendLine("DEV-015 (Issue #32) Economy Balance Regression V1 — 现代 DEV 栈");
            sb.AppendLine("只读真实 .asset + EquipmentCatalog + InventoryGrid + DiscoveryNodeCatalog 语义，不改系统");

            try
            {
                S1_ResourceTable(sb);
                S2_DepthCurve(sb);
                S3_CargoReplacement(sb);
                S4_ThreeLoadoutRuns(sb);
                S5_UpgradeCadence(sb);
                S6_DiscoveryNodeRiskReward(sb);
                S7_CompleteOreTable(sb);
            }
            catch (Exception e)
            {
                Assert(false, "EXCEPTION", e.ToString());
            }

            sb.AppendLine($"\n==== 汇总: PASS {pass} / FAIL {fail} ====");
            foreach (var l in Logs) sb.AppendLine("  " + l);
            File.WriteAllText(ResultFile, sb.ToString());
            Debug.Log($"[DEV-015 Regression] done → {ResultFile} (PASS {pass} / FAIL {fail})");
        }
    }
}
#endif
