using System;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Ashfall
{
    /// <summary>
    /// 地下网格管理器：分层生成、挖穿、落石、Tilemap 渲染。
    /// 挂在哪：Grid 物体（或任意物体），需要拖入 tilemap 与 database 引用。
    /// 坐标约定：网格 y = 0 是地表，y 越大越深；世界坐标 y = -(y+0.5)，即越深世界 y 越小。
    /// </summary>
    public class DigGrid : MonoBehaviour
    {
        [Header("网格尺寸")]
        public int width = 48;
        [Tooltip("v2：原 520 太浅，母矿区（580+）几乎没有探索空间")]
        public int depth = 640;

        [Header("引用（必填）")]
        public Tilemap tilemap;
        public TileDatabase database;

        [Header("生成")]
        public bool useRandomSeed = true;
        public int seed = 12345;

        [Tooltip("地表坑口的半宽（格），让玩家有地方下潜")]
        public int surfaceOpeningHalfWidth = 3;

        [Header("落石")]
        public bool enableFallingRocks = true;

        [Tooltip("挖空一格后，上方方块塌落的概率 0~1")]
        [Range(0f, 1f)] public float rockFallChance = 0.3f;

        TileDefinition[,] grid;
        Tile solidTile;
        Grid layoutGrid;

        [Header("DEV-001 耐久 V1")]
        [Tooltip("背景层 Tilemap（可选）。若指定，挖穿前景后露出背景（背景 Tilemap sortingOrder 应低于前景）。" +
                 "测试场景 BlockV1Test.unity 用它来显示 Dirt 背景")]
        public Tilemap backgroundTilemap;

        /// <summary>DEV-001：每格当前耐久（旁路数组，跟 grid 平行）。0=崩碎；-1=空格/未初始化；&gt;0=剩余击数。</summary>
        int[,] curDurability;

        /// <summary>每格的世界尺寸（取自 Grid 组件，默认 1）</summary>
        Vector3 CellSize => layoutGrid != null ? layoutGrid.cellSize : Vector3.one;

        /// <summary>网格原点（取自 Grid 组件）</summary>
        Vector3 Origin => layoutGrid != null ? layoutGrid.transform.position : transform.position;

        /// <summary>有方块塌落到该格时触发（用于砸伤玩家）</summary>
        public event Action<Vector2Int> OnRockFell;

        /// <summary>某格被挖穿时触发（格子坐标, 被挖到的方块）</summary>
        public event Action<Vector2Int, TileDefinition> OnTileDug;

        public int Width => width;
        public int Depth => depth;

        void Awake()
        {
            if (useRandomSeed) seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            UnityEngine.Random.InitState(seed);

            solidTile = CreateSolidTile();
            layoutGrid = tilemap != null ? tilemap.layoutGrid : null;

            if (layoutGrid == null)
                Debug.LogWarning("[DigGrid] 未找到 Grid 组件，坐标换算将按 Cell Size = 1 处理。");

            Generate();
        }

        // ---------- 生成 ----------

        void Generate()
        {
            if (database == null)
            {
                Debug.LogError("[DigGrid] 未指定 TileDatabase，无法生成地图。请在 Inspector 拖入。");
                return;
            }

            grid = new TileDefinition[width, depth];
            curDurability = new int[width, depth];
            int center = width / 2;

            for (int y = 0; y < depth; y++)
            {
                DepthLayer layer = database.GetLayerAtDepth(y);

                for (int x = 0; x < width; x++)
                {
                    // 左右与底部边界：基岩，不可挖
                    if (x == 0 || x == width - 1 || y >= depth - 1)
                    {
                        grid[x, y] = database.bedrockTile;
                        continue;
                    }

                    // 地表坑口：留空，方便起步下潜
                    if (y <= 2 && Mathf.Abs(x - center) <= surfaceOpeningHalfWidth)
                    {
                        grid[x, y] = database.emptyTile;
                        continue;
                    }

                    // 天然空洞
                    if (layer != null && UnityEngine.Random.value < layer.caveChance)
                    {
                        grid[x, y] = database.emptyTile;
                        continue;
                    }

                    grid[x, y] = database.PickRandom(layer);
                }
            }

            // DEV-001：初始化当前耐久（实心格 = digHits，空格 = -1）
            for (int y = 0; y < depth; y++)
                for (int x = 0; x < width; x++)
                {
                    var def = grid[x, y];
                    curDurability[x, y] = (def != null && def.isSolid) ? def.digHits : -1;
                }

            RefreshAll();
        }

        // ---------- 渲染 ----------

        /// <summary>纯代码生成 1x1 白色方块 Tile，实现零美术依赖的原型渲染</summary>
        static Tile CreateSolidTile()
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();

            var sprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            sprite.name = "Ashfall_WhitePixel";

            var tile = ScriptableObject.CreateInstance<Tile>();
            tile.sprite = sprite;
            tile.name = "Ashfall_SolidTile";
            // DEV-001：Tile 默认 flags=LockColor 会让 SetTile 后 cell 的 SetColor 被忽略，
            // 显式改 None 才能让 RefreshTile 的裂纹阶段调灰生效。
            tile.flags = TileFlags.None;
            return tile;
        }

        void RefreshAll()
        {
            if (tilemap == null) return;
            tilemap.ClearAllTiles();
            for (int y = 0; y < depth; y++)
                for (int x = 0; x < width; x++)
                    RefreshTile(x, y);
        }

        void RefreshTile(int x, int y)
        {
            if (tilemap == null) return;

            var def = GetTile(x, y);
            var cell = new Vector3Int(x, -y, 0);

            if (def == null || !def.isSolid)
            {
                tilemap.SetTile(cell, null);
                return;
            }

            tilemap.SetTile(cell, solidTile);
            tilemap.SetTileFlags(cell, TileFlags.None);

            // DEV-001：按当前裂纹阶段调灰（完整=def.color → 崩碎=黑）
            int max = Mathf.Max(1, def.digHits);
            int cur = (curDurability != null && InBounds(x, y)) ? curDurability[x, y] : max;
            if (cur <= 0 || cur > max) cur = max;
            int stage = GetCrackStage(cur, max);
            float dark = stage / 3f;
            tilemap.SetColor(cell, Color.Lerp(def.color, Color.black, dark * 0.55f));
        }

        // ---------- 查询 ----------

        public bool InBounds(int x, int y) => x >= 0 && x < width && y >= 0 && y < depth;

        public TileDefinition GetTile(int x, int y)
        {
            // 懒重建：编译完成前进入 Play 会触发二次域重载，非序列化字段 grid 被清空
            // 而 Awake 不会重跑（实测踩坑：GetTile NRE → Error Pause 反复暂停编辑器）。
            // 首次查询时重建网格，而不是直接崩。
            if (grid == null) Generate();
            if (grid == null) return null;
            return InBounds(x, y) ? grid[x, y] : null;
        }

        public bool IsSolid(int x, int y)
        {
            var def = GetTile(x, y);
            return def != null && def.isSolid;
        }

        /// <summary>每格的世界尺寸（供外部做网格级碰撞，避免依赖 TilemapCollider2D）</summary>
        public Vector3 CellSizeWorld => CellSize;

        /// <summary>某世界坐标处的格子是否为实心（用于玩家网格阻挡）</summary>
        public bool IsSolidAtWorld(Vector3 worldPos)
        {
            var c = WorldToGrid(worldPos);
            return IsSolid(c.x, c.y);
        }

        // ---------- 坐标转换 ----------

        /// <summary>世界坐标 → 网格坐标（y 越大越深）</summary>
        public Vector2Int WorldToGrid(Vector3 worldPos)
        {
            Vector3 local = worldPos - Origin;
            Vector3 size = CellSize;

            int gx = Mathf.FloorToInt(local.x / Mathf.Max(0.0001f, size.x));
            int gy = Mathf.FloorToInt(-local.y / Mathf.Max(0.0001f, size.y));
            return new Vector2Int(gx, gy);
        }

        /// <summary>网格坐标 → 世界坐标（该格中心）</summary>
        public Vector3 GridToWorld(int x, int y)
        {
            Vector3 size = CellSize;
            return Origin + new Vector3((x + 0.5f) * size.x, -(y + 0.5f) * size.y, 0f);
        }

        /// <summary>某世界坐标处的深度（0 = 地表）</summary>
        public int DepthOf(Vector3 worldPos) => WorldToGrid(worldPos).y;

        // ---------- 挖掘 ----------

        /// <summary>挖穿一格。成功返回 true，并通过 dug 输出被挖到的方块</summary>
        public bool Dig(int x, int y, out TileDefinition dug)
        {
            dug = null;
            if (!InBounds(x, y)) return false;

            var def = GetTile(x, y);
            if (def == null || !def.isSolid) return false;

            grid[x, y] = database.emptyTile;
            RefreshTile(x, y);

            dug = def;
            OnTileDug?.Invoke(new Vector2Int(x, y), def);

            if (enableFallingRocks) TryRockFall(x, y);
            return true;
        }

        /// <summary>
        /// 落石：挖空一格后，正上方（y-1）的方块失去支撑，有概率塌落下来，可连锁。
        /// </summary>
        void TryRockFall(int x, int y)
        {
            int above = y - 1;
            if (above < 0) return;

            var upper = GetTile(x, above);
            if (upper == null || !upper.isSolid) return;
            if (UnityEngine.Random.value > rockFallChance) return;

            // 塌落：上方方块移动到刚挖空的格子
            grid[x, y] = upper;
            grid[x, above] = database.emptyTile;
            RefreshTile(x, y);
            RefreshTile(x, above);

            OnRockFell?.Invoke(new Vector2Int(x, y));

            // 连锁检查更上方
            TryRockFall(x, above);
        }

        // ---------- DEV-001：耐久 / 裂纹 / 崩碎 ----------

        /// <summary>
        /// DEV-001：单格受击。耐久 -1，未崩碎时刷新裂纹视觉；崩碎时走 Dig() 统一挖穿路径
        /// （含落石、OnTileDug）。返回 true 表示命中了实心格，dug 非空表示崩碎产出。
        /// 多次调用 HitBlock 是"击打多次"的意思——计数由 DigGrid 承载，玩家侧不再持有进度。
        /// </summary>
        public bool HitBlock(int x, int y, out TileDefinition dug)
        {
            dug = null;
            if (!InBounds(x, y)) return false;

            var def = GetTile(x, y);
            if (def == null || !def.isSolid) return false;

            if (curDurability == null) Generate();
            if (curDurability == null) return false;

            int cur = curDurability[x, y];
            if (cur <= 0) cur = Mathf.Max(1, def.digHits);   // 防御性初始化
            cur--;
            curDurability[x, y] = cur;

            if (cur <= 0)
            {
                // 崩碎：走 Dig() 统一挖穿路径（grid→empty + RefreshTile + 落石 + OnTileDug）
                return Dig(x, y, out dug);
            }

            // 未崩碎：只刷新视觉
            RefreshTile(x, y);
            return true;
        }

        /// <summary>DEV-001：某格当前耐久（剩余击数）。0 表示崩碎；-1 表示空格；正数表示剩余。</summary>
        public int GetDurability(int x, int y)
        {
            if (curDurability == null || !InBounds(x, y)) return -1;
            return curDurability[x, y];
        }

        /// <summary>
        /// DEV-001：裂纹阶段（0=完整 1=裂纹1 2=裂纹2 3=裂纹3/崩碎临界）。
        /// 按剩余耐久比例分档：≥100% 完整 / ≥75% 裂纹1 / ≥50% 裂纹2 / ≥25% 裂纹3；0 已崩碎（由调用方判定）。
        /// max=4 时完整轨迹：4(完整)→3(裂纹1)→2(裂纹2)→1(裂纹3)→0(崩碎→消失)。
        /// </summary>
        public static int GetCrackStage(int cur, int max)
        {
            if (max <= 0 || cur >= max) return 0;
            if (cur >= Mathf.CeilToInt(max * 0.75f)) return 1;
            if (cur >= Mathf.CeilToInt(max * 0.50f)) return 2;
            return 3;
        }

        /// <summary>
        /// DEV-001：外部设置某格的方块（测试场景搭建用，会同步初始化耐久并刷新渲染）。
        /// </summary>
        public void SetTile(int x, int y, TileDefinition def)
        {
            if (!InBounds(x, y)) return;
            if (curDurability == null) Generate();
            if (grid == null) return;

            grid[x, y] = def;
            if (curDurability != null)
                curDurability[x, y] = (def != null && def.isSolid) ? Mathf.Max(1, def.digHits) : -1;
            RefreshTile(x, y);
        }

#if UNITY_EDITOR
        /// <summary>DEV-001：编辑器下强制重新生成（BlockV1Builder 用）。Play 模式下 Awake 也会自动调。</summary>
        public void RegenerateFromDatabase()
        {
            Generate();
        }
#endif
    }
}
