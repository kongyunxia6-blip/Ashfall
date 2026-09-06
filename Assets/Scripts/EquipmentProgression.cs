using System;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-010：装备成长 + 模块系统 V1（权威组件）。
    ///
    /// 职责（对齐 Issue #21）：
    ///  - 保存四条装备线等级（Drill/FuelTank/CargoHold/Mobility，Lv0..3）与现金交易；
    ///  - 保存模块「拥有 / 已装备」状态、槽位、购买 / Equip / Unequip（仅 Workbench 范围）；
    ///  - 从「Base Vehicle Stats + Equipment Level Modifiers + Module Modifiers」合成
    ///    Effective Stats —— 单一来源，升级/装卸后即时刷新，绝无每帧累乘漂移；
    ///  - 只读查询给 DrillVehicle / GameHUD / Workbench / 验收脚本；
    ///  - 发出 OnStatsChanged / OnEquipmentChanged 事件；
    ///  - 不负责地形生成，不直接修改 DigGrid，永不改变采矿范围（每次仍 1 Block）。
    ///
    /// 挂载：GameManager 同物体（GameManager.Equipment 自动 GetComponent 拿到）。
    /// 旧场景（无本组件）继续走 UpgradeSystem —— 本组件存在时 DrillVehicle 属性源切换到这里，
    /// 互不干扰，DEV-005~009 旧测试场景零回归。
    ///
    /// 数值唯一来源：EquipmentCatalog（价格 / 效果表全部集中在那里，本类不出现 magic number）。
    /// </summary>
    public class EquipmentProgression : MonoBehaviour
    {
        // ---------- 等级状态 ----------
        [Header("装备等级（Lv0..3；Inspector 可直接改，便于调试）")]
        [Range(0, 3)] public int drillLevel;
        [Range(0, 3)] public int fuelTankLevel;
        [Range(0, 3)] public int cargoHoldLevel;
        [Range(0, 3)] public int mobilityLevel;

        // ---------- 模块状态 ----------
        [Header("模块（拥有 + 已装备）")]
        [Tooltip("已购买（解锁）的模块。同模块只能拥有一次。")]
        public bool[] ownedModules = new bool[5];        // 索引 = (int)EquipmentModule（DEV-013 扩到 5：新增 RuinAccessKey）
        [Tooltip("当前已装备的模块（长度 = 槽位上限 2；null 位 = 空槽）。")]
        public EquipmentModule?[] equipped = new EquipmentModule?[2];

        // ---------- 事件 ----------
        /// <summary>任意装备变化（升级 / 购买 / 装卸）后触发。HUD/DrillVehicle 用它即时刷新。</summary>
        public event Action OnStatsChanged;

        // ---------- 只读：等级 ----------
        public int GetLevel(EquipmentLine line)
        {
            switch (line)
            {
                case EquipmentLine.Drill: return drillLevel;
                case EquipmentLine.FuelTank: return fuelTankLevel;
                case EquipmentLine.CargoHold: return cargoHoldLevel;
                case EquipmentLine.Mobility: return mobilityLevel;
                default: return 0;
            }
        }

        public bool IsMaxLevel(EquipmentLine line) => GetLevel(line) >= EquipmentCatalog.MaxLevel;

        public int NextLevelCost(EquipmentLine line) => EquipmentCatalog.CostOf(line, GetLevel(line) + 1);

        // ---------- 只读：模块 ----------
        public int SlotCount => EquipmentCatalog.SlotCount(drillLevel, fuelTankLevel, cargoHoldLevel, mobilityLevel);
        public int OwnedCount => CountOwned();
        public int EquippedCount => CountEquipped();
        public bool IsOwned(EquipmentModule m) => ownedModules[(int)m];
        public bool IsEquipped(EquipmentModule m)
        {
            for (int i = 0; i < equipped.Length; i++)
                if (equipped[i].HasValue && equipped[i].Value == m) return true;
            return false;
        }

        // ---------- 只读：Effective Stats（单一来源合成，非累乘） ----------
        // 公式：Effective = Base(车辆出厂) ∘ Level(等级表) ∘ Module(模块表)。
        // 每次按当前快照重新计算（确定性），Equip→Unequip→Equip 结果一致，无漂移。

        public float EffectiveDigSpeedMultiplier
        {
            get
            {
                float m = EquipmentCatalog.DigSpeedMultiplier(drillLevel);
                if (IsEquipped(EquipmentModule.DrillCooling)) m *= EquipmentCatalog.DrillCoolingDigMult;
                return m;
            }
        }

        public float EffectiveMaxFuel => EquipmentCatalog.MaxFuel(fuelTankLevel);

        /// <summary>载重上限 = 货舱线基础 + 强化货架模块加成。只提高上限，不凭空送货物。</summary>
        public float EffectiveMaxCarryWeight
        {
            get
            {
                float w = EquipmentCatalog.MaxCarryWeight(cargoHoldLevel);
                if (IsEquipped(EquipmentModule.ReinforcedCargoRack)) w += EquipmentCatalog.CargoRackCarryBonus;
                return w;
            }
        }

        /// <summary>格行数：仅货舱线决定（模块不加行 → 卸货架不会触发格子收缩丢货）。</summary>
        public int EffectiveCargoRows => EquipmentCatalog.CargoRows(cargoHoldLevel);

        public float EffectiveMoveSpeed => EquipmentCatalog.MoveSpeed(mobilityLevel);

        /// <summary>移动/悬停耗油倍率：节能电机未装 = 1.0；已装 = 0.85。挖掘耗油不受模块影响。</summary>
        public float EffectiveMoveFuelMultiplier
            => IsEquipped(EquipmentModule.EfficientMotor) ? EquipmentCatalog.EfficientMotorFuelMult : 1f;

        /// <summary>Scanner 半径加成：勘探传感器未装 = 0；已装 = +2。</summary>
        public int EffectiveScannerRadiusBonus
            => IsEquipped(EquipmentModule.SurveySensor) ? EquipmentCatalog.SurveySensorRadiusBonus : 0;

        // ---------- 升级 ----------

        /// <summary>
        /// 尝试升级一条线。规则：
        ///  - 需在 Workbench 范围内（或场景无 Workbench → 地表任意位置，兼容旧场景风格）；
        ///  - Cash 不足 / 已满级 → 失败，Cash/等级不变；
        ///  - 成功扣 Cash + 等级 +1 → ApplyUpgradeStats 即时生效 → 事件。
        /// 返回 (成功, 原因)。
        /// </summary>
        public (bool ok, string reason) TryUpgrade(EquipmentLine line)
        {
            if (!CanAccessWorkbench())
                return (false, "升级需靠近装备工作台");

            if (IsMaxLevel(line))
                return (false, $"{EquipmentCatalog.LineDisplayName(line)} 已满级");

            int cost = NextLevelCost(line);
            var gm = GameManager.Instance;
            if (gm == null) return (false, "无 GameManager");
            if (gm.Cash < cost)
                return (false, $"现金不足：{EquipmentCatalog.LineDisplayName(line)} Lv{GetLevel(line) + 1} 需要 ${cost}");

            gm.SpendCash(cost);
            SetLevel(line, GetLevel(line) + 1);

            // 升 MaxFuel 只提高上限，不免费补满（玩家仍需 FuelStation 补油）
            var p = gm.Player;
            if (p != null) p.ApplyUpgradeStats();
            NotifyChanged();
            Debug.Log($"[EquipmentProgression] 升级 {EquipmentCatalog.LineDisplayName(line)} → Lv{GetLevel(line)}，花费 ${cost}，现金 ${gm.Cash}");
            return (true, $"已升级 {EquipmentCatalog.LineDisplayName(line)} → Lv{GetLevel(line)}");
        }

        // ---------- 模块交易 ----------

        /// <summary>购买（解锁）模块。已拥有 / Cash 不足 → 失败且状态不变。只在 Workbench 范围可买。</summary>
        public (bool ok, string reason) TryBuyModule(EquipmentModule m)
        {
            if (!CanAccessWorkbench())
                return (false, "购买模块需靠近装备工作台");
            if (IsOwned(m))
                return (false, $"已拥有 {EquipmentCatalog.ModuleDisplayName(m)}，请勿重复购买");

            int cost = EquipmentCatalog.ModuleCost(m);
            var gm = GameManager.Instance;
            if (gm == null) return (false, "无 GameManager");
            if (gm.Cash < cost)
                return (false, $"现金不足：购买 {EquipmentCatalog.ModuleDisplayName(m)} 需要 ${cost}");

            gm.SpendCash(cost);
            ownedModules[(int)m] = true;
            NotifyChanged();
            Debug.Log($"[EquipmentProgression] 购买模块 {EquipmentCatalog.ModuleDisplayName(m)}（${cost}），现金 ${gm.Cash}");
            return (true, $"已购买模块 {EquipmentCatalog.ModuleDisplayName(m)}（${cost}）");
        }

        /// <summary>装备模块。只在 Workbench 范围可装。已拥有 + 未装 + 有空槽 → 成功。</summary>
        public (bool ok, string reason) TryEquipModule(EquipmentModule m)
        {
            if (!CanAccessWorkbench())
                return (false, "装卸模块需靠近装备工作台");
            if (!IsOwned(m))
                return (false, $"尚未拥有 {EquipmentCatalog.ModuleDisplayName(m)}");
            if (IsEquipped(m))
                return (false, $"{EquipmentCatalog.ModuleDisplayName(m)} 已装备");

            int free = FindFreeSlot();
            if (free < 0)
                return (false, $"模块槽已满（{SlotCount}/{SlotCount}），请先卸下再换装");

            equipped[free] = m;
            ApplyToPlayerAndNotify();
            return (true, $"已装备 {EquipmentCatalog.ModuleDisplayName(m)}");
        }

        /// <summary>卸下模块。只在 Workbench 范围可卸。已装备 → 成功（清空该槽）。</summary>
        public (bool ok, string reason) TryUnequipModule(EquipmentModule m)
        {
            if (!CanAccessWorkbench())
                return (false, "装卸模块需靠近装备工作台");
            if (!IsEquipped(m))
                return (false, $"{EquipmentCatalog.ModuleDisplayName(m)} 未装备");

            for (int i = 0; i < equipped.Length; i++)
            {
                if (equipped[i].HasValue && equipped[i].Value == m)
                {
                    equipped[i] = null;
                    ApplyToPlayerAndNotify();
                    return (true, $"已卸下 {EquipmentCatalog.ModuleDisplayName(m)}");
                }
            }
            return (false, "状态异常：模块槽未找到");
        }

        /// <summary>快速 toggle：未拥有→购买；已拥有未装→装备；已装→卸下。供 HUD 5~8 键。</summary>
        public (bool ok, string reason) TryToggleModule(EquipmentModule m)
        {
            if (!IsOwned(m)) return TryBuyModule(m);
            if (IsEquipped(m)) return TryUnequipModule(m);
            return TryEquipModule(m);
        }

        // ---------- 内部 ----------

        void SetLevel(EquipmentLine line, int level)
        {
            switch (line)
            {
                case EquipmentLine.Drill: drillLevel = level; break;
                case EquipmentLine.FuelTank: fuelTankLevel = level; break;
                case EquipmentLine.CargoHold: cargoHoldLevel = level; break;
                case EquipmentLine.Mobility: mobilityLevel = level; break;
            }
        }

        void ApplyToPlayerAndNotify()
        {
            var p = GameManager.Instance != null ? GameManager.Instance.Player : null;
            if (p != null) p.ApplyUpgradeStats();
            NotifyChanged();
        }

        void NotifyChanged()
        {
            try { OnStatsChanged?.Invoke(); }
            catch (Exception e) { Debug.LogWarning("[EquipmentProgression] OnStatsChanged 订阅者异常：" + e.Message); }
        }

        int CountOwned()
        {
            int n = 0;
            for (int i = 0; i < ownedModules.Length; i++) if (ownedModules[i]) n++;
            return n;
        }

        int CountEquipped()
        {
            int n = 0;
            for (int i = 0; i < equipped.Length; i++) if (equipped[i].HasValue) n++;
            return n;
        }

        int FindFreeSlot()
        {
            int cap = SlotCount;   // 只允许使用已解锁的槽位（初始 1，任一装备 Lv2 解锁第 2 个）
            for (int i = 0; i < cap; i++)
                if (!equipped[i].HasValue) return i;
            return -1;
        }

        /// <summary>
        /// Workbench 访问门控（DEV-010 规则）：升级/购买/装卸只能在 Workbench 范围操作。
        /// 场景必须存在 Workbench 且玩家在其 PlayerInRange 内才放行；
        /// 无 Workbench 的场景一律拒绝（不再回退地表任意位置）。
        /// 说明：GameHUD 面板可见性 (canShop) 保持宽松仅为兼容旧 UpgradeSystem 场景，
        /// 一切状态修改都经本门控在数据层拒绝。
        /// </summary>
        public static bool CanAccessWorkbench()
        {
            var gm = GameManager.Instance;
            if (gm == null || !gm.IsAtSurface) return false;
            if (!UpgradeWorkbench.AnyExists) return false;
            return UpgradeWorkbench.PlayerInRange;
        }

        // ---------- 状态重置（验收/新 Run 用） ----------

        /// <summary>重置为出厂状态：四线 Lv0、无模块。不触碰 Cash / Cargo / 地形。</summary>
        public void ResetProgression()
        {
            drillLevel = fuelTankLevel = cargoHoldLevel = mobilityLevel = 0;
            for (int i = 0; i < ownedModules.Length; i++) ownedModules[i] = false;
            for (int i = 0; i < equipped.Length; i++) equipped[i] = null;
            NotifyChanged();
        }
    }
}
