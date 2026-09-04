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
    /// DEV-006 第二轮：每实例计数收拢到 ZonePresence —— 正常 OnTriggerExit 逐 Collider 配对，
    /// Disable/Destroy 走 ReleaseAll 一次性归还本实例全部占用（多 Collider 玩家也不残留全局计数）。
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

        /// <summary>本实例在场计数（ZonePresence：逐 Collider 配对；禁用/销毁时 ReleaseAll 一次归还）。</summary>
        readonly ZonePresence presence = new ZonePresence(HubZoneKind.Workbench);

        void Awake()
        {
            aliveCount++;
            var col = GetComponent<Collider2D>();
            col.isTrigger = true;
        }

        void OnTriggerEnter2D(Collider2D other)
        {
            if (other.GetComponent<DrillVehicle>() == null) return;
            presence.Enter();
        }

        void OnTriggerExit2D(Collider2D other)
        {
            if (other.GetComponent<DrillVehicle>() == null) return;
            presence.Exit();
        }

        void OnDisable() => presence.ReleaseAll();

        void OnDestroy()
        {
            presence.ReleaseAll();
            aliveCount = Mathf.Max(0, aliveCount - 1);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => aliveCount = 0;
    }
}
