using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-007：OreVeinV1Test 场景的运行时布局重放器。
    ///
    /// 与 SurfaceHubV1TestLayout 同构（地表 Hub + 下矿口 3 列手工矿柱），因为：
    ///  - DigGrid 基础地层改为 veinMode（纯填充 Dirt + 矿脉 pass 叠加），手工列不再必要作为唯一矿源，
    ///    但保留 3 列确定性矿柱（铁/铜/锡 浅深两趟）作为「单格采矿回归 / Hub 出售闭环回归」的确定性落点；
    ///  - 其余地下空间全部由 OreVeinGenerator 按 Shallow/Mid/Deep 三带随机铺矿脉（Play 后可观察）；
    ///  - 地表带 y=0..1 Empty / y=2 Dirt 全宽，由本 Layout 在 Play 后重放（DigGrid.Awake 的随机被覆盖）。
    ///
    /// 布局坐标约定与 SurfaceHubV1 完全一致：
    ///  - 玩家出生 x=6,y=1；功能区 x=6(Lander)/14(Sell)/22(Workbench)/30(Fuel)；
    ///  - 手工矿柱 x=36(Iron)/40(Copper)/44(Tin)，每列 y3 土 + y4-5 矿（第一趟）→ y6-7 土 + y8-9 矿（第二趟）+ y10 托底。
    ///  - Scanner 无矿基线区：OreVeinV1Builder 在 OreVeinGenerator.reservedRects 里把
    ///    x∈[2,18],y∈[16,26] 划为保留区（永不种矿脉）→ MCP 脚本可在其中 SetTile 摆 Scanner 四组 fixture。
    /// </summary>
    public class OreVeinV1TestLayout : MonoBehaviour
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

        /// <summary>第 depthBatch 趟（0=浅 4..5 / 1=深 8..9）第 kind 列（0=铁 1=铜 2=锡）的矿层首格。</summary>
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
                Debug.LogWarning("[DEV-007] OreVeinV1TestLayout: 未找到 DigGrid 或 database，跳过布局");
                return;
            }

            var empty = grid.database.emptyTile;
            if (iron == null || copper == null || tin == null || dirt == null || empty == null)
            {
                Debug.LogWarning("[DEV-007] OreVeinV1TestLayout: 资产缺失（iron/copper/tin/dirt），跳过布局");
                return;
            }

            int filled = 0;

            // 地表带：y0/y1 空、y2 全宽地面（玩家活动带）
            for (int x = 0; x < grid.width; x++)
            {
                grid.SetTile(x, 0, empty); filled++;
                grid.SetTile(x, 1, empty); filled++;
                grid.SetTile(x, 2, dirt); filled++;
            }

            // 下矿口手工矿柱：铁/铜/锡 三列，浅深两段矿层（确定性回归落点）
            for (int kind = 0; kind < 3; kind++)
            {
                var ore = kind == 0 ? iron : (kind == 1 ? copper : tin);
                int x = shaftColumns[kind];

                // 第一趟：y3 土(dur1) + y4-5 矿
                grid.SetTile(x, 3, dirt, 1); filled++;
                grid.SetTile(x, oreShallowY, ore, 1); filled++;
                grid.SetTile(x, oreShallowY + 1, ore, 1); filled++;
                // 第二趟通道：y6-7 土(dur1) + y8-9 矿
                grid.SetTile(x, oreDeepY - 2, dirt, 1); filled++;
                grid.SetTile(x, oreDeepY - 1, dirt, 1); filled++;
                grid.SetTile(x, oreDeepY, ore, 1); filled++;
                grid.SetTile(x, oreDeepY + 1, ore, 1); filled++;
                // 托底
                grid.SetTile(x, oreDeepY + 2, dirt); filled++;
            }

            Debug.Log($"[DEV-007] OreVeinV1TestLayout: 布局已应用（{filled} 格：地表带 + 手工矿柱 3 列×2 趟；" +
                      "其余地下 = 三带矿脉由 OreVeinGenerator 提供）");
        }
    }
}
