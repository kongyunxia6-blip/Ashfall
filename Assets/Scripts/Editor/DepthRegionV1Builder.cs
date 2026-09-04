#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// 一键搭建 DEV-009 深度 / 区域推进验收场景。
    /// 用法：菜单栏 → 灰烬之下 → 搭建 DEV-009 深度/区域推进测试场景
    ///
    /// 基线：DEV-008 地下探索空间 V1（c3f175c）。
    /// 场景结构（与 DEV-008 验收场景同款，仅新增「区域语义层」）：
    ///  - DigGrid 56x72、seed 固定、enableFallingRocks=false、breakDuration=0；
    ///  - Base Strata → Space Pass → Ore Vein Pass 顺序不变（spaceMode）；
    ///  - UndergroundSpaceGenerator + OreVeinGenerator 的带边界【全部取自 DepthRegionLayout】
    ///    （唯一来源：Shallow 3..21 / Mid 22..43 / Deep 44..62；空间带 Shallow 从 spaceStartY=5 起），
    ///    本 Builder 不再出现任何裸边界数字；
    ///  - DepthRegionProgression 挂在 GameManager 上（只读区域状态 + 首次进入事件/提示）；
    ///  - GameHUD.regionProgression 指向它 → 左上显示「深度 x m | 区域：xx」，并画首次进入横幅；
    ///  - 保留区/功能区/中心坑口同 DEV-008；
    ///  - 地表带布局由 DepthRegionV1TestLayout 重放（复用 UndergroundSpaceV1TestLayout 实现）。
    ///
    /// 不破坏 DEV-006/007/008 原测试场景。
    /// </summary>
    public static class DepthRegionV1Builder
    {
        const string ScenePath = "Assets/Scenes/DepthRegionV1Test.unity";
        const string SceneFolder = "Assets/Scenes";
        const string DataFolder = "Assets/Ashfall/Data";

        // 网格尺寸（Issue §10：与 DEV-008 同量级）
        const int GridWidth = 56;
        const int GridDepth = 72;

        // 固定 seed（沿用 DEV-008 验收 seed：世界已知、坑口下存在 Pocket 可作为真实下潜目标）
        public const int AcceptanceSeed = 20260908;

        [MenuItem("灰烬之下/搭建 DEV-009 深度/区域推进测试场景")]
        public static void Build()
        {
            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>($"{DataFolder}/TileDatabase.asset");
            var iron = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Iron_铁矿.asset");
            var copper = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Copper_铜矿.asset");
            var tin = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Tin_锡矿.asset");
            var silver = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Silver_银矿.asset");
            var dirt = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Dirt_泥土.asset");
            if (db == null || iron == null || copper == null || tin == null || silver == null || dirt == null)
            {
                Debug.LogError("[DEV-009] 必要资产缺失（Iron/Copper/Tin/Silver/Dirt/DB）。请先运行 Tools/Ashfall/一键生成默认方块与数据库");
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

            // ---- 3. DigGrid（spaceMode + veinMode；带边界唯一来源 = DepthRegionLayout）----
            var digGrid = fgGo.AddComponent<DigGrid>();
            digGrid.tilemap = fgTilemap;
            digGrid.backgroundTilemap = bgTilemap;
            digGrid.database = db;
            digGrid.width = GridWidth;
            digGrid.depth = GridDepth;
            digGrid.useRandomSeed = false;
            digGrid.seed = AcceptanceSeed;
            digGrid.enableFallingRocks = false;   // 确定性
            digGrid.breakDuration = 0f;
            digGrid.surfaceOpeningHalfWidth = 3; // 中心坑口 ±3

            // 共享保留区（空间与矿脉同一套）
            OreReservedRect[] sharedReserved =
            {
                new OreReservedRect { label = "SurfaceHub 地表带", x = 0, y = 0, w = GridWidth, h = 3 },
                new OreReservedRect { label = "ScannerPad 无矿基线区", x = 2, y = 16, w = 16, h = 10 },
            };

            // ---- 3a. UndergroundSpaceGenerator（DEV-008 space pass；边界来自 DepthRegionLayout）----
            var spaceGen = fgGo.AddComponent<UndergroundSpaceGenerator>();
            spaceGen.grid = digGrid;
            spaceGen.seedOverride = -1;            // 跟随 digGrid.seed
            spaceGen.bands = new[]
            {
                // Shallow：空腔少而小（空间带起点 spaceStartY=5 为贴近地表带的既有细节）
                new UndergroundSpaceBand
                {
                    bandName = "Shallow", minDepth = DepthRegionLayout.Shallow.spaceStartY, maxDepth = DepthRegionLayout.Shallow.maxDepth,
                    pocketTarget = 6, pocketMinCells = 6, pocketMaxCells = 10,
                    pocketWidthMax = 5, pocketHeightMax = 4,
                    tunnelTarget = 4, tunnelMinLength = 4, tunnelMaxLength = 6,
                    branchTarget = 1, smallRoomTarget = 2,
                    branchSideMin = 2, branchSideMax = 3,
                },
                // Mid：空间频率提高、房间略大、明显岔路
                new UndergroundSpaceBand
                {
                    bandName = "Mid", minDepth = DepthRegionLayout.Mid.minDepth, maxDepth = DepthRegionLayout.Mid.maxDepth,
                    pocketTarget = 7, pocketMinCells = 8, pocketMaxCells = 13,
                    pocketWidthMax = 6, pocketHeightMax = 5,
                    tunnelTarget = 5, tunnelMinLength = 5, tunnelMaxLength = 8,
                    branchTarget = 2, smallRoomTarget = 1,
                    branchSideMin = 2, branchSideMax = 4,
                },
                // Deep：结构更复杂、更大 pocket / 更长通道
                new UndergroundSpaceBand
                {
                    bandName = "Deep", minDepth = DepthRegionLayout.Deep.minDepth, maxDepth = DepthRegionLayout.Deep.maxDepth,
                    pocketTarget = 8, pocketMinCells = 10, pocketMaxCells = 18,
                    pocketWidthMax = 7, pocketHeightMax = 5,
                    tunnelTarget = 5, tunnelMinLength = 7, tunnelMaxLength = 11,
                    branchTarget = 3, smallRoomTarget = 1,
                    branchSideMin = 2, branchSideMax = 4,
                },
            };
            spaceGen.reservedRects = sharedReserved;
            digGrid.undergroundSpaceGenerator = spaceGen;

            // ---- 3b. OreVeinGenerator（DEV-007 ore pass；Ore 带 = region.min..max）----
            var oreGen = fgGo.AddComponent<OreVeinGenerator>();
            oreGen.grid = digGrid;
            oreGen.seedOverride = -1;
            oreGen.bands = new[]
            {
                new OreDepthBand
                {
                    bandName = "Shallow", minDepth = DepthRegionLayout.Shallow.minDepth, maxDepth = DepthRegionLayout.Shallow.maxDepth,
                    ores = new[] { iron, tin, copper },
                    weights = new[] { 45f, 40f, 15f },
                    veinMinSize = 2, veinMaxSize = 4, veinFrequency = 0.7f,
                },
                new OreDepthBand
                {
                    bandName = "Mid", minDepth = DepthRegionLayout.Mid.minDepth, maxDepth = DepthRegionLayout.Mid.maxDepth,
                    ores = new[] { copper, iron, tin, silver },
                    weights = new[] { 55f, 25f, 15f, 5f },
                    veinMinSize = 3, veinMaxSize = 6, veinFrequency = 0.8f,
                },
                new OreDepthBand
                {
                    bandName = "Deep", minDepth = DepthRegionLayout.Deep.minDepth, maxDepth = DepthRegionLayout.Deep.maxDepth,
                    ores = new[] { copper, iron, silver, tin },
                    weights = new[] { 62f, 15f, 15f, 8f },
                    veinMinSize = 4, veinMaxSize = 8, veinFrequency = 0.9f,
                },
            };
            oreGen.reservedRects = sharedReserved;
            digGrid.oreVeinGenerator = oreGen;

            // 运行时布局重放（地表带 + 中心坑口；复用 DEV-008 重放实现）
            var layout = fgGo.AddComponent<DepthRegionV1TestLayout>();
            layout.grid = digGrid;
            layout.dirt = dirt;
            layout.spawnX = GridWidth / 2;
            layout.surfaceY = 1;
            layout.groundY = 2;
            layout.openingHalfWidth = 3;

            // 编辑期先生成一次：让 .unity 序列化带正确的空间/矿脉引用（验收走 Play 重载为准）
            digGrid.RegenerateFromDatabase();

            // ---- 4. 背景 Dirt ----
            var bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            bgTex.SetPixel(0, 0, Color.white);
            bgTex.Apply();
            var bgSprite = Sprite.Create(bgTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            bgSprite.name = "DEV009_DirtBg";
            var bgTile = ScriptableObject.CreateInstance<Tile>();
            bgTile.sprite = bgSprite;
            bgTile.name = "DEV009_DirtBg";
            bgTile.flags = TileFlags.None;

            for (int y = 0; y < digGrid.depth; y++)
                for (int x = 0; x < digGrid.width; x++)
                {
                    var cell = new Vector3Int(x, -y, 0);
                    bgTilemap.SetTile(cell, bgTile);
                    bgTilemap.SetTileFlags(cell, TileFlags.None);
                    bgTilemap.SetColor(cell, dirt.color);
                }

            // ---- 5. 玩家（出生在坑口中心 x=width/2,y=1）----
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
            feel.attackInterval = 0.22f;
            feel.showTargetHighlight = true;

            var scanner = playerGo.AddComponent<OreScanner>();
            scanner.scanRadius = 4;
            scanner.scanKey = KeyCode.R;

            // ---- 6. 总控 + HUD + 背包 + 表现 + 【DEV-009 区域推进】----
            var gmGo = new GameObject("GameManager");
            gmGo.AddComponent<UpgradeSystem>().costMultiplier = 0.4f;
            gmGo.AddComponent<InventoryPanel>();

            // DEV-009：区域推进（只读状态；同一物体上供 GameHUD GetComponent 兜底）
            var prog = gmGo.AddComponent<DepthRegionProgression>();
            prog.vehicle = vehicle;
            prog.grid = digGrid;

            var hud = gmGo.AddComponent<GameHUD>();
            hud.regionProgression = prog;

            var gm = gmGo.AddComponent<GameManager>();
            gm.startingCash = 200;
            gm.usePlayerStartAsSpawn = true;
            gm.spawnPoint = spawnPos;

            gmGo.AddComponent<BlockDropHook>().grid = digGrid;
            gmGo.AddComponent<BlockVisualController>().grid = digGrid;

            // ---- 7. 地表功能区（与 DEV-006/007/008 相同布局）----
            var hubGo = new GameObject("SurfaceHub_Zones");

            var hubCenter = digGrid.GridToWorld(GridWidth / 2, layout.surfaceY);
            var hubZoneGo = new GameObject("SurfaceHubZone");
            hubZoneGo.transform.SetParent(hubGo.transform);
            hubZoneGo.transform.position = new Vector3(hubCenter.x, hubCenter.y, 0f);
            var hubCol = hubZoneGo.AddComponent<BoxCollider2D>();
            hubCol.size = new Vector2(GridWidth + 4f, 3f);
            hubCol.isTrigger = true;
            hubZoneGo.AddComponent<SurfaceHubZone>();

            var lander = NewZoneObject("Lander_LandingPad", hubGo.transform, digGrid, 6, layout.surfaceY, new Vector2(3f, 2f));
            var surfaceBase = lander.AddComponent<SurfaceBase>();
            surfaceBase.autoSellCargo = false;
            surfaceBase.enableAutoService = false;

            var sell = NewZoneObject("SellTerminal", hubGo.transform, digGrid, 14, layout.surfaceY, new Vector2(2f, 2f));
            sell.AddComponent<SellTerminal>();

            var bench = NewZoneObject("UpgradeWorkbench", hubGo.transform, digGrid, 22, layout.surfaceY, new Vector2(2f, 2f));
            bench.AddComponent<UpgradeWorkbench>();

            var fuel = NewZoneObject("FuelStation", hubGo.transform, digGrid, 30, layout.surfaceY, new Vector2(2f, 2f));
            fuel.AddComponent<FuelStation>();

            var signGo = new GameObject("DepthRegion_Signs");
            signGo.transform.SetParent(hubGo.transform);

            MakeSign(signGo.transform, "登陆舱 / 返航安全区", digGrid.GridToWorld(6, layout.surfaceY), new Color(0.5f, 1f, 0.6f));
            MakeSign(signGo.transform, "出售终端", digGrid.GridToWorld(14, layout.surfaceY), new Color(1f, 0.85f, 0.35f));
            MakeSign(signGo.transform, "装备工作台", digGrid.GridToWorld(22, layout.surfaceY), new Color(0.45f, 0.9f, 1f));
            MakeSign(signGo.transform, "能源补给点", digGrid.GridToWorld(30, layout.surfaceY), new Color(1f, 0.65f, 0.3f));
            MakeSign(signGo.transform, "↓ 下矿坑口（垂直下挖穿越 浅层→中层→深层）", digGrid.GridToWorld(GridWidth / 2, layout.surfaceY + 2), Color.white);
            MakeSign(signGo.transform, "Scanner: 按 R 扫描附近矿脉（只读提示）", digGrid.GridToWorld(GridWidth / 2, 8), new Color(0.8f, 0.9f, 1f));

            // 区域界标（边界值取自 DepthRegionLayout —— 与 Ore/Space band、运行时判定同一来源）
            MakeSign(signGo.transform, "── 浅层 Shallow (y3..21) ──", digGrid.GridToWorld(8, DepthRegionLayout.Shallow.maxDepth), new Color(0.7f, 1f, 0.7f));
            MakeSign(signGo.transform, "── 中层 Mid (y22..43) ──", digGrid.GridToWorld(8, DepthRegionLayout.Mid.maxDepth), new Color(1f, 1f, 0.7f));
            MakeSign(signGo.transform, "── 深层 Deep (y44..62) ──", digGrid.GridToWorld(8, DepthRegionLayout.Deep.maxDepth), new Color(1f, 0.75f, 0.7f));

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

            Debug.Log($"[DEV-009] 深度/区域推进测试场景已搭建：{ScenePath}\n" +
                      $"seed={digGrid.seed}，网格 {GridWidth}x{GridDepth}，" +
                      $"Region: Surface(y0..{DepthRegionLayout.Surface.maxDepth}) / " +
                      $"Shallow({DepthRegionLayout.Shallow.minDepth}..{DepthRegionLayout.Shallow.maxDepth}) / " +
                      $"Mid({DepthRegionLayout.Mid.minDepth}..{DepthRegionLayout.Mid.maxDepth}) / " +
                      $"Deep({DepthRegionLayout.Deep.minDepth}..{DepthRegionLayout.Deep.maxDepth})；" +
                      $"spaces={spaceGen.Spaces.Count} empty={spaceGen.TotalEmptyCells}，" +
                      $"veins={oreGen.Veins.Count} planted={oreGen.TotalCellsPlanted}。" +
                      $"功能区 x=6/14/22/30；坑口 x={GridWidth / 2}±3。");
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
