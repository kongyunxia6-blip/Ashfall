using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-004：SupportRockV1Test 场景的运行时布局重放器。
    ///
    /// 与 DEV-001/003 的 Layout 同理：Builder 在 Editor 的 SetTile 只改内存数组，
    /// Play 时 Awake→Generate() 会随机覆盖，布局必须在 Play 后重放。
    ///
    /// 三组（每组一根竖直测试柱 + 玩家站格），玩家出生在 A 组站格、测试用 MCP 传送换组：
    ///  - A 组（4 连 LooseRock 全塌）：SupportRock(ay) + LooseRock×(ay-1..ay-4) + 普通岩段顶(ay-5)。
    ///    挖掉 SupportRock → 4 块 LooseRock Unstable → 全部落格。
    ///  - B 组（传播遇 Bedrock 停止）：SupportRock(ay) + LooseRock(ay-1,ay-2) + Bedrock(ay-3) + LooseRock(ay-4)。
    ///    只 ay-1..ay-2 塌落；Bedrock 与其上方 LooseRock 保持原样。
    ///  - C 组（普通岩不触发）：普通岩 Dirt(ay) + LooseRock(ay-1,ay-2)。
    ///    挖掉 Dirt（Normal）→ 不触发坍塌系统，上方 LooseRock 原样静止。
    ///
    /// 玩家站格 = 支撑格正下方（支撑在头顶），向上挖穿；站格下方铺实心土当立足点。
    /// </summary>
    public class SupportRockV1TestLayout : MonoBehaviour
    {
        [Tooltip("要覆盖的 DigGrid（默认取同一物体上的）")]
        public DigGrid grid;

        [Header("特殊块资产（Builder 从 AssetDatabase 注入，运行时引用）")]
        public TileDefinition supportRock;
        public TileDefinition looseRock;

        [Header("布局")]
        [Tooltip("支撑行（grid y）。支撑格 = 该行，玩家站格 = 该行 + 1")]
        public int anchorY = 6;
        [Tooltip("A 组列（4 连 LooseRock）")]
        public int groupAX = 18;
        [Tooltip("B 组列（Bedrock 截断）")]
        public int groupBX = 24;
        [Tooltip("C 组列（普通岩）")]
        public int groupCX = 30;
        [Tooltip("承重岩耐久（击数，需 ≥1 且 ≤ 玩家单格连击可快速挖穿）")]
        public int supportDigHits = 2;
        [Tooltip("松散岩耐久（1 击崩碎）")]
        public int looseDigHits = 1;

        /// <summary>某组的玩家站格（支撑格正下方）。</summary>
        public Vector2Int StandCell(int groupX) => new Vector2Int(groupX, anchorY + 1);

        /// <summary>某组的支撑格。</summary>
        public Vector2Int SupportCell(int groupX) => new Vector2Int(groupX, anchorY);

        void Start()
        {
            if (grid == null) grid = GetComponent<DigGrid>();
            if (grid == null || grid.database == null)
            {
                Debug.LogWarning("[DEV-004] SupportRockV1TestLayout: 未找到 DigGrid 或 database，跳过布局");
                return;
            }

            var empty = grid.database.emptyTile;
            var dirt = FindByDisplayName("Dirt_泥土");
            var bedrock = grid.database.bedrockTile;
            if (supportRock == null || looseRock == null || dirt == null || bedrock == null)
            {
                Debug.LogWarning("[DEV-004] SupportRockV1TestLayout: 资产缺失（supportRock/looseRock/dirt/bedrock），跳过布局");
                return;
            }

            int ay = anchorY;
            int filled = 0;

            // ---- A 组：4 连 LooseRock 全塌 ----
            BuildColumn(groupAX, ay, empty, dirt, bedrock, supportRock, looseRock, ref filled,
                topChain: 4, hasBedrockBlock: false, supportIsDirt: false);

            // ---- B 组：Bedrock 截断传播（bedrock 上方仍有 1 块 LooseRock，应保持）----
            BuildColumn(groupBX, ay, empty, dirt, bedrock, supportRock, looseRock, ref filled,
                topChain: 2, hasBedrockBlock: true, supportIsDirt: false);

            // ---- C 组：普通岩被挖 → 不触发（上方 LooseRock 原样）----
            BuildColumn(groupCX, ay, empty, dirt, bedrock, supportRock, looseRock, ref filled,
                topChain: 2, hasBedrockBlock: false, supportIsDirt: true);

            Debug.Log($"[DEV-004] SupportRockV1TestLayout: 布局已应用（{filled} 格，锚定行 {ay}，" +
                      $"A={groupAX}/B={groupBX}/C={groupCX}；未修改共享 SO）");
        }

        /// <summary>
        /// 建一根竖直测试柱：
        ///  站格(ay+1)=空 + 脚格(ay+2)=Dirt 实心
        ///  支撑格(ay) = Dirt(普通) 或 SupportRock
        ///  上方 ay-1..ay-topChain 为 LooseRock；若 hasBedrockBlock，在 loose 段顶再放 Bedrock + 再上 1 块 LooseRock
        ///  最上方放一块 Dirt 当「段顶稳定块」（普通岩，链会在此停）
        /// </summary>
        void BuildColumn(int x, int ay, TileDefinition empty, TileDefinition dirt, TileDefinition bedrock,
                         TileDefinition supportRock, TileDefinition looseRock, ref int filled,
                         int topChain, bool hasBedrockBlock, bool supportIsDirt)
        {
            // 玩家立足：站格空 + 脚格实心
            grid.SetTile(x, ay + 1, empty); filled++;
            grid.SetTile(x, ay + 2, dirt); filled++;

            // 支撑格
            if (supportIsDirt)
            {
                grid.SetTile(x, ay, dirt, 1); filled++;
            }
            else
            {
                grid.SetTile(x, ay, supportRock, supportDigHits); filled++;
            }

            // 上方 LooseRock 链
            int y = ay - 1;
            for (int i = 0; i < topChain; i++)
            {
                grid.SetTile(x, y, looseRock, looseDigHits); filled++;
                y--;
            }

            // Bedrock 截断（B 组）：loose 段之上放 Bedrock，Bedrock 之上再放 1 块 LooseRock
            if (hasBedrockBlock)
            {
                grid.SetTile(x, y, bedrock); filled++;
                y--;
                grid.SetTile(x, y, looseRock, looseDigHits); filled++;
                y--;
            }

            // 段顶稳定块（普通岩），保证链顶之上有明确的「非 LooseRock」边界
            grid.SetTile(x, y, dirt); filled++;
        }

        TileDefinition FindByDisplayName(string name)
        {
            if (grid.database == null || grid.database.layers == null) return null;
            foreach (var layer in grid.database.layers)
            {
                if (layer?.tiles == null) continue;
                foreach (var tile in layer.tiles)
                    if (tile != null && tile.displayName == name) return tile;
            }
            return null;
        }
    }
}
