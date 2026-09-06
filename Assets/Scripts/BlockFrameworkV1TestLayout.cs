using System.Collections.Generic;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-012：BlockFrameworkV1Test 场景的运行时布局重放器。
    ///
    /// 说明：Builder 在 Editor 的 SetTile 只改内存数组，Play 时 Awake→Generate() 会随机覆盖，
    /// 布局必须在 Play 后重放（与 DEV-004/007 等 Layout 同理）。
    ///
    /// 布局（在生成的地层之上叠加验收区块，全部用真实 TileDefinition 资产，不改共享 SO 值）：
    ///  - 地层：由 DigGrid 分层生成（Shallow 带基础填充 = NormalRock 代表层等）；
    ///  - 硬度墙（highHardnessX 列）：HardRock_硬岩(durability 3) 竖墙，验证硬度/能力与单格；
    ///  - SupportRock 柱（supportX）：SupportRock + 上方 3 LooseRock → 复用 DEV-004 BlockCollapseSystem 回归；
    ///  - HotRock A-B 区（hotX，两层）：HotRock_高温岩 竖柱，供无/有冷却 A-B 对照。
    /// 资产由 Builder 从 AssetDatabase 注入；本类只重放，不触碰共享 SO。
    /// </summary>
    public class BlockFrameworkV1TestLayout : MonoBehaviour
    {
        [Tooltip("要覆盖的 DigGrid（默认取同物体）")]
        public DigGrid grid;

        [Header("资产（Builder 注入）")]
        public TileDefinition dirt;          // 普通岩（NormalRock 代表，用 Dirt_泥土 或 HardRock）
        public TileDefinition hardRock;      // 高硬度（HardRock_硬岩）
        public TileDefinition supportRock;   // 承重岩
        public TileDefinition looseRock;     // 松散岩
        public TileDefinition hotRock;       // 高温岩
        public TileDefinition iron;          // 铁矿（矿物 × 地层）

        [Header("布局锚点")]
        public int anchorY = 6;              // 测试柱锚定行（玩家站该行下方）
        public int hardnessX = 18;           // 硬度墙列
        public int supportX = 24;            // SupportRock 柱列
        public int hotX = 30;                // HotRock A-B 列

        /// <summary>硬度墙顶行（格子坐标）。</summary>
        public int WallTopY => anchorY - 3;

        /// <summary>某列玩家站格（该列测试柱正下方空位）。</summary>
        public Vector2Int StandCell(int x) => new Vector2Int(x, anchorY + 1);

        void Start()
        {
            if (grid == null) grid = GetComponent<DigGrid>();
            if (grid == null || grid.database == null)
            {
                Debug.LogWarning("[DEV-012] BlockFrameworkV1TestLayout: 未找到 DigGrid 或 database");
                return;
            }

            if (dirt == null || hardRock == null || supportRock == null ||
                looseRock == null || hotRock == null || iron == null)
            {
                Debug.LogWarning("[DEV-012] BlockFrameworkV1TestLayout: 资产缺失，跳过布局重放");
                return;
            }

            int filled = 0;

            // ---- 硬度墙：high-durability HardRock 竖柱（anchorY..anchorY-3，玩家站下方挖穿验证硬度/能力/单格） ----
            filled += BuildWall(hardnessX, hardRock);

            // ---- SupportRock 柱：support + 3 LooseRock + 段顶普通岩（复用 BlockCollapseSystem） ----
            filled += BuildSupportColumn(supportX, supportRock, looseRock, dirt);

            // ---- HotRock A-B 柱：两格 HotRock 竖柱（验证无/有冷却） ----
            filled += BuildColumn(hotX, hotRock, 2);

            Debug.Log($"[DEV-012] BlockFrameworkV1TestLayout: 布局已应用（{filled} 格叠加）；" +
                      $"hardnessX={hardnessX}/supportX={supportX}/hotX={hotX}；未修改共享 SO");
        }

        int BuildWall(int x, TileDefinition def)
        {
            int n = 0;
            // 站格空
            grid.SetTile(x, anchorY + 1, grid.database.emptyTile); n++;
            grid.SetTile(x, anchorY + 2, dirt); n++;
            // 高耐久竖柱 4 格（硬岩 digHits 一般 3，实例耐久用满值）
            for (int i = 0; i < 4; i++)
            {
                grid.SetTile(x, anchorY - i, def, Mathf.Max(1, def.digHits)); n++;
            }
            return n;
        }

        int BuildSupportColumn(int x, TileDefinition support, TileDefinition loose, TileDefinition top)
        {
            int n = 0;
            grid.SetTile(x, anchorY + 1, grid.database.emptyTile); n++;   // 站格空
            grid.SetTile(x, anchorY + 2, dirt); n++;                        // 立足
            grid.SetTile(x, anchorY, support, 2); n++;                      // 承重岩（2 击）
            for (int i = 1; i <= 3; i++)                                     // 上方 3 块松散岩
            {
                grid.SetTile(x, anchorY - i, loose, 1); n++;
            }
            grid.SetTile(x, anchorY - 4, dirt); n++;                        // 段顶稳定块
            return n;
        }

        int BuildColumn(int x, TileDefinition def, int count)
        {
            int n = 0;
            grid.SetTile(x, anchorY + 1, grid.database.emptyTile); n++;   // 站格空
            grid.SetTile(x, anchorY + 2, dirt); n++;                        // 立足
            for (int i = 0; i < count; i++)
            {
                grid.SetTile(x, anchorY - i, def, Mathf.Max(1, def.digHits)); n++;
            }
            return n;
        }
    }
}
