using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-012：采矿能力解析薄层（Issue §8 MiningCapabilityResolver）。
    ///
    /// 职责：把装备 / 模块状态（EquipmentProgression）转成采矿能力查询：
    ///  - GetHardnessCapability()  → 由钻头等级映射 HardnessTier；
    ///  - HasCapability(Cooling)   → 由「已装备 DrillCooling 模块」解析（V1 的 Cooling provider）。
    ///
    /// 设计约束：
    ///  - HotRock / 任何特殊块只查询本层，禁止直接写 `if (module == DrillCooling)`；
    ///  - 未来高级钻头 / 遗迹科技 / 新模块经本层扩展，不回头改 HotRock；
    ///  - 读取 EquipmentProgression 快照，无每帧累乘 / 无 multiplier drift；
    ///  - 特殊块不在此缓存第二份装备状态（装备状态唯一来源仍是 EquipmentProgression）。
    /// </summary>
    public static class MiningCapabilityResolver
    {
        /// <summary>
        /// 取当前硬度能力（钻头等级 → HardnessTier）。
        /// 优先 DEV-010 权威（GameManager.Equipment.drillLevel），否则回落到 UpgradeSystem.DrillLevel。
        /// V1：drill Lv 0..3 → Tier 1..3；Lv 不足 6 级，≥4 的 Tier 视为长期规格（能力由升级扩展）。
        /// </summary>
        public static HardnessTier GetHardnessCapability()
        {
            int level = GetAuthorityDrillLevel();
            // 映射：Lv0→Tier1，Lv1→Tier2，Lv2→Tier3，Lv3→Tier4（V1 钻头最大 Lv3 覆盖到 GraniteBasalt 门槛）。
            int tier = Mathf.Clamp(level + 1, 1, 6);
            return (HardnessTier)tier;
        }

        /// <summary>统一能力查询。V1 支持 Cooling；其余恒 false（预留）。</summary>
        public static bool HasCapability(MiningCapability capability)
        {
            if ((capability & MiningCapability.Cooling) != 0)
                return HasCooling();
            // Resonance / Conductive / RuinAccess：V1 无 provider，恒 false（预留扩展）。
            return false;
        }

        /// <summary>冷却能力：是否已装备 DrillCooling（V1 唯一 provider）。不经本层，禁止在 HotRock 直查模块。</summary>
        public static bool HasCooling()
        {
            var gm = GameManager.Instance;
            if (gm == null || gm.Equipment == null) return false;
            return gm.Equipment.IsEquipped(EquipmentModule.DrillCooling);
        }

        /// <summary>
        /// 返回当前解析用钻头等级（供日志/验收）。优先 Equipment，回落到 UpgradeSystem。
        /// </summary>
        public static int GetAuthorityDrillLevel()
        {
            var gm = GameManager.Instance;
            if (gm == null) return 1;
            if (gm.Equipment != null) return Mathf.Clamp(gm.Equipment.drillLevel, 0, 6);
            if (gm.Upgrades != null) return Mathf.Clamp(gm.Upgrades.DrillLevel, 0, 6);
            return 1;
        }

        /// <summary>是否有权威装备系统（决定用什么数值源）。</summary>
        public static bool HasEquipmentAuthority()
        {
            var gm = GameManager.Instance;
            return gm != null && gm.Equipment != null;
        }
    }
}
