#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// DEV-015：搭建 EconomyBalanceV1Test 验收场景（受控、确定性经济闭环）。
    /// 菜单：灰烬之下 → 搭建 DEV-015 经济平衡测试场景
    ///
    /// 场景内容（只复用现代 DEV 栈，无第二套系统）：
    ///  - DigGrid（width=32, depth=44, 固定 seed，无随机矿/空间/节点生成器 → 全地层填充物）；
    ///  - 中心 3 列宽竖井从地表坑口(y≤2)预清通到浅层画廊(y≈20) → 载具纯竖直真实下潜，消除穿岩几何不确定性；
    ///  - 地表 Workbench + 起始 Cash：探针在 Run 前经 EquipmentProgression.TryUpgrade 合法准备 Drill Lv2；
    ///  - 画廊下层排列「低值铁矿 pocket」（Lv0 载重 24 → 24 铁 = 满载）+ 紧邻 1 格高值 Copper（替换触发）；
    ///  - 真实 DrillVehicle（Hover）+ MiningFeelController + OreScanner + GameManager(UpgradeSystem/Equipment/RunRisk) + Workbench + SellTerminal + EconomyBalanceV1Probe。
    /// 玩家只走真实物理移动（下潜/返航）沿已清通通道；挖矿用真实 TryDigHit→HandleTileDug→InventoryGrid。
    /// 本场景刻意不挂任何生成器/节点/遗迹，保证「低值填满→高值顶替」经济场景 100% 可复现。
    /// </summary>
    public static class EconomyBalanceV1Builder
    {
        const string ScenePath = "Assets/Scenes/EconomyBalanceV1Test.unity";
        const string SceneFolder = "Assets/Scenes";
        const string DataFolder = "Assets/Ashfall/Data";

        const int W = 64;
        const int D = 48;
        const int Center = 32;
        const int GalleryY = 22;        // 画廊行（载具抵达高度）
        const int SurfaceClearY = 2;    // DigGrid 自带地表坑口最高行
        const int OreRowY = GalleryY + 5;  // 低值铁排所在行（站台下 5 格，挖得到且不挡移动）
        const int IronCount = 30;          // 铁排格数（>24 → 必能填满 Lv0 载重）
        const int IronStartX = Center - 14;  // x=18，落在 bedrock(1..62) 内

        [MenuItem("灰烬之下/搭建 DEV-015 经济平衡测试场景")]
        public static void Build()
        {
            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>($"{DataFolder}/TileDatabase.asset");
            var dirt = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Dirt_泥土.asset");
            var iron = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Iron_铁矿.asset");
            var tin = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Tin_锡矿.asset");
            var copper = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Copper_铜矿.asset");
            var gold = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Gold_金矿.asset");
            var bedrock = db != null ? db.bedrockTile : null;
            var emptyTile = db != null ? db.emptyTile : null;

            if (db == null || dirt == null || iron == null || tin == null || copper == null || gold == null
                || bedrock == null || emptyTile == null)
            {
                Debug.LogError("[DEV-015] 必要资产缺失。请先运行 Tools/Ashfall/一键生成默认方块与数据库");
                return;
            }

            if (!AssetDatabase.IsValidFolder(SceneFolder))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // ---- Grid + Tilemaps ----
            var gridGo = new GameObject("Grid");
            // DigGrid.Awake 会立即生成地图；先保持 inactive，等数据库和 Tilemap 引用配置完成再启用。
            gridGo.SetActive(false);
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
            bgGo.AddComponent<TilemapRenderer>().sortingOrder = 0;

            var digGrid = fgGo.AddComponent<DigGrid>();
            digGrid.tilemap = fgTilemap;
            digGrid.backgroundTilemap = bgTilemap;
            digGrid.database = db;
            digGrid.width = W;
            digGrid.depth = D;
            digGrid.useRandomSeed = false;
            digGrid.seed = 20260915;
            digGrid.enableFallingRocks = false;
            digGrid.breakDuration = 0f;
            digGrid.surfaceOpeningHalfWidth = 3;
            // 刻意不挂任何 OreVeinGenerator / UndergroundSpaceGenerator / RuinGenerator / DiscoveryNodeGenerator
            // → Generate 走 veinMode=false/spaceMode=false 分支：纯 strata 填充物 + 中心坑口留空。

            gridGo.SetActive(true);

            // 背景 Dirt
            var bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            bgTex.SetPixel(0, 0, Color.white); bgTex.Apply();
            var bgSprite = Sprite.Create(bgTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            bgSprite.name = "DEV015_DirtBg";
            var bgTile = ScriptableObject.CreateInstance<Tile>();
            bgTile.sprite = bgSprite; bgTile.name = "DEV015_DirtBg"; bgTile.flags = TileFlags.None;
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
            playerGo.transform.position = digGrid.GridToWorld(Center, 1);
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
            vehicle.startMode = DrillVehicle.MovementMode.Hover;
            vehicle.startJetting = false;
            playerGo.AddComponent<MiningFeelController>();
            playerGo.AddComponent<OreScanner>();

            // ---- 总控（同现代栈）----
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
            // 前置资金代表已完成的早期 Run；探针必须在 Surface / Workbench 通过正式交易花掉，
            // 不允许直接写任何装备等级。Drill Lv1 + Lv2 = 150 + 500。
            gm.startingCash = EquipmentCatalog.CostOf(EquipmentLine.Drill, 1)
                            + EquipmentCatalog.CostOf(EquipmentLine.Drill, 2);
            gmGo.AddComponent<BlockDropHook>().grid = digGrid;

            // ---- UpgradeWorkbench（Vertical Slice 开始前合法准备装备）----
            var workbenchGo = new GameObject("UpgradeWorkbench");
            var workbenchCollider = workbenchGo.AddComponent<BoxCollider2D>();
            workbenchCollider.size = new Vector2(3f, 2f);
            workbenchCollider.isTrigger = true;
            workbenchGo.transform.position = digGrid.GridToWorld(Center, 1);
            workbenchGo.AddComponent<UpgradeWorkbench>();

            // ---- SellTerminal（返航售货 → Run 收官）----
            var sellGo = new GameObject("SellTerminal");
            var sellCircle = sellGo.AddComponent<CircleCollider2D>();
            sellCircle.radius = 1f;
            sellCircle.isTrigger = true;
            sellGo.transform.position = digGrid.GridToWorld(Center + 5, 1);
            sellGo.AddComponent<SellTerminal>();

            // ---- 探针 ----
            var probe = gmGo.AddComponent<EconomyBalanceV1Probe>();
            probe.grid = digGrid;
            probe.vehicle = vehicle;
            probe.equipment = gm.Equipment;
            probe.oreScanner = playerGo.GetComponent<OreScanner>();
            probe.iron = iron;
            probe.highValueOre = copper;
            probe.gold = gold;
            probe.dirt = dirt;
            probe.emptyTile = emptyTile;
            probe.autoRun = true;

            // ---- 相机 ----
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = 8f;
            cam.transform.position = playerGo.transform.position + new Vector3(0, 0, -10f);
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.07f);
            var follow = camGo.AddComponent<CameraFollow>();
            follow.target = playerGo.transform; follow.orthoSize = 8f; follow.maxOrthoSize = 14f;

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
            Debug.Log($"[DEV-015] 经济平衡验收场景已搭建：{ScenePath}（受控竖井 + 低值满载→高值顶替 course）");
        }
    }
}
#endif
