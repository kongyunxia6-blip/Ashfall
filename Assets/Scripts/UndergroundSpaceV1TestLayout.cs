using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-008：UndergroundSpaceV1Test 场景的运行时布局重放器。
    ///
    /// 职责（Start 重放，覆盖 DigGrid.Awake 随机生成的浅表几行）：
    ///  - 地表带 y0/y1 全宽 Empty（空气），y2 全宽 Dirt 地面（玩家活动带）；
    ///  - 中心下矿坑口（x ∈ center ± openingHalfWidth）：y0..y2 保持 Empty，
    ///    形成通往地下第一层(填满)的「矿坑入口」——与 DigGrid 的 surfaceOpening 列一致；
    ///  - 其余地下全部由 UndergroundSpaceGenerator（空间）+ OreVeinGenerator（矿脉）
    ///    按 Shallow / Mid / Deep 三带生成，Layout 不再手工摆矿柱。
    ///
    /// 布局坐标约定（与 DEV-006/007 一致）：
    ///  - 玩家出生 x=center,y=1（坑口内，由重力落到坑底后逐格下挖）；
    ///  - 功能区 x=6(Lander)/14(Sell)/22(Workbench)/30(Fuel)；SurfaceHubZone 大区覆盖地表带；
    ///  - 验收路径：坑口垂直下挖 → 挖通自然空间（Pocket/Tunnel/Branch…）→ 进入 →
    ///    Scanner → 墙体单格采矿 → 返回 Hub → Sell。
    /// </summary>
    public class UndergroundSpaceV1TestLayout : MonoBehaviour
    {
        [Tooltip("要覆盖的 DigGrid（默认取同一物体上的）")]
        public DigGrid grid;

        [Tooltip("地表 Dirt（填充资产，Builder 注入）")]
        public TileDefinition dirt;

        [Header("布局")]
        [Tooltip("玩家出生列（= 坑口中心，通常 width/2）")]
        public int spawnX = 28;
        [Tooltip("玩家活动行（地表带空气行）")]
        public int surfaceY = 1;
        [Tooltip("地表地面行（Dirt）")]
        public int groundY = 2;
        [Tooltip("坑口半宽（与 DigGrid.surfaceOpeningHalfWidth 一致）")]
        public int openingHalfWidth = 3;

        /// <summary>布局重放入口（protected virtual：供 DEV-009 DepthRegionV1TestLayout 派生复用同一实现）。</summary>
        protected virtual void Start()
        {
            if (grid == null) grid = GetComponent<DigGrid>();
            if (grid == null || grid.database == null)
            {
                Debug.LogWarning("[DEV-008] UndergroundSpaceV1TestLayout: 未找到 DigGrid 或 database，跳过布局");
                return;
            }

            var empty = grid.database.emptyTile;
            if (dirt == null || empty == null)
            {
                Debug.LogWarning("[DEV-008] UndergroundSpaceV1TestLayout: 资产缺失（dirt/empty），跳过布局");
                return;
            }

            int filled = 0;
            int center = grid.width / 2;

            for (int x = 0; x < grid.width; x++)
            {
                // 地表带：y0/y1 空（空气），y2 地面；坑口列 y2 保持空 → 下矿口
                grid.SetTile(x, 0, empty); filled++;
                grid.SetTile(x, 1, empty); filled++;
                bool inPit = Mathf.Abs(x - center) <= openingHalfWidth;
                grid.SetTile(x, 2, inPit ? empty : dirt); filled++;
            }

            Debug.Log($"[DEV-008] UndergroundSpaceV1TestLayout: 布局已应用（{filled} 格：地表带 + 中心坑口 x∈" +
                      $"[{center - openingHalfWidth},{center + openingHalfWidth}]；其余地下 = 三带空间+矿脉由生成器提供）");
        }
    }
}
