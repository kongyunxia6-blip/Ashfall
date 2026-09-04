using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// 地表登陆舱 / 返航安全区。
    ///
    /// DEV-005：出售不再自动 —— 正式规则是「返航不自动卖，玩家需到出售终端按 E 交互出售」。
    /// autoSellCargo 默认 false；旧原型场景若依赖「进基地自动卖矿」，可手动勾回 true。
    ///
    /// DEV-006：本区正式职责 = 「登陆舱 / 返航安全区（Lander 上下文）」，不再代表整个地表。
    ///  - 玩家在场计数经 HubZonePresence 提供（HubZoneTracker.Lander，HUD 显示「已安全返回」用）；
    ///  - 自动补给收束到独立功能点：Hub 场景把 enableAutoService 设 false，玩家到 FuelStation 按 E 加油；
    ///  - GameManager.IsAtSurface 由覆盖整个据点的 SurfaceHubZone 负责（见 SurfaceHubZone 类注释）。
    ///
    /// 兼容（surfaceOwner 兜底）：场景中不存在 SurfaceHubZone 时（旧原型场景 / DEV-005 测试场景），
    /// 本 Lander 维持原行为自己管理 IsAtSurface（进 Lander=地表、出 Lander=离开地表）；
    /// 一旦场景里放置了 SurfaceHubZone（DEV-006 Hub 场景），本组件不再写 IsAtSurface。
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
    public class SurfaceBase : MonoBehaviour
    {
        /// <summary>玩家当前是否在登陆舱安全区内（HUD 上下文提示用）。</summary>
        public static bool PlayerInRange => HubZoneTracker.InRange(HubZoneKind.Lander);

        [Tooltip("重复补给的最小间隔（秒），避免刷屏")]
        public float serviceInterval = 0.5f;

        [Tooltip("进基地是否自动卖矿。正式规则=false（返航只补给，出售走 SellTerminal 交互）；旧原型可开 true 保留旧行为")]
        public bool autoSellCargo = false;

        [Tooltip("是否自动补给（进入/停留即加油+维修，按现金收费）。正式 Hub 场景=false：补给收束到 FuelStation 按 E 交互，本区只做返航安全判定；旧场景默认 true 保持原行为")]
        public bool enableAutoService = true;

        float cooldown;

        /// <summary>本实例在场计数（ZonePresence：逐 Collider 配对；禁用/销毁时 ReleaseAll 一次归还）。</summary>
        readonly ZonePresence presence = new ZonePresence(HubZoneKind.Lander);

        /// <summary>本 Lander 是否负责 GameManager.IsAtSurface（= 场景中不存在 SurfaceHubZone 时的旧场景兜底）。</summary>
        bool SurfaceOwner => !SurfaceHubZone.AnyExists;

        void Awake()
        {
            var col = GetComponent<Collider2D>();
            col.isTrigger = true;
        }

        void OnTriggerEnter2D(Collider2D other)
        {
            var v = other.GetComponent<DrillVehicle>();
            if (v == null) return;

            presence.Enter();
            if (SurfaceOwner && GameManager.Instance != null) GameManager.Instance.IsAtSurface = true;

            if (!enableAutoService) return;
            Service(v);
            cooldown = serviceInterval;
        }

        void OnTriggerStay2D(Collider2D other)
        {
            var v = other.GetComponent<DrillVehicle>();
            if (v == null) return;

            if (SurfaceOwner && GameManager.Instance != null) GameManager.Instance.IsAtSurface = true;
            if (v.IsDead || !enableAutoService) return;

            cooldown -= Time.deltaTime;
            if (cooldown <= 0f)
            {
                Service(v);
                cooldown = serviceInterval;
            }
        }

        void OnTriggerExit2D(Collider2D other)
        {
            if (other.GetComponent<DrillVehicle>() == null) return;
            presence.Exit();
            if (SurfaceOwner && GameManager.Instance != null) GameManager.Instance.IsAtSurface = false;
        }

        void OnDisable() => presence.ReleaseAll();

        void OnDestroy() => presence.ReleaseAll();

        /// <summary>补给/服务（供 trigger 与外部脚本/测试调用；幂等：满油满血时花费为 0）。</summary>
        public void ServiceNow(DrillVehicle v) => Service(v);

        void Service(DrillVehicle v)
        {
            var gm = GameManager.Instance;
            if (gm == null || v.IsDead) return;

            string msg = "";

            // DEV-005：出售不再自动 —— 只在旧原型兼容开关打开时才 UnloadCargo。
            // 正式路径：玩家回地表后在 SellTerminal 按 E 交互出售。
            if (autoSellCargo)
            {
                int earned = v.UnloadCargo();
                if (earned > 0)
                {
                    gm.AddCash(earned);
                    msg += $"卖出矿物 +${earned}   ";
                }
            }

            int fuelCost = v.Refuel(gm.fuelPricePerUnit, gm.Cash);
            if (fuelCost > 0)
            {
                gm.SpendCash(fuelCost);
                msg += $"加油 -${fuelCost}   ";
            }

            int repairCost = v.Repair(gm.repairPricePerPoint, gm.Cash);
            if (repairCost > 0)
            {
                gm.SpendCash(repairCost);
                msg += $"维修 -${repairCost}";
            }

            if (!string.IsNullOrEmpty(msg))
            {
                gm.LastServiceMessage = msg;
                Debug.Log($"[SurfaceBase] {msg}");
            }
        }
    }
}
