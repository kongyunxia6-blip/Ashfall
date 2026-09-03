#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// 一键搭建 DEV-003 单格采矿交互与挖掘手感 V1 验收场景。
    /// 用法：菜单栏 → 灰烬之下 → 搭建 DEV-003 采矿手感测试场景
    ///
    /// 场景内容（在 DEV-002 表现管线基础上新增手感层）：
    ///  - 十字矿脉布局（MiningFeelV1TestLayout 运行时重放）：
    ///    竖井 14 格（首格耐久4，其余耐久1）+ 左臂 4 格铁矿耐久4 + 右臂 3 格普通岩耐久1 + 上方 1 格耐久4
    ///  - 玩家挂 MiningFeelController：4 方向单格锁定 / 按住连挖 / hit-stop / breakExtraPause / 目标描边提示
    ///  - Walk 运动模式（Motherload 脚踏实地）：向下挖穿后靠重力自然落入下一格，
    ///    验证「挖一格 → 消失 → 向下推进 → 再挖下一格」的连续节奏
    ///  - breakDuration = 0.25s：崩碎窗口沿用 DEV-002 表现
    ///  - 背景 Dirt：挖穿后露出
    ///
    /// 操作：方向键选方向 + 按住鼠标左键连挖；H = 目标提示开关。
    /// </summary>
    public static class MiningFeelV1Builder
    {
        const string ScenePath = "Assets/Scenes/MiningFeelV1Test.unity";
        const string SceneFolder = "Assets/Scenes";
        const string DataFolder = "Assets/Ashfall/Data";

        [MenuItem("灰烬之下/搭建 DEV-003 采矿手感测试场景")]
        public static void Build()
        {
            // ---- 0. 确保数据库 + placeholder 视觉资产存在 ----
            AshfallSetup.CreateDefaults();
            BlockVisualProfileBuilder.Build();

            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>($"{DataFolder}/TileDatabase.asset");
            var dirt = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Dirt_泥土.asset");
            if (db == null || dirt == null)
            {
                Debug.LogError("[DEV-003] 必要资产缺失。请先运行 灰烬之下/一键搭建可运行场景");
                return;
            }

            // ---- 1. 新建场景 ----
            if (!AssetDatabase.IsValidFolder(SceneFolder))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // ---- 2. Grid + 前景 Tilemap + 背景 Tilemap ----
            var gridGo = new GameObject("Grid");
            var unityGrid = gridGo.AddComponent<Grid>();
            unityGrid.cellSize = Vector3.one;
            unityGrid.cellGap = Vector3.zero;
            unityGrid.cellLayout = GridLayout.CellLayout.Rectangle;

            var fgGo = new GameObject("Tilemap_Foreground");
            fgGo.transform.SetParent(gridGo.transform);
            var fgTilemap = fgGo.AddComponent<Tilemap>();
            var fgRenderer = fgGo.AddComponent<TilemapRenderer>();
            fgRenderer.sortingOrder = 1;

            var bgGo = new GameObject("Tilemap_Background");
            bgGo.transform.SetParent(gridGo.transform);
            var bgTilemap = bgGo.AddComponent<Tilemap>();
            var bgRenderer = bgGo.AddComponent<TilemapRenderer>();
            bgRenderer.sortingOrder = 0;

            // ---- 3. DigGrid ----
            var digGrid = fgGo.AddComponent<DigGrid>();
            digGrid.tilemap = fgTilemap;
            digGrid.backgroundTilemap = bgTilemap;
            digGrid.database = db;
            digGrid.width = 48;
            digGrid.depth = 60;
            digGrid.useRandomSeed = false;
            digGrid.seed = 20260903;
            digGrid.enableFallingRocks = false;
            digGrid.breakDuration = 0.25f;

            // DEV-003：运行时布局重放（十字矿脉 + 竖井）
            var layout = fgGo.AddComponent<MiningFeelV1TestLayout>();
            layout.grid = digGrid;
            layout.anchorY = 6;
            layout.columnLength = 14;
            layout.leftArmLength = 4;
            layout.rightArmLength = 3;
            layout.ironDigHits = 4;
            layout.rockDigHits = 1;

            digGrid.RegenerateFromDatabase();

            // ---- 4. 背景 Dirt ----
            var bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            bgTex.SetPixel(0, 0, Color.white);
            bgTex.Apply();
            var bgSprite = Sprite.Create(bgTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            bgSprite.name = "DEV003_DirtBg";
            var bgTile = ScriptableObject.CreateInstance<Tile>();
            bgTile.sprite = bgSprite;
            bgTile.name = "DEV003_DirtBg";
            bgTile.flags = TileFlags.None;

            for (int y = 0; y < digGrid.depth; y++)
                for (int x = 0; x < digGrid.width; x++)
                {
                    var cell = new Vector3Int(x, -y, 0);
                    bgTilemap.SetTile(cell, bgTile);
                    bgTilemap.SetTileFlags(cell, TileFlags.None);
                    bgTilemap.SetColor(cell, dirt.color);
                }

            // ---- 5. 玩家（锚定格中心出生，Walk 模式脚踏实地）----
            int cx = digGrid.width / 2;
            Vector3 spawnPos = digGrid.GridToWorld(cx, layout.anchorY);
            var playerGo = new GameObject("Player");
            playerGo.transform.position = spawnPos;

            var rb = playerGo.AddComponent<Rigidbody2D>();
            rb.gravityScale = 0f;
            rb.freezeRotation = true;
            rb.interpolation = RigidbodyInterpolation2D.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;

            var circle = playerGo.AddComponent<CircleCollider2D>();
            circle.radius = 0.36f;

            playerGo.AddComponent<SpriteRenderer>();
            playerGo.AddComponent<PlayerVisual>();

            var vehicle = playerGo.AddComponent<DrillVehicle>();
            vehicle.grid = digGrid;
            vehicle.startMode = DrillVehicle.MovementMode.Walk;   // 脚踏实地：挖穿后靠重力落入下一格
            vehicle.startJetting = false;

            // DEV-003：挖掘手感控制器（单格锁定 / 连挖节奏 / hit-stop / 目标提示）
            var feel = playerGo.AddComponent<MiningFeelController>();
            feel.grid = digGrid;
            feel.attackInterval = 0.28f;
            feel.hitStopDuration = 0.04f;
            feel.recoveryTime = 0.05f;
            feel.breakExtraPause = 0.12f;
            feel.breakHitStopMultiplier = 2f;
            feel.miningSpeedMultiplier = 1f;
            feel.useUpgradeDrillSpeed = true;
            feel.showTargetHighlight = true;

            // ---- 6. 总控 + 表现层 ----
            var gmGo = new GameObject("GameManager");
            gmGo.AddComponent<UpgradeSystem>();
            gmGo.AddComponent<GameHUD>();
            gmGo.AddComponent<InventoryPanel>();

            var gm = gmGo.AddComponent<GameManager>();
            gm.spawnPoint = spawnPos;
            gm.usePlayerStartAsSpawn = true;
            gm.startingCash = 100;

            var hook = gmGo.AddComponent<BlockDropHook>();
            hook.grid = digGrid;

            var visual = gmGo.AddComponent<BlockVisualController>();
            visual.grid = digGrid;
            visual.breakDebrisCount = 6;
            visual.hitDebrisCount = 2;
            visual.hitFlashFrames = 2;

            // DEV-003：验收探针（事件计数 / 每帧命中数 / 间隔测量，供 MCP 实测读取）
            var probe = gmGo.AddComponent<MiningFeelV1Probe>();
            probe.grid = digGrid;
            probe.feel = feel;

            // ---- 7. 相机 ----
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 12f;
            cam.transform.position = new Vector3(spawnPos.x, spawnPos.y, -10f);
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.07f);

            var follow = camGo.AddComponent<CameraFollow>();
            follow.target = playerGo.transform;
            follow.orthoSize = 12f;
            follow.maxOrthoSize = 16f;

            // ---- 8. 保存 + Build Settings ----
            EditorSceneManager.SaveScene(scene, ScenePath);

            var scenes = EditorBuildSettings.scenes;
            bool exists = false;
            foreach (var s in scenes)
                if (s.path == ScenePath) { exists = true; s.enabled = true; }

            if (!exists)
            {
                var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(scenes)
                {
                    new EditorBuildSettingsScene(ScenePath, true)
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

            Debug.Log($"[DEV-003] 采矿手感测试场景已搭建：{ScenePath}\n" +
                      $"布局：竖井 {layout.columnLength} 格（首格耐久4/其余1）+ 左臂 {layout.leftArmLength} 铁矿耐久4 + " +
                      $"右臂 {layout.rightArmLength} 普通岩耐久1 + 上方 1 格耐久4，锚定 ({cx},{layout.anchorY})\n" +
                      "MiningFeelController：attackInterval=0.28s / hitStop=0.04s / recovery=0.05s / breakExtraPause=0.12s\n" +
                      "操作：方向键选方向 + 按住鼠标左键连挖；H = 目标提示开关；Walk 模式挖穿后自然下落");
        }
    }
}
#endif
