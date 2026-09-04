using UnityEngine;

namespace Ashfall
{
    /// <summary>六个可升级部件</summary>
    public enum UpgradePart
    {
        Drill,      // 钻头：唯一硬门槛，决定能挖多硬
        Hull,       // 船体：生命值
        Engine,     // 引擎：移动 & 钻探速度
        FuelTank,   // 油箱：燃料上限
        Radiator,   // 散热器：抵抗深层高温
        CargoBay    // 货舱：载货容量
    }

    /// <summary>
    /// 升级系统：六项部件的等级、价格与效果。
    /// 挂在哪：与 GameManager 同一个物体（GameManager 会自动 GetComponent 拿到它）。
    /// </summary>
    public class UpgradeSystem : MonoBehaviour
    {
        [Header("当前等级（可在 Inspector 直接改，方便调试）")]
        [Min(1)] public int drillLevel = 1;
        [Min(1)] public int hullLevel = 1;
        [Min(1)] public int engineLevel = 1;
        [Min(1)] public int fuelTankLevel = 1;
        [Min(1)] public int radiatorLevel = 1;
        [Min(1)] public int cargoBayLevel = 1;

        [Header("成长曲线（每级增量）— v2 平衡值")]
        [Tooltip("油箱每级增量。v2：120 + 140×(Lv-1)。原值 100+40/级 会在阶段 2 就出现「下去了上不来」")]
        public float fuelPerTankLevel = 140f;
        public float hullPerLevel = 30f;

        [Tooltip("货舱每级增加的背包行数（列固定 6 列）→ 格数 12/18/24/30/36/42")]
        public int cargoRowsPerLevel = 1;

        [Tooltip("货舱每级增加的载重上限。这才是真正的容量 —— 见 MaxCarryWeight 注释")]
        public float carryWeightPerLevel = 14f;

        public float speedPerEngineLevel = 0.8f;
        public float drillSpeedPerEngineLevel = 0.25f;
        [Tooltip("安全深度每级增量。v2：250 + 70×(Lv-1)")]
        public float safeDepthPerRadiatorLevel = 70f;

        [Header("平衡（CoreLoop 测试场景可调，默认 1 不影响既有场景）")]
        [Tooltip("全局升级价格乘数。1 = 原价。测试/调试可调低以便短循环内可购买，不改公式结构")]
        [Min(0.01f)] public float costMultiplier = 1f;

        // ---------- 派生属性 ----------

        /// <summary>钻头等级 = 能挖穿的最高硬度</summary>
        public int DrillLevel => drillLevel;

        public float MaxFuel => 120f + (fuelTankLevel - 1) * fuelPerTankLevel;
        public float MaxHull => 100f + (hullLevel - 1) * hullPerLevel;
        public float MoveSpeed => 4f + (engineLevel - 1) * speedPerEngineLevel;

        /// <summary>背包行数：Lv1 起 2 行，每级 +1 → 2/3/4/5/6/7</summary>
        public int CargoRows => 2 + (cargoBayLevel - 1) * cargoRowsPerLevel;

        /// <summary>背包格数 = 6 列 × 行数 → 12/18/24/30/36/42。只是容器上限，通常不会装满。</summary>
        public int CargoCapacity => InventoryGrid.Columns * CargoRows;

        /// <summary>
        /// 载重上限（真正的容量）：24 + 14×(Lv-1) → 24/38/52/66/80/94。
        ///
        /// 【为什么不由格子数决定容量】格子背包若按格计容量，24 格 × 16 堆叠 = 384 件，
        /// 是原设计（20~95 件）的 19 倍，经济系统会当场崩掉。
        /// 所以改由重量把关：铁矿 1.0/块 ⇒ Lv1 最多带 24 块，与原设计的 20 块同量级；
        /// 钻石 3.5/块 ⇒ 同样载重只能带 6 块。矿物「重而小」，重量先到顶；
        /// 未来的残骸「大而轻」，会把格子撑满 —— 两套约束各管一件事，形成取舍。
        /// </summary>
        public float MaxCarryWeight => 24f + (cargoBayLevel - 1) * carryWeightPerLevel;
        public float DrillSpeedMultiplier => 1f + (engineLevel - 1) * drillSpeedPerEngineLevel;

        /// <summary>
        /// 散热效果直接以「安全深度」给出：超过该深度开始过热。v2：250 + 70×(Lv-1)。
        /// 【为什么不再用冷却系数】原设计把换算劈成两半 —— UpgradeSystem 给 Cooling 系数，
        /// DrillVehicle 再写死 baseSafeDepth + (Cooling-1)×60。要得到 v2 的 +70/级，
        /// coolingPerRadiatorLevel 得填 1.1667 这种凑数，是坏设计。现在单点定义。
        /// </summary>
        public float SafeDepth => 250f + (radiatorLevel - 1) * safeDepthPerRadiatorLevel;

