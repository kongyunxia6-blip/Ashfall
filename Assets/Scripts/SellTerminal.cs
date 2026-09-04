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
    /// DEV-006 第一轮：静态生命周期修复 —— 在场状态改由 HubZoneTracker 按 kind 全局计数（唯一权威）：
    ///  - 多个 SellTerminal 时，离开其中一个不会错误清掉另一个仍在场的状态；
    ///  - 本实例 OnDisable/OnDestroy 主动归还计数，场景卸载/对象禁用后 PlayerInRange 不残留 true；
    ///  - 按 E 出售只在【本实例】范围内有效（ZonePresence.Present），避免多终端同帧重复结算路径。
    ///
    /// DEV-006 第二轮：每实例计数收拢到 ZonePresence —— 正常 OnTriggerExit 逐 Collider 配对，
    /// Disable/Destroy 走 ReleaseAll 一次性归还本实例全部占用（多 Collider 玩家也不残留全局计数）。
    ///
    /// 设计要点：
    ///  - 货舱数据唯一真相源仍是 InventoryGrid（经 DrillVehicle），本终端只做「结算」；
    ///  - UnloadCargo 按 TotalValue = Σ(def.value × count) 逐种计价，不按固定单价；
    ///  - 交互范围用 Trigger Collider（isTrigger 自动设置），InRange 供 HUD 提示。
    ///
    /// 挂在哪：地表出售终端占位物上，需带 Collider2D。
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
    public class SellTerminal : MonoBehaviour
    {
        /// <summary>玩家当前是否在任一终端交互范围内（HUD 提示用）。</summary>
        public static bool PlayerInRange => HubZoneTracker.InRange(HubZoneKind.Sell);

        [Tooltip("出售按键")]
        public KeyCode sellKey = KeyCode.E;

        [Tooltip("出售时是否顺带恢复满燃料/维修（终端=整备台？默认否——补给走 FuelStation / SurfaceBase）")]
        public bool alsoRefuel = false;

        /// <summary>本实例在场计数（ZonePresence：逐 Collider 配对；禁用/销毁时 ReleaseAll 一次归还）。</summary>
        readonly ZonePresence presence = new ZonePresence(HubZoneKind.Sell);

        void Awake()
        {
            var col = GetComponent<Collider2D>();
            col.isTrigger = true;
        }

        void Update()
        {
            var p = GameManager.Instance != null ? GameManager.Instance.Player : null;
            if (p == null || p.IsDead) return;

            if (presence.Present && Input.GetKeyDown(sellKey))
                TrySell(p);
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

        void OnDestroy() => presence.ReleaseAll();

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
