#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// DEV-013：一键搭建 AncientRelayRoomV1Test 验收场景。
    /// 用法：菜单 → 灰烬之下 → 搭建 DEV-013 小型遗迹测试场景
    ///
    /// 场景内容（Issue §13）：
    ///  - DigGrid（width=48, depth=64, 固定 seed）同时挂 UndergroundSpaceGenerator + OreVeinGenerator
    ///    （边界唯一来源 DepthRegionLayout）+ RuinGenerator（DEV-013，Deep 区 8×4 AncientRelayRoom）；
    ///  - 生成顺序 Base → Space → Vein → Ruin（DigGrid.Generate 集中定义）；
    ///  - GameManager：UpgradeSystem + EquipmentProgression（DEV-010 权威）+ RunRiskState（DEV-011）；
    ///  - RuinSealSystem + AncientRelayCoreSystem + RuinDiscoveryService（DEV-013）；
    ///  - AncientRelayRoomV1Probe（播放验收驱动，autoRun）；
    ///  - 资产惰性创建：RuinWall_遗迹墙 / RuinSeal_遗迹封印 / AncientRelayCore_中继核心 /
    ///    AncientAlloy_古代合金 / AncientDataFragment_古代数据碎片。
    ///
    /// 不重写 DEV-004~012 任何系统；不建第二套 Grid / Inventory / Region / Block HP。
    /// </summary>
    public static class AncientRelayRoomV1Builder
    {
        const string ScenePath = "Assets/Scenes/Tests/AncientRelayRoomV1Test.unity";
        const string SceneFolder = "Assets/Scenes";
        const string DataFolder = "Assets/Ashfall/Data";

        const int GridWidth = 48;
        const int GridDepth = 64;
        const int AcceptanceSeed = 20260906;

        [MenuItem("灰烬之下/搭建 DEV-013 小型遗迹测试场景")]
        public static void Build()
        {
            // ---- 资产（惰性创建 DEV-013 相关；复用既有地层/矿资产）----
            var wallTile = EnsureAsset("RuinWall_遗迹墙", CreateWall);
            var sealTile = EnsureAsset("RuinSeal_遗迹封印", CreateSeal);
            var coreTile = EnsureAsset("AncientRelayCore_中继核心", CreateCore);
            var ancientAlloy = EnsureAsset("AncientAlloy_古代合金", CreateAncientAlloy);
            var dataFragment = EnsureAsset("AncientDataFragment_古代数据碎片", CreateDataFragment);

            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>($"{DataFolder}/TileDatabase.asset");
            var dirt = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Dirt_泥土.asset");
            var iron = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Iron_铁矿.asset");
            if (db == null || dirt == null || iron == null || wallTile == null || sealTile == null || coreTile == null)
            {
                Debug.LogError("[DEV-013] 必要资产缺失。请先运行 Tools/Ashfall/一键生成默认方块与数据库");
                return;
            }

            if (!AssetDatabase.IsValidFolder(SceneFolder))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // ---- Grid + Tilemaps ----
            var gridGo = new GameObject("Grid");
            var ugrid = gridGo.AddComponent<Grid>();
            ugrid.cellSize = Vector3.one; ugrid.cellGap = Vector3.zero;
            ugrid.cellLayout = GridLayout.CellLayout.Rectangle;

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

            // ---- DigGrid ----
            var digGrid = fgGo.AddComponent<DigGrid>();
            digGrid.tilemap = fgTilemap;
            digGrid.backgroundTilemap = bgTilemap;
            digGrid.database = db;
            digGrid.width = GridWidth;
            digGrid.depth = GridDepth;
            digGrid.useRandomSeed = false;
            digGrid.seed = AcceptanceSeed;
            digGrid.enableFallingRocks = false;
            digGrid.breakDuration = 0f;
            digGrid.surfaceOpeningHalfWidth = 3;

            // 保留区：Surface 带 + 玩家出生区不铺矿/空间
            OreReservedRect[] sharedReserved =
            {
                new OreReservedRect { label = "SurfaceBand", x = 0, y = 0, w = GridWidth, h = 3 },
            };

            // ---- UndergroundSpaceGenerator（DEV-008；边界来自 DepthRegionLayout）----
            var spaceGen = fgGo.AddComponent<UndergroundSpaceGenerator>();
            spaceGen.grid = digGrid;
            spaceGen.seedOverride = -1;
            spaceGen.bands = new[]
            {
                new UndergroundSpaceBand { bandName = "Shallow", minDepth = DepthRegionLayout.Shallow.spaceStartY, maxDepth = DepthRegionLayout.Shallow.maxDepth, pocketTarget = 5, pocketMinCells = 5, pocketMaxCells = 9, pocketWidthMax = 4, pocketHeightMax = 3, tunnelTarget = 3, tunnelMinLength = 3, tunnelMaxLength = 5, branchTarget = 0, smallRoomTarget = 1, branchSideMin = 2, branchSideMax = 3 },
                new UndergroundSpaceBand { bandName = "Mid", minDepth = DepthRegionLayout.Mid.minDepth, maxDepth = DepthRegionLayout.Mid.maxDepth, pocketTarget = 6, pocketMinCells = 7, pocketMaxCells = 12, pocketWidthMax = 5, pocketHeightMax = 4, tunnelTarget = 4, tunnelMinLength = 4, tunnelMaxLength = 6, branchTarget = 1, smallRoomTarget = 1, branchSideMin = 2, branchSideMax = 3 },
                new UndergroundSpaceBand { bandName = "Deep", minDepth = DepthRegionLayout.Deep.minDepth, maxDepth = DepthRegionLayout.Deep.maxDepth, pocketTarget = 7, pocketMinCells = 9, pocketMaxCells = 15, pocketWidthMax = 6, pocketHeightMax = 4, tunnelTarget = 4, tunnelMinLength = 6, tunnelMaxLength = 8, branchTarget = 2, smallRoomTarget = 1, branchSideMin = 2, branchSideMax = 4 },
            };
            spaceGen.reservedRects = sharedReserved;
            digGrid.undergroundSpaceGenerator = spaceGen;

            // ---- OreVeinGenerator（DEV-007；Ore 带 = region.min..max）----
            var oreGen = fgGo.AddComponent<OreVeinGenerator>();
            oreGen.grid = digGrid;
            oreGen.seedOverride = -1;
            oreGen.bands = new[]
            {
                new OreDepthBand { bandName = "Shallow", minDepth = DepthRegionLayout.Shallow.minDepth, maxDepth = DepthRegionLayout.Shallow.maxDepth, ores = new[] { iron }, weights = new[] { 100f }, veinMinSize = 2, veinMaxSize = 4, veinFrequency = 0.6f },
                new OreDepthBand { bandName = "Mid", minDepth = DepthRegionLayout.Mid.minDepth, maxDepth = DepthRegionLayout.Mid.maxDepth, ores = new[] { iron }, weights = new[] { 100f }, veinMinSize = 3, veinMaxSize = 5, veinFrequency = 0.7f },
                new OreDepthBand { bandName = "Deep", minDepth = DepthRegionLayout.Deep.minDepth, maxDepth = DepthRegionLayout.Deep.maxDepth, ores = new[] { iron }, weights = new[] { 100f }, veinMinSize = 4, veinMaxSize = 6, veinFrequency = 0.7f },
            };
            oreGen.reservedRects = sharedReserved;
            digGrid.oreVeinGenerator = oreGen;

            // ---- RuinGenerator（DEV-013；在 vein 之后叠加）----
            var ruinGen = fgGo.AddComponent<RuinGenerator>();
            ruinGen.grid = digGrid;
            ruinGen.seedOverride = -1;
            ruinGen.wallTile = wallTile;
            ruinGen.sealTile = sealTile;
            ruinGen.coreTile = coreTile;
            ruinGen.rewardTile = ancientAlloy;
            digGrid.ruinGenerator = ruinGen;

            // 生成
            digGrid.RegenerateFromDatabase();

            // 背景 Dirt
            var bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            bgTex.SetPixel(0, 0, Color.white); bgTex.Apply();
            var bgSprite = Sprite.Create(bgTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            bgSprite.name = "DEV013_DirtBg";
            var bgTile = ScriptableObject.CreateInstance<Tile>();
            bgTile.sprite = bgSprite; bgTile.name = "DEV013_DirtBg"; bgTile.flags = TileFlags.None;
            for (int y = 0; y < digGrid.depth; y++)
                for (int x = 0; x < digGrid.width; x++)
                {
                    var cell = new Vector3Int(x, -y, 0);
                    bgTilemap.SetTile(cell, bgTile);
                    bgTilemap.SetTileFlags(cell, TileFlags.None);
                    bgTilemap.SetColor(cell, dirt.color);
                }

            // ---- 玩家 ----
            var playerGo = new GameObject("Player");
            playerGo.transform.position = digGrid.GridToWorld(GridWidth / 2, 1);
            var rb = playerGo.AddComponent<Rigidbody2D>();
            rb.gravityScale = 1f; rb.freezeRotation = true;
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
            playerGo.AddComponent<MiningFeelController>();

            // DEV-007 扫描（挂玩家，R 键手势）。grid 由 OreScanner.Start 自动取 vehicle.grid；
            // ruinDiscovery 在下面 discovery 组件创建后回填（DEV-013 文明异常接入）。
            var oreScanner = playerGo.AddComponent<OreScanner>();
            oreScanner.scanRadius = 4;

            // ---- 总控（Equipment / RunRisk / HUD / 环境）----
            var gmGo = new GameObject("GameManager");
            gmGo.AddComponent<UpgradeSystem>();
            gmGo.AddComponent<EquipmentProgression>();
            gmGo.AddComponent<RunRiskState>();       // DEV-011：风险撤离权威
            var regionProg = gmGo.AddComponent<DepthRegionProgression>();   // DEV-009：Region/MaxDepth 权威（RunRisk.BeginRun 依赖）
            regionProg.grid = digGrid;                                       // 需显式 grid 引用，否则 Start.Tick NRE
            gmGo.AddComponent<GameHUD>();
            gmGo.AddComponent<InventoryPanel>();
            var gm = gmGo.AddComponent<GameManager>();
            gm.spawnPoint = playerGo.transform.position;
            gm.usePlayerStartAsSpawn = true;
            gm.startingCash = 500;
            gmGo.AddComponent<BlockDropHook>().grid = digGrid;

            // DEV-013 系统 + 探针（挂 GameManager 物体，方便 DrillVehicle Start 场景级查找）
            var sealSys = gmGo.AddComponent<RuinSealSystem>();
            var discovery = gmGo.AddComponent<RuinDiscoveryService>();
            discovery.generator = ruinGen;                 // 运行时自动把 generator.Instances 同步成布局（正式闭环）
            var coreSys = gmGo.AddComponent<AncientRelayCoreSystem>();
            coreSys.discovery = discovery;
            coreSys.ancientDataTile = dataFragment;
            coreSys.ancientAlloyTile = ancientAlloy;

            // 把玩家扫描组件接入文明异常信号（同一 R 键扫描在扫矿后得到异常反馈）
            oreScanner.ruinDiscovery = discovery;

            var probe = gmGo.AddComponent<AncientRelayRoomV1Probe>();
            probe.grid = digGrid;
            probe.generator = ruinGen;
            probe.discovery = discovery;
            probe.core = coreSys;
            probe.seal = sealSys;
            probe.vehicle = vehicle;
            probe.equipment = gm.Equipment;
            probe.oreScanner = oreScanner;   // Blocker1：真实扫描动作（OreScanner.ScanAndReportAround）
            probe.wallAsset = wallTile;
            probe.sealAsset = sealTile;
            probe.coreAsset = coreTile;
            probe.alloyAsset = ancientAlloy;
            probe.fragmentAsset = dataFragment;
            probe.autoRun = true;

            // ---- 相机 ----
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 10f;
            cam.transform.position = playerGo.transform.position + new Vector3(0, 0, -10f);
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.07f);
            var follow = camGo.AddComponent<CameraFollow>();
            follow.target = playerGo.transform;
            follow.orthoSize = 10f;
            follow.maxOrthoSize = 16f;

            EditorSceneManager.SaveScene(scene, ScenePath);
            var scenes = EditorBuildSettings.scenes;
            bool exists = false;
            foreach (var s in scenes) if (s.path == ScenePath) { exists = true; s.enabled = true; }
            if (!exists)
            {
                var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(scenes)
                { new EditorBuildSettingsScene(ScenePath, true) };
                EditorBuildSettings.scenes = list.ToArray();
            }
            else EditorBuildSettings.scenes = scenes;
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeGameObject = playerGo;
            Debug.Log($"[DEV-013] 小型遗迹验收场景已搭建：{ScenePath}（seed={AcceptanceSeed}，Base→Space→Vein→Ruin）");
        }

        // ---------- 资产惰性创建 ----------

        static TileDefinition EnsureAsset(string fileName, System.Func<TileDefinition> factory)
        {
            if (!AssetDatabase.IsValidFolder(DataFolder))
            {
                Debug.LogError("[DEV-013] Data 目录不存在。请先运行 Tools/Ashfall/一键生成默认方块与数据库");
                return null;
            }
            string path = $"{DataFolder}/{fileName}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<TileDefinition>(path);
            if (existing != null) return existing;
            var tile = factory();
            AssetDatabase.CreateAsset(tile, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[DEV-013] 已创建资产：{path}");
            return tile;
        }

        static TileDefinition CreateWall()
        {
            var t = ScriptableObject.CreateInstance<TileDefinition>();
            t.displayName = "遗迹墙"; t.color = new Color(0.30f, 0.30f, 0.33f);
            t.hardness = 6;           // 超出 V1 钻头最大等级 → 挖不动，保证房间边界牢靠
            t.digHits = 60; t.drillTime = 0.3f; t.value = 0; t.isSolid = true;
            t.weight = 0f; t.stackLimit = 1; t.gridWidth = 1; t.dropId = ""; t.blockType = BlockType.Normal;
            return t;
        }

        static TileDefinition CreateSeal()
        {
            var t = ScriptableObject.CreateInstance<TileDefinition>();
            t.displayName = "遗迹封印"; t.color = new Color(0.55f, 0.25f, 0.7f);
            t.hardness = 1; t.digHits = 2; t.drillTime = 0.3f; t.value = 0; t.isSolid = true;
            t.weight = 0f; t.stackLimit = 1; t.gridWidth = 1; t.dropId = ""; t.blockType = BlockType.RuinSeal;
            return t;
        }

        static TileDefinition CreateCore()
        {
            var t = ScriptableObject.CreateInstance<TileDefinition>();
            t.displayName = "古代中继核心"; t.color = new Color(0.95f, 0.78f, 0.2f);
            t.hardness = 1; t.digHits = 1; t.drillTime = 0.2f; t.value = 0; t.isSolid = true;
            t.weight = 0f; t.stackLimit = 1; t.gridWidth = 1; t.dropId = ""; t.blockType = BlockType.AncientRelayCore;
            return t;
        }

        static TileDefinition CreateAncientAlloy()
        {
            var t = ScriptableObject.CreateInstance<TileDefinition>();
            t.displayName = "古代合金"; t.color = new Color(0.35f, 0.8f, 0.7f);
            t.hardness = 2; t.digHits = 2; t.drillTime = 0.3f; t.value = 60; t.isSolid = true;
            t.weight = 0.8f; t.stackLimit = 16; t.gridWidth = 1; t.dropId = OreCatalog.AncientAlloy; t.blockType = BlockType.Normal;
            return t;
        }

        static TileDefinition CreateDataFragment()
        {
            var t = ScriptableObject.CreateInstance<TileDefinition>();
            t.displayName = "古代数据碎片"; t.color = new Color(0.45f, 0.9f, 0.55f);
            t.hardness = 1; t.digHits = 1; t.drillTime = 0.2f; t.value = 90; t.isSolid = true;
            t.weight = 0.2f; t.stackLimit = 16; t.gridWidth = 1; t.dropId = RuinCatalog.AncientDataFragment; t.blockType = BlockType.Normal;
            return t;
        }
    }
}
#endif
