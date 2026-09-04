using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-006 第二轮：地表据点区（Surface Hub）大触发区 —— GameManager.IsAtSurface 的唯一权威来源。
    ///
    /// 背景：第一轮把 IsAtSurface 绑在 Lander 的小 Trigger（SurfaceBase）上，玩家一离开登陆舱
    /// （走向分开的 Sell / Fuel / Workbench）就被判为「离开地表」，工作台门槛与整个 Hub HUD 因此失效。
    ///
    /// 本组件覆盖整个地表据点（四个功能区 + 下矿口入口所在的地表带）：
    ///  - 任一玩家 Collider 位于本区内 → IsAtSurface = true（源计数 &gt; 0）；
    ///  - 全部离开（真正下矿钻出本区 / 走出据点）→ IsAtSurface = false；
    ///  - 多个实例 / 玩家多 Collider 安全：全局源计数统计，只在 0↔1 边界写 GameManager；
    ///  - OnDisable / OnDestroy 一次性释放本实例全部占用，不残留地表状态。
    ///
    /// 职责边界：只管「是否处于地表 Hub」；不做卖矿 / 补给 / 升级 / 安全区。
    /// 挂在哪：覆盖整个 Hub 的 BoxCollider2D(trigger) 物体上（Builder 自动创建）。
    ///
    /// 兼容：SurfaceBase（登陆舱）在有本组件的场景里不再写 IsAtSurface（只做 Lander 上下文）；
    /// 旧场景没有本组件时 SurfaceBase 自动兜底维持原行为（见 SurfaceBase.surfaceOwner）。
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
    public class SurfaceHubZone : MonoBehaviour
    {
        /// <summary>场景中是否存在地表据点区（SurfaceBase 据此决定是否兜底管 IsAtSurface）。</summary>
        public static bool AnyExists => aliveCount > 0;

        /// <summary>有玩家在场的 Hub 区实例源个数（各实例在 0↔1 边界增减）。&gt;0 = 玩家正处地表 Hub。</summary>
        static int sourceCount;

        static int aliveCount;

        /// <summary>本实例内玩家 Collider 数（决定本实例是否为「在场源」）。</summary>
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
            EnterLocal();
        }

        void OnTriggerStay2D(Collider2D other)
        {
            if (other.GetComponent<DrillVehicle>() == null) return;
            // 兜底：极端时序下物理可能先于 Enter 回调（如生成时已重叠），Stay 补记一次。
            if (localCount == 0) EnterLocal();
        }

        void OnTriggerExit2D(Collider2D other)
        {
            if (other.GetComponent<DrillVehicle>() == null) return;
            ExitLocal();
        }

        void OnDisable() => ReleaseAll();

        void OnDestroy()
        {
            ReleaseAll();
            aliveCount = Mathf.Max(0, aliveCount - 1);
        }

        void EnterLocal()
        {
            if (localCount == 0) ReportSource(true);
            localCount++;
        }

        void ExitLocal()
        {
            if (localCount <= 0) return;
            localCount--;
            if (localCount == 0) ReportSource(false);
        }

        /// <summary>一次性释放本实例全部本地占用（禁用/销毁/场景卸载），全局源只退一次。</summary>
        void ReleaseAll()
        {
            if (localCount <= 0) return;
            localCount = 0;
            ReportSource(false);
        }

        static void ReportSource(bool entered)
        {
            var gm = GameManager.Instance;
            if (entered)
            {
                if (sourceCount == 0 && gm != null) gm.IsAtSurface = true;
                sourceCount++;
            }
            else
            {
                if (sourceCount <= 0) return;
                sourceCount--;
                if (sourceCount == 0 && gm != null) gm.IsAtSurface = false;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            aliveCount = 0;
            sourceCount = 0;
        }
    }
}
