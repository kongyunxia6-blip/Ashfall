using System;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-010：四条装备成长线。Lv0 = 基础（出厂），Lv3 为 V1 上限。
    /// 永久规则：任何升级都【不改变采矿范围】——只影响挖掘速度 / 能耗 / 容量 / 机动。
    /// </summary>
    public enum EquipmentLine
    {
        Drill,        // 钻头：挖掘速度（单格不变）
        FuelTank,     // 燃料箱：MaxFuel 上限（只提上限，不免费补满）
        CargoHold,    // 货舱：载重/格子容量（只增不减，绝不凭空送货）
        Mobility      // 机动：移动速度（返航/穿洞更舒服，不做高速动作化）
    }

    /// <summary>
    /// DEV-010：V1 轻量模块。拥有与装备分离；同一模块不可重复装备；
    /// 装卸只能在地表 Workbench 范围；装卸不扣 Cash，购买模块扣 Cash。
    /// </summary>
    public enum EquipmentModule
    {
        EfficientMotor,        // 节能电机：降低移动/悬停 Fuel 消耗（不改变采矿范围）
        ReinforcedCargoRack,   // 强化货架：增加 CargoCapacity（不自动出售）
        DrillCooling,          // 钻头冷却：提升单格连续挖掘效率（不减命中格数）
        SurveySensor,          // 勘探传感器：小幅扩大 Scanner 半径（Scanner 仍只读）
        RuinAccessKey          // DEV-013 遗迹密钥：提供 RuinAccess 能力（打开 RuinSeal / 进入遗迹）
    }

    /// <summary>
    /// DEV-010：集中式数值表（价格/效果唯一来源，禁止散落 magic number）。
    /// V1 用静态集中定义（不强制 ScriptableObject 化，符合 Issue「可测试配置」要求）。
    ///
    /// 数值设计原则（对齐 DEV-005 经济 + Issue 价格原则）：
    ///  - 每线 Lv0 → Lv1 价 40~80：第一次返航出售后能接近/购买 1 个 Lv1；
    ///  - Lv2/Lv3 价按 ~2.5x / ~6x 递增：需要多趟积累，玩家不能一次全升满；
    ///  - 四条线不指数爆炸。
    ///
    /// 效果语义（单一来源，运行时由 EquipmentProgression 合成 Effective Stats）：
    ///  - Drill:         digSpeedMultiplier 1.0 → 1.25 / 1.55 / 1.9（间隔等比缩短，单格不变）
    ///  - FuelTank:      MaxFuel 120 → 150 / 185 / 225（只提高上限；补满走 FuelStation）
    ///  - CargoHold:     MaxCarryWeight 24 → 30 / 37 / 45；格行 2 → 3 / 4 / 5（升容不赠货，
    ///                   等级只增不减 → 收缩路径不会丢货）
    ///  - Mobility:      MoveSpeed 4 → 4.6 / 5.3 / 6.1（hover/walk/jet 共用同一 speed 参数）
    ///  - EfficientMotor:     移动/悬停 Fuel 消耗 ×0.85（挖掘耗油不变，采矿范围不变）
    ///  - ReinforcedCargoRack:载重 +6（仅加重量，不加格行 → 卸下时收缩不会丢格内货物；
    ///                       超重时只是拒绝继续装载，产生确定、安全状态）
    ///  - DrillCooling:        挖掘效率 ×1.22（等效攻速提升；一次命中 Block 数仍为 1）
    ///  - SurveySensor:        Scanner 半径 +2（Scanner 保持只读，不做全图/minimap）
    ///
    /// 模块槽：初始 1 个；任意一条装备线升到 Lv2 时解锁第 2 个槽（最多 2 个）。
    /// </summary>
    public static class EquipmentCatalog
    {
        // ---------- 每条线：满级 / 每级价格 / 效果 ----------

        public const int MaxLevel = 3;   // Lv0..3

        public static int MaxLevelOf(EquipmentLine line) => MaxLevel;

        /// <summary>升级到 nextLevel（1..3）的价格。</summary>
        public static int CostOf(EquipmentLine line, int nextLevel)
        {
            switch (line)
            {
                case EquipmentLine.Drill:      return P(nextLevel, 60, 150, 380);
                case EquipmentLine.FuelTank:   return P(nextLevel, 70, 170, 420);
                case EquipmentLine.CargoHold:  return P(nextLevel, 80, 200, 500);
                case EquipmentLine.Mobility:   return P(nextLevel, 45, 110, 280);
                default: return 9999;
            }
        }

        static int P(int lv, int c1, int c2, int c3)
        {
            switch (lv)
            {
                case 1: return c1;
                case 2: return c2;
                case 3: return c3;
                default: return 0;   // 已满级 / 非法 → 0 价（调用方须先 IsMaxLevel）
            }
        }

        // ---------- 效果函数（输入 0..3 级 → 输出 modifier / 绝对量） ----------

        /// <summary>挖掘速度倍率（等比缩短攻击间隔）。Lv0=1.0。</summary>
        public static float DigSpeedMultiplier(int level)
        {
            float[] v = { 1.0f, 1.25f, 1.55f, 1.9f };
            return v[Mathf.Clamp(level, 0, MaxLevel)];
        }

        /// <summary>燃料上限（绝对量）。Lv0=120（DEV-005 出厂）。</summary>
        public static float MaxFuel(int level)
        {
            float[] v = { 120f, 150f, 185f, 225f };
            return v[Mathf.Clamp(level, 0, MaxLevel)];
        }

        /// <summary>载重上限（绝对量）。Lv0=24（DEV-005 出厂）。</summary>
        public static float MaxCarryWeight(int level)
        {
            float[] v = { 24f, 30f, 37f, 45f };
            return v[Mathf.Clamp(level, 0, MaxLevel)];
        }

        /// <summary>背包格行数（绝对量）。列固定 6。Lv0=2 行（12 格）。</summary>
        public static int CargoRows(int level)
        {
            int[] v = { 2, 3, 4, 5 };
            return v[Mathf.Clamp(level, 0, MaxLevel)];
        }

        /// <summary>移动速度（绝对量）。Lv0=4（DEV-005 出厂）。</summary>
        public static float MoveSpeed(int level)
        {
            float[] v = { 4f, 4.6f, 5.3f, 6.1f };
            return v[Mathf.Clamp(level, 0, MaxLevel)];
        }

        // ---------- 模块：价格 / 效果 ----------

        public static int ModuleCost(EquipmentModule m)
        {
            switch (m)
            {
                case EquipmentModule.EfficientMotor:      return 130;
                case EquipmentModule.ReinforcedCargoRack: return 150;
                case EquipmentModule.DrillCooling:        return 140;
                case EquipmentModule.SurveySensor:        return 90;
                case EquipmentModule.RuinAccessKey:       return 120;
                default: return 9999;
            }
        }

        public static string ModuleDisplayName(EquipmentModule m)
        {
            switch (m)
            {
                case EquipmentModule.EfficientMotor:      return "节能电机";
                case EquipmentModule.ReinforcedCargoRack: return "强化货架";
                case EquipmentModule.DrillCooling:        return "钻头冷却";
                case EquipmentModule.SurveySensor:        return "勘探传感器";
                case EquipmentModule.RuinAccessKey:       return "遗迹密钥";
                default: return m.ToString();
            }
        }

        /// <summary>节能电机：移动/悬停耗油倍率（未装 = 1.0）。</summary>
        public const float EfficientMotorFuelMult = 0.85f;

        /// <summary>强化货架：载重 +6（不加格行 → 卸下安全）。</summary>
        public const float CargoRackCarryBonus = 6f;

        /// <summary>钻头冷却：挖掘效率倍率（未装 = 1.0）。</summary>
        public const float DrillCoolingDigMult = 1.22f;

        /// <summary>勘探传感器：Scanner 半径 +2。</summary>
        public const int SurveySensorRadiusBonus = 2;

        // ---------- 显示 ----------

        public static string LineDisplayName(EquipmentLine line)
        {
            switch (line)
            {
                case EquipmentLine.Drill:      return "钻头 Drill";
                case EquipmentLine.FuelTank:   return "燃料箱 FuelTank";
                case EquipmentLine.CargoHold:  return "货舱 CargoHold";
                case EquipmentLine.Mobility:   return "机动 Mobility";
                default: return line.ToString();
            }
        }

        public static string LineEffectText(EquipmentLine line, int level)
        {
            switch (line)
            {
                case EquipmentLine.Drill:      return $"挖掘效率 ×{DigSpeedMultiplier(level):0.00}（单格不变）";
                case EquipmentLine.FuelTank:   return $"燃料上限 {MaxFuel(level):F0}";
                case EquipmentLine.CargoHold:  return $"载重 {MaxCarryWeight(level):F0} · {CargoRows(level)} 行格";
                case EquipmentLine.Mobility:   return $"移动速度 {MoveSpeed(level):F1}";
                default: return "";
            }
        }

        // ---------- 升级槽位解锁 ----------

        /// <summary>任意一条线达到该等级 → 解锁第 2 个模块槽。</summary>
        public const int SecondSlotUnlockLineLevel = 2;

        /// <summary>按当前四线等级计算可用模块槽数（1 或 2）。</summary>
        public static int SlotCount(int drill, int fuel, int cargo, int mobility)
        {
            int maxLv = Mathf.Max(drill, Mathf.Max(fuel, Mathf.Max(cargo, mobility)));
            return maxLv >= SecondSlotUnlockLineLevel ? 2 : 1;
        }
    }
}
