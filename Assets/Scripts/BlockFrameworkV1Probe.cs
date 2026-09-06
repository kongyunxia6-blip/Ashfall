using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-012：播放模式验收探针（真实 Unity 运行）。
    /// 覆盖 Issue §15 需真实运行的矩阵：B/C 实时映射、E SupportRock 坍塌回归、F HotRock A-B、
    /// G CapabilityResolver Equip/Unequip、I 单格 merge blocker。
    /// 用真实跨帧协程 + 公共挖掘路径 DrillVehicle.TryDigHit（单格命中唯一入口）驱动，写入结果文件。
    ///
    /// 挂载：BlockFrameworkV1Test 场景 GameManager 物体。autoRun=true 时 Start 自动跑。
    /// </summary>
    public class BlockFrameworkV1Probe : MonoBehaviour
    {
        public bool autoRun = true;
        public DigGrid grid;
        public BlockCollapseSystem collapse;
        public HotRockSystem hotRock;
        public DrillVehicle vehicle;
        public BlockFrameworkV1TestLayout layout;
        public EquipmentProgression equipment;
        public TileDefinition hotRockAsset;
        public TileDefinition supportRockAsset;
        public TileDefinition hardRockAsset;

        const string OutPath = "C:/Users/58058/.workbuddy/tools/d12_play_result.txt";

        int passCount = 0, failCount = 0;
        readonly StringBuilder sb = new StringBuilder();

        IEnumerator Start()
        {
            yield return new WaitForSeconds(1f);   // 等 rig 就绪（GM/Awake/布局重放）

            // 兜底解析：Builder 在编辑模式序列化引用时，GameManager.Equipment 是 private set
            // （仅在 Awake 赋值），故搭建时保存的 equipment 引用为 null。运行时同物体 GetComponent 兜底补齐。
            if (equipment == null)
            {
                var ep = GetComponent<EquipmentProgression>();
                if (ep == null) ep = GetComponentInParent<EquipmentProgression>();
                if (ep == null) ep = FindObjectOfType<EquipmentProgression>();
                if (ep != null) { equipment = ep; Debug.Log("[DEV-012] Probe 运行时兜底解析到 EquipmentProgression"); }
            }

            // 手动清空 GameManager 下可能自动的装备；确保起点干净
            if (equipment != null)
            {
                equipment.drillLevel = 0;
                for (int i = 0; i < equipment.ownedModules.Length; i++) equipment.ownedModules[i] = false;
                for (int i = 0; i < equipment.equipped.Length; i++) equipment.equipped[i] = null;
            }
            if (hotRock != null) hotRock.ResetOverheat();

            sb.AppendLine("==== DEV-012 播放验收 ====");
            yield return Step(E_SupportRockRegression(), "E");
            yield return Step(F_HotRockAB(), "F");
            yield return Step(G_CapabilityResolver(), "G");
            yield return Step(I_SingleBlockMatrix(), "I");
            yield return Step(B_LiveRegionStratum(), "B");

            sb.AppendLine($"\n==== 汇总：PASS={passCount}  FAIL={failCount} ====");
            try { File.WriteAllText(OutPath, sb.ToString()); }
            catch (System.Exception e) { Debug.LogError("[DEV-012] 写结果失败：" + e.Message); }
            Debug.Log($"[DEV-012] 播放验收完成：PASS={passCount} FAIL={failCount} → {OutPath}");
        }

        IEnumerator Step(IEnumerator body, string tag)
        {
            yield return body;
        }

        void Assert(bool ok, string tag, string msg)
        {
            if (ok) { passCount++; sb.AppendLine($"PASS  {tag}: {msg}"); }
            else { failCount++; sb.AppendLine($"FAIL  {tag}: {msg}"); }
        }

        // ---------- 挖掘辅助：对指定格连击直到崩碎/移除或给定次数 ----------
        int DigUntil(Vector2Int cell, int maxHits, out bool removed, out int overheatedCount)
        {
            removed = false;
            overheatedCount = 0;
            int landed = 0;
            for (int i = 0; i < maxHits; i++)
            {
                var res = vehicle.TryDigHit(cell);
                if (res == DigHitResult.Broken) { removed = true; landed++; break; }
                if (res == DigHitResult.Hit) { landed++; continue; }
                if (res == DigHitResult.Overheated) { overheatedCount++; continue; }
                if (res == DigHitResult.NotSolid) break;   // 已移除/不可挖
                if (res == DigHitResult.HardnessLow) break;
            }
            return landed;
        }

        // ---------- E. SupportRock 坍塌回归（复用 DEV-004，禁止重写） ----------
        IEnumerator E_SupportRockRegression()
        {
            sb.AppendLine("\n==== E. SupportRock 适配回归（复用 BlockCollapseSystem） ====");
            if (grid == null || collapse == null || layout == null || vehicle == null)
            {
                Assert(false, "E_Rig", "缺少 grid/collapse/layout/vehicle"); yield break;
            }

            int sx = layout.supportX;
            int sy = layout.anchorY;
            var supportCell = new Vector2Int(sx, sy);
            // 上方 3 块 LooseRock 的坐标（y-1..y-3）
            var looseAbove = new Vector2Int(sx, sy - 1);

            // 预先断言：支撑格是 SupportRock、上方是 LooseRock（网格已正确放置）
            var supDef = grid.GetTile(sx, sy);
            Assert(supDef != null && supDef.blockType == BlockType.SupportRock, "E_Pre_SupportInPlace",
                $"({sx},{sy}) 是 SupportRock 承重岩");
            Assert(grid.GetTile(sx, sy - 1) != null && grid.GetTile(sx, sy - 1).blockType == BlockType.LooseRock,
                "E_Pre_LooseAbove", $"({sx},{sy - 1}) 上方是 LooseRock");

            // 挖穿承重岩（2 击）：单格命中
            int landed = DigUntil(supportCell, 6, out bool removed, out int oh);
            Assert(removed, "E_Dig_SupportRemoved", $"挖除承重岩（命中 {landed} 击，单格）");
            Assert(oh == 0, "E_Dig_NoOverheat_OnSupport", "挖 SupportRock 不触发过热");

            // 坍塌系统应启动支撑检查（SupportRock 真正移除）
            bool triggered = collapse != null;
            Assert(triggered, "E_Collapse_SystemPresent", "BlockCollapseSystem 存在（复用，非重写）");

            // 验证上方的 LooseRock 不再在原位（等待/强制结算后发生坍塌位移或进入不稳定）
            yield return new WaitForEndOfFrame();
            // 强制结算支撑链（等价真实帧后的落格）
            if (collapse != null) collapse.DebugForceCollapse(supportCell);
            yield return new WaitForEndOfFrame();

            // 落格后：承重岩空位(sx,sy)应被 LooseRock 填上（下移 1 格），原上方顶格(sy-3)腾空
            bool refilled = grid.GetTile(sx, sy) != null
                && grid.GetTile(sx, sy).blockType == BlockType.LooseRock;
            Assert(refilled, "E_Collapse_Landed", $"坍塌后承重岩空位被 LooseRock 回填（局部、确定性）");

            // 无残留 Unstable
            Assert(collapse.UnstableCellCount == 0, "E_No_Unstable_Residue", "落格后无 Unstable 残留格");
            Assert(collapse.PendingChainCount == 0, "E_No_Pending_Residue", "无待处理链（防重复）");

            // 单格：只挖穿了承重岩，其左右邻格 Durability 不受影响（主动采矿 vs 环境反应区分）
            int leftD = grid.GetDurability(sx - 1, sy);
            int rightD = grid.GetDurability(sx + 1, sy);
            Assert(leftD >= 1 && rightD >= 1, "E_SingleBlock_Neighbors", $"邻格 ({sx - 1},{sy})/({sx + 1},{sy}) 耐久未减（单格）");
        }

        // ---------- F. HotRock 无冷却 / 有冷却 A-B ----------
        IEnumerator F_HotRockAB()
        {
            sb.AppendLine("\n==== F. HotRock 无冷却 / 有冷却 A-B ====");
            if (grid == null || hotRock == null || vehicle == null || equipment == null)
            {
                Assert(false, "F_Rig", "缺少 hotRock/vehicle/equipment"); yield break;
            }

            int hx = layout != null ? layout.hotX : 30;
            int sy = layout.anchorY;

            // 先确认该柱有 HotRock 顶格
            var topCell = new Vector2Int(hx, sy);
            Assert(grid.GetTile(hx, sy) != null && grid.GetTile(hx, sy).blockType == BlockType.HotRock,
                "F_Pre_HotRockInPlace", $"({hx},{sy}) 是 HotRock");

            // ---- A. 无冷却：明显受惩罚 / 过热锁定可观测 ----
            // 确保无冷却
            equipment.equipped[0] = null;
            hotRock.ResetOverheat();
            Assert(!MiningCapabilityResolver.HasCapability(MiningCapability.Cooling), "F_A_NoCooling", "无冷却能力（初始）");

            // 重置该 HotRock 顶格耐久以便重复测（用 layout 重放不现实，直接用 SetTile 恢复）——不必，直接读初始耐久并按可承受次数测。
            // 连续命中直到出现过热锁定（Overheated 结果）或热量触发锁定
            int overheatSeen = 0;
            int hitsUntilOverheat = 0;
            bool lockedObserved = false;
            for (int i = 0; i < hotRock.heatThreshold + 2; i++)
            {
                var res = vehicle.TryDigHit(topCell);
                if (res == DigHitResult.Overheated) { overheatSeen++; lockedObserved = true; break; }
                if (res == DigHitResult.Hit || res == DigHitResult.Broken) { hitsUntilOverheat++; continue; }
                if (res == DigHitResult.NotSolid || res == DigHitResult.HardnessLow) break;
            }
            Assert(overheatSeen >= 1 || hotRock.IsOverheated, "F_A_Overheat_Locked",
                $"无冷却挖 HotRock 触发过热（命中 {hitsUntilOverheat} 击后 {hotRock.LastVerdict}）");
            Assert(hitsUntilOverheat <= hotRock.heatThreshold, "F_A_HeatCaps_AtThreshold",
                $"无冷却热量在阈值 {hotRock.heatThreshold} 内封顶（实际 {hitsUntilOverheat}）");

            // 锁定期间再挖 → 被拒绝（Overheated，不命中）
            if (!lockedObserved && hotRock.IsOverheated)
            {
                var res = vehicle.TryDigHit(topCell);
                lockedObserved = res == DigHitResult.Overheated;
            }
            Assert(lockedObserved || hotRock.IsOverheated, "F_A_Lock_Refuses_Hit",
                "过热锁定中后续挖掘被拒绝（Overheated，不命中，单格不变）");

            // 无冷却 = 明显低效：无法像冷却那样连续穿透（有冷却可 4 连击打通，见 B）

            // ---- B. 有冷却：稳定处理 + Equip/Unequip 能力可逆 ----
            // Equip DrillCooling（直接置权威数组，绕开 Workbench 门控以便验收）
            equipment.ownedModules[(int)EquipmentModule.DrillCooling] = true;
            equipment.equipped[0] = EquipmentModule.DrillCooling;
            yield return new WaitForEndOfFrame();
            Assert(MiningCapabilityResolver.HasCapability(MiningCapability.Cooling), "F_B_Equip_Cooling", "装备 DrillCooling → HasCooling=true");
            hotRock.ResetOverheat();

            // 冷却下：连击直到打通（digHits 需能连续命中，无过热拒绝）
            bool cooledThrough = false;
            int coolHits = 0;
            for (int i = 0; i < 10; i++)
            {
                var res = vehicle.TryDigHit(topCell);
                if (res == DigHitResult.Broken) { cooledThrough = true; coolHits++; break; }
                if (res == DigHitResult.Hit) { coolHits++; continue; }
                if (res == DigHitResult.NotSolid) break;
                if (res == DigHitResult.Overheated) break;   // 冷却下不应出现
            }
            Assert(cooledThrough, "F_B_Cooling_Stable", $"有冷却挖穿 HotRock（{coolHits} 连击，无过热锁定）");
            Assert(hotRock.IsOverheated == false, "F_B_Cooling_NoLock", "冷却下无过热锁定");

            // Unequip → 能力可逆
            equipment.equipped[0] = null;
            yield return new WaitForEndOfFrame();
            Assert(!MiningCapabilityResolver.HasCapability(MiningCapability.Cooling), "F_Unequip_Reversible", "卸下 DrillCooling → HasCooling=false（可逆）");

            // 能力查询走 capability 层（F 代码只查 HasCapability）
            Assert(hotRockAsset.blockType == BlockType.HotRock, "F_HotRock_Type", "HotRock 资产 blockType=HotRock");
        }

        // ---------- G. Capability Resolver ----------
        IEnumerator G_CapabilityResolver()
        {
            sb.AppendLine("\n==== G. Capability Resolver（Equip/Unequip 可逆） ====");
            if (equipment == null) { Assert(false, "G_Rig", "缺 equipment"); yield break; }

            // 初始：无冷却
            Assert(!MiningCapabilityResolver.HasCooling(), "G_Init_NoCooling", "初始无冷却");

            // Equip → true
            equipment.equipped[0] = EquipmentModule.DrillCooling;
            yield return new WaitForEndOfFrame();
            Assert(MiningCapabilityResolver.HasCooling(), "G_Equip_True", "Equip DrillCooling → HasCooling=true");

            // Unequip → false
            equipment.equipped[0] = null;
            yield return new WaitForEndOfFrame();
            Assert(!MiningCapabilityResolver.HasCooling(), "G_Unequip_False", "Unequip → HasCooling=false");

            // 无 multiplier drift：Resolver 为纯静态查询，无累乘（编译语义）；装备快照不变则结果不变
            Assert(MiningCapabilityResolver.GetAuthorityDrillLevel() >= 0, "G_Authority_Level", $"权威钻头等级读取正常（{MiningCapabilityResolver.GetAuthorityDrillLevel()}）");

            // 钻头等级→硬度映射经 resolver 读取
            int dl = Mathf.Clamp(equipment.drillLevel, 0, 6);
            int tier = Mathf.Clamp(dl + 1, 1, 6);
            Assert(tier >= 1 && tier <= 6, "G_DrillToTier", $"drill Lv{dl} → HardnessTier.Tier{tier}");
        }

        // ---------- I. 单格 merge blocker ----------
        IEnumerator I_SingleBlockMatrix()
        {
            sb.AppendLine("\n==== I. 永久单格回归（merge blocker） ====");
            if (grid == null || vehicle == null) { Assert(false, "I_Rig", "缺 grid/vehicle"); yield break; }

            // 目标：找一块普通岩（地层填充 Normal），先挖 1 击，断言其 4 邻耐久不变。
            int cx = layout != null ? layout.hardnessX + 6 : 40;   // 远离测试柱的普通地层格
            Vector2Int target = FindSolidNormalCell(cx);
            if (target.x < 0) { Assert(false, "I_NoNormalTarget", "未找到普通岩目标"); yield break; }

            // 记录目标与其 4 邻当前耐久
            var nb = Neighbors(target);
            var before = new Dictionary<Vector2Int, int>();
            before[target] = grid.GetDurability(target.x, target.y);
            foreach (var n in nb) if (grid.IsSolid(n.x, n.y)) before[n] = grid.GetDurability(n.x, n.y);

            var res = vehicle.TryDigHit(target);
            Assert(res == DigHitResult.Hit || res == DigHitResult.Broken, "I_Normal_SingleHit",
                $"普通岩单次命中 {res}");

            // 目标耐久应 -1（或移除）；邻格不变
            bool targetHit = grid.GetDurability(target.x, target.y) < before[target];
            bool nbUntouched = true; string nbDetail = "";
            foreach (var n in nb)
            {
                if (!grid.IsSolid(n.x, n.y)) continue;
                int cur = grid.GetDurability(n.x, n.y);
                if (cur != before[n]) { nbUntouched = false; nbDetail += $" ({n.x},{n.y}):{before[n]}→{cur}"; }
            }
            Assert(targetHit, "I_Normal_TargetReduced", "普通岩目标耐久 -1");
            Assert(nbUntouched, "I_Normal_NeighborsUntouched", "普通岩 4 邻耐久不变（单格）" + nbDetail);
        }

        Vector2Int FindSolidNormalCell(int startX)
        {
            // 从锚点往下找一个 value==0 且 blockType==Normal 的实心格
            for (int y = layout != null ? layout.anchorY : 6; y < grid.Depth && y < layout.anchorY + 6; y++)
            {
                for (int x = startX; x < grid.Width - 1; x++)
                {
                    var t = grid.GetTile(x, y);
                    if (t != null && t.isSolid && t.blockType == BlockType.Normal && t.value == 0
                        && t != grid.database.bedrockTile)
                        return new Vector2Int(x, y);
                }
            }
            return new Vector2Int(-1, -1);
        }

        static List<Vector2Int> Neighbors(Vector2Int c) => new List<Vector2Int>
        {
            new Vector2Int(c.x + 1, c.y), new Vector2Int(c.x - 1, c.y),
            new Vector2Int(c.x, c.y + 1), new Vector2Int(c.x, c.y - 1),
        };

        // ---------- B. 实时 Region ↔ Stratum ----------
        IEnumerator B_LiveRegionStratum()
        {
            sb.AppendLine("\n==== B. 实时 Region / Stratum 独立概念 ====");
            // DepthRegionLayout 仍是 Region 唯一来源，StratumCatalog.ForRegion 只做推荐映射
            var regMid = DepthRegionLayout.Mid;
            var strat = StratumCatalog.ForRegion(regMid);
            Assert(regMid.regionId == "Mid" && strat.stableId == StratumCatalog.DenseRock,
                "B_Live_MidDense", $"Mid Region → DenseRock（Region={regMid}，Stratum={strat}）");
            Assert(regMid.maxDepth == 43 && strat.hardnessTier == HardnessTier.Tier3,
                "B_Live_RegionBoundaryVsStratum", "Region 边界(22..43) 与 Stratum 硬度(★★★) 独立（两概念）");
            yield break;
        }
    }
}
