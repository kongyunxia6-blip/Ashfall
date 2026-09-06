using System;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-012：特殊 Block 类别（Issue §7 SpecialBlockDefinition.category）。
    /// 仅用于「描述」特殊块，不放采矿行为分支。行为通过 reactionHook 扩展点接入。
    /// </summary>
    public enum SpecialBlockCategory
    {
        Structural,     // 结构类（SupportRock 承重岩）
        Environmental,  // 环境类（HotRock 高温岩）
        Resource,       // 资源/遗物类（预留）
        Seal,           // 封印/门（预留 RuinSeal）
    }

    /// <summary>
    /// 特殊块可选的反应钩子（world reaction / callback）。
    /// 用于描述「挖到此块时环境/系统可做什么」，不与玩家单格挖掘核心耦合。
    /// V1 实现枚举 + 由对应运行时系统消费；未来可换成委托/事件表，不改此处扩展性。
    /// </summary>
    [Flags]
    public enum SpecialReactionHook
    {
        None = 0,
        /// <summary>承重坍塌：挖穿后由 BlockCollapseSystem（DEV-004，非重写）处理上方松散岩。</summary>
        SupportCollapse = 1 << 0,
        /// <summary>高温过热：挖穿/触碰时按 Cooling 能力产生过热锁定反馈（HotRockSystem）。</summary>
        OverheatLock = 1 << 1,
        /// <summary>DEV-013 封印破拆：由 RuinSealSystem 按 RuinAccess 能力门控入口封印（无权限拒挖反馈）。</summary>
        SealBreak = 1 << 2,
        /// <summary>DEV-013 中继交互：点击命中 AncientRelayCore 触发一次性调查/奖励（AncientRelayCoreSystem）。</summary>
        RelayInteract = 1 << 3,
        // 预留：ResonancePulse / ConductArc —— 本 DEV 不实现。
    }

    /// <summary>
    /// DEV-012：特殊 Block 元数据定义（Issue §7）。
    /// 只登记「是什么 + 要求什么 + 触发什么反应」，不复制第二套 HP/Breaking/Remove。
    /// 耐久 / Breaking / 移除始终由 DigGrid 唯一承载。
    /// </summary>
    [Serializable]
    public class SpecialBlockDefinition
    {
        /// <summary>stable id（如 SupportRock / HotRock）。</summary>
        public string specialBlockId = "";

        /// <summary>显示名（如 承重岩 / 高温岩）。</summary>
        public string displayName = "";

        /// <summary>类别。</summary>
        public SpecialBlockCategory category = SpecialBlockCategory.Environmental;

        /// <summary>映射到的实际 Block 定义（TileDefinition.asset；可为 null=仅登记）。</summary>
        public TileDefinition baseTile;

        /// <summary>要求的挖掘能力（无则 Normal 硬块；HotRock 要求 Cooling 才「稳定」处理）。</summary>
        public MiningCapability requiredCapability = MiningCapability.None;

        /// <summary>挖掘这类块触发的能力 / 行为钩子。</summary>
        public MiningCapability capabilityForStable = MiningCapability.None;

        /// <summary>世界反应钩子（见 SpecialReactionHook）。</summary>
        public SpecialReactionHook reactionHook = SpecialReactionHook.None;

        /// <summary>一句话描述（日志/图鉴/验收）。</summary>
        public string shortDescription = "";

        /// <summary>当前 `reaction 由哪个系统消费`（描述性，供审计/PR；不驱动调度）。</summary>
        public string runtimeOwner = "";
    }

    /// <summary>
    /// DEV-012/DEV-013：特殊块元数据唯一集中登记处。
    /// 首批：SupportRock（复用 BlockCollapseSystem，禁止重写）+ HotRock（新实现）。
    /// DEV-013 追加：RuinSeal（入口封印，RuinSealSystem）+ AncientRelayCore（中继交互点）。
    /// 预留：ResonanceCrystal / ConductiveOre（仅登记占位 stable id，不实现）。
    /// </summary>
    public static class SpecialBlockCatalog
    {
        public const string SupportRock = "SupportRock";
        public const string HotRock = "HotRock";

        // ---- DEV-013：遗迹系统已实现 ----
        public const string AncientRelayCore = "AncientRelayCore";

        // ---- 预留（不实现，仅保证架构可表达） ----
        public const string ResonanceCrystal = "ResonanceCrystal";
        public const string ConductiveOre = "ConductiveOre";
        public const string RuinSeal = "RuinSeal";

        public static readonly SpecialBlockDefinition Support =
            new SpecialBlockDefinition
            {
                specialBlockId = SupportRock,
                displayName = "承重岩",
                category = SpecialBlockCategory.Structural,
                reactionHook = SpecialReactionHook.SupportCollapse,
                shortDescription = "被真正移除后触发上方松散岩坍塌检查（DEV-004 BlockCollapseSystem，非重写）。",
                runtimeOwner = "BlockCollapseSystem",
            };

        public static readonly SpecialBlockDefinition Hot =
            new SpecialBlockDefinition
            {
                specialBlockId = HotRock,
                displayName = "高温岩",
                category = SpecialBlockCategory.Environmental,
                capabilityForStable = MiningCapability.Cooling,
                reactionHook = SpecialReactionHook.OverheatLock,
                shortDescription = "无冷却挖掘受明显惩罚/短暂过热锁定；有冷却可稳定处理。单格不变。",
                runtimeOwner = "HotRockSystem",
            };

        /// <summary>DEV-013：遗迹入口封印。无 RuinAccess → 明确拒挖反馈；有 → 单格打开入口。非 invisible wall。</summary>
        public static readonly SpecialBlockDefinition Seal =
            new SpecialBlockDefinition
            {
                specialBlockId = RuinSeal,
                displayName = "遗迹封印",
                category = SpecialBlockCategory.Seal,
                requiredCapability = MiningCapability.RuinAccess,
                capabilityForStable = MiningCapability.RuinAccess,
                reactionHook = SpecialReactionHook.SealBreak,
                shortDescription = "遗迹入口的能力门：无 RuinAccess 拒挖并给明确反馈；有 RuinAccess 后经单格挖掘管线打开（RuinSealSystem）。",
                runtimeOwner = "RuinSealSystem",
            };

        /// <summary>DEV-013：古代中继核心。房间内交互点：点击命中触发一次性调查与奖励（不崩碎消失）。</summary>
        public static readonly SpecialBlockDefinition RelayCore =
            new SpecialBlockDefinition
            {
                specialBlockId = AncientRelayCore,
                displayName = "古代中继核心",
                category = SpecialBlockCategory.Resource,
                reactionHook = SpecialReactionHook.RelayInteract,
                shortDescription = "遗迹核心：首次交互给 AncientDataFragment/AncientAlloy 并标记 investigated；二次仅提示。",
                runtimeOwner = "AncientRelayCoreSystem",
            };

        public static readonly SpecialBlockDefinition[] All =
            { Support, Hot, Seal, RelayCore };

        public static SpecialBlockDefinition Find(string id)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i].specialBlockId == id) return All[i];
            return null;
        }

        /// <summary>
        /// 按实际 Block 的 blockType 分类到 SpecialBlockDefinition（运行时只读映射）。
        /// 这是唯一一处「blockType → 特殊块元数据」的映射；DigGrid / HotRockSystem /
        /// 验收脚本都经这里，不散落 if(blockType==x) 分支到各系统。Normal / 未登记类型返回 null。
        /// </summary>
        public static SpecialBlockDefinition Classify(TileDefinition def)
        {
            if (def == null) return null;
            switch (def.blockType)
            {
                case BlockType.SupportRock: return Support;
                case BlockType.HotRock: return Hot;
                case BlockType.RuinSeal: return Seal;
                case BlockType.AncientRelayCore: return RelayCore;
                default: return null;
            }
        }
    }
}
