using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// 地表基地触发区：进入即自动卖矿，并按当前现金加油 / 修船。
    /// 挂在哪：地表坑口的空物体，需带 Collider2D（会自动设为 Trigger）。
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
    public class SurfaceBase : MonoBehaviour
    {
        [Tooltip("重复补给的最小间隔（秒），避免刷屏")]
        public float serviceInterval = 0.5f;

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

        void Service(DrillVehicle v)
        {
            var gm = GameManager.Instance;
            if (gm == null || v.IsDead) return;

            string msg = "";

            int earned = v.UnloadCargo();
            if (earned > 0)
            {
                gm.AddCash(earned);
                msg += $"卖出矿物 +${earned}   ";
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
