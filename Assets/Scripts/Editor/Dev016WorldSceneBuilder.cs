#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;
using Spine.Unity;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// DEV-016：正式地层世界场景搭建（V1 只铺地层岩色 + 按地层带叠矿）。
    ///
    /// 目标：把 DEV-016 的正式世界生成落到一个**可玩** 48×640 场景：
    ///  - DigGrid 用 TileDatabase_Strata（5 地层：soil_rubble/normal_rock/dense_rock/granite/basalt 岩色）
    ///  - 挂 OreVeinGenerator（veinMode=true → 基底纯地层岩，矿脉按 OreRegionPreset_Strata 5 带叠加）
    ///  - 中央竖井保留区（矿石不在井道/出生区生成），地表有出售终端与能源补给
    ///
    /// 产物：Assets/Scenes/WorldGenV1.unity（**不覆盖 Game.unity**）。
    /// 运行前需先跑「灰烬之下 → DEV-016 搭建：地层基底资产」生成 DB/Preset。
    /// </summary>
    public static class Dev016WorldSceneBuilder
    {
        const string SceneFolder = "Assets/Scenes";
        const string ScenePath = "Assets/Scenes/WorldGenV1.unity";
        const string StrataDbPath = "Assets/Ashfall/Data/TileDatabase_Strata.asset";
        const string StrataPresetPath = "Assets/Ashfall/Data/OreRegionPreset_Strata.asset";

        const int WorldWidth = 48;
        const int WorldDepth = 640;

        [MenuItem("灰烬之下/DEV-016 搭建：正式地层世界 (48×640)")]
        public static void Build()
        {
            // ---- 0. 先确保 canonical 基础资产（Empty/Bedrock/矿/层）存在，再生成地层资产 ----
            AshfallSetup.CreateDefaults();
            Dev016StrataSetup.Build();
            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>(StrataDbPath);
            var preset = AssetDatabase.LoadAssetAtPath<OreRegionPreset>(StrataPresetPath);
            if (db == null || preset == null)
            {
                Debug.LogError("[DEV-016] 地层 DB/Preset 生成失败。请检查 Dev016StrataSetup 输出。");
                return;
            }

            // ---- 1. 场景 ----
            if (!AssetDatabase.IsValidFolder(SceneFolder))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ---- 2. Grid + Tilemap + DigGrid（地层 DB + 竖井保留区）----
            int center = WorldWidth / 2;   // 24
            int shaftHalf = 4;             // 井道半宽
            int spawnX = center;           // 坑口中心

            var gridGo = new GameObject("Grid");
            var ug = gridGo.AddComponent<Grid>();
            ug.cellSize = Vector3.one; ug.cellLayout = GridLayout.CellLayout.Rectangle;

            var tmGo = new GameObject("Tilemap");
            tmGo.transform.SetParent(gridGo.transform);
            var tilemap = tmGo.AddComponent<Tilemap>();
            var tr = tmGo.AddComponent<TilemapRenderer>();
            tr.sortingOrder = 0;

            var digGrid = tmGo.AddComponent<DigGrid>();
            digGrid.tilemap = tilemap;
            digGrid.database = db;
            digGrid.width = WorldWidth;
            digGrid.depth = WorldDepth;
            digGrid.surfaceOpeningHalfWidth = shaftHalf;

            // 矿脉生成器：读地层 preset；竖井 + 地表 Hub 区作为保留区，矿不落井道
            var oreGo = new GameObject("OreVeinGenerator");
            oreGo.transform.SetParent(gridGo.transform);
            var og = oreGo.AddComponent<OreVeinGenerator>();
            og.grid = digGrid;
            og.preset = preset;
            og.seedOverride = -1;   // 跟随 grid.seed
            og.reservedRects = new[]
            {
                new OreReservedRect { label = "Shaft", x = center - shaftHalf - 2, y = 0,
                    w = shaftHalf * 2 + 5, h = 12 },   // 井道出口带（含两侧留白）
            };
            // 挂到 DigGrid 的生成器链（veinMode = true）
            digGrid.oreVeinGenerator = og;

            // ---- 3. 玩家（DEV-018：坑口边缘地表出生，Walk 脚踏实地进入矿口）----
            // 坑口带 = 网格 x[center-halfWidth .. center+halfWidth]（中心24、半宽4 → x20..28），y≤2 挖空。
            // 玩家不悬空出生在坑口正上方（Hover 时代可以；Walk 模式会直接坠落穿洞），而是站到坑口
            // 右缘外第一块**实心**行0 格（网格 x=29）上，能站稳起步、往左一步即走进坑口开口坠落下潜。
            // DEV-018 正式玩法默认 Walk（Motherload 式地面行走 + 重力坠落 + 空格喷气悬浮返航）。
            int surfaceSpawnX = center + shaftHalf + 1;          // 24+4+1=29：坑口右缘外第一块实心行0
            // 出生即站定：行0 顶面世界 y=0，Collider 半径 0.36 → 圆心 y=0.36（不悬空、不坠落，可站稳起步）。
            // 圆心落在网格行 -1（地表空气带），脚下行0 实心 → 往下挖即破土进入矿口。
            Vector3 playerSpawn = new Vector3(surfaceSpawnX + 0.5f, 0.36f, 0f);

            var playerGo = new GameObject("Player");
            playerGo.transform.position = playerSpawn;
            var rb = playerGo.AddComponent<Rigidbody2D>();
            rb.gravityScale = 0f; rb.freezeRotation = true;      // SetMovementMode 会覆写为 Walk 所需重力
            rb.interpolation = RigidbodyInterpolation2D.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            var circle = playerGo.AddComponent<CircleCollider2D>();
            circle.radius = 0.36f;
            var vehicle = playerGo.AddComponent<DrillVehicle>();
            vehicle.grid = digGrid;
            vehicle.startMode = DrillVehicle.MovementMode.Walk;  // DEV-018：地面行走进矿口 + 喷气返航
            vehicle.startJetting = false;                        // 脚踏实地起步，空格开喷气悬浮
            var miningFeel = playerGo.AddComponent<MiningFeelController>();
            miningFeel.grid = digGrid;
            AddPlayerVisual(playerGo, vehicle, miningFeel);

            // ---- 4. 总控（对齐 EconomyBalanceV1Test 权威 GameManager 配置）----
            // DEV-011/DEV-016：补 RunRiskState + DepthRegionProgression + EquipmentProgression + BlockDropHook，
            // 使正式 Run 生命周期闭环成立（离 Hub 自动 BeginRun → 下矿维护风险 → Sell 结算 → Run end）。
            var gmGo = new GameObject("GameManager");
            gmGo.AddComponent<UpgradeSystem>();
            gmGo.AddComponent<EquipmentProgression>();   // DEV-010 装备成长权威（DrillVehicle 无 EP 时回退 UpgradeSystem）
            gmGo.AddComponent<GameHUD>();
            gmGo.AddComponent<InventoryPanel>();
            gmGo.AddComponent<BlockDropHook>();          // DEV-001 通用掉落派发（grid 留空自动 FindFirst<DigGrid>）
            var gm = gmGo.AddComponent<GameManager>();
            // RunRisk 状态机（同体，字段留空 → Start 自动 GetComponent GameManager/DRP/Player）
            gmGo.AddComponent<RunRiskState>();
            // 深度/区域权威：必须显式拖 grid（DRP 挂 GameManager 同体时 GetComponent<DigGrid>() 为 null，
            // 不接线则深度/风险结算失效；vehicle 走 FindFirst 自动找）
            var drp = gmGo.AddComponent<DepthRegionProgression>();
            drp.grid = digGrid;
            gm.spawnPoint = playerSpawn;              // DEV-018：重生点=坑口右缘地表（与出生一致）
            gm.usePlayerStartAsSpawn = true;
            gm.startingCash = 100;

            // ---- 5. 地表基地：返航判定 + 出售 + 燃料 ----
            // 整个地表活动带统一维护 IsAtSurface。否则玩家离开中央登陆区去 Sell/Fuel 时会在地表误开 Run。
            var hubZoneGo = new GameObject("SurfaceHubZone");
            hubZoneGo.transform.position = new Vector3(center, -1f, 0f);
            var hubZoneCollider = hubZoneGo.AddComponent<BoxCollider2D>();
            hubZoneCollider.isTrigger = true;
            hubZoneCollider.size = new Vector2(WorldWidth, 5f); // y = 1.5 .. -3.5；下到深度 3 后才离开地表
            hubZoneGo.AddComponent<SurfaceHubZone>();

            var surfaceGo = new GameObject("SurfaceBase");
            surfaceGo.transform.position = new Vector3(spawnX, -0.5f, 0f);
            var sb = surfaceGo.AddComponent<BoxCollider2D>();
            sb.isTrigger = true; sb.size = new Vector2(9f, 3f);
            var surfaceBase = surfaceGo.AddComponent<SurfaceBase>();
            surfaceBase.autoSellCargo = false;
            surfaceBase.enableAutoService = false;

            // 出售终端（地表 x 偏左）
            var sellGo = new GameObject("SellTerminal");
            sellGo.transform.position = new Vector3(spawnX - 8f, 1.5f, 0f);
            var sbc = sellGo.AddComponent<BoxCollider2D>();
            sbc.isTrigger = true; sbc.size = new Vector2(2f, 2f);
            sellGo.AddComponent<SellTerminal>();

            // 燃料补给（地表 x 偏右）
            var fuelGo = new GameObject("FuelStation");
            fuelGo.transform.position = new Vector3(spawnX + 8f, 1.5f, 0f);
            var fbc = fuelGo.AddComponent<BoxCollider2D>();
            fbc.isTrigger = true; fbc.size = new Vector2(2f, 2f);
            fuelGo.AddComponent<FuelStation>();

            // ---- 6. 相机 ----
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = 14f;
            cam.transform.position = new Vector3(surfaceSpawnX + 0.5f, 1f, -10f);   // DEV-018：开局对准坑口右缘出生点
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.08f);
            var follow = camGo.AddComponent<CameraFollow>();
            follow.target = playerGo.transform;
            follow.orthoSize = 14f; follow.maxOrthoSize = 18f;

            // ---- 7. 保存 ----
            EditorSceneManager.SaveScene(scene, ScenePath);
            var buildScenes = EditorBuildSettings.scenes;
            if (!Array.Exists(buildScenes, entry => entry.path == ScenePath))
            {
                Array.Resize(ref buildScenes, buildScenes.Length + 1);
                buildScenes[buildScenes.Length - 1] = new EditorBuildSettingsScene(ScenePath, true);
                EditorBuildSettings.scenes = buildScenes;
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeGameObject = playerGo;

            Debug.Log("[DEV-016] 正式地层世界已搭建：Assets/Scenes/WorldGenV1.unity\n" +
                      $"  48×{WorldDepth} · 5 地层岩色（soil/normal/dense/granite/basalt）· 竖井保留区 中央 x={spawnX}\n" +
                      "  挂 OreVeinGenerator + OreRegionPreset_Strata（按地层带叠矿）。点 Play 可下潜挖矿。\n" +
                      "  （未覆盖 Game.unity）");
        }

        static void AddPlayerVisual(GameObject playerGo, DrillVehicle vehicle, MiningFeelController miningFeel)
        {
            const string skeletonPath = "Assets/Art/Characters/Player/Spine/PlayerMiner_SkeletonData.asset";
            var skeletonData = AssetDatabase.LoadAssetAtPath<SkeletonDataAsset>(skeletonPath);
            if (skeletonData == null)
            {
                playerGo.AddComponent<SpriteRenderer>();
                playerGo.AddComponent<PlayerVisual>();
                Debug.LogWarning($"[DEV-016] 未找到玩家 Spine 资产，暂用圆点占位：{skeletonPath}");
                return;
            }

            var visualGo = new GameObject("PlayerSpineVisual");
            visualGo.transform.SetParent(playerGo.transform, false);
            visualGo.transform.localPosition = new Vector3(0f, -0.35f, 0f);
            visualGo.transform.localScale = Vector3.one * 0.45f;

            var skeleton = SkeletonAnimation.AddToGameObject(visualGo, skeletonData);
            skeleton.loop = true;
            skeleton.AnimationName = PlayerSpineVisual.IdleAnimation;
            skeleton.GetComponent<MeshRenderer>().sortingOrder = 10;
            var driver = visualGo.AddComponent<PlayerSpineVisual>();
            driver.vehicle = vehicle;
            driver.mining = miningFeel;
        }
    }
}
#endif
