using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-005：地表出售终端。
    ///
    /// 正式售矿路径（不自动卖）：玩家带着矿物回到地表，走进终端交互范围后按 E，
    /// 一次清空货舱并按每种矿物自己的 value 计价 → Cash 增加 → 货舱清空。
    /// 空舱 / 已卖完再按 E 不产生任何结算（天然防重复出售）。
    ///
    /// 设计要点：
    ///  - 货舱数据唯一真相源仍是 InventoryGrid（经 DrillVehicle），本终端只做「结算」；
    ///  - UnloadCargo 按 TotalValue = Σ(def.value × count) 逐种计价，不按固定单价；
    ///  - 大型遗物 / 研究物品等未来特殊物品不在 V1 出售范围（届时在此扩展过滤）；
    ///  - 交互范围用 Trigger Collider（isTrigger 自动设置），InRange 供 HUD 提示。
    ///
    /// 挂在哪：地表出售终端占位物上，需带 Collider2D。
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
    public class SellTerminal : MonoBehaviour
    {
        /// <summary>玩家当前是否在任一终端交互范围内（HUD 提示用）。</summary>
        public static bool PlayerInRange { get; private set; }

        [Tooltip("出售按键")]
        public KeyCode sellKey = KeyCode.E;

        [Tooltip("出售时是否顺带恢复满燃料/维修（终端=整备台？默认否——补给走 SurfaceBase）")]
        public bool alsoRefuel = false;

        int inRangeCount;   // 玩家进出计数（OnTriggerEnter/Exit 配对）

        void Awake()
        {
            var col = GetComponent<Collider2D>();
            col.isTrigger = true;
        }

        void Update()
        {
            var p = GameManager.Instance != null ? GameManager.Instance.Player : null;
            if (p == null || p.IsDead) return;

            if (Input.GetKeyDown(sellKey) && PlayerInRange)
                TrySell(p);
        }

        void OnTriggerEnter2D(Collider2D other)
        {
            if (other.GetComponent<DrillVehicle>() != null)
            {
                inRangeCount++;
                PlayerInRange = inRangeCount > 0;
            }
        }

        void OnTriggerExit2D(Collider2D other)
        {
            if (other.GetComponent<DrillVehicle>() != null)
            {
                inRangeCount = Mathf.Max(0, inRangeCount - 1);
                PlayerInRange = inRangeCount > 0;
            }
        }

        /// <summary>
        /// 出售全部矿物。返回本次收益；0 = 空舱 / 无效（不结算、不加钱）。
        /// 测试 / 脚本可直接调用（等价玩家按 E 的结算路径）。
        /// </summary>
        public int TrySell(DrillVehicle v)
        {
            var gm = GameManager.Instance;
            if (v == null || gm == null || v.IsDead) return 0;

            int earned = v.UnloadCargo();
            if (earned <= 0) return 0;   // 空舱 / 已售完：不重复结算

            gm.AddCash(earned);
            gm.LastServiceMessage = $"出售矿物 +${earned}  现金 ${gm.Cash}";
            Debug.Log($"[SellTerminal] 出售矿物 +${earned}，现金 ${gm.Cash}");

            if (alsoRefuel)
            {
                int fuelCost = v.Refuel(gm.fuelPricePerUnit, gm.Cash);
                if (fuelCost > 0)
                {
                    gm.SpendCash(fuelCost);
                    gm.LastServiceMessage += $"  加油 -${fuelCost}";
                }
            }
            return earned;
        }
    }
}
