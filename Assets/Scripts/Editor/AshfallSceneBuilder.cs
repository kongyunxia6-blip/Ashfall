#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// 一键搭建《灰烬之下》可运行场景。
    /// 用法：菜单栏 → 灰烬之下 → 一键搭建可运行场景 / 搭建 60×40 测试场景
    /// 会生成：Grid/Tilemap（DigGrid）、Player（钻探服）、GameManager、地表基地、相机跟随，
    /// 并把场景加入 Build Settings。重复点击会复用已有物体，不会重复创建。
    ///
    /// 两个菜单项共用 BuildCore：
    ///   - 一键搭建可运行场景  → Assets/Scenes/Game.unity（48×640，正式工程）
    ///   - 搭建 60×40 测试场景  → Assets/Scenes/TestMining.unity（60×40，M2/M3 验证用，不碰 Game.unity）
    /// </summary>
    public static class AshfallSceneBuilder
    {
        const string SceneFolder = "Assets/Scenes";
        const string ScenePath = "Assets/Scenes/Game.unity";
        const string TestScenePath = "Assets/Scenes/TestMining.unity";
        const string WalkScenePath = "Assets/Scenes/WalkTest.unity";

        [MenuItem("灰烬之下/一键搭建可运行场景")]
        public static void Build()
        {
            BuildCore(ScenePath, 48, 640);
        }

        [MenuItem("灰烬之下/搭建 60×40 测试场景")]
        public static void BuildTestScene()
        {
            BuildCore(TestScenePath, 60, 40);
        }

        [MenuItem("灰烬之下/搭建行走测试场景 (Walk 60×40)")]
        public static void BuildWalkScene()
        {
            // Walk 模式验证场：出生点右移 6 格避开坑口，落在实心地表平面上，
            // 一开局即可左右行走测试 Motherload 手感。
            BuildCore(WalkScenePath, 60, 40, DrillVehicle.MovementMode.Walk, 6f);
        }

        /// <summary>
        /// 核心搭建流程。scenePath 决定存盘路径，w/d 决定世界尺寸。
        /// 所有正式场景与测试场景共享同一套物体结构。
        /// startMode = 玩家运动模式（Game.unity 永远 Hover，手感不变）；
        /// spawnShiftX = 出生点相对坑口中心的水平偏移（Walk 场景用来避开坑口站上平地）。
        /// </summary>
        static void BuildCore(string scenePath, int worldWidth, int worldDepth,
                              DrillVehicle.MovementMode startMode = DrillVehicle.MovementMode.Hover,
                              float spawnShiftX = 0f)
        {
            bool isTest = scenePath != ScenePath;

            // ---- 0a. 输入系统 ----
            // DrillVehicle / GameHUD 用的是旧版 UnityEngine.Input（Input.GetKey / GetKeyDown）。
            // 若工程的 Active Input Handling 是 "Input System Package (New)"，这些 API 会在运行时抛异常。
            // 这里强制设为 Both（旧 + 新 都可用）。
            bool inputChanged = EnsureInputHandlingAllowsLegacy();

            // ---- 0b. 先生成方块与分层数据库 ----
            AshfallSetup.CreateDefaults();
            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>(
                "Assets/Ashfall/Data/TileDatabase.asset");

            if (db == null)
            {
                Debug.LogError("[灰烬之下] TileDatabase 生成失败，请先检查 AshfallSetup。");
                return;
            }

            // ---- 1. 新建场景 ----
            if (!AssetDatabase.IsValidFolder(SceneFolder))
                AssetDatabase.CreateFolder("Assets", "Scenes");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // ---- 2. Grid + Tilemap ----
            var gridGo = GameObject.Find("Grid");
            if (gridGo == null) gridGo = new GameObject("Grid");
            var unityGrid = gridGo.GetComponent<Grid>();
            if (unityGrid == null) unityGrid = gridGo.AddComponent<Grid>();
            unityGrid.cellSize = Vector3.one;
            unityGrid.cellGap = Vector3.zero;
            unityGrid.cellLayout = GridLayout.CellLayout.Rectangle;

            var tilemapGo = GameObject.Find("Tilemap");
            if (tilemapGo == null)
            {
                tilemapGo = new GameObject("Tilemap");
                tilemapGo.transform.SetParent(gridGo.transform);
            }

            var tilemap = tilemapGo.GetComponent<Tilemap>();
            if (tilemap == null) tilemap = tilemapGo.AddComponent<Tilemap>();

            var renderer = tilemapGo.GetComponent<TilemapRenderer>();
            if (renderer == null) renderer = tilemapGo.AddComponent<TilemapRenderer>();
            renderer.sortingOrder = 0;

            var digGrid = tilemapGo.GetComponent<DigGrid>();
            if (digGrid == null) digGrid = tilemapGo.AddComponent<DigGrid>();
            digGrid.tilemap = tilemap;
            digGrid.database = db;
            digGrid.width = worldWidth;
            digGrid.depth = worldDepth;

            // ---- 3. 玩家 ----
            float spawnX = digGrid.width * 0.5f + spawnShiftX;   // 坑口中心（+偏移可站上平地）
            var playerGo = GameObject.Find("Player");
            if (playerGo == null) playerGo = new GameObject("Player");
            playerGo.transform.position = new Vector3(spawnX, 1f, 0f);

            var rb = playerGo.GetComponent<Rigidbody2D>();
            if (rb == null) rb = playerGo.AddComponent<Rigidbody2D>();
            rb.gravityScale = 0f;
            rb.freezeRotation = true;
            rb.interpolation = RigidbodyInterpolation2D.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;

            var circle = playerGo.GetComponent<CircleCollider2D>();
            if (circle == null) circle = playerGo.AddComponent<CircleCollider2D>();
            circle.radius = 0.36f;

            var sr = playerGo.GetComponent<SpriteRenderer>();
            if (sr == null) sr = playerGo.AddComponent<SpriteRenderer>();
            if (playerGo.GetComponent<PlayerVisual>() == null)
                playerGo.AddComponent<PlayerVisual>();

            var vehicle = playerGo.GetComponent<DrillVehicle>();
            if (vehicle == null) vehicle = playerGo.AddComponent<DrillVehicle>();
            vehicle.grid = digGrid;
            vehicle.startMode = startMode;
            vehicle.startJetting = false;

            // ---- 4. 总控 ----
            var gmGo = GameObject.Find("GameManager");
            if (gmGo == null) gmGo = new GameObject("GameManager");

            if (gmGo.GetComponent<UpgradeSystem>() == null)
                gmGo.AddComponent<UpgradeSystem>();
            if (gmGo.GetComponent<GameHUD>() == null)
                gmGo.AddComponent<GameHUD>();

            // 格子背包面板（自��� Canvas 与 EventSystem，挂在总控上即可）
            if (gmGo.GetComponent<InventoryPanel>() == null)
                gmGo.AddComponent<InventoryPanel>();

            var gm = gmGo.GetComponent<GameManager>();
            if (gm == null) gm = gmGo.AddComponent<GameManager>();
            gm.spawnPoint = new Vector3(spawnX, 1f, 0f);
            gm.usePlayerStartAsSpawn = true;
            gm.startingCash = 100;

            // ---- 5. 地表基地触发区 ----
            var baseGo = GameObject.Find("SurfaceBase");
            if (baseGo == null) baseGo = new GameObject("SurfaceBase");
            baseGo.transform.position = new Vector3(spawnX, -0.5f, 0f);

            var box = baseGo.GetComponent<BoxCollider2D>();
            if (box == null) box = baseGo.AddComponent<BoxCollider2D>();
            box.isTrigger = true;
            box.size = new Vector2(9f, 3f);

            if (baseGo.GetComponent<SurfaceBase>() == null)
                baseGo.AddComponent<SurfaceBase>();

            // ---- 6. 相机 ----
            var cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera");
                camGo.tag = "MainCamera";
                cam = camGo.AddComponent<Camera>();
            }

            cam.orthographic = true;
            cam.orthographicSize = 12f;
            cam.transform.position = new Vector3(spawnX, 1f, -10f);
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.07f);

            var follow = cam.gameObject.GetComponent<CameraFollow>();
            if (follow == null) follow = cam.gameObject.AddComponent<CameraFollow>();
            follow.target = playerGo.transform;
            follow.orthoSize = 12f;
            follow.maxOrthoSize = 16f;

            // ---- 7. 保存并加入 Build Settings ----
            EditorSceneManager.SaveScene(scene, scenePath);

            var scenes = EditorBuildSettings.scenes;
            bool exists = false;
            foreach (var s in scenes)
                if (s.path == scenePath) { exists = true; s.enabled = true; }

            if (!exists)
            {
                var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(scenes)
                {
                    new EditorBuildSettingsScene(scenePath, true)
                };
                EditorBuildSettings.scenes = list.ToArray();
            }
            else
            {
                EditorBuildSettings.scenes = scenes;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.activeGameObject = playerGo;

            string tip = inputChanged
                ? "\n⚠ 已把 Active Input Handling 改为 Both，请重启 Unity 编辑器后再按 Play。"
                : "";

            if (isTest)
            {
                string modeTip = startMode == DrillVehicle.MovementMode.Walk
                    ? "操作：A/D 左右行走（加速度+摩擦），空格开/关喷气（力驱动惯性），" +
                      "按住方向键+单击左键挖掘。走出边缘会坠落，落地有冲击提示。"
                    : "操作：WASD / 方向键移动，朝方块按住方向键钻探。";
                Debug.Log($"[灰烬之下] 测试场景已搭建：{scenePath}（{worldWidth}×{worldDepth}，{startMode} 模式，未改动 Game.unity）\n" +
                          "直接点 Play 即可运行。" + modeTip + tip);
            }
            else
            {
                Debug.Log("[灰烬之下] 场景已搭建：Assets/Scenes/Game.unity\n" +
                          "直接点 Play 即可运行。操作：WASD / 方向键移动，朝方块按住方向键钻探，" +
                          "回到地表自动卖矿加油，按 U 打开升级商店（1~6 购买）。" + tip);
            }
        }

        /// <summary>
        /// 确保 Active Input Handling 包含旧版 Input Manager。
        /// 0 = 仅旧版，1 = 仅新版 Input System，2 = 两者皆可。
        /// 返回 true 表示本次做出了修改（需要重启编辑器生效）。
        /// </summary>
        static bool EnsureInputHandlingAllowsLegacy()
        {
            const string path = "ProjectSettings/ProjectSettings.asset";

            var assets = AssetDatabase.LoadAllAssetsAtPath(path);
            if (assets == null || assets.Length == 0) return false;

            var so = new SerializedObject(assets[0]);
            var prop = so.FindProperty("activeInputHandler");
            if (prop == null) return false;

            if (prop.intValue == 2 || prop.intValue == 0) return false;

            prop.intValue = 2;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();

            Debug.LogWarning("[灰烬之下] 已将 Active Input Handling 设为 Both（旧版 Input.GetKey 需要）。" +
                             "请重启 Unity 编辑器使其生效。");
            return true;
        }
    }
}
#endif
