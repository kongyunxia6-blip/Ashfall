#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// 一键搭建 DEV-002 Block 受击表现与崩碎动画管线 V1 验收场景。
    /// 用法：菜单栏 → 灰烬之下 → 搭建 DEV-002 Block 视觉测试场景
    ///
    /// 场景内容（在 DEV-001 基础上新增表现层）：
    ///  - 前景 Tilemap（方块）：12 列 × 4 行 = 48 格（40 铁矿 + 8 普通岩）
    ///  - 背景 Tilemap（Dirt）：挖穿前景后露出
    ///  - 铁矿实例耐久 4（完整→裂纹1→裂纹2→裂纹3→崩碎→消失 6 态）
    ///  - 普通岩实例耐久 1（1 击崩碎）
    ///  - breakDuration = 0.25s：崩碎窗口，BlockVisualController 在此播放 break 帧
    ///  - 铁矿 / 普通岩各挂一套 BlockVisualProfile（placeholder Sprite，可在 Inspector 替换）
    ///  - BlockVisualController：命中闪亮 + 碎屑 + 崩碎动画 + 音效事件入口
    ///
    /// 先自动生成 placeholder 视觉资产（BlockVisualProfileBuilder），再搭场景。
    /// </summary>
    public static class BlockV2Builder
    {
        const string ScenePath = "Assets/Scenes/Tests/BlockV2Test.unity";
        const string SceneFolder = "Assets/Scenes";
        const string DataFolder = "Assets/Ashfall/Data";

        const int LayoutWidth = 12;
        const int LayoutHeight = 4;
        const int IronCount = 40;
        const int RockCount = 8;

        [MenuItem("灰烬之下/搭建 DEV-002 Block 视觉测试场景")]
        public static void Build()
        {
            // ---- 0. 确保数据库 + placeholder 视觉资产存在 ----
            AshfallSetup.CreateDefaults();
            BlockVisualProfileBuilder.Build();

            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>($"{DataFolder}/TileDatabase.asset");
            var iron = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Iron_铁矿.asset");
            var hardRock = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/HardRock_硬岩.asset");
            var dirt = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Dirt_泥土.asset");

            if (db == null || iron == null || hardRock == null || dirt == null)
            {
                Debug.LogError("[DEV-002] 必要资产缺失。请先运行 Tools/Ashfall/一键生成默认方块与数据库");
                return;
            }

            // ---- 1. 新建场景 ----
            if (!AssetDatabase.IsValidFolder(SceneFolder))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // ---- 2. Grid + 前景 Tilemap + 背景 Tilemap ----
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

            // ---- 3. DigGrid（DEV-002：breakDuration>0 给崩碎动画窗口）----
            var digGrid = fgGo.AddComponent<DigGrid>();
            digGrid.tilemap = fgTilemap;
            digGrid.backgroundTilemap = bgTilemap;
            digGrid.database = db;
            digGrid.width = 48;
            digGrid.depth = 40;
            digGrid.useRandomSeed = false;
            digGrid.seed = 20260903;
            digGrid.enableFallingRocks = false;
            digGrid.breakDuration = 0.25f;   // 崩碎窗口，BlockVisualController 播放 break 帧

            // 运行时布局重放（复用 DEV-001 的 BlockV1TestLayout，SetTile 实例耐久覆盖）
            var layout = fgGo.AddComponent<BlockV1TestLayout>();
            layout.grid = digGrid;
            layout.layoutWidth = LayoutWidth;
            layout.layoutHeight = LayoutHeight;
            layout.startY = 5;
            layout.ironEveryN = 6;
            layout.ironDigHits = 4;
            layout.rockDigHits = 1;

            digGrid.RegenerateFromDatabase();

            // ---- 4. 填充精确布局（铁矿耐久4 / 普通岩耐久1）----
            int ironLeft = IronCount;
            int rockLeft = RockCount;
            int startY = 5;
            int startX = digGrid.width / 2 - LayoutWidth / 2;

            for (int ly = 0; ly < LayoutHeight; ly++)
            {
                for (int lx = 0; lx < LayoutWidth; lx++)
                {
                    int x = startX + lx;
                    int y = startY + ly;
                    if (!digGrid.InBounds(x, y)) continue;

                    bool isIron = (lx + ly * LayoutWidth) % 6 != 5;
                    if (isIron && ironLeft > 0)
                    {
                        digGrid.SetTile(x, y, iron, 4);
                        ironLeft--;
                    }
                    else if (rockLeft > 0)
                    {
                        digGrid.SetTile(x, y, hardRock, 1);
                        rockLeft--;
                    }
                    else
                    {
                        digGrid.SetTile(x, y, iron, 4);
                    }
                }
            }

            // ---- 5. 背景 Dirt ----
            var bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            bgTex.SetPixel(0, 0, Color.white);
            bgTex.Apply();
            var bgSprite = Sprite.Create(bgTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            bgSprite.name = "DEV002_DirtBg";
            var bgTile = ScriptableObject.CreateInstance<Tile>();
            bgTile.sprite = bgSprite;
            bgTile.name = "DEV002_DirtBg";
            bgTile.flags = TileFlags.None;

            for (int y = 0; y < digGrid.depth; y++)
                for (int x = 0; x < digGrid.width; x++)
                {
                    var cell = new Vector3Int(x, -y - 1, 0);
                    bgTilemap.SetTile(cell, bgTile);
                    bgTilemap.SetTileFlags(cell, TileFlags.None);
                    bgTilemap.SetColor(cell, dirt.color);
                }

            // ---- 6. 玩家 ----
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

            // ---- 7. 总控 + 表现层控制器 ----
            var gmGo = new GameObject("GameManager");
            gmGo.AddComponent<UpgradeSystem>();
            gmGo.AddComponent<GameHUD>();
            gmGo.AddComponent<InventoryPanel>();

            var gm = gmGo.AddComponent<GameManager>();
            gm.spawnPoint = new Vector3(spawnX, 1f, 0f);
            gm.usePlayerStartAsSpawn = true;
            gm.startingCash = 100;

            var hook = gmGo.AddComponent<BlockDropHook>();
            hook.grid = digGrid;

            // DEV-002：表现层控制器（命中闪亮 + 碎屑 + 崩碎动画 + 音效入口）
            var visual = gmGo.AddComponent<BlockVisualController>();
            visual.grid = digGrid;
            visual.breakDebrisCount = 6;
            visual.hitDebrisCount = 2;
            visual.hitFlashFrames = 2;

            // ---- 8. 地表基地触发区 ----
            var baseGo = new GameObject("SurfaceBase");
            baseGo.transform.position = new Vector3(spawnX, -0.5f, 0f);
            var box = baseGo.AddComponent<BoxCollider2D>();
            box.isTrigger = true;
            box.size = new Vector2(9f, 3f);
            baseGo.AddComponent<SurfaceBase>();

            // ---- 9. 相机 ----
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

            Debug.Log($"[DEV-002] Block 视觉测试场景已搭建：{ScenePath}\n" +
                      $"布局：{LayoutWidth}×{LayoutHeight} = {LayoutWidth * LayoutHeight} 格（{IronCount} 铁矿 + {RockCount} 普通岩）\n" +
                      "铁矿实例耐久=4（完整→裂纹1→裂纹2→裂纹3→崩碎→消失），普通岩实例耐久=1\n" +
                      "breakDuration=0.25s：崩碎窗口内 BlockVisualController 播放 break 帧 + 碎屑\n" +
                      "命中反馈：闪亮 + 碎屑；音效入口 onHitSound/onBreakSound（V1 占位）\n" +
                      "操作：按 S+左键 向下挖掘，观察裂纹 Sprite 切换、命中闪亮、崩碎动画与 Dirt 背景露出");
        }
    }
}
#endif
