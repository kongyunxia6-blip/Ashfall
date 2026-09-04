using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-005：CoreLoopV1Test 场景的运行时布局重放器。
    ///
    /// Builder 在 Editor 的 SetTile 只改内存数组，Play 时 DigGrid.Awake→Generate() 会随机覆盖，
    /// 布局必须在 Play 后重放（与 DEV-001/003/004 的 Layout 同理）。
    ///
    /// 布局：
    ///  - 地表带：y=0..1 全宽 Empty（玩家活动带），y=2 全宽 Dirt（地表地面，玩家站 y=1 脚下实心）；
    ///  - 三批矿柱（批 = 铁/铜/锡 各 1 根，模拟一次完整下矿能采到 3 种矿）：
    ///      批1 x=6/10/14，矿层 y=4..5；
    ///      批2 x=20/24/28，矿层 y=7..8（更深，多挖几格土）；
    ///      批3 x=34/38/42，矿层 y=10..11。
    ///    每根柱 = 该列 y=2 起先挖土隧道，到矿层放同种矿 2 块，矿下托 Dirt。
    ///  - 三次完整循环可分别用批1/批2/批3（脚本每趟选一批挖）。
    /// </summary>
    public class CoreLoopV1TestLayout : MonoBehaviour
    {
        [Tooltip("要覆盖的 DigGrid（默认取同一物体上的）")]
        public DigGrid grid;

        [Header("矿种资产（Builder 注入）")]
        public TileDefinition iron;
        public TileDefinition copper;
        public TileDefinition tin;
        public TileDefinition dirt;

        [Header("布局")]
        [Tooltip("玩家出生列（批1 铜柱正上方）")]
        public int spawnX = 10;
        [Tooltip("玩家活动行（地表带）")]
        public int surfaceY = 1;
        [Tooltip("地表地面行")]
        public int groundY = 2;

        /// <summary>第 batch 批（0..2）第 kind 根柱的矿层首格坐标（kind 0=铁 1=铜 2=锡）。</summary>
        public Vector2Int OreCell(int batch, int kind)
        {
            int baseX = 6 + batch * 14;
            int oreY = 4 + batch * 3;
            return new Vector2Int(baseX + kind * 4, oreY);
        }

        void Start()
        {
            if (grid == null) grid = GetComponent<DigGrid>();
            if (grid == null || grid.database == null)
            {
                Debug.LogWarning("[DEV-005] CoreLoopV1TestLayout: 未找到 DigGrid 或 database，跳过布局");
                return;
            }

            var empty = grid.database.emptyTile;
            if (iron == null || copper == null || tin == null || dirt == null || empty == null)
            {
                Debug.LogWarning("[DEV-005] CoreLoopV1TestLayout: 资产缺失（iron/copper/tin/dirt），跳过布局");
                return;
            }

            int filled = 0;

            // 地表带：y0/y1 空、y2 地面
            for (int x = 0; x < grid.width; x++)
            {
                grid.SetTile(x, 0, empty); filled++;
                grid.SetTile(x, 1, empty); filled++;
                grid.SetTile(x, 2, dirt); filled++;
            }

            // 三批矿柱
            for (int batch = 0; batch < 3; batch++)
            {
                for (int kind = 0; kind < 3; kind++)
                {
                    var ore = kind == 0 ? iron : (kind == 1 ? copper : tin);
                    var top = OreCell(batch, kind);
                    int x = top.x;
                    int oreY = top.y;

                    // 隧道：y=3 .. oreY-1 挖土（y=2 已是地面 Dirt，一并算）
                    for (int y = groundY; y < oreY; y++)
                    {
                        grid.SetTile(x, y, dirt, 1); filled++;
                    }
                    // 矿层 2 块
                    grid.SetTile(x, oreY, ore, 1); filled++;
                    grid.SetTile(x, oreY + 1, ore, 1); filled++;
                    // 矿下托土
                    grid.SetTile(x, oreY + 2, dirt); filled++;
                }
            }

            Debug.Log($"[DEV-005] CoreLoopV1TestLayout: 布局已应用（{filled} 格：地表带 + 3 批×3 矿柱，锚定行 {surfaceY}/{groundY}；未修改共享 SO）");
        }
    }
}
