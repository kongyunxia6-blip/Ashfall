#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// 一键搭建 DEV-001 Block 核心架构 V1 验收场景。
    /// 用法：菜单栏 → 灰烬之下 → 搭建 DEV-001 Block 测试场景
    ///
    /// 场景内容：
    ///  - 前景 Tilemap（方块）：12 列 × 4 行 = 48 格，其中 40 铁矿 + 8 普通岩（满足「连续破坏 ≥20」）
    ///  - 背景 Tilemap（Dirt）：整片填 Dirt 颜色，挖穿前景后露出
    ///  - 铁矿 digHits=4（完整→裂纹1→裂纹2→裂纹3→崩碎→消失 6 态）
    ///  - 普通岩 digHits=1（1 击崩碎）
    ///  - 铁矿 dropsIronOre=true，触发 IronOreDropHook 日志
    ///
    /// 玩家从地表坑口下潜，按 S+左键 向下挖掘。
    /// </summary>
    public static class BlockV1Builder
    {
        const string ScenePath = "Assets/Scenes/BlockV1Test.unity";
        const string SceneFolder = "Assets/Scenes";
        const string DataFolder = "Assets/Ashfall/Data";

        // 布局：12 列 × 4 行
        const int LayoutWidth = 12;
        const int LayoutHeight = 4;
        const int IronCount = 40;    // ≥20 满足验收
        const int RockCount = 8;

        [MenuItem("灰烬之下/搭建 DEV-001 Block 测试场景")]
        public static void Build()
        {
            // ---- 0. 确保数据库与方块存在 ----
            AshfallSetup.CreateDefaults();
            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>($"{DataFolder}/TileDatabase.asset");
            var iron = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Iron_铁矿.asset");
            var hardRock = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/HardRock_硬岩.asset");
            var dirt = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Dirt_泥土.asset");

            if (db == null || iron == null || hardRock == null || dirt == null)
            {
                Debug.LogError("[DEV-001] 必要资产缺失。请先运行 Tools/Ashfall/一键生成默认方块与数据库");
                return;
            }

            // ---- 1. 配置铁矿与普通岩的不同耐久 ----
            // 铁矿：digHits=4（完整→裂纹1→裂纹2→裂纹3→崩碎→消失 6 态），dropsIronOre=true
            iron.digHits = 4;
            iron.dropsIronOre = true;
            EditorUtility.SetDirty(iron);

            // 普通岩：digHits=1（1 击崩碎）
            hardRock.digHits = 1;
            EditorUtility.SetDirty(hardRock);

            // ---- 2. 新建场景 ----
            if (!AssetDatabase.IsValidFolder(SceneFolder))
                AssetDatabase.CreateFolder("Assets", "Scenes");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // ---- 3. Grid + 前景 Tilemap + 背景 Tilemap ----
            var gridGo = new GameObject("Grid");
            var unityGrid = gridGo.AddComponent<Grid>();
            unityGrid.cellSize = Vector3.one;
            unityGrid.cellGap = Vector3.zero;
            unityGrid.cellLayout = GridLayout.CellLayout.Rectangle;

            // 前景 Tilemap（方块）
            var fgGo = new GameObject("Tilemap_Foreground");
            fgGo.transform.SetParent(gridGo.transform);
            var fgTilemap = fgGo.AddComponent<Tilemap>();
            var fgRenderer = fgGo.AddComponent<TilemapRenderer>();
            fgRenderer.sortingOrder = 1;   // 前景在上

            // 背景 Tilemap（Dirt）
            var bgGo = new GameObject("Tilemap_Background");
            bgGo.transform.SetParent(gridGo.transform);
            var bgTilemap = bgGo.AddComponent<Tilemap>();
            var bgRenderer = bgGo.AddComponent<TilemapRenderer>();
            bgRenderer.sortingOrder = 0;   // 背景在下

            // ---- 4. DigGrid（挂在前景 Tilemap 上，负责挖掘/裂纹/崩碎）----
            var digGrid = fgGo.AddComponent<DigGrid>();
            digGrid.tilemap = fgTilemap;
            digGrid.backgroundTilemap = bgTilemap;
            digGrid.database = db;
            digGrid.width = 48;            // 跟 Game.unity 同宽，保持一致
            digGrid.depth = 40;            // 测试场景，40 层够用
            digGrid.useRandomSeed = false;
            digGrid.seed = 20260903;
            digGrid.enableFallingRocks = false;   // 测试场景禁落石，结果可预期

            // DEV-001：运行时布局组件——Play 时 DigGrid.Awake→Generate 会重新随机生成，
            // 本组件在 Start() 时再次应用精确布局（避免 Editor SetTile 被 Play 覆盖）
            var layout = fgGo.AddComponent<BlockV1TestLayout>();
            layout.grid = digGrid;
            layout.layoutWidth = LayoutWidth;
            layout.layoutHeight = LayoutHeight;
            layout.startY = 5;
            layout.ironEveryN = 6;
            layout.ironDigHits = 4;
            layout.rockDigHits = 1;

            // 先跑一次 Generate 让 grid 数组成型，再用 SetTile 覆盖成精确布局
            digGrid.RegenerateFromDatabase();

            // ---- 5. 填充 24 格精确布局（混合铁矿与普通岩，从 y=5 开始避开坑口）----
            int ironLeft = IronCount;
            int rockLeft = RockCount;
            int startY = 5;
            int startX = digGrid.width / 2 - LayoutWidth / 2;   // 水平居中

            for (int ly = 0; ly < LayoutHeight; ly++)
            {
                for (int lx = 0; lx < LayoutWidth; lx++)
                {
                    int x = startX + lx;
                    int y = startY + ly;
                    if (!digGrid.InBounds(x, y)) continue;

                    // 铁矿 5:1 混合普通岩（随机但确定性种子）
                    bool isIron = (lx + ly * LayoutWidth) % 6 != 5;   // 每 6 格 1 个普通岩
                    if (isIron && ironLeft > 0)
                    {
                        digGrid.SetTile(x, y, iron);
                        ironLeft--;
                    }
                    else if (rockLeft > 0)
                    {
                        digGrid.SetTile(x, y, hardRock);
                        rockLeft--;
                    }
                    else
                    {
                        digGrid.SetTile(x, y, iron);   // 兜底
                    }
                }
            }

            // ---- 6. 背景 Tilemap 填 Dirt ----
            // 用一个临时 Tile（颜色由 Tilemap.SetColor 控制）
            var bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            bgTex.SetPixel(0, 0, Color.white);
            bgTex.Apply();
            var bgSprite = Sprite.Create(bgTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            bgSprite.name = "DEV001_DirtBg";
            var bgTile = ScriptableObject.CreateInstance<Tile>();
            bgTile.sprite = bgSprite;
            bgTile.name = "DEV001_DirtBg";
            // 关键：Tile 资产默认 flags=LockColor，会让 SetTile 后 cell 的 SetColor 被忽略。
            // 必须显式改为 None，让每个 cell 能独立 SetColor。
            bgTile.flags = TileFlags.None;

            for (int y = 0; y < digGrid.depth; y++)
            {
                for (int x = 0; x < digGrid.width; x++)
                {
                    var cell = new Vector3Int(x, -y, 0);
                    bgTilemap.SetTile(cell, bgTile);
                    // 再保险一次：清 cell flags 后 SetColor
                    bgTilemap.SetTileFlags(cell, TileFlags.None);
                    bgTilemap.SetColor(cell, dirt.color);
                }
            }

            // ---- 7. 玩家 ----
            float spawnX = digGrid.width * 0.5f;
            var playerGo = new GameObject("Player");
            playerGo.transform.position = new Vector3(spawnX, 1f, 0f);

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
            vehicle.startMode = DrillVehicle.MovementMode.Hover;
            vehicle.startJetting = false;

            // ---- 8. 总控 ----
            var gmGo = new GameObject("GameManager");
            gmGo.AddComponent<UpgradeSystem>();
            gmGo.AddComponent<GameHUD>();
            gmGo.AddComponent<InventoryPanel>();

            var gm = gmGo.AddComponent<GameManager>();
            gm.spawnPoint = new Vector3(spawnX, 1f, 0f);
            gm.usePlayerStartAsSpawn = true;
            gm.startingCash = 100;

            // DEV-001：铁矿掉落事件钩子（挂在 GameManager 上）
            var hook = gmGo.AddComponent<IronOreDropHook>();
            hook.grid = digGrid;

            // ---- 9. 地表基地触发区 ----
            var baseGo = new GameObject("SurfaceBase");
            baseGo.transform.position = new Vector3(spawnX, -0.5f, 0f);
            var box = baseGo.AddComponent<BoxCollider2D>();
            box.isTrigger = true;
            box.size = new Vector2(9f, 3f);
            baseGo.AddComponent<SurfaceBase>();

            // ---- 10. 相机 ----
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 12f;
            cam.transform.position = new Vector3(spawnX, 1f, -10f);
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.07f);

            var follow = camGo.AddComponent<CameraFollow>();
            follow.target = playerGo.transform;
            follow.orthoSize = 12f;
            follow.maxOrthoSize = 16f;

            // ---- 11. 保存并加入 Build Settings ----
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

            Debug.Log($"[DEV-001] Block 测试场景已搭建：{ScenePath}\n" +
                      $"布局：{LayoutWidth}×{LayoutHeight} = {LayoutWidth * LayoutHeight} 格（{IronCount} 铁矿 + {RockCount} 普通岩），" +
                      $"y={startY}~{startY + LayoutHeight - 1}，x={startX}~{startX + LayoutWidth - 1}\n" +
                      "铁矿 digHits=4（完整→裂纹1→裂纹2→裂纹3→崩碎→消失 6 态），普通岩 digHits=1\n" +
                      "铁矿 dropsIronOre=true → IronOreDropHook 会在崩碎时打 [DEV-001] 日志\n" +
                      "操作：按 S+左键 向下挖掘，观察裂纹视觉与崩碎后 Dirt 背景露出");
        }
    }
}
#endif
