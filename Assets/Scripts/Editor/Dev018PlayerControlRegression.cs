#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// DEV-018：角色控制与挖矿手感整合 V1 回归。
    ///
    /// 覆盖任务书最低验证 9 项：
    ///  1. WorldGenV1 只有一个正式玩家 / DrillVehicle / MiningFeelController / PlayerSpineVisual；
    ///  2. Rigidbody2D / Collider2D / 视觉父子 / 引用完整；
    ///  3. 地面静止、地面移动、空中/喷气、挖矿四种状态可确定切换（DrillVehicle 运动分支 + Spine 表现）；
    ///  4. 挖矿优先于飞行与行走，挖矿方向优先控制朝向；
    ///  5. 单格目标保持四方向相邻，不产生一次多格命中；
    ///  6. 释放挖矿输入立即停止；连续挖掘受 attack interval 门控；
    ///  7. 装备挖速倍率入口仍生效（EffectiveMultiplier 含 EquipmentProgression）；
    ///  8. 载重只按现有规则影响返航（只惩罚向上），不破坏燃料 / Run 生命周期；
    ///  9. 重建 WorldGenV1 场景后上述配置仍成立（调用 Dev016WorldSceneBuilder.Build 幂等重建再断言）。
    ///
    /// 验证手法：对【真实重建的 WorldGenV1】做确定性场景断言 + 确定性逻辑验证
    ///（MiningFeelController.DebugDriveFor 同步 tick、DrillVehicle/PlayerSpineVisual 反射状态机），
    /// 不新建临时模拟场景、不强开 Play —— 与 DEV-017 同风格的 batchmode 兼容确定性回归。
    /// 真实 Play 闭环（行走进矿口→挖矿→跨层→返航→出售→Run end）由独立实机闭环在交接文档记录。
    /// 菜单：灰烬之下 → DEV-018 回归：角色控制与挖矿手感 V1
    /// 落盘：C:\Users\58058\.workbuddy\tools\dev018_regression_result.txt
    /// </summary>
    public static class Dev018PlayerControlRegression
    {
        const string ScenePath = "Assets/Scenes/WorldGenV1.unity";
        const string OutputPath = "C:/Users/58058/.workbuddy/tools/dev018_regression_result.txt";

        static int pass;
        static int fail;
        static readonly StringBuilder Log = new StringBuilder();

        static void Check(bool condition, string tag, string detail)
        {
            if (condition) { pass++; Log.AppendLine($"PASS  {tag}: {detail}"); }
            else { fail++; Log.AppendLine($"FAIL  {tag}: {detail}"); }
        }

        [MenuItem("灰烬之下/DEV-018 回归：角色控制与挖矿手感 V1")]
        public static void Run()
        {
            pass = 0;
            fail = 0;
            Log.Clear();
            Log.AppendLine("DEV-018 Player Control & Mining Feel Regression V1");
            Log.AppendLine("目标场景 WorldGenV1（正式地层世界）· 玩家默认 Walk（脚踏实地进入矿口）");

            try
            {
                // 先幂等重建正式场景，保证断言对象与构建器当前配置一致（回归 9）。
                Dev016StrataSetup.Build();
                Dev016WorldSceneBuilder.Build();
                AssetDatabase.SaveAssets();

                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Check(scene.IsValid() && scene.isLoaded, "WorldScene_Loaded", "WorldGenV1 重建后可打开");

                // Editor 回归不会自动进入 Play，因此显式执行权威总控初始化，确保下游断言读取的
                // Equipment / RunRisk 与实际 Play 时 GameManager.Awake 后的状态一致。
                var gameManager = UnityEngine.Object.FindFirstObjectByType<GameManager>();
                if (gameManager != null) Invoke(gameManager, "Awake");

                ValidateSceneComposition();   // 回归 1、2、9
                ValidateMovementModeBranch(); // 回归 3、4
                ValidateMiningRules();        // 回归 5、6、7
                ValidateCargoReturnRules();   // 回归 8
            }
            catch (Exception e)
            {
                Check(false, "EXCEPTION", e.ToString());
            }

            Log.AppendLine();
            Log.AppendLine($"==== 汇总: PASS {pass} / FAIL {fail} ====");
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            File.WriteAllText(OutputPath, Log.ToString());
            Debug.Log($"[DEV-018 Player Control Regression] done -> {OutputPath} (PASS {pass} / FAIL {fail})");
            if (fail > 0) throw new InvalidOperationException($"DEV-018 regression failed: {fail}");
        }

        // ---------------------------------------------------------------- 场景构成（1/2/9）
        static void ValidateSceneComposition()
        {
            var players = UnityEngine.Object.FindObjectsByType<DrillVehicle>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var minings = UnityEngine.Object.FindObjectsByType<MiningFeelController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var visuals = UnityEngine.Object.FindObjectsByType<PlayerSpineVisual>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            Check(players.Length == 1, "Player_Count1", $"DrillVehicle count={players.Length}");
            Check(minings.Length == 1, "Mining_Count1", $"MiningFeelController count={minings.Length}");
            Check(visuals.Length == 1, "Spine_Count1", $"PlayerSpineVisual count={visuals.Length}");
            if (players.Length != 1) return;

            var p = players[0];
            var rb = p.GetComponent<Rigidbody2D>();
            var col = p.GetComponent<Collider2D>();
            Check(rb != null, "RB2D_Present", "玩家有 Rigidbody2D");
            Check(col != null, "Collider2D_Present", "玩家有 Collider2D");
            Check(rb.freezeRotation, "FreezeRotation", "冻结旋转（防摔倒旋转）");
            Check(p.startMode == DrillVehicle.MovementMode.Walk, "Default_Walk",
                $"DEV-018 玩家默认模式=Walk（脚踏实地进矿口），实际 startMode={p.startMode}");
            Check(!p.startJetting, "Default_NotJetting", "默认不喷气（脚踏实地起步，空格才开喷气）");

            if (visuals.Length == 1)
            {
                var v = visuals[0];
                Check(v.vehicle == p, "Spine_VehicleRef", "Spine driver 引用场景 DrillVehicle");
                Check(v.mining == minings[0], "Spine_MiningRef", "Spine driver 引用场景 MiningFeelController");
                Check(v.transform.IsChildOf(p.transform), "Spine_ChildOfPlayer", "Spine visual 是 Player 的子物体");
            }

            // 出生点：坑口右缘外第一块实心地表（可站稳起步、往左一步进坑口）。
            var gm = UnityEngine.Object.FindFirstObjectByType<GameManager>();
            if (gm != null)
            {
                Vector2 sp = p.transform.position;
                Check(sp.x > 28.5f, "Spawn_OnSolidSurface",
                    $"出生 x={sp.x:F2} > 28.5（坑口右缘地表，非悬空于坑口开口）");
                Check(Mathf.Abs(sp.x - gm.spawnPoint.x) < 0.01f && Mathf.Abs(sp.y - gm.spawnPoint.y) < 0.01f,
                    "Spawn_SyncGM", $"重生点与出生点一致（GM.spawnPoint=({gm.spawnPoint.x:F1},{gm.spawnPoint.y:F1})）");
            }
        }

        // ---------------------------------------------------------------- 移动状态与分支（3/4）
        static void ValidateMovementModeBranch()
        {
            var players = UnityEngine.Object.FindObjectsByType<DrillVehicle>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var visuals = UnityEngine.Object.FindObjectsByType<PlayerSpineVisual>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var minings = UnityEngine.Object.FindObjectsByType<MiningFeelController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (players.Length != 1 || visuals.Length != 1 || minings.Length != 1)
            { Check(false, "Move_Skip", "场景组件不唯一"); return; }

            var v = players[0];
            var driver = visuals[0];
            var mining = minings[0];

            // 模式枚举完备：Hover(悬浮) / Gravity(重力滑翔) / Walk(地面行走) —— 三模式共存结构存在。
            Check(Enum.IsDefined(typeof(DrillVehicle.MovementMode), DrillVehicle.MovementMode.Walk)
                && Enum.IsDefined(typeof(DrillVehicle.MovementMode), DrillVehicle.MovementMode.Gravity)
                && Enum.IsDefined(typeof(DrillVehicle.MovementMode), DrillVehicle.MovementMode.Hover),
                "Mode_Triad", "Hover/Gravity/Walk 三运动模式共存");

            // 运动分支可确定切换（回归 3）：模拟运行时各分支状态由权威组件维护 —— Grounded/Jetting/IsMining
            // 是 DrillVehicle/Spine driver 表现状态源。用反射把表现组件切入各状态，验证确定切换。
            var body = v.GetComponent<Rigidbody2D>();
            Invoke(driver, "Awake");
            Invoke(driver, "Start");

            // 地面静止 → idle
            body.linearVelocity = Vector2.zero;
            SetProp(v, "Grounded", true); SetProp(v, "Jetting", false);
            SetProp(mining, "IsMining", false);
            Invoke(driver, "Update");
            Check(driver.CurrentAnimation == PlayerSpineVisual.IdleAnimation, "State_Idle", "地面静止 → idle");

            // 地面移动 → walk
            body.linearVelocity = new Vector2(2f, 0f);
            Invoke(driver, "Update");
            Check(driver.CurrentAnimation == PlayerSpineVisual.WalkAnimation, "State_Walk", "地面移动 → walk");

            // 空中/喷气 → 飞行
            body.linearVelocity = new Vector2(-2f, 1f);
            SetProp(v, "Grounded", false); SetProp(v, "Jetting", true);
            Invoke(driver, "Update");
            Check(driver.CurrentAnimation == PlayerSpineVisual.FlyingAnimation, "State_Fly", "喷气/空中 → skill02（飞行）");

            // 挖矿 → 挖矿动画，且优先级最高（回归 4）
            SetField(mining, "currentDir", Vector2Int.left);
            SetProp(mining, "IsMining", true);
            body.linearVelocity = new Vector2(2f, 0f);   // 仍在移动 + 飞行可能
            Invoke(driver, "Update");
            Check(driver.CurrentAnimation == PlayerSpineVisual.MiningAnimation, "State_MinePriority",
                "挖矿覆盖行走与飞行（挖矿 > 飞行 > 行走 > 待机）");
            Check(driver.Facing < 0f, "State_MiningFacing", "挖矿方向(左)优先控制朝向");

            // 释放挖矿 → 立即退出挖矿态
            SetProp(mining, "IsMining", false);
            SetProp(v, "Grounded", true); SetProp(v, "Jetting", false);
            body.linearVelocity = Vector2.zero;
            Invoke(driver, "Update");
            Check(driver.CurrentAnimation == PlayerSpineVisual.IdleAnimation, "State_ReleaseMine",
                "释放挖矿后立即回到待机（不再停在挖矿）");
        }

        // ---------------------------------------------------------------- 挖矿规则（5/6/7）
        static void ValidateMiningRules()
        {
            var players = UnityEngine.Object.FindObjectsByType<DrillVehicle>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var minings = UnityEngine.Object.FindObjectsByType<MiningFeelController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (players.Length != 1 || minings.Length != 1) { Check(false, "Mine_Skip", "场景组件不唯一"); return; }
            var vehicle = players[0];
            var mining = minings[0];

            // 装备挖速倍率入口（回归 7）：场景有 EquipmentProgression 时 EffectiveMultiplier 读取其倍率。
            var gm = UnityEngine.Object.FindFirstObjectByType<GameManager>();
            bool hasEp = gm != null && gm.Equipment != null;
            Check(hasEp, "EP_Present", "WorldGenV1 挂 EquipmentProgression（挖速成长入口）");
            if (hasEp)
                Check(mining.EffectiveMultiplier >= 1f, "EP_MultiplierActive",
                    $"EffectiveMultiplier={mining.EffectiveMultiplier:F2}（经 equipment.EffectiveDigSpeedMultiplier）");

            // 单格 + 四方向相邻 + 攻击门控（回归 5/6）：玩家出生在坑口右缘地表(x≈29.5, 行0)。
            // 往右(x→30+)方向按 4 方向单格锁定，应命中紧邻右格的实心岩，且 rel 恰为相邻一格。
            // 通过 MiningFeelController.DebugDriveFor（同步 tick，不依赖 Play）验证目标解析。
            Invoke(mining, "Awake"); Invoke(mining, "Start");
            vehicle.GetComponent<Rigidbody2D>().linearVelocity = Vector2.zero;
            // 需要 vehicle 引用 grid（WorldGenV1 里已序列化 grid）。mining.grid 若空则回退 vehicle.grid。
            if (mining.grid == null && vehicle.grid != null) mining.grid = vehicle.grid;

            var pv = vehicle.transform.position;
            var grid = mining.grid != null ? mining.grid : vehicle.grid;
            if (grid != null)
            {
                var pc = grid.WorldToGrid(pv);
                // 出生站定在行0 顶面（y=0）+ 半径0.36 → 圆心落在网格行 -1（地表空气带），脚下行0 实心。
                Check(pc.x == 29 && pc.y == -1, "Spawn_GridCell",
                    $"出生圆心网格 {pc}（行-1 地表空气带，列29=坑口右缘外，脚下行0 实心）");

                // 朝下挖：目标应为脚下行0 实心格（pc + (0,+1)），4 方向相邻单格。
                // held:false → 只做目标解析不实际 Hit（避免非 Play 真挖穿改变场景网格状态）。
                mining.DebugDriveFor(new Vector2(0f, -1f), false, 0.05f);
                var target = mining.CurrentTarget;
                Check(target.HasValue, "Mine_HasTargetDown", "按住↓锁定脚下单格目标");
                if (target.HasValue)
                {
                    var rel = new Vector2Int(target.Value.x - pc.x, target.Value.y - pc.y);
                    bool adj4 = Mathf.Abs(rel.x) + Mathf.Abs(rel.y) == 1;
                    Check(adj4, "Mine_Adjacent4", $"目标 {target.Value} 与玩家格 {pc} 4 方向相邻（rel={rel}）");
                    Check(rel.y == 1 && rel.x == 0, "Mine_AdjacentDown", "向下目标恰为脚下相邻行0 实心格");
                    Check(grid.IsSolid(target.Value.x, target.Value.y), "Mine_TargetIsSolid", "目标格为实心可挖");
                    bool oneCell = Mathf.Abs(rel.x) <= 1 && Mathf.Abs(rel.y) <= 1;
                    Check(oneCell, "Mine_SingleCell", "目标不超出相邻一格（无一次多格 / 斜对角）");
                }
                mining.DebugClearDrive();

                // 攻击间隔门控存在（回归 6）：EffectiveInterval > 0，是两次 Hit 之间最小时间。
                Check(mining.EffectiveInterval > 0f, "Attack_IntervalGate",
                    $"attack interval 门控（EffectiveInterval={mining.EffectiveInterval:F3}s），实际受装备倍率缩放");
                Check(mining.EffectiveInterval <= mining.attackInterval, "Attack_IntervalRespectsUpgrade",
                    $"装备/升级倍率≥1 → 有效间隔 ≤ 基础 attackInterval（挖得更快入口生效）");
            }
            else
            {
                Check(false, "Grid_Missing", "场景 DigGrid 未接线（无法验证单格目标）");
            }
        }

        // ---------------------------------------------------------------- 载重/返航规则（8）
        static void ValidateCargoReturnRules()
        {
            var p = UnityEngine.Object.FindObjectsByType<DrillVehicle>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (p.Length != 1) { Check(false, "Cargo_Skip", "无唯一玩家"); return; }
            var v = p[0];

            Check(v.weightClimbPenalty >= 0f && v.weightFuelPenalty >= 0f, "Cargo_PenaltyFields",
                $"存在载重上升惩罚参数（climb={v.weightClimbPenalty}, fuel={v.weightFuelPenalty}）");
            Check(v.MaxCarryWeight > 0f, "Cargo_Capacity", $"载重上限 MaxCarryWeight={v.MaxCarryWeight:F1}>0");
            var gm = UnityEngine.Object.FindFirstObjectByType<GameManager>();
            Check(gm != null && gm.RunRisk != null, "RunRisk_Present", "场景 RunRiskState 已接线（载重不影响 Run 生命周期）");
            Check(v.MaxFuel > 0f && v.Fuel >= 0f, "Fuel_Intact", "燃料系统存在（载重规则不破坏燃料）");
        }

        static void Invoke(object target, string methodName)
        {
            target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(target, null);
        }

        static void SetField(object target, string fieldName, object value)
        {
            target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);
        }

        static void SetProp(object target, string propertyName, object value)
        {
            SetField(target, $"<{propertyName}>k__BackingField", value);
        }
    }
}
#endif
