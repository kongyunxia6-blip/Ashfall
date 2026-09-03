#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// 一键搭建 DEV-004 承重岩 / 局部坍塌 V1 验收场景。
    /// 用法：菜单栏 → 灰烬之下 → 搭建 DEV-004 承重坍塌测试场景
    ///
    /// 场景内容：
    ///  - SupportRockV1TestLayout 运行时重放三根竖直测试柱：
    ///    A(4 连 LooseRock 全塌) / B(Bedrock 截断传播) / C(普通岩不触发)
    ///  - 玩家挂 MiningFeelController（单格挖掘，验证 SupportRock 经 HitBlock→Break→Remove 挖除）
    ///  - BlockCollapseSystem（环境系统）：只监听 OnTileDug，SupportRock 真正移除才启动检查
    ///  - DigGrid.enableFallingRocks=false：关掉 DEV-001 的旧随机落石，避免干扰新坍塌系统的确定性判定
    ///  - breakDuration=0.25s：崩碎窗口沿用 DEV-002 表现
    ///
    /// 资产：惰性创建 SupportRock_承重岩 / LooseRock_松散岩（绕开 AshfallSetup，不污染既有资产）。
    /// </summary>
    public static class SupportRockV1Builder
    {
        const string ScenePath = "Assets/Scenes/SupportRockV1Test.unity";
        const string SceneFolder = "Assets/Scenes";
        const string DataFolder = "Assets/Ashfall/Data";

        [MenuItem("灰烬之下/搭建 DEV-004 承重坍塌测试场景")]
        public static void Build()
        {
            // ---- 0. 确保特殊块资产 + 数据库存在 ----
            EnsureSpecialTiles();
            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>($"{DataFolder}/TileDatabase.asset");
            var supportRock = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/SupportRock_承重岩.asset");
            var looseRock = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/LooseRock_松散岩.asset");
            if (db == null || supportRock == null || looseRock == null)
            {
                Debug.LogError("[DEV-004] 必要资产缺失。请先运行 Tools/Ashfall/一键生成默认方块与数据库");
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
            digGrid.seed = 20260904;
            digGrid.enableFallingRocks = false;   // 旧随机落石关闭，只测新坍塌系统
            digGrid.breakDuration = 0.25f;

            // 运行时布局重放（三根测试柱）
            var layout = fgGo.AddComponent<SupportRockV1TestLayout>();
            layout.grid = digGrid;
            layout.supportRock = supportRock;
            layout.looseRock = looseRock;
            layout.anchorY = 6;
            layout.groupAX = 18;
            layout.groupBX = 24;
            layout.groupCX = 30;
            layout.supportDigHits = 2;
            layout.looseDigHits = 1;

            digGrid.RegenerateFromDatabase();

            // ---- 4. 背景 Dirt ----
            var dirt = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Dirt_泥土.asset");
            var bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            bgTex.SetPixel(0, 0, Color.white);
            bgTex.Apply();
            var bgSprite = Sprite.Create(bgTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            bgSprite.name = "DEV004_DirtBg";
            var bgTile = ScriptableObject.CreateInstance<Tile>();
            bgTile.sprite = bgSprite;
            bgTile.name = "DEV004_DirtBg";
            bgTile.flags = TileFlags.None;

            for (int y = 0; y < digGrid.depth; y++)
                for (int x = 0; x < digGrid.width; x++)
                {
                    var cell = new Vector3Int(x, -y, 0);
                    bgTilemap.SetTile(cell, bgTile);
                    bgTilemap.SetTileFlags(cell, TileFlags.None);
                    bgTilemap.SetColor(cell, dirt != null ? dirt.color : new Color(0.3f, 0.25f, 0.2f));
                }

            // ---- 5. 玩家（出生在 A 组站格，头顶是支撑格）----
            int spawnX = layout.groupAX;
            Vector2Int stand = layout.StandCell(spawnX);
            Vector3 spawnPos = digGrid.GridToWorld(stand.x, stand.y);

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

            // ---- 6. 总控 + 环境坍塌系统 + 表现层 + 探针 ----
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

            // DEV-004：环境坍塌系统 + 验收探针
            var collapse = gmGo.AddComponent<BlockCollapseSystem>();
            collapse.grid = digGrid;
            collapse.maxCollapseHeight = 5;
            collapse.warningDuration = 0.4f;

            var probe = gmGo.AddComponent<SupportRockV1Probe>();
            probe.grid = digGrid;
            probe.collapse = collapse;

            // ---- 7. 相机 ----
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

            Debug.Log($"[DEV-004] 承重坍塌测试场景已搭建：{ScenePath}\n" +
                      $"布局：A=4连LooseRock全塌 / B=Bedrock截断 / C=普通岩不触发，锚定行 {layout.anchorY}\n" +
                      "玩家出生在 A 组站格（头顶承重岩，向上挖验证 HitBlock→Break→Remove→坍塌）\n" +
                      "BlockCollapseSystem: maxCollapseHeight=5 / warning=0.4s");
        }

        // ---------- 特殊块资产（惰性创建，绕开 AshfallSetup） ----------

        static void EnsureSpecialTiles()
        {
            if (!AssetDatabase.IsValidFolder(DataFolder))
            {
                Debug.LogError("[DEV-004] Data 目录不存在。请先运行 Tools/Ashfall/一键生成默认方块与数据库");
                return;
            }

            EnsureTile("SupportRock_承重岩", new Color(0.55f, 0.5f, 0.45f),
                       BlockType.SupportRock, digHits: 2, value: 0);
            EnsureTile("LooseRock_松散岩", new Color(0.68f, 0.6f, 0.42f),
                       BlockType.LooseRock, digHits: 1, value: 0);
        }

        static TileDefinition EnsureTile(string name, Color color, BlockType blockType,
                                         int digHits, int value)
        {
            string path = $"{DataFolder}/{name}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<TileDefinition>(path);
            if (existing != null)
            {
                // 已存在：确保关键字段正确（幂等修复）
                bool dirty = false;
                if (existing.blockType != blockType) { existing.blockType = blockType; dirty = true; }
                if (existing.digHits != digHits) { existing.digHits = digHits; dirty = true; }
                if (existing.value != value) { existing.value = value; dirty = true; }
                if (dirty) { EditorUtility.SetDirty(existing); AssetDatabase.SaveAssets(); }
                return existing;
            }

            var tile = ScriptableObject.CreateInstance<TileDefinition>();
            tile.displayName = name;
            tile.color = color;
            tile.hardness = 1;          // 起始钻头 Lv1 可挖（验收：SupportRock 可正常单格挖除）
            tile.drillTime = 0.3f;
            tile.value = value;
            tile.isSolid = true;
            tile.isHazard = false;
            tile.digHits = digHits;
            tile.weight = 0f;
            tile.stackLimit = 1;
            tile.gridWidth = 1;
            tile.dropId = "";
            tile.blockType = blockType;

            AssetDatabase.CreateAsset(tile, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[DEV-004] 已创建特殊块资产：{path}（{blockType}）");
            return tile;
        }
    }
}
#endif
