#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// 一键搭建 DEV-007 矿物分层 / 矿脉 / 扫描提示验收场景。
    /// 用法：菜单栏 → 灰烬之下 → 搭建 DEV-007 矿脉分层测试场景
    ///
    /// 场景内容：
    ///  - DigGrid 挂 OreVeinGenerator（veinMode）：基础地层只铺 Dirt 纯填充，矿物全部由矿脉 pass 叠加；
    ///  - 三条深度带（Shallow 3..21 / Mid 22..43 / Deep 44..62），Iron/Tin/Copper 权重随深度变化，
    ///    Deep 附极少量 Silver（已有稳定资产，体现「稀有矿概率上升」，不新增资产）；
    ///  - 保留区（reservedRects，集中配置）：地表 Hub 带 y0..2、手工矿柱区 x34..46 y3..10、
    ///    Scanner 无矿基线区 x2..17 y16..25（MCP Scanner fixture 用，永不种矿脉）；
    ///  - 地表 Hub 功能区与 SurfaceHubV1 相同（x6 Lander / x14 Sell / x22 Workbench / x30 Fuel）；
    ///  - 手工矿柱 3 列（x36 Iron / x40 Copper / x44 Tin，浅深两趟）作为单格采矿回归与出售闭环的确定性落点；
    ///  - 玩家 Walk + MiningFeelController；OreScanner（R 键扫描，0 消耗）；GameHUD/背包/掉落/表现全套；
    ///  - DigGrid depth=66, seed 固定, enableFallingRocks=false, breakDuration=0（确定性验收）。
    ///
    /// 资产：全部复用既有资产（含 DEV-005 的 Tin_锡矿 / DB 已有的 Silver_银矿），本 Builder 不创建任何新资产。
    /// </summary>
    public static class OreVeinV1Builder
    {
        const string ScenePath = "Assets/Scenes/Tests/OreVeinV1Test.unity";
        const string SceneFolder = "Assets/Scenes";
        const string DataFolder = "Assets/Ashfall/Data";

        // 网格尺寸（Issue §7 建议宽度 40~60、深度 50~80）
        const int GridWidth = 52;
        const int GridDepth = 66;

        // 深度带边界：DEV-009 起统一取自 DepthRegionLayout（唯一来源），禁止再散落裸数字。
        //  ORE 带边界 = [region.minDepth, region.maxDepth]（Shallow 3..21 / Mid 22..43 / Deep 44..62）。

        [MenuItem("灰烬之下/搭建 DEV-007 矿脉分层测试场景")]
        public static void Build()
        {
            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>($"{DataFolder}/TileDatabase.asset");
            var iron = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Iron_铁矿.asset");
            var copper = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Copper_铜矿.asset");
            var tin = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Tin_锡矿.asset");
            var silver = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Silver_银矿.asset");
            var dirt = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Dirt_泥土.asset");
            if (db == null || iron == null || copper == null || tin == null || dirt == null)
            {
                Debug.LogError("[DEV-007] 必要资产缺失（Iron/Copper/Tin/Dirt/DB）。请先运行 Tools/Ashfall/一键生成默认方块与数据库");
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

            // ---- 3. DigGrid（veinMode）----
            var digGrid = fgGo.AddComponent<DigGrid>();
            digGrid.tilemap = fgTilemap;
            digGrid.backgroundTilemap = bgTilemap;
            digGrid.database = db;
            digGrid.width = GridWidth;
            digGrid.depth = GridDepth;
            digGrid.useRandomSeed = false;
            digGrid.seed = 20260904;
            digGrid.enableFallingRocks = false;   // 确定性
            digGrid.breakDuration = 0f;

            // 矿脉生成器（同物体；DigGrid.Generate 会自动跑 ore pass）
            var gen = fgGo.AddComponent<OreVeinGenerator>();
            gen.grid = digGrid;
            gen.seedOverride = -1;                 // 跟随 digGrid.seed → 地层+矿脉整体可复现
            gen.bands = new[]
            {
                new OreDepthBand
                {
                    bandName = "Shallow",
                    minDepth = DepthRegionLayout.Shallow.minDepth, maxDepth = DepthRegionLayout.Shallow.maxDepth,
                    ores = new[] { iron, tin, copper },
                    weights = new[] { 45f, 40f, 15f },
                    veinMinSize = 2, veinMaxSize = 4,
                    veinFrequency = 0.7f,
                },
                new OreDepthBand
                {
                    bandName = "Mid",
                    minDepth = DepthRegionLayout.Mid.minDepth, maxDepth = DepthRegionLayout.Mid.maxDepth,
                    ores = new[] { copper, iron, tin, silver },
                    weights = new[] { 55f, 25f, 15f, 5f },
                    veinMinSize = 3, veinMaxSize = 6,
                    veinFrequency = 0.8f,
                },
                new OreDepthBand
                {
                    bandName = "Deep",
                    minDepth = DepthRegionLayout.Deep.minDepth, maxDepth = DepthRegionLayout.Deep.maxDepth,
                    ores = new[] { copper, iron, silver, tin },
                    weights = new[] { 62f, 15f, 15f, 8f },
                    veinMinSize = 4, veinMaxSize = 8,
                    veinFrequency = 0.9f,
                },
            };
            gen.reservedRects = new[]
            {
                // 地表 Hub 带（含出生点 / 全部功能区）：y0..2 全宽不种矿脉
                new OreReservedRect { label = "SurfaceHub 地表带", x = 0, y = 0, w = GridWidth, h = 3 },
                // 手工矿柱区（x34..46, y3..10）：保证矿柱不被矿脉覆盖，回归落点确定
                new OreReservedRect { label = "手工矿柱", x = 34, y = 3, w = 13, h = 8 },
                // Scanner 无矿基线区：MCP Scanner fixture 专用，永不种矿脉
                new OreReservedRect { label = "ScannerPad", x = 2, y = 16, w = 16, h = 10 },
            };
            digGrid.oreVeinGenerator = gen;

            // 运行时布局重放（地表带 + 手工矿柱）
            var layout = fgGo.AddComponent<OreVeinV1TestLayout>();
            layout.grid = digGrid;
            layout.iron = iron;
            layout.copper = copper;
            layout.tin = tin;
            layout.dirt = dirt;
            layout.spawnX = 6;
            layout.surfaceY = 1;
            layout.groundY = 2;

            digGrid.RegenerateFromDatabase();

            // ---- 4. 背景 Dirt ----
            var bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            bgTex.SetPixel(0, 0, Color.white);
            bgTex.Apply();
            var bgSprite = Sprite.Create(bgTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            bgSprite.name = "DEV007_DirtBg";
            var bgTile = ScriptableObject.CreateInstance<Tile>();
            bgTile.sprite = bgSprite;
            bgTile.name = "DEV007_DirtBg";
            bgTile.flags = TileFlags.None;

            for (int y = 0; y < digGrid.depth; y++)
                for (int x = 0; x < digGrid.width; x++)
                {
                    var cell = new Vector3Int(x, -y - 1, 0);
                    bgTilemap.SetTile(cell, bgTile);
                    bgTilemap.SetTileFlags(cell, TileFlags.None);
                    bgTilemap.SetColor(cell, dirt.color);
                }

            // ---- 5. 玩家（出生在 x=6,y=1 登陆舱）----
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

            var feel = playerGo.AddComponent<MiningFeelController>();
            feel.grid = digGrid;
            feel.attackInterval = 0.28f;
            feel.showTargetHighlight = true;

            // DEV-007：基础扫描器（R 键，0 消耗，只读）
            var scanner = playerGo.AddComponent<OreScanner>();
            scanner.scanRadius = 4;
            scanner.scanKey = KeyCode.R;

            // ---- 6. 总控 + HUD + 背包 + 表现 + 探针 ----
            var gmGo = new GameObject("GameManager");
            gmGo.AddComponent<UpgradeSystem>().costMultiplier = 0.4f;
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

            // ---- 7. 地表功能区（与 DEV-006 相同布局）----
            var hubGo = new GameObject("SurfaceHub_Zones");

            // 7.0 地表据点大区：IsAtSurface 唯一权威
            var hubCenter = digGrid.GridToWorld(24, layout.surfaceY);
            var hubZoneGo = new GameObject("SurfaceHubZone");
            hubZoneGo.transform.SetParent(hubGo.transform);
            hubZoneGo.transform.position = new Vector3(hubCenter.x, hubCenter.y, 0f);
            var hubCol = hubZoneGo.AddComponent<BoxCollider2D>();
            hubCol.size = new Vector2(48f, 3f);
            hubCol.isTrigger = true;
            hubZoneGo.AddComponent<SurfaceHubZone>();

            // 7.1 登陆舱（x=6）
            var lander = NewZoneObject("Lander_LandingPad", hubGo.transform, digGrid, 6, layout.surfaceY, new Vector2(3f, 2f));
            var surfaceBase = lander.AddComponent<SurfaceBase>();
            surfaceBase.autoSellCargo = false;
            surfaceBase.enableAutoService = false;

            // 7.2 出售终端（x=14）
            var sell = NewZoneObject("SellTerminal", hubGo.transform, digGrid, 14, layout.surfaceY, new Vector2(2f, 2f));
            sell.AddComponent<SellTerminal>();

            // 7.3 装备工作台（x=22）
            var bench = NewZoneObject("UpgradeWorkbench", hubGo.transform, digGrid, 22, layout.surfaceY, new Vector2(2f, 2f));
            bench.AddComponent<UpgradeWorkbench>();

            // 7.4 能源补给点（x=30）
            var fuel = NewZoneObject("FuelStation", hubGo.transform, digGrid, 30, layout.surfaceY, new Vector2(2f, 2f));
            fuel.AddComponent<FuelStation>();

            // 7.5 标牌
            var signGo = new GameObject("OreVein_Signs");
            signGo.transform.SetParent(hubGo.transform);

            MakeSign(signGo.transform, "登陆舱 / 返航安全区", digGrid.GridToWorld(6, layout.surfaceY), new Color(0.5f, 1f, 0.6f));
            MakeSign(signGo.transform, "出售终端", digGrid.GridToWorld(14, layout.surfaceY), new Color(1f, 0.85f, 0.35f));
            MakeSign(signGo.transform, "装备工作台", digGrid.GridToWorld(22, layout.surfaceY), new Color(0.45f, 0.9f, 1f));
            MakeSign(signGo.transform, "能源补给点", digGrid.GridToWorld(30, layout.surfaceY), new Color(1f, 0.65f, 0.3f));
            MakeSign(signGo.transform, "Scanner: 按 R 扫描附近矿脉（只读提示）", digGrid.GridToWorld(20, layout.surfaceY + 2), new Color(0.8f, 0.9f, 1f));
            MakeSign(signGo.transform, "下矿口（手工铁/铜/锡 柱）→", digGrid.GridToWorld(40, layout.surfaceY), Color.white);

            // 深度带标记（便于观察浅/中/深）
            MakeSign(signGo.transform, "── Shallow 浅层带 (y3..21) ──", digGrid.GridToWorld(6, DepthRegionLayout.Shallow.maxDepth), new Color(0.7f, 1f, 0.7f));
            MakeSign(signGo.transform, "── Mid 中层带 (y22..43) ──", digGrid.GridToWorld(6, DepthRegionLayout.Mid.maxDepth), new Color(1f, 1f, 0.7f));
            MakeSign(signGo.transform, "── Deep 深层带 (y44..62) ──", digGrid.GridToWorld(6, DepthRegionLayout.Deep.maxDepth), new Color(1f, 0.75f, 0.7f));

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

            Debug.Log($"[DEV-007] 矿脉分层测试场景已搭建：{ScenePath}\n" +
                      $"深度带 Shallow({DepthRegionLayout.Shallow.minDepth}..{DepthRegionLayout.Shallow.maxDepth}) / " +
                      $"Mid({DepthRegionLayout.Mid.minDepth}..{DepthRegionLayout.Mid.maxDepth}) / " +
                      $"Deep({DepthRegionLayout.Deep.minDepth}..{DepthRegionLayout.Deep.maxDepth})，" +
                      $"seed={digGrid.seed}，veins={gen.Veins.Count}，grid 实际矿格待 MCP 统计。" +
                      $"功能区 x=6/14/22/30；手工矿柱 x=36/40/44（铁/铜/锡）；ScannerPad(x2..17,y16..25) 无矿保留区。");
        }

        // ---------- 工具 ----------

        static GameObject NewZoneObject(string name, Transform parent, DigGrid grid, int cellX, int cellY, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.position = grid.GridToWorld(cellX, cellY);
            var col = go.AddComponent<BoxCollider2D>();
            col.size = size;
            col.isTrigger = true;
            return go;
        }

        static void MakeSign(Transform parent, string text, Vector3 basePos, Color color)
        {
            var go = new GameObject($"Sign_{text.Split('/')[0]}");
            go.transform.SetParent(parent);
            go.transform.position = basePos;
            var sign = go.AddComponent<HubSign>();
            sign.text = text;
            sign.color = color;
            sign.offset = new Vector3(0f, 1.35f, 0f);
        }
    }
}
#endif
