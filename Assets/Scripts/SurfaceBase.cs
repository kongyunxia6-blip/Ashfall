using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// 地表基地触发区：返航判定（IsAtSurface）+ 自动补给（加油 / 维修，按现金收费）。
    ///
    /// DEV-005：出售不再自动 —— 正式规则是「返航不自动卖，玩家需到出售终端按 E 交互出售」。
    /// autoSellCargo 默认 false；旧原型场景若依赖「进基地自动卖矿」，可手动勾回 true。
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
    public class SurfaceBase : MonoBehaviour
    {
        [Tooltip("重复补给的最小间隔（秒），避免刷屏")]
        public float serviceInterval = 0.5f;

        [Tooltip("进基地是否自动卖矿。正式规则=false（返航只补给，出售走 SellTerminal 交互）；旧原型可开 true 保留旧行为")]
        public bool autoSellCargo = false;

        float cooldown;

        void Awake()
        {
            var col = GetComponent<Collider2D>();
            col.isTrigger = true;
        }

        void OnTriggerEnter2D(Collider2D other)
        {
            var v = other.GetComponent<DrillVehicle>();
            if (v == null) return;

            if (GameManager.Instance != null) GameManager.Instance.IsAtSurface = true;
            Service(v);
            cooldown = serviceInterval;
        }

        void OnTriggerStay2D(Collider2D other)
        {
            var v = other.GetComponent<DrillVehicle>();
            if (v == null) return;

            if (GameManager.Instance != null) GameManager.Instance.IsAtSurface = true;
            if (v.IsDead) return;

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
            if (GameManager.Instance != null) GameManager.Instance.IsAtSurface = false;
        }

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
