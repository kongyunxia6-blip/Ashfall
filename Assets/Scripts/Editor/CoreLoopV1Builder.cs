#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// 一键搭建 DEV-005 核心循环（挖矿 → 返航 → 出售 → 升级 → 再下矿）验收场景。
    /// 用法：菜单栏 → 灰烬之下 → 搭建 DEV-005 核心闭环测试场景
    ///
    /// 场景内容：
    ///  - CoreLoopV1TestLayout 运行时重放：地表带 + 3 批 × (铁/铜/锡) 矿柱，供 3 次循环
    ///  - 玩家 Walk + MiningFeelController（单格挖掘，全程一次只 Hit 1 Block）
    ///  - 地表：SurfaceBase 登陆舱（返航判定 + 自动付费补给，不再自动卖）+ SellTerminal（按 E 交互出售）
    ///  - GameHUD：U 开升级商店、1~6 购买（Engine=挖掘效率 / FuelTank=续航 …）
    ///  - DigGrid.enableFallingRocks=false（确定性）；startingCash=200；升级价 costMultiplier=0.4（短循环可购买）
    ///  - Fuel 消耗在场景组件上调大（不动默认资产/默认参数），让一次短循环能感知能源压力
    ///
    /// 资产：惰性创建 Tin_锡矿（Assets/Ashfall/Data/Tin_锡矿.asset，绕开 AshfallSetup 不污染既有资产）。
    /// </summary>
    public static class CoreLoopV1Builder
    {
        const string ScenePath = "Assets/Scenes/Tests/CoreLoopV1Test.unity";
        const string SceneFolder = "Assets/Scenes";
        const string DataFolder = "Assets/Ashfall/Data";

        [MenuItem("灰烬之下/搭建 DEV-005 核心闭环测试场景")]
        public static void Build()
        {
            // ---- 0. 确保锡矿资产 + 数据库存在 ----
            EnsureTin();
            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>($"{DataFolder}/TileDatabase.asset");
            var iron = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Iron_铁矿.asset");
            var copper = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Copper_铜矿.asset");
            var tin = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Tin_锡矿.asset");
            var dirt = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Dirt_泥土.asset");
            if (db == null || iron == null || copper == null || tin == null || dirt == null)
            {
                Debug.LogError("[DEV-005] 必要资产缺失。请先运行 Tools/Ashfall/一键生成默认方块与数据库");
                return;
            }

            // ---- 1. 新建场景 ----
            if (!AssetDatabase.IsValidFolder(SceneFolder))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // ---- 2. Grid + 前景/背景 Tilemap ----
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
            digGrid.seed = 20260910;
            digGrid.enableFallingRocks = false;   // 旧随机落石关闭，聚焦核心循环确定性
            digGrid.breakDuration = 0.25f;

            // 运行时布局重放
            var layout = fgGo.AddComponent<CoreLoopV1TestLayout>();
            layout.grid = digGrid;
            layout.iron = iron;
            layout.copper = copper;
            layout.tin = tin;
            layout.dirt = dirt;
            layout.spawnX = 10;
            layout.surfaceY = 1;
            layout.groundY = 2;

            digGrid.RegenerateFromDatabase();

            // ---- 4. 背景 Dirt ----
            var bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            bgTex.SetPixel(0, 0, Color.white);
            bgTex.Apply();
            var bgSprite = Sprite.Create(bgTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            bgSprite.name = "DEV005_DirtBg";
            var bgTile = ScriptableObject.CreateInstance<Tile>();
            bgTile.sprite = bgSprite;
            bgTile.name = "DEV005_DirtBg";
            bgTile.flags = TileFlags.None;

            for (int y = 0; y < digGrid.depth; y++)
                for (int x = 0; x < digGrid.width; x++)
                {
                    var cell = new Vector3Int(x, -y - 1, 0);
                    bgTilemap.SetTile(cell, bgTile);
                    bgTilemap.SetTileFlags(cell, TileFlags.None);
                    bgTilemap.SetColor(cell, dirt.color);
                }

            // ---- 5. 玩家（出生在地表带，批1 铜柱上方）----
            var spawnPos = digGrid.GridToWorld(layout.spawnX, layout.surfaceY);

            var playerGo = new GameObject("Player");
            playerGo.transform.position = spawnPos;

            var rb = playerGo.AddComponent<Rigidbody2D>();
            rb.gravityScale = 1f;
            rb.freezeRotation = true;
            rb.interpolation = RigidbodyInterpolation2D.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;

            var circle = playerGo.AddComponent<CircleCollider2D>();
            circle.radius = 0.36f;

            playerGo.AddComponent<SpriteRenderer>();
            playerGo.AddComponent<PlayerVisual>();

            var vehicle = playerGo.AddComponent<DrillVehicle>();
            vehicle.grid = digGrid;
            vehicle.startMode = DrillVehicle.MovementMode.Walk;
            vehicle.startJetting = false;
            // CoreLoop 测试参数：燃料消耗调大，一次短循环即可感知能源压力（仅本场景组件覆盖）
            vehicle.fuelIdleDrain = 1.0f;
            vehicle.fuelMoveDrain = 1.8f;
            vehicle.fuelDrillDrain = 3.2f;

            var feel = playerGo.AddComponent<MiningFeelController>();
            feel.grid = digGrid;
            feel.attackInterval = 0.28f;
            feel.showTargetHighlight = true;

            // ---- 6. 总控 + HUD + 背包 + 表现 + 探针 ----
            var gmGo = new GameObject("GameManager");
            gmGo.AddComponent<UpgradeSystem>().costMultiplier = 0.4f;   // 短循环内可购买（Engine 160 / FuelTank 240）
            gmGo.AddComponent<GameHUD>();
            gmGo.AddComponent<InventoryPanel>();

            var gm = gmGo.AddComponent<GameManager>();
            gm.startingCash = 200;
            gm.usePlayerStartAsSpawn = true;
            gm.spawnPoint = spawnPos;

            var hook = gmGo.AddComponent<BlockDropHook>();
            hook.grid = digGrid;

            gmGo.AddComponent<BlockVisualController>().grid = digGrid;

            var probe = gmGo.AddComponent<CoreLoopV1Probe>();
            probe.grid = digGrid;
            probe.iron = iron;
            probe.copper = copper;
            probe.tin = tin;

            // ---- 7. 地表功能区：登陆舱（补给，不卖矿）+ 出售终端 ----
            var basePos = digGrid.GridToWorld(2, layout.surfaceY);
            var baseGo = new GameObject("SurfaceBase_LandingZone");
            baseGo.transform.position = basePos;
            var baseCol = baseGo.AddComponent<BoxCollider2D>();
            baseCol.size = new Vector2(2f, 2f);
            var surfaceBase = baseGo.AddComponent<SurfaceBase>();
            surfaceBase.autoSellCargo = false;   // 正式规则：不自动卖

            var sellPos = digGrid.GridToWorld(4, layout.surfaceY);
            var sellGo = new GameObject("SellTerminal");
            sellGo.transform.position = sellPos;
            var sellCol = sellGo.AddComponent<BoxCollider2D>();
            sellCol.size = new Vector2(2f, 2f);
            sellGo.AddComponent<SellTerminal>();

            // ---- 8. 相机 ----
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 10f;
            cam.transform.position = new Vector3(spawnPos.x, spawnPos.y, -10f);
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.07f);

            var follow = camGo.AddComponent<CameraFollow>();
            follow.target = playerGo.transform;
            follow.orthoSize = 10f;
            follow.maxOrthoSize = 16f;

            // ---- 9. 保存 + Build Settings ----
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

            Debug.Log($"[DEV-005] 核心闭环测试场景已搭建：{ScenePath}\n" +
                      "地表带 + 3 批×(铁/铜/锡) 矿柱 | 登陆舱=返航判定+自动付费补给（不卖矿）| SellTerminal 按 E 出售\n" +
                      "起始现金 200 · 升级价 ×0.4（Engine 160/FuelTank 240）· Fuel 消耗调大以感知压力");
        }

        // ---------- 锡矿资产（惰性创建，绕开 AshfallSetup） ----------

        static void EnsureTin()
        {
            if (!AssetDatabase.IsValidFolder(DataFolder))
            {
                Debug.LogError("[DEV-005] Data 目录不存在。请先运行 Tools/Ashfall/一键生成默认方块与数据库");
                return;
            }

            string path = $"{DataFolder}/Tin_锡矿.asset";
            var existing = AssetDatabase.LoadAssetAtPath<TileDefinition>(path);
            if (existing != null)
            {
                bool dirty = false;
                if (existing.displayName != "Tin_锡矿") { existing.displayName = "Tin_锡矿"; dirty = true; }
                if (existing.digHits != 1) { existing.digHits = 1; dirty = true; }
                if (existing.value != 20) { existing.value = 20; dirty = true; }
                if (dirty) { EditorUtility.SetDirty(existing); AssetDatabase.SaveAssets(); }
                return;
            }

            var tin = ScriptableObject.CreateInstance<TileDefinition>();
            tin.displayName = "Tin_锡矿";
            tin.color = new Color(0.66f, 0.72f, 0.78f);   // 锡银白偏蓝
            tin.hardness = 1;
            tin.drillTime = 0.3f;
            tin.value = 20;                                // 介于铁 12 与铜 40 之间
            tin.isSolid = true;
            tin.isHazard = false;
            tin.digHits = 1;
            tin.weight = 1.1f;
            tin.stackLimit = 16;
            tin.gridWidth = 1;
            tin.dropId = "tin_ore";
            tin.blockType = BlockType.Normal;
            AssetDatabase.CreateAsset(tin, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[DEV-005] 已创建锡矿资产：{path}");
        }
    }
}
#endif
