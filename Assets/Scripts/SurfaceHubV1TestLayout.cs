using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-006：SurfaceHubV1Test 场景的运行时布局重放器。
    ///
    /// Builder 在 Editor 的 SetTile 只改内存数组，Play 时 DigGrid.Awake→Generate() 会随机覆盖，
    /// 布局必须在 Play 后重放（与 DEV-001/003/004/005 的 Layout 同理）。
    ///
    /// 布局：
    ///  - 地表带：y=0..1 全宽 Empty（玩家活动带），y=2 全宽 Dirt（地表地面，玩家站 y=1 脚下实心）；
    ///  - 玩家出生在登陆舱（x=6）附近；四个 Hub 功能区都放在地表带（y=1，由 Builder 摆触发器）；
    ///  - 下矿口（右侧 shaftColumns，铁/铜/锡 各一列）分两段深度，支持 ≥2 趟完整循环：
    ///      每列：y=3 土(dur1) / y=4..5 矿A（第一趟）/ y=6..7 土(dur1) / y=8..9 矿B（第二趟）/ y=10 土托底
    ///    第一趟挖到 y4-5 矿石后返航；第二趟沿同一竖井下到 y8-9 采更深矿石（动线自然）。
    ///  - 地表功能区间距 ≥8 列，触发器不重叠；玩家必须实际移动到位才能交互。
    /// </summary>
    public class SurfaceHubV1TestLayout : MonoBehaviour
    {
        [Tooltip("要覆盖的 DigGrid（默认取同一物体上的）")]
        public DigGrid grid;

        [Header("矿种资产（Builder 注入）")]
        public TileDefinition iron;
        public TileDefinition copper;
        public TileDefinition tin;
        public TileDefinition dirt;

        [Header("布局")]
        [Tooltip("玩家出生列（登陆舱上）")]
        public int spawnX = 6;
        [Tooltip("玩家活动行（地表带）")]
        public int surfaceY = 1;
        [Tooltip("地表地面行")]
        public int groundY = 2;

        [Tooltip("下矿口三列（铁/铜/锡 顺序），与功能区错开")]
        public int[] shaftColumns = { 36, 40, 44 };
        [Tooltip("第一趟矿石层首行")]
        public int oreShallowY = 4;
        [Tooltip("第二趟（更深）矿石层首行")]
        public int oreDeepY = 8;

        /// <summary>第 depthBatch 趟（0=浅层 4..5 / 1=深层 8..9）第 kind 列（0=铁 1=铜 2=锡）的矿层首格。</summary>
        public Vector2Int OreCell(int depthBatch, int kind)
        {
            int x = shaftColumns[kind];
            int y = depthBatch == 0 ? oreShallowY : oreDeepY;
            return new Vector2Int(x, y);
        }

        void Start()
        {
            if (grid == null) grid = GetComponent<DigGrid>();
            if (grid == null || grid.database == null)
            {
                Debug.LogWarning("[DEV-006] SurfaceHubV1TestLayout: 未找到 DigGrid 或 database，跳过布局");
                return;
            }

            var empty = grid.database.emptyTile;
            if (iron == null || copper == null || tin == null || dirt == null || empty == null)
            {
                Debug.LogWarning("[DEV-006] SurfaceHubV1TestLayout: 资产缺失（iron/copper/tin/dirt），跳过布局");
                return;
            }

            int filled = 0;

            // 地表带：y0/y1 空、y2 全宽地面
            for (int x = 0; x < grid.width; x++)
            {
                grid.SetTile(x, 0, empty); filled++;
                grid.SetTile(x, 1, empty); filled++;
                grid.SetTile(x, 2, dirt); filled++;
            }

            // 下矿口：铁/铜/锡 三列，浅深两段矿层
            for (int kind = 0; kind < 3; kind++)
            {
                var ore = kind == 0 ? iron : (kind == 1 ? copper : tin);
                int x = shaftColumns[kind];
                int shY = oreShallowY;
                int dpY = oreDeepY;

                // 第一趟：y3 土(dur1) + y4-5 矿
                grid.SetTile(x, 3, dirt, 1); filled++;
                grid.SetTile(x, shY, ore, 1); filled++;
                grid.SetTile(x, shY + 1, ore, 1); filled++;
                // 第二趟通道：y6-7 土(dur1) + y8-9 矿
                grid.SetTile(x, dpY - 2, dirt, 1); filled++;
                grid.SetTile(x, dpY - 1, dirt, 1); filled++;
                grid.SetTile(x, dpY, ore, 1); filled++;
                grid.SetTile(x, dpY + 1, ore, 1); filled++;
                // 托底
                grid.SetTile(x, dpY + 2, dirt); filled++;
            }

            Debug.Log($"[DEV-006] SurfaceHubV1TestLayout: 布局已应用（{filled} 格：地表带 + 下矿口 3 列×(浅深 2 趟矿)）");
        }
    }
}
