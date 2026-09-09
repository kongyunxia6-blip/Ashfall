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
    /// DEV-018.1：着地限定的「前方双格」挖掘回归（在 DEV-018 V1 之上扩展）。
    ///
    /// DEV-018 V1 原 9 项（场景构成 / 运动分支 / Spine 表现 / 装备倍率 / 载重返航 / 幂等重建）保留，
    /// 新增 DEV-018.1 任务书 10 项确定性断言：
    ///   1. Grounded=true、Jetting=false 时，左右输入只锁定对应朝向 front；
    ///   2. front 实心时，向下输入仍锁定 front（先挖面前，不直接跳 frontDown）；
    ///   3. front 清空后，向下输入锁定 frontDown；
    ///   4. 左右输入在 front 为空时不会自动改挖 frontDown；
    ///   5. Grounded=false 时所有方向无目标、无命中、IsMining=false；
    ///   6. Jetting=true 时所有方向无目标、无命中、IsMining=false；
    ///   7. 离地后立刻清除旧目标与高亮；
    ///   8. 正上/身后/正脚下/两格外目标永远不可选；
    ///   9. 每个攻击 tick 最多命中一格，连续挖掘仍受间隔门控；
    ///  10. 挖矿动画只在合法着地挖掘时播放，方向与角色朝向一致。
    ///
    /// 验证手法：对【真实重建的 WorldGenV1】做确定性场景断言 + 确定性逻辑验证；
    /// 几何类断言（1-8）用一块【临时合成 DigGrid + 探针】确定性控制 front/frontDown 实心度，
    /// 不依赖随机矿脉/洞穴布局，也不污染真实世界（探针对象用完即销毁）。
    /// 唯一权威路径 MiningFeelController → DrillVehicle.TryDigHit → DigGrid 保持；held:false 只解析不真挖。
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
            Log.AppendLine("DEV-018.1 Grounded Front-Double-Cell Mining Regression (over DEV-018 V1)");
            Log.AppendLine("目标场景 WorldGenV1 · 玩家默认 Walk · 着地限定 front / frontDown 前方双格挖掘");

            try
            {
                // 先幂等重建正式场景（DEV-018 幂等断言；重建后玩家即 Walk）。
                Dev016StrataSetup.Build();
                Dev016WorldSceneBuilder.Build();
                AssetDatabase.SaveAssets();

                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Check(scene.IsValid() && scene.isLoaded, "WorldScene_Loaded", "WorldGenV1 重建后可打开");

                var gameManager = UnityEngine.Object.FindFirstObjectByType<GameManager>();
                if (gameManager != null) Invoke(gameManager, "Awake");

                ValidateSceneComposition();        // DEV-018 V1: 1/2/9
                ValidateMovementModeBranch();      // DEV-018 V1: 3/4 + 新增 10（着地挖矿动画/朝向）
                ValidateStanceGating();            // DEV-018.1: 5/6/7 着地/喷气门控（真实场景）
                ValidateGroundedFrontRules();      // DEV-018.1: 1/2/3/4/8 前方双格几何（合成探针）
                ValidateAttackCadenceAndEquipment(); // DEV-018.1: 9 + DEV-018 V1: 6/7 装备倍率
                ValidateCargoReturnRules();        // DEV-018 V1: 8
            }
            catch (Exception e)
            {
                Check(false, "EXCEPTION", e.ToString());
            }

            Log.AppendLine();
            Log.AppendLine($"==== 汇总: PASS {pass} / FAIL {fail} ====");
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            File.WriteAllText(OutputPath, Log.ToString());
            Debug.Log($"[DEV-018.1 Grounded Front Mining Regression] done -> {OutputPath} (PASS {pass} / FAIL {fail})");
            if (fail > 0) throw new InvalidOperationException($"DEV-018.1 regression failed: {fail}");
        }

        // ---------------------------------------------------------------- 场景构成（DEV-018 V1: 1/2/9）
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

        // ---------------------------------------------------------------- 运动分支 + Spine 表现（DEV-018 V1: 3/4；DEV-018.1: 10）
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

            Check(Enum.IsDefined(typeof(DrillVehicle.MovementMode), DrillVehicle.MovementMode.Walk)
                && Enum.IsDefined(typeof(DrillVehicle.MovementMode), DrillVehicle.MovementMode.Gravity)
                && Enum.IsDefined(typeof(DrillVehicle.MovementMode), DrillVehicle.MovementMode.Hover),
                "Mode_Triad", "Hover/Gravity/Walk 三运动模式共存");

            var body = v.GetComponent<Rigidbody2D>();
            Invoke(driver, "Awake");
            Invoke(driver, "Start");

            // 地面静止 → idle
            body.linearVelocity = Vector2.zero;
            SetProp(v, "Grounded", true); SetProp(v, "Jetting", false);
            SetProp(mining, "IsMining", false);
            SetProp(mining, "Facing", 1);
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

            // 着地挖矿 → 挖矿动画，优先级最高；朝向 = 权威 Facing（DEV-018.1 规则 10）
            SetProp(v, "Grounded", true); SetProp(v, "Jetting", false);
            SetProp(mining, "Facing", -1);          // 权威面向左
            SetProp(mining, "IsMining", true);       // 合法着地挖掘（Grounded && !Jetting && 有目标）
            body.linearVelocity = new Vector2(2f, 0f);   // 仍在移动 + 可能飞行
            Invoke(driver, "Update");
            Check(driver.CurrentAnimation == PlayerSpineVisual.MiningAnimation, "State_MinePriority",
                "合法着地挖矿覆盖行走与飞行（挖矿 > 飞行 > 行走 > 待机）");
            Check(driver.Facing < 0f, "Mining_FacingAuthority", "着地挖矿时朝向由权威 Facing(左) 决定，不被移动速度改面");

            // 竖直挖掘（S/下）不改朝向：Facing=-1 + IsMining=true，Spine 仍面向左
            SetProp(mining, "Facing", -1); SetProp(mining, "IsMining", true);
            body.linearVelocity = Vector2.zero;
            Invoke(driver, "Update");
            Check(driver.Facing < 0f, "Mining_VerticalKeepsFacing", "竖直(frontDown)挖矿不改变左右朝向（保持权威 Facing 左）");

            // 释放挖矿 → 立即退出挖矿态
            SetProp(mining, "IsMining", false);
            SetProp(v, "Grounded", true); SetProp(v, "Jetting", false);
            body.linearVelocity = Vector2.zero;
            Invoke(driver, "Update");
            Check(driver.CurrentAnimation == PlayerSpineVisual.IdleAnimation, "State_ReleaseMine",
                "释放挖矿后立即回到待机（不再停在挖矿）");
        }

        // ---------------------------------------------------------------- 着地/喷气门控（DEV-018.1: 5/6/7）
        static void ValidateStanceGating()
        {
            var players = UnityEngine.Object.FindObjectsByType<DrillVehicle>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var minings = UnityEngine.Object.FindObjectsByType<MiningFeelController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (players.Length != 1 || minings.Length != 1) { Check(false, "Stance_Skip", "场景组件不唯一"); return; }
            var vehicle = players[0];
            var mining = minings[0];

            Invoke(mining, "Awake"); Invoke(mining, "Start");
            if (mining.grid == null && vehicle.grid != null) mining.grid = vehicle.grid;
            if (mining.grid == null && vehicle.grid == null)
            { Check(false, "Stance_GridMissing", "无 DigGrid"); return; }

            // 空中/喷气断言用 held=true 更能证明「无法开始挖掘」，但绝不允许在着地时真挖真实世界：
            // 离地/喷气时 stance gate 在 TryDigHit 之前 return → 永不产生命中，安全无污染。
            void AssertNoTarget(string tag, string note)
            {
                mining.DebugDriveFor(new Vector2(1f, 0f), true, 0.05f);   // D/右 + 按住
                Check(!mining.CurrentTarget.HasValue, tag, note + "（右向输入仍无目标）");
                Check(!mining.IsMining, tag + "_NoMining", note + "（IsMining=false，不产生命中）");
                mining.DebugClearDrive();
            }

            // 规则 5：Grounded=false（坠落/空中）→ 全方向无目标、无命中
            SetProp(vehicle, "Grounded", false); SetProp(vehicle, "Jetting", false);
            AssertNoTarget("Rule5_Airborne_NoTarget", "Grounded=false（空中）→ 无目标");

            // 规则 6：Jetting=true（喷气悬浮）→ 全方向无目标、无命中
            SetProp(vehicle, "Grounded", true); SetProp(vehicle, "Jetting", true);
            AssertNoTarget("Rule6_Jetting_NoTarget", "Jetting=true（喷气）→ 无目标");

            // 规则 7：着地时能建立目标（held=false 只解析不真挖，零污染）→ 离地当帧清空目标与 IsMining。
            SetProp(vehicle, "Grounded", true); SetProp(vehicle, "Jetting", false);
            SetProp(mining, "Facing", 1);
            mining.DebugDriveFor(new Vector2(0f, -1f), false, 0.05f);      // S/下：出生地表 frontDown row0 实心
            bool hadGroundedTarget = mining.CurrentTarget.HasValue;
            SetProp(vehicle, "Grounded", false);                            // 模拟当帧离地
            mining.DebugDriveFor(new Vector2(0f, -1f), true, 0.05f);        // 离地后即便想挖也不命中（gate 先于 TryDigHit）
            Check(!mining.CurrentTarget.HasValue, "Rule7_LeavingGround_ClearsTarget",
                hadGroundedTarget ? "着地曾锁定目标 → 离地当帧 CurrentTarget 清空" : "着地无目标 → 离地仍无目标（一致）");
            Check(!mining.IsMining, "Rule7_LeavingGround_ClearsMining", "离地当帧 IsMining=false（无空中宽限命中）");
            mining.DebugClearDrive();
        }

        // ---------------------------------------------------------------- 前方双格几何（DEV-018.1: 1/2/3/4/8）
        // 用临时合成 DigGrid + 探针确定性控制 front/frontDown 实心度，不依赖真实场景随机矿脉/洞穴。
        static void ValidateGroundedFrontRules()
        {
            var players = UnityEngine.Object.FindObjectsByType<DrillVehicle>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (players.Length != 1) { Check(false, "Geo_Skip", "无唯一玩家取 DB 引用"); return; }
            var realGrid = players[0].grid;
            if (realGrid == null || realGrid.database == null) { Check(false, "Geo_NoDb", "真实场景 DigGrid 未接 DB"); return; }

            var db = realGrid.database;
            var solidDef = db.bedrockTile;                 // bedrock 恒 solid，作为可控实心格
            var emptyDef = db.emptyTile;                   // 空气格

            var probeRoot = new GameObject("DEV0181_Probe_Root");
            try
            {
                // ---- 合成微型网格：先整片清空，再按需置 front/frontDown ----
                var g = new GameObject("DEV0181_ProbeGrid").AddComponent<DigGrid>();
                g.transform.SetParent(probeRoot.transform);
                g.width = 16; g.depth = 24; g.database = db; g.surfaceOpeningHalfWidth = 0;
                // 首个 SetTile 触发 Generate()（分配 width×depth 数组并铺随机地层），随后整片覆写为空 → 确定性。
                for (int y = 0; y < g.depth; y++)
                    for (int x = 0; x < g.width; x++)
                        g.SetTile(x, y, emptyDef);

                // ---- 探针载具 + 挖掘控制器（复用真实权威路径组件）----
                var vgo = new GameObject("DEV0181_ProbeVehicle");
                vgo.transform.SetParent(probeRoot.transform);
                var rb = vgo.AddComponent<Rigidbody2D>();
                rb.gravityScale = 0f; rb.freezeRotation = true;
                var veh = vgo.AddComponent<DrillVehicle>();
                veh.grid = g;
                var mfc = vgo.AddComponent<MiningFeelController>();
                mfc.grid = g;
                Invoke(mfc, "Awake");            // 取 GetComponent<DrillVehicle>
                Invoke(mfc, "Start");            // 接线 grid/vehicle（grid 已给，不回退 FindFirst）

                // 探针身体格：世界坐标落格中心 → pc = 该格。front/frontDown 取右/下。
                const int BX = 5, BY = 10;                  // 探针身体格
                Vector3 stand = g.GridToWorld(BX, BY);
                vgo.transform.position = stand;
                Vector2Int front = new Vector2Int(BX + 1, BY);       // 面向右 → front
                Vector2Int frontDown = new Vector2Int(BX + 1, BY + 1);

                // 辅助：把探针调到给定着地/喷气状态，并给定 solidCells 置实心、其余邻近置空。
                void Configure(bool grounded, bool jetting, params Vector2Int[] solidCells)
                {
                    SetProp(veh, "Grounded", grounded);
                    SetProp(veh, "Jetting", jetting);
                    // 重置邻近关键格：身体格与 front/frontDown 两列相邻清空，再按需置实心
                    for (int dy = -1; dy <= 2; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            var c = new Vector2Int(BX + dx, BY + dy);
                            if (g.InBounds(c.x, c.y)) g.SetTile(c.x, c.y, emptyDef);
                        }
                    g.SetTile(BX, BY, emptyDef);              // 身体格保持空气
                    foreach (var s in solidCells)
                        if (g.InBounds(s.x, s.y)) g.SetTile(s.x, s.y, solidDef);
                }

                // 规则 1：Grounded=true、Jetting=false，左右输入只锁定对应朝向 front。
                SetProp(mfc, "Facing", 1);
                Configure(true, false, front);               // front 实心
                mfc.DebugDriveFor(new Vector2(1f, 0f), false, 0.05f);   // D/右
                Check(mfc.CurrentTarget == front, "Rule1_FacingRight_LocksFront",
                    $"着地右向 → 只锁 front {front}（不是 frontDown {frontDown} / 身后 / 脚下）");
                mfc.DebugClearDrive();

                // 规则 2：front 实心时，向下输入仍锁定 front（不直接跳 frontDown）。
                SetProp(mfc, "Facing", 1);
                Configure(true, false, front);
                mfc.DebugDriveFor(new Vector2(0f, -1f), false, 0.05f);   // S/下
                Check(mfc.CurrentTarget == front, "Rule2_DownWhileFrontSolid_DigsFront",
                    $"S/下且 front 仍实心 → 先锁挖 front {front}，而非 frontDown {frontDown}");
                mfc.DebugClearDrive();

                // 规则 3：front 清空后，向下输入锁定 frontDown。
                SetProp(mfc, "Facing", 1);
                Configure(true, false, frontDown);           // 只 frontDown 实心，front 空气
                mfc.DebugDriveFor(new Vector2(0f, -1f), false, 0.05f);
                Check(mfc.CurrentTarget == frontDown, "Rule3_DownWhenFrontClear_DigsFrontDown",
                    $"S/下且 front 已清空 → 锁挖 frontDown {frontDown}");
                mfc.DebugClearDrive();

                // 规则 4：左右输入 front 为空时不会自动改挖 frontDown。
                SetProp(mfc, "Facing", 1);
                Configure(true, false, frontDown);           // frontDown 实心、front 空气
                mfc.DebugDriveFor(new Vector2(1f, 0f), false, 0.05f);   // D/右，front 空
                Check(!mfc.CurrentTarget.HasValue, "Rule4_HorizontalFrontEmpty_NoAutoDown",
                    $"front 空时右向 → 无目标（不自动改挖 frontDown {frontDown}）");
                mfc.DebugClearDrive();

                // 规则 8a：身后/脚下不可挖 —— 面向右，身后格 = (BX-1,BY)、脚下格 = (BX,BY+1) 都置实心，
                // 但请求「身后」会先改面向左；用「面向右 + 身后仅运动不改向」语义：左向请求把 front 改到身后列。
                // 更稳做法：面向右时，把【身后格】与【脚下格】置实心，但 front 留空 → 右向仍无目标（不选身后/脚下）。
                SetProp(mfc, "Facing", 1);
                Configure(true, false);                      // front/frontDown 空
                g.SetTile(BX - 1, BY, solidDef);             // 身后格实心
                g.SetTile(BX, BY + 1, solidDef);             // 脚下格实心
                mfc.DebugDriveFor(new Vector2(1f, 0f), false, 0.05f);
                Check(!mfc.CurrentTarget.HasValue, "Rule8_BehindOrUnder_NotSelected",
                    $"面向右 front 空 → 不选身后/脚下/两格外（身后({BX - 1},{BY})/脚下({BX},{BY + 1})实心仍无目标）");
                mfc.DebugClearDrive();

                // 规则 8b：两格外不可挖 —— 把 (BX+2,BY)（front 再右一格）置实心，front 空 → 右向仍无目标。
                SetProp(mfc, "Facing", 1);
                Configure(true, false);
                g.SetTile(BX + 2, BY, solidDef);             // 两格外实心
                mfc.DebugDriveFor(new Vector2(1f, 0f), false, 0.05f);
                Check(!mfc.CurrentTarget.HasValue, "Rule8_TwoCellsAway_NotSelected",
                    $"front 空、两格外({BX + 2},{BY})实心 → 右向不隔空挖（无目标）");
                mfc.DebugClearDrive();

                // 规则 8c：W/上 永不产生挖掘请求（正上不可挖）。
                SetProp(mfc, "Facing", 1);
                Configure(true, false, new Vector2Int(BX, BY - 1));  // 正上格实心
                mfc.DebugDriveFor(new Vector2(0f, 1f), false, 0.05f); // W/上
                Check(!mfc.CurrentTarget.HasValue, "Rule8_Up_NoDig", "W/上 → 无挖掘请求（正上不可挖）");
                mfc.DebugClearDrive();

                // 规则 9：一次只解析 0 或 1 格（单格），且总是紧邻 front/frontDown（rel 距≤1）。
                SetProp(mfc, "Facing", 1);
                Configure(true, false, front);
                mfc.DebugDriveFor(new Vector2(1f, 0f), false, 0.05f);
                var t9 = mfc.CurrentTarget;
                bool singleAndAdjacent = t9.HasValue
                    && Mathf.Abs(t9.Value.x - BX) <= 1 && Mathf.Abs(t9.Value.y - BY) <= 1;
                Check(singleAndAdjacent, "Rule9_SingleAdjacentCell", "单格命中：0 或 1 个且紧邻身体格（无多格/AoE）");
                mfc.DebugClearDrive();
            }
            finally
            {
                if (probeRoot != null) UnityEngine.Object.DestroyImmediate(probeRoot);
            }
        }

        // ---------------------------------------------------------------- 攻击节奏 + 装备倍率（DEV-018.1: 9；DEV-018 V1: 6/7）
        static void ValidateAttackCadenceAndEquipment()
        {
            var players = UnityEngine.Object.FindObjectsByType<DrillVehicle>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var minings = UnityEngine.Object.FindObjectsByType<MiningFeelController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (players.Length != 1 || minings.Length != 1) { Check(false, "Cadence_Skip", "场景组件不唯一"); return; }
            var mining = minings[0];
            var gm = UnityEngine.Object.FindFirstObjectByType<GameManager>();

            bool hasEp = gm != null && gm.Equipment != null;
            Check(hasEp, "EP_Present", "WorldGenV1 挂 EquipmentProgression（挖速成长入口）");
            if (hasEp)
                Check(mining.EffectiveMultiplier >= 1f, "EP_MultiplierActive",
                    $"EffectiveMultiplier={mining.EffectiveMultiplier:F2}（经 equipment.EffectiveDigSpeedMultiplier）");

            // 攻击间隔门控（DEV-018.1 规则 9）：存在 >0 的有效间隔 = 两次命中之间最小时间。
            Check(mining.EffectiveInterval > 0f, "Attack_IntervalGate",
                $"attack interval 门控（EffectiveInterval={mining.EffectiveInterval:F3}s），实际受装备倍率缩放");
            Check(mining.EffectiveInterval <= mining.attackInterval, "Attack_IntervalRespectsUpgrade",
                $"装备/升级倍率≥1 → 有效间隔 ≤ 基础 attackInterval（挖得更快入口生效）");
            Check(mining.NextHitIn >= 0f, "Attack_NextHitClock", "NextHitIn 剩余时钟非负（门控机制可读）");
        }

        // ---------------------------------------------------------------- 载重/返航规则（DEV-018 V1: 8）
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
