using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-006：装备工作台（升级终端 V1）。
    ///
    /// 把 UpgradeSystem 的购买入口收束到地表工作台交互范围：
    ///  - 玩家只有靠近工作台时（HubZoneTracker.Workbench 计数 &gt; 0）才能打开/执行升级；
    ///  - 本组件不持有升级逻辑 —— 升级数据/价格/效果仍是 UpgradeSystem（单一权威）；
    ///  - 打开商店的按键（U）与购买按键（1~6）由 GameHUD 处理，但 GameHUD 只在
    ///    UpgradeWorkbench.PlayerInRange 时允许（离开范围自动关面板，见 GameHUD）。
    ///
    /// 兼容旧测试场景：若场景里没有任何工作台（AnyExists=false），GameHUD 回退到
    /// 「地表任意位置可开商店」的原原型行为 —— 正式路径（本场景放置了工作台）不受影响。
    ///
    /// 挂在哪：地表工作台占位物上，需带 Collider2D（isTrigger 自动设置）。
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
    public class UpgradeWorkbench : MonoBehaviour
    {
        /// <summary>玩家当前是否在任一工作台交互范围内（HUD 开/关商店与购买门槛用）。</summary>
        public static bool PlayerInRange => HubZoneTracker.InRange(HubZoneKind.Workbench);

        /// <summary>场景中是否至少存在一个工作台实例。false = 旧场景回退全局商店（兼容）。</summary>
        public static bool AnyExists => aliveCount > 0;

        static int aliveCount;

        /// <summary>本实例是否正有玩家在范围内（0/1 边界才动全局计数）。</summary>
        int localCount;

        void Awake()
        {
            aliveCount++;
            var col = GetComponent<Collider2D>();
            col.isTrigger = true;
        }

        void OnTriggerEnter2D(Collider2D other)
        {
            if (other.GetComponent<DrillVehicle>() == null) return;
            if (localCount == 0) HubZoneTracker.Enter(HubZoneKind.Workbench);
            localCount++;
        }

        void OnTriggerExit2D(Collider2D other)
        {
            if (other.GetComponent<DrillVehicle>() == null) return;
            ReleaseLocal();
        }

        void OnDisable() => ReleaseLocal();

        void OnDestroy()
        {
            ReleaseLocal();
            aliveCount = Mathf.Max(0, aliveCount - 1);
        }

        /// <summary>归还本实例的在场计数（OnDisable/OnDestroy/玩家离开共用；幂等）。</summary>
        void ReleaseLocal()
        {
            if (localCount <= 0) return;
            localCount--;
            if (localCount == 0) HubZoneTracker.Exit(HubZoneKind.Workbench);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => aliveCount = 0;
    }
}
