#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// 一键搭建 DEV-006 地表据点 Hub 与整备流程验收场景。
    /// 用法：菜单栏 → 灰烬之下 → 搭建 DEV-006 地表据点测试场景
    ///
    /// 场景内容：
    ///  - SurfaceHubV1TestLayout 运行时重放：地表带 + 下矿口 3 列（铁/铜/锡）× 浅深 2 趟矿
    ///  - 地表四功能区（x 间距 ≥8，触发器不重叠，需实际移动交互）：
    ///      登陆舱/返航安全区 (SurfaceBase, enableAutoService=false 只做返航判定)
    ///      出售终端 (SellTerminal, 按 E 卖矿)
    ///      装备工作台 (UpgradeWorkbench, 范围内 U 开商店 1~6 购买)
    ///      能源补给点 (FuelStation, 按 E 加油)
    ///  - HubSign 世界空间标牌（免字体资产，IMGUI 投影）
    ///  - 玩家 Walk + MiningFeelController（单格挖掘）；GameHUD/InventoryPanel/BlockDropHook/BlockVisualController
    ///  - DigGrid.enableFallingRocks=false（确定性）；startingCash=200；升级价 costMultiplier=0.4
    ///  - Fuel 消耗在场景组件上调大（不动默认资产/默认参数），让一次短循环能感知能源压力
    ///
    /// 资产：全部复用既有资产（含 DEV-005 的 Tin_锡矿），本 Builder 不创建任何新资产。
    /// </summary>
    public static class SurfaceHubV1Builder
    {
        const string ScenePath = "Assets/Scenes/Tests/SurfaceHubV1Test.unity";
        const string SceneFolder = "Assets/Scenes";
        const string DataFolder = "Assets/Ashfall/Data";
        const string BackgroundFolder = "Assets/Art/Environment/Backgrounds/SurfaceHub";
        const string StructureFolder = "Assets/Art/Environment/SurfaceHub/Structures";
        const string PropFolder = "Assets/Art/Environment/SurfaceHub/Props";

        [MenuItem("灰烬之下/搭建 DEV-006 地表据点测试场景")]
        public static void Build()
        {
            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>($"{DataFolder}/TileDatabase.asset");
            var iron = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Iron_铁矿.asset");
            var copper = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Copper_铜矿.asset");
            var tin = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Tin_锡矿.asset");
            var dirt = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Dirt_泥土.asset");
            if (db == null || iron == null || copper == null || tin == null || dirt == null)
            {
                Debug.LogError("[DEV-006] 必要资产缺失（Iron/Copper/Tin/Dirt/DB）。请先运行 Tools/Ashfall/一键生成默认方块与数据库，并确认已含 DEV-005 的 Tin_锡矿");
                return;
            }

            // ---- 1. 新建场景 ----
            if (!AssetDatabase.IsValidFolder(SceneFolder))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            // 空场景开始，避免 DefaultGameObjects 自带相机与下方正式相机重复。
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

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
            fgRenderer.sortingOrder = -30;

            var bgGo = new GameObject("Tilemap_Background");
            bgGo.transform.SetParent(gridGo.transform);
            var bgTilemap = bgGo.AddComponent<Tilemap>();
            var bgRenderer = bgGo.AddComponent<TilemapRenderer>();
            bgRenderer.sortingOrder = -110;

            // ---- 3. DigGrid ----
            var digGrid = fgGo.AddComponent<DigGrid>();
            digGrid.tilemap = fgTilemap;
            digGrid.backgroundTilemap = bgTilemap;
            digGrid.database = db;
            digGrid.width = 48;
            digGrid.depth = 60;
            digGrid.useRandomSeed = false;
            digGrid.seed = 20260911;
            digGrid.enableFallingRocks = false;   // 确定性
            digGrid.breakDuration = 0.25f;

            // 运行时布局重放
            var layout = fgGo.AddComponent<SurfaceHubV1TestLayout>();
            layout.grid = digGrid;
            layout.iron = iron;
            layout.copper = copper;
            layout.tin = tin;
            layout.dirt = dirt;
            layout.spawnX = 17;
            layout.surfaceY = 1;
            layout.groundY = 2;
            layout.shaftColumns = new[] { 31, 32, 33 };

            digGrid.RegenerateFromDatabase();

            // ---- 4. 背景 Dirt ----
            var bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            bgTex.SetPixel(0, 0, Color.white);
            bgTex.Apply();
            var bgSprite = Sprite.Create(bgTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            bgSprite.name = "DEV006_DirtBg";
            var bgTile = ScriptableObject.CreateInstance<Tile>();
            bgTile.sprite = bgSprite;
            bgTile.name = "DEV006_DirtBg";
            bgTile.flags = TileFlags.None;

            for (int y = 0; y < digGrid.depth; y++)
                for (int x = 0; x < digGrid.width; x++)
                {
                    var cell = new Vector3Int(x, -y, 0);
                    bgTilemap.SetTile(cell, bgTile);
                    bgTilemap.SetTileFlags(cell, TileFlags.None);
                    bgTilemap.SetColor(cell, dirt.color);
                }

            // ---- 5. 地表美术：远景 ×2；中景/近景各三张横向拼合；建筑落在近景地面 ----
            BuildSurfaceArt(digGrid, layout.groundY);

            // ---- 6. 玩家（出生在登陆舱上，x=17）----
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

            playerGo.AddComponent<SpriteRenderer>().sortingOrder = 10;
            playerGo.AddComponent<PlayerVisual>();

            var vehicle = playerGo.AddComponent<DrillVehicle>();
            vehicle.grid = digGrid;
            vehicle.startMode = DrillVehicle.MovementMode.Walk;
            vehicle.startJetting = false;
            // Fuel 消耗调大（仅本场景组件覆盖），短循环即可感知能源压力
            vehicle.fuelIdleDrain = 1.0f;
            vehicle.fuelMoveDrain = 1.8f;
            vehicle.fuelDrillDrain = 3.2f;

            var feel = playerGo.AddComponent<MiningFeelController>();
            feel.grid = digGrid;
            feel.attackInterval = 0.28f;
            feel.showTargetHighlight = true;

            // ---- 7. 总控 + HUD + 背包 + 表现 + 探针 ----
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

            // ---- 8. 地表功能区：触发器与对应建筑的落点保持一致 ----
            var hubGo = new GameObject("SurfaceHub_Zones");

            // 7.0 地表据点大区 SurfaceHubZone：GameManager.IsAtSurface 的唯一权威来源。
            //     覆盖整个 Hub 地表带（全宽 cell 0..47，y 世界 ∈ [-3.0, 0.0] = 网格地表带 y 0..2），
            //     含下矿口入口上方 —— 玩家在地表带水平移动（Lander→Sell→Fuel→Workbench→洞口）恒为
            //     IsAtSurface=true；真正向下挖穿地面（下到 y≥3，世界 y &lt; -3.0）才离开本区 → false。
            //     中心取 cell(24,1) 的世界坐标，尺寸 48×3（cellSize=1 布局约定）。
            var hubCenter = digGrid.GridToWorld(24, layout.surfaceY);
            var hubZoneGo = new GameObject("SurfaceHubZone");
            hubZoneGo.transform.SetParent(hubGo.transform);
            hubZoneGo.transform.position = new Vector3(hubCenter.x, hubCenter.y, 0f);
            var hubCol = hubZoneGo.AddComponent<BoxCollider2D>();
            hubCol.size = new Vector2(48f, 3f);
            hubCol.isTrigger = true;
            hubZoneGo.AddComponent<SurfaceHubZone>();

            // 登陆舱 / 返航安全区（x=17）：只做 Lander 上下文（IsAtSurface 已由 SurfaceHubZone 负责），
            //     自动服务关闭 → 补给走 FuelStation
            var lander = NewZoneObject("Lander_LandingPad", hubGo.transform, digGrid, 17, layout.surfaceY, new Vector2(6f, 2f));
            var surfaceBase = lander.AddComponent<SurfaceBase>();
            surfaceBase.autoSellCargo = false;
            surfaceBase.enableAutoService = false;

            // 出售终端（x=25）
            var sell = NewZoneObject("SellTerminal", hubGo.transform, digGrid, 25, layout.surfaceY, new Vector2(4f, 2f));
            sell.AddComponent<SellTerminal>();

            // 装备工作台使用维修站建筑（x=43）
            var bench = NewZoneObject("UpgradeWorkbench", hubGo.transform, digGrid, 43, layout.surfaceY, new Vector2(4f, 2f));
            bench.AddComponent<UpgradeWorkbench>();

            // 能源补给点使用生命补给站建筑（x=8）
            var fuel = NewZoneObject("FuelStation", hubGo.transform, digGrid, 8, layout.surfaceY, new Vector2(4f, 2f));
            fuel.AddComponent<FuelStation>();

            // 7.5 标牌（各功能区上方 + 下矿入口）
            var signGo = new GameObject("SurfaceHub_Signs");
            signGo.transform.SetParent(hubGo.transform);

            MakeSign(signGo.transform, "能源补给点", digGrid.GridToWorld(8, layout.surfaceY), new Color(1f, 0.65f, 0.3f));
            MakeSign(signGo.transform, "登陆舱 / 返航安全区", digGrid.GridToWorld(17, layout.surfaceY), new Color(0.5f, 1f, 0.6f));
            MakeSign(signGo.transform, "出售终端", digGrid.GridToWorld(25, layout.surfaceY), new Color(1f, 0.85f, 0.35f));
            MakeSign(signGo.transform, "下矿入口 →（铁/铜/锡 矿脉）", digGrid.GridToWorld(32, layout.surfaceY), Color.white);
            MakeSign(signGo.transform, "装备工作台", digGrid.GridToWorld(43, layout.surfaceY), new Color(0.45f, 0.9f, 1f));

            // ---- 9. 相机 ----
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

            // ---- 10. 保存 + Build Settings ----
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

            Debug.Log($"[DEV-006] 地表据点测试场景已搭建：{ScenePath}\n" +
                      "地表美术：远景 ×2；中景/近景各三张拼合；建筑按参考图落在近景地面\n" +
                      "功能区：能源补给(x=8) · 登陆舱(x=17) · 出售(x=25) · 矿井(x=31..33) · 装备工作台(x=43)");
        }

        // ---------- 工具 ----------

        static void BuildSurfaceArt(DigGrid grid, int groundY)
        {
            var root = new GameObject("SurfaceHub_Art");
            float centerX = (grid.GridToWorld(0, 0).x + grid.GridToWorld(grid.width - 1, 0).x) * 0.5f;
            float groundTop = grid.GridToWorld(0, groundY).y + 0.5f;

            var far = LoadSprite($"{BackgroundFolder}/surface_sky_far.png");
            if (far != null)
                MakeSprite(root.transform, "Background_Far", far,
                    new Vector3(centerX, 3.2f, 0f), -100, Vector3.one * 2f);

            PlaceStrip(root.transform, "Background_Mid", centerX, groundTop - 1f, -80,
                LoadSprite($"{BackgroundFolder}/surface_midground_01.png"),
                LoadSprite($"{BackgroundFolder}/surface_midground_02.png"),
                LoadSprite($"{BackgroundFolder}/surface_midground_03.png"));

            PlaceStrip(root.transform, "Background_Foreground", centerX, groundTop - 2.5f, -20,
                LoadSprite($"{BackgroundFolder}/surface_foreground_01.png"),
                LoadSprite($"{BackgroundFolder}/surface_foreground_02.png"),
                LoadSprite($"{BackgroundFolder}/surface_foreground_03.png"));

            var structures = new GameObject("Structures");
            structures.transform.SetParent(root.transform);
            MakeSprite(structures.transform, "CommsStation", LoadSprite($"{StructureFolder}/surface_comms_station.png"), new Vector3(2.5f, groundTop, 0f), -10, Vector3.one);
            MakeSprite(structures.transform, "MedicalStation", LoadSprite($"{StructureFolder}/surface_medical_station.png"), new Vector3(8f, groundTop, 0f), -10, Vector3.one);
            MakeSprite(structures.transform, "StorageCrate", LoadSprite($"{PropFolder}/surface_storage_crate.png"), new Vector3(12f, groundTop, 0f), -10, Vector3.one);
            MakeSprite(structures.transform, "Lander", LoadSprite($"{StructureFolder}/surface_lander.png"), new Vector3(17f, groundTop, 0f), -10, Vector3.one);
            MakeSprite(structures.transform, "SellTerminal", LoadSprite($"{StructureFolder}/surface_sell_terminal.png"), new Vector3(25f, groundTop, 0f), -10, Vector3.one);
            MakeSprite(structures.transform, "MineEntrance", LoadSprite($"{StructureFolder}/surface_mine_entrance.png"), new Vector3(32f, groundTop, 0f), -10, Vector3.one);
            MakeSprite(structures.transform, "MineCart", LoadSprite($"{PropFolder}/surface_mine_cart.png"), new Vector3(37f, groundTop, 0f), -10, Vector3.one);
            MakeSprite(structures.transform, "RepairStation", LoadSprite($"{StructureFolder}/surface_repair_station.png"), new Vector3(43f, groundTop, 0f), -10, Vector3.one);
        }

        static Sprite LoadSprite(string path)
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null) Debug.LogWarning($"[DEV-006] 地表美术缺失：{path}");
            return sprite;
        }

        static GameObject MakeSprite(Transform parent, string name, Sprite sprite, Vector3 position, int sortingOrder, Vector3 scale)
        {
            if (sprite == null) return null;
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.position = position;
            go.transform.localScale = scale;
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.sortingOrder = sortingOrder;
            return go;
        }

        static void PlaceStrip(Transform parent, string groupName, float centerX, float bottomY, int sortingOrder, params Sprite[] sprites)
        {
            var group = new GameObject(groupName);
            group.transform.SetParent(parent);
            float totalWidth = 0f;
            foreach (var sprite in sprites) if (sprite != null) totalWidth += sprite.bounds.size.x;
            float cursor = centerX - totalWidth * 0.5f;
            for (int i = 0; i < sprites.Length; i++)
            {
                var sprite = sprites[i];
                if (sprite == null) continue;
                float x = cursor + sprite.bounds.size.x * 0.5f;
                float y = bottomY - sprite.bounds.min.y;
                MakeSprite(group.transform, $"Part_{i + 1:00}", sprite, new Vector3(x, y, 0f), sortingOrder, Vector3.one);
                cursor += sprite.bounds.size.x;
            }
        }

        static GameObject NewZoneObject(string name, Transform parent, DigGrid grid, int cellX, int cellY, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.position = grid.GridToWorld(cellX, cellY);
            var col = go.AddComponent<BoxCollider2D>();
            col.size = size;
            col.isTrigger = true;   // 组件 Awake 也会设，这里先设避免编辑器下 gizmo 误导
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
