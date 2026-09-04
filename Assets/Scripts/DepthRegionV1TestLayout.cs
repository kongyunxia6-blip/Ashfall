using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-009：DepthRegionV1Test 场景的运行时布局重放器。
    ///
    /// 地表带（y0/y1 空气、y2 地面 + 中心坑口 ±3 列）与 DEV-008 完全相同，
    /// 直接复用 UndergroundSpaceV1TestLayout 的重放逻辑（不复制实现、不引入第二套布局数字）：
    ///  - 地表带 y0/y1 全宽 Empty（空气），y2 全宽 Dirt 地面；
    ///  - 中心下矿坑口（center ± openingHalfWidth）y0..y2 保持 Empty → 通往地下第一层；
    ///  - 其余地下全部由 SpaceGenerator（三带空间）+ OreVeinGenerator（三带矿脉）生成，
    ///    区域边界统一来自 DepthRegionLayout（本场景即验证其 Shallow/Mid/Deep 语义）。
    /// </summary>
    public class DepthRegionV1TestLayout : UndergroundSpaceV1TestLayout
    {
        // 布局参数（spawnX/surfaceY/groundY/openingHalfWidth/dirt）沿用基类字段，由 Builder 注入。
        // 重放逻辑全部来自基类 UndergroundSpaceV1TestLayout.Start（避免复制第二份地表布局实现）。

        protected override void Start()
        {
            base.Start();
            Debug.Log("[DEV-009] DepthRegionV1TestLayout: 复用 DEV-008 地表带/坑口重放完成（区域边界来自 DepthRegionLayout）");
        }
    }
}
