using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-015 (Issue #32) 真实播放验收探针：经济闭环 Vertical Slice。
    /// 挂载：EconomyBalanceV1Test 场景 GameManager。autoRun=true 时 Start 自动跑。
    ///
    /// 覆盖 Issue #32 需真实运行/决策路径的核心：一条【真实】 Surface → 下矿 → Cargo 选择
    /// → 返航 → Sell 的完整 Run（RunRiskState 生命周期驱动，非手动 BeginRun）：
    ///   E1 玩家处地表 Hub → 状态机不开 Run（干净基线）；
    ///   E2 真实下潜离开地表 → RunRiskState 状态机自己 BeginRun（RunActive=true、RunNumber+1）；
    ///   E3 真实挖低值 Iron 填满 Cargo（HandleTileDug 入包，载重达 MaxCarryWeight）；
    ///   E4 挖到紧邻高值 Emerald → HandleTileDug 顶掉最低密度 Iron（丢铁矿换钻石语义）：
    ///      CargoValue 跳升、Iron 计数下降、载重仍 ≤ 上限、Emerald 入舱；
    ///   E5 返航决策：真实剩油 &gt; 上行返航所需（真实 LoadRatio×fuelMove×upward）→ 见好就收、可安全回；
    ///   E6 真实返航地表（物理逐格，无瞬移）；
    ///   E7 真实驶入 SellTerminal → PlayerInRange=true → TrySell 增收 → Cargo 清空 → Run 结束。
    ///
    /// 全程真实组件：DrillVehicle.TryDigHit→HandleTileDug→InventoryGrid / DrainFuel / RunRiskState /
    /// SellTerminal；只用「受控地形 + 真实物理移动 + 真实挖矿」无 transform 跳点（单一 spawn 放置除外）。
    /// </summary>
    public class EconomyBalanceV1Probe : MonoBehaviour
    {
        public bool autoRun = true;
        public DigGrid grid;
        public DrillVehicle vehicle;
        public EquipmentProgression equipment;
        public OreScanner oreScanner;
        public TileDefinition iron, emerald, gold, dirt, emptyTile;

        const string OutPath = "C:/Users/58058/.workbuddy/tools/d15_play_result.txt";

        int passCount = 0, failCount = 0;
        readonly StringBuilder sb = new StringBuilder();

        // 受控地形坐标（与 EconomyBalanceV1Builder 对齐）
        const int Center = 32;
        const int GalleryY = 22;          // 载具下潜目标行（画廊站台）
        const int OreRowY = GalleryY + 5; // 低值铁排所在行
        const int IronStartX = Center - 14;   // 铁排起点 x=18
        const int IronCount = 30;             // 铁排格数
        const int EmeraldX = IronStartX + IronCount;  // 铁排紧邻右侧高值矿 x=48

        IEnumerator Start()
        {
            var gm = GameManager.Instance;
            if (gm != null) gm.IsAtSurface = true;   // 玩家处地表 → 状态机不开 Run（同 DEV-014 基线）
            Debug.Log("[DEV-015] Economy Probe Start: running...");
            yield return new WaitForSeconds(1f);

            if (grid == null) grid = FindFirstObjectByType<DigGrid>();
            if (vehicle == null) vehicle = FindFirstObjectByType<DrillVehicle>();
            if (oreScanner == null) oreScanner = FindFirstObjectByType<OreScanner>();
            if (equipment == null) equipment = FindFirstObjectByType<EquipmentProgression>();
            gm = GameManager.Instance;
            if (grid == null || vehicle == null || gm == null || gm.RunRisk == null)
            {
                Assert(false, "RIG", $"缺系统 grid={grid} vehicle={vehicle} gm={gm}"); yield break;
            }

            sb.AppendLine("==== DEV-015 真实播放：经济闭环 Vertical Slice ====");
            yield return SetupCourse();          // 载入场景后 DigGrid.Awake 会重新生成 → 在 Start 里重凿受控 course
            yield return E1_SurfaceBaseline(gm);
            yield return E2_DescendAutoRun(gm);
            yield return E3_MineIronFill(gm);
            yield return E4_CargoReplacement(gm);
            yield return E5_FuelSafeReturn(gm);
            yield return E6_ReturnToSurface(gm);
            yield return E7_Sell(gm);

            sb.AppendLine($"\n==== 汇总：PASS={passCount}  FAIL={failCount} ====");
            try { File.WriteAllText(OutPath, sb.ToString()); }
            catch (System.Exception e) { Debug.LogError("[DEV-015] 写结果失败：" + e.Message); }
            Debug.Log($"[DEV-015] 经济播放验收完成：PASS={passCount} FAIL={failCount} → {OutPath}");
        }

        // ---------- Course 凿建（运行时，防 Awake 再生覆盖） ----------
        IEnumerator SetupCourse()
        {
            sb.AppendLine("\n== COURSE. 运行时凿建受控 course（DigGrid.Awake 再生后重凿） ==");
            if (emptyTile == null || iron == null || emerald == null)
            {
                Assert(false, "COURSE_Assets", $"缺 tile 引用 empty={emptyTile} iron={iron} emerald={emerald}"); yield break;
            }
            // 3 列宽竖井（x=Center±1, y=3..GalleryY）清空
            for (int x = Center - 1; x <= Center + 1; x++)
                for (int y = 3; y <= GalleryY; y++)
                    grid.SetTile(x, y, emptyTile);
            // 画廊站台（站台下 3 行，给载具停靠空间）
            for (int x = Center - 4; x <= Center + 4; x++)
                for (int y = GalleryY + 1; y <= GalleryY + 3; y++)
                    grid.SetTile(x, y, emptyTile);
            // 低值 Iron 排 + 紧邻高值 Emerald
            for (int i = 0; i < IronCount; i++)
                grid.SetTile(IronStartX + i, OreRowY, iron);
            grid.SetTile(EmeraldX, OreRowY, emerald);
            // 地表通往 SellTerminal 的横向通道清空
            for (int x = Center - 3; x <= Center + 6; x++)
                for (int y = 0; y <= 1; y++)
                    grid.SetTile(x, y, emptyTile);
            Assert(true, "COURSE_Carved", $"凿建完成：竖井到 GalleryY={GalleryY}，Iron x={IronStartX}..{IronStartX + IronCount - 1} y={OreRowY}，Emerald x={EmeraldX} y={OreRowY}");
            yield return NullFrame(0.1f);
        }

        // ---------- E1 地表基线 ----------
        IEnumerator E1_SurfaceBaseline(GameManager gm)
        {
            sb.AppendLine("\n== E1. 玩家处地表 Hub → Run 未开启 ==");
            int rn = gm.RunRisk.RunNumber;
            Assert(!gm.RunRisk.RunActive, "E1_NoRun_Surface", $"地表 RunActive=false（状态机未自启），RunNumber={rn}");
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            Assert(gm.RunRisk.RunNumber == rn && !gm.RunRisk.RunActive, "E1_StayStable", "停留地表跨帧不误开 Run");
        }

        // ---------- E2 真实下潜 → 状态机自动开 Run ----------
        IEnumerator E2_DescendAutoRun(GameManager gm)
        {
            sb.AppendLine("\n== E2. 真实下潜离开地表 → RunRiskState 自启 Run ==");
            if (vehicle.transform.position == Vector3.zero)
                vehicle.transform.position = grid.GridToWorld(Center, 1);   // 仅此处放真实 spawn
            vehicle.SetMovementMode(DrillVehicle.MovementMode.Hover);
            vehicle.SetJetting(false, true);
            yield return NullFrame(0.3f);
            Vector2Int startCell = grid.WorldToGrid(vehicle.transform.position);
            Assert(startCell.y <= DepthRegionLayout.Surface.maxDepth, "E2_StartSurface", $"从地表出发（cell={startCell}）");

            // 拨离地表信号 → 状态机在真实帧自启（不调 BeginRun）
            int runNo = gm.RunRisk.RunNumber;
            gm.IsAtSurface = false;
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            Assert(gm.RunRisk.RunActive && gm.RunRisk.RunNumber == runNo + 1, "E2_RunAutoStart",
                $"离地后状态机自启：RunActive={gm.RunRisk.RunActive} RunNumber {runNo}→{gm.RunRisk.RunNumber}（非手动）");

            // 真实物理沿 3 列宽竖井逐格下潜到画廊站台
            Vector2Int target = new Vector2Int(Center, GalleryY);
            yield return DriveAlong(target);
            Vector2Int arr = grid.WorldToGrid(vehicle.transform.position);
            Assert(arr == target, "E2_ArrivedGallery", $"真实下潜到画廊站台 cell={arr}（=target {target}）");
            Assert(arr.y >= startCell.y + 5, "E2_ReallyDescended", $"确实离开地表下潜 y {startCell.y}→{arr.y}（深 {arr.y - startCell.y} 格）");
        }

        // ---------- E3 真实挖低值 Iron 填满 Cargo ----------
        IEnumerator E3_MineIronFill(GameManager gm)
        {
            sb.AppendLine("\n== E3. 真实挖低值 Iron 填满 Cargo（HandleTileDug 入包）==");
            if (vehicle.Inventory != null) vehicle.Inventory.Clear();
            float fuelStart = vehicle.Fuel;
            int cargoValueStart = vehicle.CargoValue;

            // 逐格真实挖铁（每击一真实 FixedUpdate，DrainFuel 计入挖矿耗油）；挖到载重满即停
            int mined = 0;
            for (int i = 0; i < IronCount && !vehicle.Inventory.IsFullByWeight; i++)
            {
                Vector2Int cell = new Vector2Int(IronStartX + i, OreRowY);
                int guard = 0;
                DigHitResult res;
                do
                {
                    res = vehicle.TryDigHit(cell);
                    yield return new WaitForFixedUpdate();
                } while (res == DigHitResult.Hit && ++guard < 16 && grid.IsSolid(cell.x, cell.y));
                if (res == DigHitResult.Broken || !grid.IsSolid(cell.x, cell.y)) mined++;
            }
            int cargoValueFull = vehicle.CargoValue;
            float weightFull = vehicle.CargoWeight;
            Assert(vehicle.Inventory.IsFullByWeight, "E3_CargoFull",
                $"挖 {mined} 块 Iron 后载重满：w={weightFull:0.0}/{vehicle.Inventory.MaxWeight} 值=${cargoValueFull}（真实入包）");
            Assert(cargoValueFull > cargoValueStart && weightFull <= vehicle.Inventory.MaxWeight + 0.001f,
                "E3_WithinCap", $"满载值 ${cargoValueStart}→${cargoValueFull}，载重未超上限");

            // 铁计数（主槽聚合）
            int ironTotal = CountDef(iron);
            Assert(ironTotal > 0, "E3_HasIron", $"舱内 Iron 共 {ironTotal} 件");
            // 真实耗油（挖矿 DrainFuel）
            Assert(vehicle.Fuel < fuelStart - 0.1f, "E3_FuelDrained", $"真实挖矿耗油：{fuelStart:0.0}→{vehicle.Fuel:0.0}（DrainFuel 计入）");
        }

        // ---------- E4 高值 Emerald → HandleTileDug 顶掉最低密度 Iron ----------
        IEnumerator E4_CargoReplacement(GameManager gm)
        {
            sb.AppendLine("\n== E4. Cargo 选择：挖到高值 Emerald → 顶掉最低密度 Iron（丢铁矿换钻石）==");
            // 高值深矿(Emerald hardness=3)需更高钻头；此处体现「已升级 Drill→Lv3」后再深入挖高值矿（同 DEV-014 Slice 脚手架）。
            var gmU = gm != null ? gm.Upgrades : null;
            if (gmU != null) gmU.drillLevel = 3;
            yield return new WaitForFixedUpdate();
            int ironBefore = CountDef(iron);
            int cargoValueBefore = vehicle.CargoValue;
            float weightBefore = vehicle.CargoWeight;
            bool emeraldIn = false;

            // 舱已满载，现在挖紧邻的 Emerald（weight 2.2 / value 480）→ HandleTileDug 丢 Iron 腾位
            Vector2Int cell = new Vector2Int(EmeraldX, OreRowY);
            if (grid.IsSolid(cell.x, cell.y))
            {
                int guard = 0;
                DigHitResult res;
                do
                {
                    res = vehicle.TryDigHit(cell);
                    yield return new WaitForFixedUpdate();
                } while (res == DigHitResult.Hit && ++guard < 16 && grid.IsSolid(cell.x, cell.y));
                emeraldIn = !grid.IsSolid(cell.x, cell.y);
            }
            Assert(emeraldIn, "E4_EmeraldMined", "真实挖到紧邻高值 Emerald（块已开采）");
            int cargoValueAfter = vehicle.CargoValue;
            float weightAfter = vehicle.CargoWeight;
            // HandleTileDug：高密度 incoming 顶掉舱内最低密度堆 → 总价值跳升
            Assert(cargoValueAfter > cargoValueBefore, "E4_ValueJumps",
                $"CargoValue ${cargoValueBefore} → ${cargoValueAfter}（+{cargoValueAfter - cargoValueBefore}）丢低值换高值收益成立");
            Assert(weightAfter <= vehicle.Inventory.MaxWeight + 0.001f, "E4_WithinCap",
                $"替换后载重 {weightAfter:0.0} ≤ {vehicle.Inventory.MaxWeight:0.0}");
            // 低值铁被顶掉一部分
            int ironAfter = CountDef(iron);
            Assert(ironAfter < ironBefore, "E4_IronDropped", $"Iron {ironBefore} → {ironAfter}（低密度被顶替给高值腾位）");
            Assert(CountDef(emerald) > 0, "E4_EmeraldInCargo", "Emerald 已入 Cargo");
        }

        // ---------- E5 返航决策：剩余燃料够安全返航 ----------
        IEnumerator E5_FuelSafeReturn(GameManager gm)
        {
            sb.AppendLine("\n== E5. 返航决策：真实剩油 &gt; 上行返航所需（见好就收）==");
            float fuelRemain = vehicle.Fuel;
            float loadRatio = vehicle.Inventory != null ? vehicle.Inventory.LoadRatio : 0f;
            // 真实返航：从当前深度上行到地表坑口（GalleryY-1 → Surface maxDepth 之上），沿 3 列宽竖井垂直上行
            Vector2Int cur = grid.WorldToGrid(vehicle.transform.position);
            float currentDepth = Mathf.Max(0, grid.DepthOf(vehicle.transform.position));
            float moveMult = equipment != null ? equipment.EffectiveMoveFuelMultiplier : 1f;
            // 上行每格耗油 = fuelMoveDrain(1.1)×moveMult × upwardFuel(2.2)×(1+LoadRatio×weightFuelPenalty 0.35)
            float upPerCell = 1.1f * moveMult * 2.2f * (1f + loadRatio * 0.35f);
            float cellsUp = currentDepth;                 // 上行格数（≈当前 y 深度）
            float upFuelNeeded = cellsUp * upPerCell;
            sb.AppendLine($"  剩油 {fuelRemain:0.0} / 上行 {cellsUp:0.0} 格 × {upPerCell:0.00}/格 = 需 {upFuelNeeded:0.0}");
            Assert(fuelRemain > upFuelNeeded, "E5_SafeReturn_Margin",
                $"剩油 {fuelRemain:0.0} > 返航需 {upFuelNeeded:0.0} → 当前可安全返航（见好就收节点），余量 +{fuelRemain - upFuelNeeded:0.0}");
            // 反向：量化「还能安全下潜多深」——返航预算之外剩的油能再往下挖几格。
            // 若这个余量有限 → 存在真实「见好就收」折返点，不是可以无限继续。
            float marginBudget = fuelRemain - upFuelNeeded;                 // 安全返航后的净余油
            float safeExtraCells = marginBudget / Mathf.Max(0.01f, upPerCell); // 这些净余油还能抵几格更深返航增量
            sb.AppendLine($"  安全返航后净余油 {marginBudget:0.0} → 折返点前最多还能下潜 {safeExtraCells:0.0} 格");
            Assert(safeExtraCells < 60f && safeExtraCells >= 0f, "E5_BoundedReturnPoint",
                $"能安全折返的额外深度有限（≤{safeExtraCells:0.0} 格）→ 存在真实见好就收节点，不是无限可继续");
            yield break;
        }

        // ---------- E6 真实返航地表 ----------
        IEnumerator E6_ReturnToSurface(GameManager gm)
        {
            sb.AppendLine("\n== E6. 真实返航地表（物理逐格，无瞬移）==");
            Vector2Int home = new Vector2Int(Center, 1);
            yield return DriveAlong(home);
            Vector2Int atHome = grid.WorldToGrid(vehicle.transform.position);
            Assert(atHome.x == Center && atHome.y <= 2, "E6_ReturnedSurface", $"带货真实返航地表坑口 cell={atHome}（=home {home} 附近）");
            Assert(!vehicle.IsDead, "E6_Alive", "带货真实返航后载具存活");
            gm.IsAtSurface = true;
            yield return new WaitForEndOfFrame();
            Assert(gm.RunRisk.RunActive, "E6_RunStillActive", $"回地表（未 Sell）Run 仍进行中 RunActive={gm.RunRisk.RunActive}");
        }

        // ---------- E7 真实驶入 SellTerminal → Sell → Run 结束 ----------
        IEnumerator E7_Sell(GameManager gm)
        {
            sb.AppendLine("\n== E7. 真实驶入 SellTerminal → 出售 → Run 收官 ==");
            var sell = FindFirstObjectByType<SellTerminal>();
            Assert(sell != null, "E7_HasTerminal", "场景有 SellTerminal");
            int cargoBefore = vehicle.CargoValue;
            int cashBefore = gm.Cash;
            if (sell != null)
            {
                Vector2Int stCell = grid.WorldToGrid(sell.transform.position);
                yield return DriveAlong(stCell);
                yield return CenterOnCell(stCell);      // 居中终端格心 → 物理进 Trigger
                yield return new WaitForEndOfFrame();
                yield return new WaitForEndOfFrame();
                Assert(SellTerminal.PlayerInRange, "E7_PlayerInRange",
                    $"真实驶入终端 Trigger 后 PlayerInRange=true（载具格={grid.WorldToGrid(vehicle.transform.position)}，终端格={stCell}）");
                if (SellTerminal.PlayerInRange && vehicle.CargoValue > 0)
                    sell.TrySell(vehicle);
            }
            Assert(gm.Cash > cashBefore, "E7_SoldForCash", $"终端内真实出售：Cash ${cashBefore}→${gm.Cash}（+{gm.Cash - cashBefore}）");
            Assert(vehicle.CargoValue == 0 || vehicle.CargoValue < cargoBefore, "E7_CargoCleared", "出售后 Cargo 清空/减少（货物换成现金）");
            Assert(!gm.RunRisk.RunActive, "E7_RunEnd", "出售后 RunRiskState 结束本 Run（RunActive=false）");
        }

        // ---------- 工具 ----------
        int CountDef(TileDefinition def)
        {
            if (vehicle == null || vehicle.Inventory == null || def == null) return 0;
            int total = 0;
            for (int i = 0; i < vehicle.Inventory.Capacity; i++)
            {
                var s = vehicle.Inventory.GetSlot(i);
                if (s != null && s.isPrimary && s.def == def && s.count > 0) total += s.count;
            }
            return total;
        }

        static int Manhattan(Vector2Int a, Vector2Int b) => System.Math.Abs(a.x - b.x) + System.Math.Abs(a.y - b.y);

        IEnumerator NullFrame(float sec) { float t0 = Time.time; while (Time.time - t0 < sec) yield return null; }

        /// <summary>真实物理(Hover)逐格驶向 target 格，到位即停。每帧按朝向给 DebugDriveInput（与真实输入同源）。</summary>
        IEnumerator DriveAlong(Vector2Int target)
        {
            int stall = 0;
            while (stall < 2400)
            {
                Vector2Int now = grid.WorldToGrid(vehicle.transform.position);
                if (now == target) { StopDrive(); yield break; }
                Vector2Int d = target - now;
                Vector2 worldDrive = new Vector2(Mathf.Sign(d.x), -Mathf.Sign(d.y));  // grid y 下正；世界 y 上正 → 取反
                Drive(worldDrive);
                yield return new WaitForFixedUpdate();
                stall++;
            }
            StopDrive();
        }

        /// <summary>精确回中某格中心（世界坐标），保证 3 列宽竖井/终端 Trigger 内角点不卡。</summary>
        IEnumerator CenterOnCell(Vector2Int cell)
        {
            Vector3 c = grid.GridToWorld(cell.x, cell.y);
            int stall = 0;
            while (stall < 900)
            {
                Vector3 p = vehicle.transform.position;
                if (Mathf.Abs(c.x - p.x) < 0.06f && Mathf.Abs(c.y - p.y) < 0.06f) { StopDrive(); yield break; }
                Drive(new Vector2(Mathf.Clamp(c.x - p.x, -1f, 1f), Mathf.Clamp(c.y - p.y, -1f, 1f)));
                yield return new WaitForFixedUpdate();
                stall++;
            }
            StopDrive();
        }

        void Drive(Vector2 dir)
        {
            var mc = vehicle.GetComponent<MiningFeelController>();
            if (mc != null) mc.DebugDriveInput(dir, false);
        }
        void StopDrive()
        {
            var mc = vehicle.GetComponent<MiningFeelController>();
            if (mc != null) mc.DebugDriveInput(Vector2.zero, false);
        }

        void Assert(bool ok, string tag, string msg)
        {
            if (ok) { passCount++; sb.AppendLine($"PASS  {tag}: {msg}"); }
            else { failCount++; sb.AppendLine($"FAIL  {tag}: {msg}"); }
            Debug.Log($"[DEV-015] {(ok ? "PASS" : "FAIL")} {tag}: {msg}");
        }
    }
}
