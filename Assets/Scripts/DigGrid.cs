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

        [Header("DEV-007 矿脉（可选）")]
        [Tooltip("非空时：基础地层只生成纯填充物（value==0，PickStrata），矿物全部由本 OreVeinGenerator " +
                 "以矿脉形式叠加（ore pass）。为空 = 维持旧版逐格随机（含散点矿）行为不变。" +
                 "本字段为空时旧场景逐字节零回归。")]
        public OreVeinGenerator oreVeinGenerator;

        [Header("DEV-008 地下探索空间（可选）")]
        [Tooltip("非空时：基础地层跳过逐格 caveChance 椒盐打洞（天然 Empty 全部由本生成器以连贯空间叠加），" +
                 "且基础地层只铺纯填充物。生成顺序 = Base Strata → Space Pass → Ore Vein Pass" +
                 "（space pass 先挖空连贯空间，矿脉再基于剩余实心地层找墙体落点）。" +
                 "为空 = 维持旧行为（含逐格 caveChance）。注：启用后若需要矿，请同时挂 OreVeinGenerator。")]
        public UndergroundSpaceGenerator undergroundSpaceGenerator;

        [Header("DEV-013 小型遗迹（可选）")]
        [Tooltip("非空时：在 Ore Vein pass 之后叠加遗迹 pass。生成顺序 = Base Strata → Space Pass → " +
                 "Ore Vein Pass → Ruin Pass（遗迹后置覆盖矿脉落点，作为人工结构优先于天然矿脉）。" +
                 "为空 = 维持旧行为，旧场景逐字节零回归。")]
        public RuinGenerator ruinGenerator;

        TileDefinition[,] grid;
        Tile solidTile;
        Grid layoutGrid;

        /// <summary>
        /// DEV-002：Sprite → Tile 缓存。Tilemap 每个 cell 需要一个带该 Sprite 的 Tile 实例，
        /// 按 Sprite 复用可避免每格新建 Tile 对象。
        /// </summary>
        readonly System.Collections.Generic.Dictionary<Sprite, Tile> spriteTileCache =
            new System.Collections.Generic.Dictionary<Sprite, Tile>();

        [Header("DEV-001 耐久 V1")]
        [Tooltip("背景层 Tilemap（可选）。若指定，挖穿前景后露出背景（背景 Tilemap sortingOrder 应低于前景）。" +
                 "测试场景 BlockV1Test.unity 用它来显示 Dirt 背景")]
        public Tilemap backgroundTilemap;

        [Tooltip("崩碎状态持续秒数（方块耐久归零到真正从 Grid/Tilemap 移除之间的窗口）。" +
                 "0 = 立即移除（V1 默认）；>0 时给崩碎帧/粒子/音效留播放时间。")]
        [Min(0f)] public float breakDuration = 0f;

        /// <summary>
        /// DEV-001：每格当前耐久（旁路数组，跟 grid 平行）。
        /// &gt;0=剩余击数；0=已进入崩碎（Break）状态、尚未移除；-1=空格/未初始化。
        /// </summary>
        int[,] curDurability;

        /// <summary>
        /// DEV-001：每格满耐久（用于裂纹阶段比例与「完整」判定）。跟 curDurability 平行。
        /// 测试脚手架可通过 SetTile 的 durability 参数做「实例级耐久覆盖」，不污染共享 SO 的 digHits。
        /// </summary>
        int[,] maxDurability;

        /// <summary>每格的世界尺寸（取自 Grid 组件，默认 1）</summary>
        Vector3 CellSize => layoutGrid != null ? layoutGrid.cellSize : Vector3.one;

        /// <summary>网格原点（取自 Grid 组件）</summary>
        Vector3 Origin => layoutGrid != null ? layoutGrid.transform.position : transform.position;

        /// <summary>有方块塌落到该格时触发（用于砸伤玩家）</summary>
        public event Action<Vector2Int> OnRockFell;

        /// <summary>某格被挖穿时触发（格子坐标, 被挖到的方块）</summary>
        public event Action<Vector2Int, TileDefinition> OnTileDug;

        /// <summary>
        /// DEV-001：某格耐久归零、进入「崩碎」状态时触发（格子坐标, 方块定义）。
        /// 触发时机在方块从 Grid/Tilemap 移除之前——崩碎帧/粒子/音效等表现可在此接入，
        /// 配合 breakDuration 的延迟移除窗口播放完整动画。
        /// </summary>
        public event Action<Vector2Int, TileDefinition> OnBlockBreakStart;

        /// <summary>
        /// DEV-002：某格被成功命中（耐久 -1 且未崩碎）时触发（格子坐标, 方块定义）。
        /// 供表现层做单格命中反馈（局部抖动 / 闪亮 / 碎屑 / 音效）。崩碎那一击走 OnBlockBreakStart，不重复触发本事件。
        /// </summary>
        public event Action<Vector2Int, TileDefinition> OnBlockHit;

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

            // DEV-007：在 Generate 入口统一播种 —— 保证「同一 seed 反复 RegenerateFromDatabase 结果逐字节一致」
            // （Awake 里也 InitState 过一次，这里重复执行等价；旧场景调用路径与行为不变）。
            UnityEngine.Random.InitState(seed);

            grid = new TileDefinition[width, depth];
            curDurability = new int[width, depth];
            maxDurability = new int[width, depth];
            int center = width / 2;

            // DEV-007：启用矿脉生成时，基础地层只铺纯填充物（矿物全部交给 ore pass 叠加）
            bool veinMode = oreVeinGenerator != null && oreVeinGenerator.isActiveAndEnabled;
            // DEV-008：启用探索空间时，禁用逐格 caveChance 椒盐打洞，天然 Empty 全部由 space pass 连贯生成
            bool spaceMode = undergroundSpaceGenerator != null && undergroundSpaceGenerator.isActiveAndEnabled;
            // DEV-013：启用遗迹时，在 vein pass 之后叠加 ruin pass（后置覆盖落点）
            bool ruinMode = ruinGenerator != null && ruinGenerator.isActiveAndEnabled;

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

                    // 天然空洞（仅非 space 模式；space 模式的 Empty 由 SpacePass 连贯生成，避免椒盐噪点）
                    if (!spaceMode && layer != null && UnityEngine.Random.value < layer.caveChance)
                    {
                        grid[x, y] = database.emptyTile;
                        continue;
                    }

                    // 基础地层：矿脉/空间模式只产填充物，否则维持旧版含散点矿的随机
                    grid[x, y] = (veinMode || spaceMode)
                        ? database.PickStrata(layer)
                        : database.PickRandom(layer);
                }
            }

            // DEV-001：初始化耐久（实心格满耐久 = digHits，空格 = -1）
            for (int y = 0; y < depth; y++)
                for (int x = 0; x < width; x++)
                {
                    var def = grid[x, y];
                    int max = (def != null && def.isSolid) ? Mathf.Max(1, def.digHits) : -1;
                    maxDurability[x, y] = max;
                    curDurability[x, y] = max;
                }

            // DEV-008：探索空间 pass（在矿脉 pass 之前 —— 先挖空连贯空间，矿脉再找墙体落点）
            if (spaceMode)
                undergroundSpaceGenerator.ApplyToGrid(this);

            // DEV-007：基础地层完成后执行矿脉 pass（可选；在耐久初始化后、整图刷新前）
            if (veinMode)
                oreVeinGenerator.ApplyToGrid(this);

            // DEV-013：遗迹 pass（可选；在矿脉 pass 之后、整图刷新前。遗迹人工结构后置覆盖落点，
            // 与矿脉/洞穴不互相覆盖竞争 —— 明确顺序由本方法集中定义）。
            if (ruinMode)
                ruinGenerator.ApplyToGrid(this);

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

            // DEV-001：裂纹阶段由「当前耐久 / 满耐久」唯一决定（数据层单一真相源）。
            // max 取该格实例级满耐久（测试可覆盖），而非共享 SO 的 digHits。
            int max = (maxDurability != null && InBounds(x, y) && maxDurability[x, y] >= 1)
                ? maxDurability[x, y] : Mathf.Max(1, def.digHits);
            int cur = (curDurability != null && InBounds(x, y)) ? curDurability[x, y] : max;
            int stage;
            if (cur <= 0)
                stage = 3;   // 崩碎（Break）状态：与「裂纹3」同档（崩碎动画由表现层 OnBlockBreakStart 播放）
            else
                stage = GetCrackStage(Mathf.Min(cur, max), max);

            // DEV-002：优先用 visualProfile 的 Sprite（真正 Sprite 替换路径）；
            // 无 profile / 无对应 stage Sprite 时，fallback 到 DEV-001 的「纯色方块 + 调暗」。
            var profile = def != null ? def.visualProfile : null;
            Sprite spr = (profile != null) ? profile.GetStageSprite(stage) : null;

            if (spr != null)
            {
                tilemap.SetTile(cell, GetTileForSprite(spr));
                tilemap.SetTileFlags(cell, TileFlags.None);
                tilemap.SetColor(cell, Color.white);   // Sprite 自带裂纹视觉，不再调色
            }
            else
            {
                tilemap.SetTile(cell, solidTile);
                tilemap.SetTileFlags(cell, TileFlags.None);
                float dark = stage / 3f;
                tilemap.SetColor(cell, Color.Lerp(def.color, Color.black, dark * 0.55f));
            }
        }

        /// <summary>DEV-002：取（或创建）给定 Sprite 对应的 Tile 实例（按 Sprite 缓存复用）。</summary>
        Tile GetTileForSprite(Sprite spr)
        {
            if (spriteTileCache.TryGetValue(spr, out var tile))
                return tile;

            tile = ScriptableObject.CreateInstance<Tile>();
            tile.sprite = spr;
            tile.name = "Ashfall_" + spr.name;
            // Tile 默认 flags=LockColor 会忽略 cell SetColor；用 Sprite 时保留原色，但显式 None 保持行为一致。
            tile.flags = TileFlags.None;
            spriteTileCache[spr] = tile;
            return tile;
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

        /// <summary>
        /// 立即挖穿一格（跳过崩碎延迟）。成功返回 true，并通过 dug 输出被挖到的方块。
        /// 保留用于兼容旧调用方；DEV-001 的「单格受击」请优先用 HitBlock。
        /// </summary>
        public bool Dig(int x, int y, out TileDefinition dug)
        {
            dug = null;
            if (!InBounds(x, y)) return false;

            var def = GetTile(x, y);
            if (def == null || !def.isSolid) return false;

            // 直接进入崩碎状态（触发 OnBlockBreakStart），再立即移除——跳过 breakDuration 延迟
            if (curDurability != null && InBounds(x, y))
                curDurability[x, y] = 0;
            OnBlockBreakStart?.Invoke(new Vector2Int(x, y), def);

            RemoveBlock(x, y, out dug);
            return dug != null;
        }

        /// <summary>
        /// DEV-001：进入「崩碎」状态（耐久归零 → 触发 OnBlockBreakStart → 按 breakDuration 延迟后移除）。
        /// 把崩碎从「移除」中拆出来，让崩碎帧/粒子/音效在方块真正消失前有一个可观察、可替换的表现窗口，
        /// 而不是 HP=0 直接消失。调用后若 breakDuration>0，方块会停留该帧等待移除。
        /// </summary>
        void BeginBreak(int x, int y, TileDefinition def)
        {
            if (curDurability != null && InBounds(x, y))
                curDurability[x, y] = 0;   // 标记崩碎中（等待移除）

            // 崩碎视觉（最暗档）；表现层可通过 OnBlockBreakStart + breakDuration 接入正式崩碎动画
            RefreshTile(x, y);

            OnBlockBreakStart?.Invoke(new Vector2Int(x, y), def);

            if (breakDuration > 0f)
                StartCoroutine(Co_RemoveAfterDelay(x, y, def));
            else
                RemoveBlock(x, y, out _);
        }

        /// <summary>
        /// DEV-001：真正移除方块（grid→empty + 前景 Tile 清除 + OnTileDug + 落石）。
        /// 与 BeginBreak 分离，构成「崩碎 → 移除」两步，便于 Sprite Sheet 崩碎帧在两步之间接入。
        /// </summary>
        public bool RemoveBlock(int x, int y, out TileDefinition dug)
        {
            dug = null;
            if (!InBounds(x, y)) return false;

            var def = GetTile(x, y);
            if (def == null || !def.isSolid) return false;

            grid[x, y] = database.emptyTile;
            if (curDurability != null) curDurability[x, y] = -1;
            if (maxDurability != null) maxDurability[x, y] = -1;
            RefreshTile(x, y);

            dug = def;
            OnTileDug?.Invoke(new Vector2Int(x, y), def);

            if (enableFallingRocks) TryRockFall(x, y);
            return true;
        }

        System.Collections.IEnumerator Co_RemoveAfterDelay(int x, int y, TileDefinition def)
        {
            yield return new WaitForSeconds(breakDuration);
            RemoveBlock(x, y, out _);
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
        /// DEV-001：单格受击。耐久 -1，未崩碎时刷新裂纹视觉；耐久归零时进入「崩碎」状态
        /// （BeginBreak → 延迟/立即 RemoveBlock），而非 HP=0 直接消失。
        /// 返回 true 表示命中了实心格并成功扣除耐久；dug 仅在 RemoveBlock（真正移除）时由 OnTileDug 消费，
        /// 本方法内 dug 恒为 null——掉落/入包统一走 OnTileDug → HandleTileDug。
        /// 多次调用 HitBlock 是"击打多次"的意思——计数由 DigGrid 承载，玩家侧不再持有进度。
        /// </summary>
        public bool HitBlock(int x, int y, out TileDefinition dug)
        {
            dug = null;
            if (!InBounds(x, y)) return false;

            var def = GetTile(x, y);
            if (def == null || !def.isSolid) return false;

            if (curDurability == null || maxDurability == null) Generate();
            if (curDurability == null || maxDurability == null) return false;

            int cur = curDurability[x, y];
            if (cur <= 0)
            {
                // 已进入崩碎（等待移除）或已移除：不再重复受击
                return false;
            }

            cur--;
            curDurability[x, y] = cur;

            if (cur <= 0)
            {
                // 崩碎：进入 Break 状态（OnBlockBreakStart + 可选延迟移除），而非立即消失
                BeginBreak(x, y, def);
                return true;
            }

            // 未崩碎：刷新裂纹视觉 + 单格命中反馈（DEV-002）
            RefreshTile(x, y);
            OnBlockHit?.Invoke(new Vector2Int(x, y), def);
            return true;
        }

        /// <summary>
        /// DEV-001：某格当前耐久（剩余击数）。&gt;0=剩余；0=崩碎中（等待移除）；-1=空格/未初始化。
        /// </summary>
        public int GetDurability(int x, int y)
        {
            if (curDurability == null || !InBounds(x, y)) return -1;
            return curDurability[x, y];
        }

        /// <summary>
        /// DEV-001：某格满耐久（耐久上限，只读）。&gt;=1=实心格满耐久；-1=空格/未初始化。
        /// 这是耐久的单一真相源：支持 SetTile 的实例级耐久覆盖（测试/特殊 Block），
        /// 调用方（如 DrillVehicle 的 HUD 进度）应以此为准，而非共享 SO 的 digHits。
        /// </summary>
        public int GetMaxDurability(int x, int y)
        {
            if (maxDurability == null || !InBounds(x, y)) return -1;
            return maxDurability[x, y];
        }

        /// <summary>DEV-001：某格是否处于「崩碎中」状态（耐久归零、等待移除）。</summary>
        public bool IsBreaking(int x, int y)
        {
            if (curDurability == null || !InBounds(x, y)) return false;
            var def = GetTile(x, y);
            return def != null && def.isSolid && curDurability[x, y] == 0;
        }

        // ---------- DEV-004：环境系统辅助（局部坍塌） ----------

        /// <summary>
        /// DEV-004：通知「有方块塌落到该格」。供 BlockCollapseSystem 落格后复用玩家砸伤通道
        /// （DrillVehicle.HandleRockFell 已订阅 OnRockFell），玩家侧零改动。
        /// </summary>
        public void NotifyRockFell(Vector2Int cell) => OnRockFell?.Invoke(cell);

        /// <summary>
        /// DEV-004：强制刷新单格视觉（恢复 tilemap 颜色等）。
        /// 供 BlockCollapseSystem 在预警闪烁结束后把该格恢复为正常渲染。
        /// </summary>
        public void RefreshCell(int x, int y)
        {
            if (InBounds(x, y)) RefreshTile(x, y);
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
        /// durability 参数为「实例级耐久覆盖」：&gt;=1 时用该值，否则回落到 def.digHits。
        /// 测试脚手架应传 durability 而非在运行时改共享 SO 的 digHits，避免主场景共享资产被污染。
        /// </summary>
        public void SetTile(int x, int y, TileDefinition def, int durability = -1)
        {
            if (!InBounds(x, y)) return;
            if (curDurability == null || maxDurability == null) Generate();
            if (grid == null) return;

            grid[x, y] = def;

            int max = -1;
            if (def != null && def.isSolid)
                max = durability >= 1 ? durability : Mathf.Max(1, def.digHits);

            if (maxDurability != null) maxDurability[x, y] = max;
            if (curDurability != null) curDurability[x, y] = max;
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
