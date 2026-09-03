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
            tilemap.SetColor(cell, def.color);
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
    }
}
