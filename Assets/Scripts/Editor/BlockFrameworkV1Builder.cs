#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// DEV-012：一键搭建 BlockFrameworkV1Test 验收场景。
    /// 用法：菜单 → 灰烬之下 → 搭建 DEV-012 地层/特殊块框架测试场景
    ///
    /// 场景内容：
    ///  - DigGrid + 分层地层（Shallow/Mid/Deep 代表层由数据库分层决定）
    ///  - BlockFrameworkV1TestLayout 运行时叠加：硬度墙 / SupportRock 柱 / HotRock A-B 柱
    ///  - GameManager：UpgradeSystem + EquipmentProgression（DEV-010 权威，供 CapabilityResolver 查询）
    ///  - BlockCollapseSystem（DEV-004，SupportRock 环境反应，禁止重写）
    ///  - HotRockSystem（DEV-012，HotRock 过热门控）
    ///  - BlockFrameworkV1Probe（播放验收驱动，autoRun）
    ///
    /// 资产：惰性创建 HotRock_高温岩（绕开 AshfallSetup）；复用既有 Dirt/SupportRock/LooseRock/HardRock/Iron。
    /// 关键：不重写 BlockCollapseSystem；不建第二套 SupportRock 系统。
    /// </summary>
    public static class BlockFrameworkV1Builder
    {
        const string ScenePath = "Assets/Scenes/Tests/BlockFrameworkV1Test.unity";
        const string SceneFolder = "Assets/Scenes";
        const string DataFolder = "Assets/Ashfall/Data";

        [MenuItem("灰烬之下/搭建 DEV-012 地层/特殊块框架测试场景")]
        public static void Build()
        {
            EnsureHotRockAsset();
            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>($"{DataFolder}/TileDatabase.asset");
            var hotRock = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/HotRock_高温岩.asset");
            var supportRock = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/SupportRock_承重岩.asset");
            var looseRock = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/LooseRock_松散岩.asset");
            var hardRock = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/HardRock_硬岩.asset");
            var dirt = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Dirt_泥土.asset");
            var iron = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Iron_铁矿.asset");
            if (db == null || hotRock == null || supportRock == null || looseRock == null ||
                hardRock == null || dirt == null || iron == null)
            {
                Debug.LogError("[DEV-012] 必要资产缺失。请先运行 Tools/Ashfall/一键生成默认方块与数据库");
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

            // ---- DigGrid + 布局 ----
            var digGrid = fgGo.AddComponent<DigGrid>();
            digGrid.tilemap = fgTilemap;
            digGrid.backgroundTilemap = bgTilemap;
            digGrid.database = db;
            digGrid.width = 48;
            digGrid.depth = 64;
            digGrid.useRandomSeed = false;
            digGrid.seed = 20260906;
            digGrid.enableFallingRocks = false;   // 避免 DEV-001 随机落石干扰环境坍塌/硬度判定
            digGrid.breakDuration = 0f;

            // 布局重放
            var layout = fgGo.AddComponent<BlockFrameworkV1TestLayout>();
            layout.grid = digGrid;
            layout.dirt = dirt;
            layout.hardRock = hardRock;
            layout.supportRock = supportRock;
            layout.looseRock = looseRock;
            layout.hotRock = hotRock;
            layout.iron = iron;
            layout.anchorY = 6;
            layout.hardnessX = 18;
            layout.supportX = 24;
            layout.hotX = 30;

            digGrid.RegenerateFromDatabase();

            // 背景 Dirt
            var bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            bgTex.SetPixel(0, 0, Color.white); bgTex.Apply();
            var bgSprite = Sprite.Create(bgTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            bgSprite.name = "DEV012_DirtBg";
            var bgTile = ScriptableObject.CreateInstance<Tile>();
            bgTile.sprite = bgSprite; bgTile.name = "DEV012_DirtBg"; bgTile.flags = TileFlags.None;
            for (int y = 0; y < digGrid.depth; y++)
                for (int x = 0; x < digGrid.width; x++)
                {
                    var cell = new Vector3Int(x, -y - 1, 0);
                    bgTilemap.SetTile(cell, bgTile);
                    bgTilemap.SetTileFlags(cell, TileFlags.None);
                    bgTilemap.SetColor(cell, dirt.color);
                }

            // ---- 玩家 ----
            Vector2Int stand = layout.StandCell(layout.hardnessX);
            Vector3 spawnPos = digGrid.GridToWorld(stand.x, stand.y);
            var playerGo = new GameObject("Player");
            playerGo.transform.position = spawnPos;
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
            var feel = playerGo.AddComponent<MiningFeelController>();
            feel.grid = digGrid;
            feel.attackInterval = 0.28f;
            feel.showTargetHighlight = true;
            var hotRockSys = playerGo.AddComponent<HotRockSystem>();   // 供 DrillVehicle 引用

            // ---- 总控 ----
            var gmGo = new GameObject("GameManager");
            gmGo.AddComponent<UpgradeSystem>();
            gmGo.AddComponent<EquipmentProgression>();   // DEV-010 权威（CapabilityResolver 查询）
            gmGo.AddComponent<GameHUD>();
            gmGo.AddComponent<InventoryPanel>();
            var gm = gmGo.AddComponent<GameManager>();
            gm.spawnPoint = spawnPos;
            gm.usePlayerStartAsSpawn = true;
            gm.startingCash = 100;
            gmGo.AddComponent<BlockDropHook>().grid = digGrid;
            var visual = gmGo.AddComponent<BlockVisualController>();
            visual.grid = digGrid;

            // 环境系统 + 探针
            var collapse = gmGo.AddComponent<BlockCollapseSystem>();
            collapse.grid = digGrid;
            collapse.maxCollapseHeight = 5;
            collapse.warningDuration = 0.4f;

            var probe = gmGo.AddComponent<BlockFrameworkV1Probe>();
            probe.grid = digGrid;
            probe.collapse = collapse;
            probe.hotRock = hotRockSys;
            probe.vehicle = vehicle;
            probe.layout = layout;
            probe.equipment = gm.Equipment;
            probe.hotRockAsset = hotRock;
            probe.supportRockAsset = supportRock;
            probe.hardRockAsset = hardRock;
            probe.autoRun = true;

            // ---- 相机 ----
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

            Debug.Log($"[DEV-012] 地层/特殊块框架测试场景已搭建：{ScenePath}\n" +
                      "硬度墙(18)/SupportRock柱(24)/HotRock A-B(30)；EquipmentProgression 权威；HotRockSystem+BlockCollapseSystem 已挂。");
        }

        // ---------- HotRock 资产（惰性创建） ----------
        static void EnsureHotRockAsset()
        {
            if (!AssetDatabase.IsValidFolder(DataFolder))
            {
                Debug.LogError("[DEV-012] Data 目录不存在。请先运行 Tools/Ashfall/一键生成默认方块与数据库");
                return;
            }
            string path = $"{DataFolder}/HotRock_高温岩.asset";
            var existing = AssetDatabase.LoadAssetAtPath<TileDefinition>(path);
            if (existing != null)
            {
                bool dirty = false;
                if (existing.blockType != BlockType.HotRock) { existing.blockType = BlockType.HotRock; dirty = true; }
                if (existing.digHits != 4) { existing.digHits = 4; dirty = true; }
                if (existing.hardness != 1) { existing.hardness = 1; dirty = true; }   // 硬度不是 HotRock 的墙（能力门才是）
                if (existing.value != 0) { existing.value = 0; dirty = true; }
                if (dirty) { EditorUtility.SetDirty(existing); AssetDatabase.SaveAssets(); }
                return;
            }

            var tile = ScriptableObject.CreateInstance<TileDefinition>();
            tile.displayName = "HotRock_高温岩";
            // 视觉：黑色/深灰 + 暗红发光裂隙（占位色）
            tile.color = new Color(0.12f, 0.10f, 0.12f);
            tile.hardness = 1;
            tile.drillTime = 0.3f;
            tile.value = 0;
            tile.isSolid = true;
            tile.isHazard = false;
            tile.hazardDamage = 0;
            tile.digHits = 4;
            tile.weight = 0f;
            tile.stackLimit = 1;
            tile.gridWidth = 1;
            tile.dropId = "";
            tile.blockType = BlockType.HotRock;
            AssetDatabase.CreateAsset(tile, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[DEV-012] 已创建特殊块资产：{path}（HotRock，digHits=4，无冷却需过热锁定）");
        }
    }
}
#endif
