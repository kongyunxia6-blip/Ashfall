using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-006：能源补给点 V1（独立补给站）。
    ///
    /// 玩家的 Fuel 补给从「SurfaceBase 进安全区自动扣费」收束到本功能点（按 E 交互加油）：
    ///  - 复用 DrillVehicle.Refuel（现有 Fuel/Cash 逻辑，不新增 Energy 系统）；
    ///  - 规则：满 Fuel 不重复扣费（返回 0）；Cash 不足时只补可支付的部分；
    ///  - HUD 通过 FuelStation.PlayerInRange 显示「按 E 加油」；结算结果写 LastServiceMessage。
    ///
    /// 职责边界：只管燃料补给，不卖矿（SellTerminal）、不卖升级（UpgradeWorkbench）。
    /// 挂在哪：地表补给站占位物上，需带 Collider2D（isTrigger 自动设置）。
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
    public class FuelStation : MonoBehaviour
    {
        /// <summary>玩家当前是否在任一补给点交互范围内（HUD 提示用）。</summary>
        public static bool PlayerInRange => HubZoneTracker.InRange(HubZoneKind.Fuel);

        [Tooltip("补给按键")]
        public KeyCode fuelKey = KeyCode.E;

        int localCount;

        void Awake()
        {
            var col = GetComponent<Collider2D>();
            col.isTrigger = true;
        }

        void Update()
        {
            var p = GameManager.Instance != null ? GameManager.Instance.Player : null;
            if (p == null || p.IsDead) return;

            if (localCount > 0 && Input.GetKeyDown(fuelKey))
                TryRefuel(p);
        }

        /// <summary>
        /// 加油结算（等价玩家按 E）。返回实际花费；0 = 满油 / 无效（不扣钱）。
        /// 满油重复操作为 0 花费；Cash 不足只补可支付部分（Refuel 语义）。
        /// 测试 / 脚本可直接调用。
        /// </summary>
        public int TryRefuel(DrillVehicle v)
        {
            var gm = GameManager.Instance;
            if (v == null || gm == null || v.IsDead) return 0;

            float need = v.MaxFuel - v.Fuel;
            if (need <= 0.001f)
            {
                gm.LastServiceMessage = $"能源补给：燃料已满 {v.Fuel:F0}/{v.MaxFuel:F0}，无需花费";
                Debug.Log("[FuelStation] 满油，未扣费");
                return 0;
            }

            if (gm.Cash <= 0)
            {
                gm.LastServiceMessage = "能源补给：现金不足，无法加油";
                Debug.Log("[FuelStation] 现金不足，未补给");
                return 0;
            }

            int cost = v.Refuel(gm.fuelPricePerUnit, gm.Cash);
            if (cost > 0)
            {
                gm.SpendCash(cost);
                gm.LastServiceMessage = $"能源补给：燃料 {v.Fuel:F0}/{v.MaxFuel:F0} · 花费 ${cost} · 现金 ${gm.Cash}";
                Debug.Log($"[FuelStation] 加油 -${cost}，燃料 {v.Fuel:F0}/{v.MaxFuel:F0}，现金 ${gm.Cash}");
                return cost;
            }

            gm.LastServiceMessage = "能源补给：现金不足，仅能补充部分燃料";
            return 0;
        }

        void OnTriggerEnter2D(Collider2D other)
        {
            if (other.GetComponent<DrillVehicle>() == null) return;
            if (localCount == 0) HubZoneTracker.Enter(HubZoneKind.Fuel);
            localCount++;
        }

        void OnTriggerExit2D(Collider2D other)
        {
            if (other.GetComponent<DrillVehicle>() == null) return;
            ReleaseLocal();
        }

        void OnDisable() => ReleaseLocal();

        void OnDestroy() => ReleaseLocal();

        /// <summary>归还本实例的在场计数（幂等；场景卸载/禁用/销毁后不残留 true）。</summary>
        void ReleaseLocal()
        {
            if (localCount <= 0) return;
            localCount--;
            if (localCount == 0) HubZoneTracker.Exit(HubZoneKind.Fuel);
        }
    }
}