        // ---------- 等级读写 ----------

        public int GetLevel(UpgradePart part)
        {
            switch (part)
            {
                case UpgradePart.Drill: return drillLevel;
                case UpgradePart.Hull: return hullLevel;
                case UpgradePart.Engine: return engineLevel;
                case UpgradePart.FuelTank: return fuelTankLevel;
                case UpgradePart.Radiator: return radiatorLevel;
                case UpgradePart.CargoBay: return cargoBayLevel;
                default: return 1;
            }
        }

        void SetLevel(UpgradePart part, int value)
        {
            int v = Mathf.Max(1, value);
            switch (part)
            {
                case UpgradePart.Drill: drillLevel = v; break;
                case UpgradePart.Hull: hullLevel = v; break;
                case UpgradePart.Engine: engineLevel = v; break;
                case UpgradePart.FuelTank: fuelTankLevel = v; break;
                case UpgradePart.Radiator: radiatorLevel = v; break;
                case UpgradePart.CargoBay: cargoBayLevel = v; break;
            }
        }

        // ---------- 价格 ----------

        /// <summary>v2 基础价。比例：钻头 1.0 / 货舱 0.7 / 油箱 0.6 / 散热 0.5 / 船体 0.45 / 引擎 0.4</summary>
        float GetBaseCost(UpgradePart part)
        {
            switch (part)
            {
                case UpgradePart.Drill: return 1000f;
                case UpgradePart.Hull: return 450f;
                case UpgradePart.Engine: return 400f;
                case UpgradePart.FuelTank: return 600f;
                case UpgradePart.Radiator: return 500f;
                case UpgradePart.CargoBay: return 700f;
                default: return 200f;
            }
        }

        /// <summary>
        /// 等级上限。钻头只到 Lv5（Lv5 即可挖穿硬度 5 的核心矿，再往上没有内容），
        /// 其余部件到 Lv6。原原型无上限，钻头能买到 Lv6 花 3276 万却毫无作用。
        /// </summary>
        public int GetMaxLevel(UpgradePart part) => part == UpgradePart.Drill ? 5 : 6;

        public bool IsMaxLevel(UpgradePart part) => GetLevel(part) >= GetMaxLevel(part);

        /// <summary>
        /// 下一级价格：base × 8^(当前等级-1)（v2）。
        /// 为什么是 ×8：矿物价值每档涨约 ×2.8、货舱同时涨 ~1.4 倍 ⇒ 单次 run 收益每阶段涨约 ×4；
        /// 要维持「2.5 次 run 升一级」成本须同步涨 ×4，再留出买副件的空间 ⇒ 取 ×8。
        /// 已满级时返回 0（由 IsMaxLevel 判断，HUD 显示「已满级」）。
        /// </summary>
        public int GetCost(UpgradePart part)
        {
            if (IsMaxLevel(part)) return 0;
            int level = GetLevel(part);
            return Mathf.RoundToInt(GetBaseCost(part) * costMultiplier * Mathf.Pow(8f, level - 1));
        }

        public string GetDisplayName(UpgradePart part)
        {
            switch (part)
            {
                case UpgradePart.Drill: return "钻头 Drill";
                case UpgradePart.Hull: return "船体 Hull";
                case UpgradePart.Engine: return "引擎 Engine";
                case UpgradePart.FuelTank: return "油箱 FuelTank";
                case UpgradePart.Radiator: return "散热 Radiator";
                case UpgradePart.CargoBay: return "货舱 CargoBay";
                default: return part.ToString();
            }
        }

        // ---------- 购买 ----------

        /// <summary>尝试购买升级。成功返回 true 并扣钱。</summary>
        public bool TryBuy(UpgradePart part)
        {
            var gm = GameManager.Instance;
            if (gm == null) return false;

            if (IsMaxLevel(part)) return false;

            int cost = GetCost(part);
            if (gm.Cash < cost) return false;

            gm.SpendCash(cost);
            SetLevel(part, GetLevel(part) + 1);

            // 让属性立刻生效
            if (gm.Player != null) gm.Player.ApplyUpgradeStats();

            Debug.Log($"[Upgrade] 已升级 {GetDisplayName(part)} → Lv{GetLevel(part)}，花费 ${cost}");
            return true;
        }
    }
}
