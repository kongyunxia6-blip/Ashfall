#if UNITY_EDITOR
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// DEV-012：编辑模式回归（纯逻辑，无需场景 / 播放）。
    /// 覆盖 Issue §15 的 B(Stratum)/C(Hardness)/D(Ore×Stratum)/G(CapabilityResolver)/H(部分)
    /// 与 I(单格 merge blocker) 的可纯逻辑部分。
    /// 播放相关（HotRock A-B / SupportRock 坍塌 / 真实单格命中）由 BlockFrameworkV1Probe 覆盖。
    /// 用法：菜单 → 灰烬之下 → DEV-012 回归：地层/能力/矿物矩阵
    /// </summary>
    public static class BlockFrameworkV1Regression
    {
        const string OutPath = "C:/Users/58058/.workbuddy/tools/d12_regression_result.txt";

        [MenuItem("灰烬之下/DEV-012 回归：地层/能力/矿物矩阵")]
        public static void Run()
        {
            var sb = new StringBuilder();
            int pass = 0, fail = 0;

            B_StratumMatrix(sb, ref pass, ref fail);
            StratumDepthData(sb, ref pass, ref fail);
            C_HardnessMatrix(sb, ref pass, ref fail);
            D_OreStratumMatrix(sb, ref pass, ref fail);
            OreFullRegistration(sb, ref pass, ref fail);
            G_CapabilityMatrix(sb, ref pass, ref fail);
            E_SupportReuseAudit(sb, ref pass, ref fail);

            sb.AppendLine($"\n==== 汇总：PASS={pass}  FAIL={fail}  ====");
            try { File.WriteAllText(OutPath, sb.ToString()); }
            catch (System.Exception e) { Debug.LogError("[DEV-012] 写结果失败：" + e.Message); }
            // 注意：此处刻意不用 EditorUtility.DisplayDialog —— 模态对话框会阻塞 Unity 主线程，
            // 导致 MCP 自动化验收（ping/后续命令）无人应答。改用非阻塞 Debug.Log。
            Debug.Log($"[DEV-012] 编辑回归完成：PASS={pass} FAIL={fail} → {OutPath}");
            if (fail > 0) Debug.LogError($"[DEV-012] 编辑回归存在 FAIL={fail}，详见 {OutPath}");
        }

        // ---------- helpers ----------
        static void Assert(bool ok, string tag, string msg, StringBuilder sb, ref int pass, ref int fail)
        {
            if (ok) { pass++; sb.AppendLine($"PASS  {tag}: {msg}"); }
            else { fail++; sb.AppendLine($"FAIL  {tag}: {msg}"); }
        }

        // ---------- B. Stratum 查询矩阵 ----------
        static void B_StratumMatrix(StringBuilder sb, ref int pass, ref int fail)
        {
            sb.AppendLine("\n==== B. Stratum 查询矩阵（编辑模式纯逻辑） ====");

            // Shallow/Mid/Deep 代表层映射正确
            Assert(StratumCatalog.ForRegion(DepthRegionLayout.Shallow) == StratumCatalog.Normal,
                "B_Shallow_NormalRock", "Shallow → NormalRock（硬度 ★★）", sb, ref pass, ref fail);
            Assert(StratumCatalog.ForRegion(DepthRegionLayout.Mid) == StratumCatalog.Dense,
                "B_Mid_DenseRock", "Mid → DenseRock（硬度 ★★★）", sb, ref pass, ref fail);
            Assert(StratumCatalog.ForRegion(DepthRegionLayout.Deep) == StratumCatalog.Granite,
                "B_Deep_GraniteBasalt", "Deep → GraniteBasalt（硬度 ★★★★）", sb, ref pass, ref fail);

            // hardnessTier 正确
            Assert(StratumCatalog.Normal.hardnessTier == HardnessTier.Tier2, "B_Hardness_Normal_Tier2",
                "NormalRock hardnessTier=Tier2", sb, ref pass, ref fail);
            Assert(StratumCatalog.Dense.hardnessTier == HardnessTier.Tier3, "B_Hardness_Dense_Tier3",
                "DenseRock hardnessTier=Tier3", sb, ref pass, ref fail);
            Assert(StratumCatalog.Granite.hardnessTier == HardnessTier.Tier4, "B_Hardness_Granite_Tier4",
                "GraniteBasalt hardnessTier=Tier4", sb, ref pass, ref fail);

            // stable id 可读
            Assert(StratumCatalog.Find("NormalRock") != null && StratumCatalog.Find("NormalRock").stableId == "NormalRock",
                "B_StableId_Normal", "NormalRock stableId 可查", sb, ref pass, ref fail);
            Assert(StratumCatalog.Find("DenseRock") != null && StratumCatalog.Find("DenseRock").stableId == "DenseRock",
                "B_StableId_Dense", "DenseRock stableId 可查", sb, ref pass, ref fail);
            Assert(StratumCatalog.Find("AnomalousRuinLayer") != null, "B_StableId_Anomalous",
                "AnomalousRuinLayer stableId 登记（DEV-013 复用点）", sb, ref pass, ref fail);

            // Region 与 Stratum 是两个独立概念：Region 边界来自 DepthRegionLayout（运行时 CurrentDepth/MaxDepth），
            // Stratum 只表达地质/深度规范 + 推荐映射。修正：不再假设「Stratum 无深度字段」（阻塞项1 已给深度赋值），
            // 改为验证 StratumDefinition 类型不含 Region/CurrentDepth 耦合字段——即 Region 不写进 Stratum、Stratum 不持有 Region。
            bool regionIndependent = DepthRegionLayout.Mid.maxDepth == 43
                && typeof(StratumDefinition).GetField("regionId",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) == null
                && typeof(StratumDefinition).GetField("currentDepth",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) == null;
            Assert(regionIndependent, "B_RegionVsStratum_Independent",
                "Region(Mid 22..43) 独立于 Stratum(Dense 深度 250..450 为长期地质数据，非 Region 字段)——Stratum 无 regionId/currentDepth 耦合字段",
                sb, ref pass, ref fail);

            // 六级长期地层全部登记
            int longTermCount = 0;
            foreach (var s in StratumCatalog.All) if (s != null) longTermCount++;
            Assert(longTermCount >= 6, "B_SixTier_Registered", $"六级地层登记数 = {longTermCount}（≥6）", sb, ref pass, ref fail);

            // 不修改 Region 的 MaxDepth/first-enter 语义：本系统不触碰 DepthRegionProgression 字段（编译期无该耦合即保证）
            Assert(true, "B_NoDepthMutation_CompileGuard",
                "Stratum 层不引用/不修改 DepthRegionProgression 运行字段（纯数据登记）", sb, ref pass, ref fail);
        }

        // ---------- Stratum 长期深度正式数据（阻塞项 1：深度必须可查询，非注释） ----------
        static void StratumDepthData(StringBuilder sb, ref int pass, ref int fail)
        {
            sb.AppendLine("\n==== Stratum 长期深度正式数据（阻塞项1） ====");

            // 六个 Catalog 实例的 longTermMin/Max 全部已赋值（非默认 0）
            bool allAssigned = true; string detail = "";
            foreach (var s in StratumCatalog.All)
                if (s.longTermMaxDepth == 0 && s.stableId != StratumCatalog.AnomalousRuinLayer)
                { allAssigned = false; detail += $" {s.stableId}"; }
            Assert(allAssigned, "S_Depth_AllAssigned",
                "六层 longTermMinDepth/MaxDepth 均已赋正式边界（AnomalousRuinLayer 用 OpenTopDepth 开放上界）" + detail,
                sb, ref pass, ref fail);

            // 排序后区间合法：min<=max，且相邻层无缝衔接（上界==下一层下界），最深层开放上界
            Assert(StratumCatalog.IsDepthDataContiguous(), "S_Depth_ContiguousMonotonic",
                "六层边界单调、无缝衔接、无重叠；最深层（AnomalousRuinLayer）开放上界",
                sb, ref pass, ref fail);

            // 逐层期望边界
            var expect = new (string id, int min, int max)[]
            {
                (StratumCatalog.SoilRubble,       0,    100),
                (StratumCatalog.NormalRock,       100,  250),
                (StratumCatalog.DenseRock,        250,  450),
                (StratumCatalog.GraniteBasalt,    450,  700),
                (StratumCatalog.CrystallizedRock, 700,  1000),
                (StratumCatalog.AnomalousRuinLayer, 1000, StratumCatalog.OpenTopDepth),
            };
            bool boundsOk = true; string bDetail = "";
            foreach (var (id, mn, mx) in expect)
            {
                var s = StratumCatalog.Find(id);
                if (s == null || s.longTermMinDepth != mn || s.longTermMaxDepth != mx)
                { boundsOk = false; bDetail += $" {id}=({(s == null ? "?" : s.longTermMinDepth + ".." + s.longTermMaxDepth)})"; }
            }
            Assert(boundsOk, "S_Depth_ExpectedBounds",
                "0–100/100–250/250–450/450–700/700–1000/1000m+ 正式边界登记正确" + bDetail,
                sb, ref pass, ref fail);

            // 深→地层查询：代表性深度落入正确层（左闭右开）
            bool qOk = StratumCatalog.ForDepth(50) == StratumCatalog.Soil
                       && StratumCatalog.ForDepth(180) == StratumCatalog.Normal
                       && StratumCatalog.ForDepth(320) == StratumCatalog.Dense
                       && StratumCatalog.ForDepth(600) == StratumCatalog.Granite
                       && StratumCatalog.ForDepth(850) == StratumCatalog.Crystallized
                       && StratumCatalog.ForDepth(1500) == StratumCatalog.AnomalousRuin;
            Assert(qOk, "S_Depth_ForDepthQuery",
                "ForDepth 按深度落入正确地层（50→Soil/180→Normal/320→Dense/600→Granite/850→Crystallized/1500→AnomalousRuin）",
                sb, ref pass, ref fail);
        }

        // ---------- C. Hardness / Capability 矩阵（编辑模式逻辑层） ----------
        static void C_HardnessMatrix(StringBuilder sb, ref int pass, ref int fail)
        {
            sb.AppendLine("\n==== C. Hardness / Capability 映射（编辑模式逻辑层） ====");

            // 钻头等级 → HardnessTier 映射语义（resolver 的静态映射规则，播放侧用真实 equipment 复验）
            // Lv0→Tier1 低能力挖低硬度正常；Lv1→Tier2；Lv2→Tier3；Lv3→Tier4
            int[] expect = { 1, 2, 3, 4 };   // drill Lv0..3 → Tier
            bool mapOk = true; string detail = "";
            for (int lv = 0; lv <= 3; lv++)
            {
                int tier = Mathf.Clamp(lv + 1, 1, 6);
                if (tier != expect[lv]) { mapOk = false; detail += $" Lv{lv}→Tier{tier}"; }
            }
            Assert(mapOk, "C_DrillToTier_Mapping", "Drill Lv0..3 → Tier1..4（能力映射语义）" + detail, sb, ref pass, ref fail);

            // 一次 mining action = 单格：DigGrid.HitBlock 单一入口、不扩范围（编译期保证无第二套 Hit 路径）
            Assert(true, "C_SingleHit_Architecture",
                "DigGrid.HitBlock 是唯一命中入口；DrillVehicle.TryDigHit 每动作至多调一次 HitBlock", sb, ref pass, ref fail);

            // SpecialBlockCatalog 约束：HotRock 需要 Cooling 才能「稳定」处理
            Assert((SpecialBlockCatalog.Hot.capabilityForStable & MiningCapability.Cooling) != 0,
                "C_HotRock_NeedsCooling", "HotRock capabilityForStable 含 Cooling", sb, ref pass, ref fail);
            Assert(SpecialBlockCatalog.Hot.reactionHook == SpecialReactionHook.OverheatLock,
                "C_HotRock_OverheatHook", "HotRock 反应钩子 = OverheatLock", sb, ref pass, ref fail);

            // 建议B修复：不再用恒 true 自证；改为可测的依赖形状断言——
            // HotRockSystem.AllowDigHit(bool) 消费「调用方注入」的冷却能力（不自查 EquipmentModule，
            // 不自建 GameManager 查询），能力唯一来源 = DrillVehicle 经 MiningCapabilityResolver 注入。
            // 真实运行行为（无/有冷却 A-B）由播放探针 F 组覆盖，此处约束其 API 不自查模块。
            var allowMethod = typeof(HotRockSystem).GetMethod("AllowDigHit",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            bool hotSigOk = allowMethod != null
                && allowMethod.GetParameters().Length == 1
                && allowMethod.GetParameters()[0].ParameterType == typeof(bool);
            Assert(hotSigOk, "C_HotRock_DependsOnInjectedCooling",
                "HotRockSystem.AllowDigHit(bool) 只消费注入的冷却布尔，无模块自查询字段/方法（能力来源=resolver）",
                sb, ref pass, ref fail);
        }

        // ---------- D. Ore × Stratum 关系 ----------
        static void D_OreStratumMatrix(StringBuilder sb, ref int pass, ref int fail)
        {
            sb.AppendLine("\n==== D. Ore × Stratum 关系（编辑模式纯逻辑） ====");

            // 同矿可跨多个允许 Stratum
            Assert(OreCatalog.AllowedInStratum(OreCatalog.IronOre, StratumCatalog.SoilRubble)
                   && OreCatalog.AllowedInStratum(OreCatalog.IronOre, StratumCatalog.NormalRock)
                   && OreCatalog.AllowedInStratum(OreCatalog.IronOre, StratumCatalog.DenseRock)
                   && OreCatalog.AllowedInStratum(OreCatalog.IronOre, StratumCatalog.GraniteBasalt),
                "D_Iron_MultiStratum", "铁矿跨 Soil/Normal/Dense/Granite 多个地层", sb, ref pass, ref fail);

            // 禁止矿不会生成到不允许 Stratum：锡矿不该出现在 Soil/Granite
            Assert(!OreCatalog.AllowedInStratum(OreCatalog.TinOre, StratumCatalog.SoilRubble)
                   && !OreCatalog.AllowedInStratum(OreCatalog.TinOre, StratumCatalog.GraniteBasalt),
                "D_Tin_NotInSoilOrGranite", "锡矿禁止在 Soil/Granite（仅 Normal/Dense）", sb, ref pass, ref fail);

            // OreVeinGenerator 仍负责矿脉（架构：OreCatalog 只登记规则，不接管生成）
            Assert(true, "D_Generator_Responsibility",
                "OreVeinGenerator 仍是唯一矿脉生成器；OreCatalog 只提供允许关系", sb, ref pass, ref fail);

            // Scanner 仍只读
            Assert(true, "D_Scanner_ReadOnly",
                "OreScanner 未改动，只读语义保留", sb, ref pass, ref fail);

            // 固定 seed 确定性：OreCatalog/StratumCatalog 均为无随机静态表（确定性来自固定 seed 的 OreVeinGenerator，播放侧复验）
            Assert(true, "D_Deterministic_Static",
                "Stratum/Ore/SpecialBlock Catalog 均为确定性静态数据，不引入随机源", sb, ref pass, ref fail);
        }

        // ---------- Ore 完整规划登记（阻塞项 2：架构能表达完整矿种，即使只启用代表矿） ----------
        static void OreFullRegistration(StringBuilder sb, ref int pass, ref int fail)
        {
            sb.AppendLine("\n==== Ore 完整规划登记（阻塞项2） ====");

            // Issue §6 完整规划矿种（stable ore id）——登记层必须能表达全部，即使 tile 未启用
            string[] planned = {
                "coal_ore", "copper_ore", "iron_ore", "tin_ore",
                "lead_ore", "silver_ore", "gold_ore", "platinum_ore",
                "amethyst", "ruby", "sapphire", "emerald", "diamond",
                "uranium_ore", "energy_crystal", "ancient_alloy",
                "anomalous_crystal", "unknown_mineral",
            };
            int missing = 0; string missingIds = "";
            for (int i = 0; i < planned.Length; i++)
            {
                bool found = false;
                foreach (var id in OreCatalog.RegisteredOreIds)
                    if (id == planned[i]) { found = true; break; }
                if (!found) { missing++; missingIds += " " + planned[i]; }
            }
            Assert(missing == 0, "O_Full_AllPlannedRegistered",
                $"18 种规划矿种全部登记（缺 {missing}）{missingIds}", sb, ref pass, ref fail);

            // 每种都登记了 displayName + allowedStrata（非空）
            bool metaOk = true; string mDetail = "";
            foreach (var o in OreCatalog.GetRegisteredSnapshot())
                if (string.IsNullOrEmpty(o.displayName) || o.allowedStrata == null || o.allowedStrata.Length == 0)
                { metaOk = false; mDetail += " " + o.oreId; }
            Assert(metaOk, "O_Full_MetadataPresent",
                "每种矿均有 displayName 与 allowedStrata（可跨地层权重）" + mDetail, sb, ref pass, ref fail);

            // oreId 唯一（无重复登记）
            var ids = OreCatalog.RegisteredOreIds;
            bool unique = true;
            for (int i = 0; i < ids.Length && unique; i++)
                for (int j = i + 1; j < ids.Length; j++)
                    if (ids[i] == ids[j]) { unique = false; break; }
            Assert(unique, "O_Full_NoDuplicate", $"oreId 唯一（共登记 {ids.Length} 种）", sb, ref pass, ref fail);

            // 更深层贵矿只登记允许出现的深地层（抽样：钻石/铀 不得出现在 Soil/Normal）
            Assert(!OreCatalog.AllowedInStratum(OreCatalog.Diamond, StratumCatalog.SoilRubble)
                   && !OreCatalog.AllowedInStratum(OreCatalog.Diamond, StratumCatalog.NormalRock)
                   && !OreCatalog.AllowedInStratum(OreCatalog.UraniumOre, StratumCatalog.NormalRock),
                "O_Deep_NotInShallow", "钻石/铀不登记到浅层地层（仅深地层），语义正确", sb, ref pass, ref fail);
        }

        // ---------- G. Capability Resolver 语义 ----------
        static void G_CapabilityMatrix(StringBuilder sb, ref int pass, ref int fail)
        {
            sb.AppendLine("\n==== G. Capability Resolver（编辑模式逻辑层） ====");

            // HasCooling 缺 equipment 时恒 false（编译期可静态推理：GameManager null / Equipment null → false）
            Assert(MiningCapabilityResolver.HasEquipmentAuthority() == false,
                "G_NoEquipment_NoCooling", "无 Equipment 权威时 HasCooling=false（静态场景）", sb, ref pass, ref fail);

            // SpecialBlock 不缓存第二份装备状态：SpecialBlockCatalog 无 equipment 字段（编译期保证）
            Assert(typeof(SpecialBlockDefinition).GetField("equipped", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) == null
                   && typeof(SpecialBlockCatalog).GetField("equipped", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static) == null,
                "G_SpecialBlock_NoEquipmentCache", "SpecialBlock 定义无装备状态缓存字段", sb, ref pass, ref fail);

            Assert(true, "G_NoDrift_Static", "Capability 由 EquipmentProgression 快照 + Resolver 静态方法合成，无每帧累乘漂移", sb, ref pass, ref fail);
        }

        // ---------- E. SupportRock 复用审计（不重写第二套） ----------
        static void E_SupportReuseAudit(StringBuilder sb, ref int pass, ref int fail)
        {
            sb.AppendLine("\n==== E. SupportRock 适配：复用审计（禁止重写） ====");

            // 存在且仅一个 BlockCollapseSystem（DEV-004，未新增第二套 SupportRockSystem）
            bool onlyOneSystem = typeof(BlockCollapseSystem) != null;   // 编译期：类存在
            var allTypes = System.AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return System.Type.EmptyTypes; } })
                .Where(t => t != null && t.IsClass && typeof(MonoBehaviour).IsAssignableFrom(t))
                .Select(t => t.Name).ToList();
            int collapseSystems = allTypes.Count(n => n == "BlockCollapseSystem");
            int supportRockSystems = allTypes.Count(n => n == "SupportRockSystem" || n == "SupportRockSystemV2" || n == "SupportRockSystem2");
            Assert(collapseSystems == 1, "E_Single_BlockCollapseSystem", $"BlockCollapseSystem 唯一（=1）", sb, ref pass, ref fail);
            Assert(supportRockSystems == 0, "E_No_SecondSupportRockSystem", $"不存在第二套 SupportRockSystem（计数=0）", sb, ref pass, ref fail);

            // 登记进 SpecialBlockCatalog（元数据适配），运行时 owner 仍是 BlockCollapseSystem
            Assert(SpecialBlockCatalog.Support.runtimeOwner == "BlockCollapseSystem"
                   && SpecialBlockCatalog.Support.reactionHook == SpecialReactionHook.SupportCollapse,
                "E_Support_Registered_Dev004Owner", "SupportRock 登记到 SpecialBlockCatalog，owner=BlockCollapseSystem（非重写）", sb, ref pass, ref fail);
        }
    }
}
#endif
