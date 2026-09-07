#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// DEV-014：一键搭建 DiscoveryNodeV1Test 验收场景。
    /// 用法：菜单 → 灰烬之下 → 搭建 DEV-014 地下发现节点测试场景
    ///
    /// 场景内容（Issue #30 §Runtime + Acceptance）：
    ///  - DigGrid（width=48, depth=64, 固定 seed）同时挂 UndergroundSpaceGenerator + OreVeinGenerator
    ///    + RuinGenerator（DEV-013 Deep 遗迹）+ DiscoveryNodeGenerator（DEV-014，Ruin 后叠加 4 类节点）；
    ///  - 生成顺序 Base → Space → Vein → Ruin → DiscoveryNode（DigGrid.Generate 集中定义）；
    ///  - GameManager：UpgradeSystem + EquipmentProgression（DEV-010）+ RunRiskState（DEV-011）
    ///    + DepthRegionProgression（DEV-009）+ HUD/Inventory/BlockDropHook；
    ///  - RuinSealSystem + HotRockSystem（DEV-012/013，DrillVehicle 在 Start 经 FindFirstObjectByType 找到）；
    ///  - 玩家 DrillVehicle + OreScanner（文明信号扫描）+ CameraFollow；
    ///  - DiscoveryNodeV1Probe（播放验收驱动，autoRun）。
    ///
    /// 不重写 DEV-004~013 任何系统；不建第二套 Grid / Inventory / Region / Block HP / collapse / heat。
    /// 资产惰性创建：DiscoveryNodeV1Test 复用既有地层/矿/特殊块 .asset（不新建重复 tile）。
    /// </summary>
    public static class DiscoveryNodeV1Builder
    {
        const string ScenePath = "Assets/Scenes/DiscoveryNodeV1Test.unity";
        const string SceneFolder = "Assets/Scenes";
        const string DataFolder = "Assets/Ashfall/Data";

        const int GridWidth = 48;
        const int GridDepth = 64;
        const int AcceptanceSeed = 20260907;

        [MenuItem("灰烬之下/搭建 DEV-014 地下发现节点测试场景")]
        public static void Build()
        {
            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>($"{DataFolder}/TileDatabase.asset");
            var dirt = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Dirt_泥土.asset");
            var iron = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Iron_铁矿.asset");
            var copper = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Copper_铜矿.asset");
            var silver = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Silver_银矿.asset");
            var gold = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Gold_金矿.asset");
            var platinum = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Platinum_铂金.asset");
            var emerald = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Emerald_绿宝石.asset");
            var diamond = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Diamond_钻石.asset");
            var supportRock = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/SupportRock_承重岩.asset");
            var looseRock = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/LooseRock_松散岩.asset");
            var hotRock = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/HotRock_高温岩.asset");
            var seal = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/RuinSeal_遗迹封印.asset");
            var wall = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/RuinWall_遗迹墙.asset");
            var coreTile = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/AncientRelayCore_中继核心.asset");
            var ancientAlloy = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/AncientAlloy_古代合金.asset");
            var dataFragment = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/AncientDataFragment_古代数据碎片.asset");

            if (db == null || dirt == null || iron == null || copper == null || silver == null || gold == null
                || supportRock == null || looseRock == null || hotRock == null || seal == null
                || ancientAlloy == null || dataFragment == null)
            {
                Debug.LogError("[DEV-014] 必要资产缺失。请先运行 Tools/Ashfall/一键生成默认方块与数据库");
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

            OreReservedRect[] sharedReserved =
            {
                new OreReservedRect { label = "SurfaceBand", x = 0, y = 0, w = GridWidth, h = 3 },
            };

            // ---- UndergroundSpaceGenerator（DEV-008）----
            var spaceGen = fgGo.AddComponent<UndergroundSpaceGenerator>();
            spaceGen.grid = digGrid; spaceGen.seedOverride = -1;
            spaceGen.bands = new[]
            {
                new UndergroundSpaceBand { bandName = "Shallow", minDepth = DepthRegionLayout.Shallow.spaceStartY, maxDepth = DepthRegionLayout.Shallow.maxDepth, pocketTarget = 5, pocketMinCells = 5, pocketMaxCells = 9, pocketWidthMax = 4, pocketHeightMax = 3, tunnelTarget = 3, tunnelMinLength = 3, tunnelMaxLength = 5, branchTarget = 0, smallRoomTarget = 1, branchSideMin = 2, branchSideMax = 3 },
                new UndergroundSpaceBand { bandName = "Mid", minDepth = DepthRegionLayout.Mid.minDepth, maxDepth = DepthRegionLayout.Mid.maxDepth, pocketTarget = 6, pocketMinCells = 7, pocketMaxCells = 12, pocketWidthMax = 5, pocketHeightMax = 4, tunnelTarget = 4, tunnelMinLength = 4, tunnelMaxLength = 6, branchTarget = 1, smallRoomTarget = 1, branchSideMin = 2, branchSideMax = 3 },
                new UndergroundSpaceBand { bandName = "Deep", minDepth = DepthRegionLayout.Deep.minDepth, maxDepth = DepthRegionLayout.Deep.maxDepth, pocketTarget = 7, pocketMinCells = 9, pocketMaxCells = 15, pocketWidthMax = 6, pocketHeightMax = 4, tunnelTarget = 4, tunnelMinLength = 6, tunnelMaxLength = 8, branchTarget = 2, smallRoomTarget = 1, branchSideMin = 2, branchSideMax = 4 },
            };
            spaceGen.reservedRects = sharedReserved;
            digGrid.undergroundSpaceGenerator = spaceGen;

            // ---- OreVeinGenerator（DEV-007）----
            var oreGen = fgGo.AddComponent<OreVeinGenerator>();
            oreGen.grid = digGrid; oreGen.seedOverride = -1;
            oreGen.bands = new[]
            {
                new OreDepthBand { bandName = "Shallow", minDepth = DepthRegionLayout.Shallow.minDepth, maxDepth = DepthRegionLayout.Shallow.maxDepth, ores = new[] { iron, copper }, weights = new[] { 70f, 30f }, veinMinSize = 2, veinMaxSize = 4, veinFrequency = 0.6f },
                new OreDepthBand { bandName = "Mid", minDepth = DepthRegionLayout.Mid.minDepth, maxDepth = DepthRegionLayout.Mid.maxDepth, ores = new[] { copper, silver, gold }, weights = new[] { 60f, 25f, 15f }, veinMinSize = 3, veinMaxSize = 5, veinFrequency = 0.7f },
                new OreDepthBand { bandName = "Deep", minDepth = DepthRegionLayout.Deep.minDepth, maxDepth = DepthRegionLayout.Deep.maxDepth, ores = new[] { gold, platinum, emerald, diamond }, weights = new[] { 40f, 30f, 20f, 10f }, veinMinSize = 4, veinMaxSize = 6, veinFrequency = 0.7f },
            };
            oreGen.reservedRects = sharedReserved;
            digGrid.oreVeinGenerator = oreGen;

            // ---- RuinGenerator（DEV-013）----
            var ruinGen = fgGo.AddComponent<RuinGenerator>();
            ruinGen.grid = digGrid; ruinGen.seedOverride = -1;
            ruinGen.wallTile = wall; ruinGen.sealTile = seal;
            ruinGen.coreTile = coreTile; ruinGen.rewardTile = ancientAlloy;
            digGrid.ruinGenerator = ruinGen;

            // ---- DiscoveryNodeGenerator（DEV-014；在 Ruin pass 之后叠加）----
            var nodeGen = fgGo.AddComponent<DiscoveryNodeGenerator>();
            nodeGen.grid = digGrid; nodeGen.seedOverride = -1;
            nodeGen.reservedRects = sharedReserved;   // node pass 额外自动避让 ruinGenerator.Instances bounds
            nodeGen.supportRockTile = supportRock;
            nodeGen.looseRockTile = looseRock;
            nodeGen.hotRockTile = hotRock;
            nodeGen.sealTile = seal;
            // Blocker2：A/B/D 奖励矿经现有 OreVeinGenerator 种植+登记（同一 oreGen），无第二个手工矿源。
            nodeGen.oreVeinSource = oreGen;
            // C 文明奖励（非 vein 语义）仍注入：
            nodeGen.ancientDataTile = dataFragment;
            nodeGen.ancientAlloyTile = ancientAlloy;
            digGrid.discoveryNodeGenerator = nodeGen;

            // ---- 环境系统（DrillVehicle 在 Start 经 FindFirstObjectByType 找到）----
            fgGo.AddComponent<BlockCollapseSystem>();   // DEV-004：SupportRock→LooseRock 坍塌（A/D 节点复用）
            fgGo.AddComponent<HotRockSystem>();          // DEV-012：HotRock 过热（B 节点复用）
            fgGo.AddComponent<RuinSealSystem>();         // DEV-013：RuinSeal 门（C 节点复用）

            // 生成
            digGrid.RegenerateFromDatabase();

            // 背景 Dirt
            var bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            bgTex.SetPixel(0, 0, Color.white); bgTex.Apply();
            var bgSprite = Sprite.Create(bgTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            bgSprite.name = "DEV014_DirtBg";
            var bgTile = ScriptableObject.CreateInstance<Tile>();
            bgTile.sprite = bgSprite; bgTile.name = "DEV014_DirtBg"; bgTile.flags = TileFlags.None;
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
            var oreScanner = playerGo.AddComponent<OreScanner>();
            oreScanner.scanRadius = 4;

            // ---- 总控 ----
            var gmGo = new GameObject("GameManager");
            gmGo.AddComponent<UpgradeSystem>();
            gmGo.AddComponent<EquipmentProgression>();
            gmGo.AddComponent<RunRiskState>();
            var regionProg = gmGo.AddComponent<DepthRegionProgression>();
            regionProg.grid = digGrid;
            gmGo.AddComponent<GameHUD>();
            gmGo.AddComponent<InventoryPanel>();
            var gm = gmGo.AddComponent<GameManager>();
            gm.spawnPoint = playerGo.transform.position;
            gm.usePlayerStartAsSpawn = true;
            gm.startingCash = 500;
            gmGo.AddComponent<BlockDropHook>().grid = digGrid;

            // RuinDiscoveryService（DEV-013 扫描接线；Deep 遗迹产生文明异常信号）
            var discovery = gmGo.AddComponent<RuinDiscoveryService>();
            discovery.generator = ruinGen;
            oreScanner.ruinDiscovery = discovery;

            // DiscoveryNodeDiscoveryService（DEV-014 Blocker1 扫描接线；节点模糊异常信号）
            var nodeDiscovery = gmGo.AddComponent<DiscoveryNodeDiscoveryService>();
            nodeDiscovery.discoveryNodeGenerator = nodeGen;
            oreScanner.discoveryNodeDiscovery = nodeDiscovery;

            // ---- SellTerminal（Blocker3 Vertical Slice：返航售货 → RunRisk.NotifyCargoSecured 收官）----
            var sellGo = new GameObject("SellTerminal");
            var sellCircle = sellGo.AddComponent<CircleCollider2D>();
            sellCircle.radius = 1f;
            sellGo.transform.position = digGrid.GridToWorld(GridWidth / 2, 1) + new Vector3(3f, 0, 0); // 地表坑口旁
            var sellTerminal = sellGo.AddComponent<SellTerminal>();

            // ---- 探针 ----
            var probe = gmGo.AddComponent<DiscoveryNodeV1Probe>();
            probe.grid = digGrid;
            probe.generator = nodeGen;
            probe.vehicle = vehicle;
            probe.equipment = gm.Equipment;
            probe.oreScanner = oreScanner;
            probe.discoveryService = nodeDiscovery;
            probe.collapse = fgGo.GetComponent<BlockCollapseSystem>();
            probe.hotRock = fgGo.GetComponent<HotRockSystem>();
            probe.seal = fgGo.GetComponent<RuinSealSystem>();
            probe.sealAsset = seal;
            probe.hotAsset = hotRock;
            probe.supportAsset = supportRock;
            probe.looseAsset = looseRock;
            probe.rewardGold = gold;
            probe.rewardPlatinum = platinum;
            probe.rewardDiamond = diamond;
            probe.fragmentAsset = dataFragment;
            probe.alloyAsset = ancientAlloy;
            probe.autoRun = true;

            // ---- 相机 ----
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = 10f;
            cam.transform.position = playerGo.transform.position + new Vector3(0, 0, -10f);
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.07f);
            var follow = camGo.AddComponent<CameraFollow>();
            follow.target = playerGo.transform; follow.orthoSize = 10f; follow.maxOrthoSize = 16f;

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
            Debug.Log($"[DEV-014] 地下发现节点验收场景已搭建：{ScenePath}（seed={AcceptanceSeed}，Base→Space→Vein→Ruin→DiscoveryNode）");
        }
    }
}
#endif
