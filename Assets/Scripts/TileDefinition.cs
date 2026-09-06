using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-004/DEV-012：Block 通用行为类型标记（不硬编码进矿物名）。
    /// Normal / SupportRock / LooseRock 由 DEV-004 引入；HotRock 由 DEV-012 追加（高温岩）。
    /// 本枚举只做「一格属于哪类行为」的标记，不放采矿行为分支；
    /// 具体反应由对应运行时系统（BlockCollapseSystem→SupportRock，HotRockSystem→HotRock）经
    /// SpecialBlockCatalog 元数据 + MiningCapabilityResolver 消费，DigGrid 不做 switch(specialType)。
    /// </summary>
    public enum BlockType
    {
        Normal = 0,        // 常规方块，无环境行为
        SupportRock = 1,   // 承重岩：被真正移除后触发上方 LooseRock 坍塌检查
        LooseRock = 2,     // 松散岩：正常静止；下方关键支撑被移除后 Unstable → 坍塌
        HotRock = 3,       // 高温岩（DEV-012）：无冷却挖掘受明显惩罚/短暂过热锁定；有冷却稳定处理
    }

    /// <summary>
    /// 单个方块 / 矿物的属性定义。
    /// 创建方式：右键 Project 面板 → Create → Ashfall → Tile Definition
    /// </summary>
    [CreateAssetMenu(fileName = "NewTile", menuName = "Ashfall/Tile Definition", order = 1)]
    public class TileDefinition : ScriptableObject
    {
        [Header("基础")]
        public string displayName = "New Tile";

        [Tooltip("方块颜色。原型阶段直接用纯色渲染，后续可替换为 Sprite / Tile 资产")]
        public Color color = Color.gray;

        [Header("挖掘")]
        [Tooltip("硬度 = 需要的钻头等级。钻头等级低于硬度时挖不动")]
        [Min(1)] public int hardness = 1;

        [Tooltip("挖穿本格需要的击打次数：鼠标左键每点一次 = 一击，打满次数才挖穿。泥土 1 击、硬岩 3~5 击、矿脉更厚")]
        [Min(1)] public int digHits = 1;

        [Tooltip("钻穿所需秒数（基准值，会被引擎等级加速）")]
        [Min(0.05f)] public float drillTime = 0.35f;

        [Header("收益")]
        [Tooltip("卖价。0 表示不值钱（如泥土、硬岩）")]
        [Min(0)] public int value = 0;

        [Header("货舱")]
        [Tooltip("单块重量。越值钱的矿越重 —— 总重量拖慢上升、增加油耗，是「回不回得去」的张力来源。\n" +
                 "泥土/硬岩/熔岩 value=0 不进背包，weight 无意义")]
        [Min(0f)] public float weight = 1f;

        [Tooltip("同一格最多堆叠几件")]
        [Min(1)] public int stackLimit = 16;

        [Tooltip("横向占几格。矿物 1；大件残骸 2（占地但很轻，与「重而小」的稀有矿形成取舍）。\n" +
                 "占位只做同行横向连续，不做 2D 形状")]
        [Min(1)] public int gridWidth = 1;

        [Header("物理")]
        [Tooltip("是否实心（阻挡移动）。空气 / 背景设为 false")]
        public bool isSolid = true;

        [Tooltip("是否危险（如熔岩），接触时持续扣船体")]
        public bool isHazard = false;

        [Tooltip("危险伤害（每秒）")]
        [Min(0)] public int hazardDamage = 0;

        [Header("DEV-001 掉落预留")]
        [Tooltip("通用掉落标识（drop id）。非空时，本格被挖穿后由独立的掉落系统（BlockDropHook）读取该 id 派发掉落。" +
                 "空字符串 = 走默认通用入包流程，无特殊掉落。\n" +
                 "核心 Block 定义只携带一个通用字符串，不感知具体矿种；「掉什么 / 掉几个 / 概率 / 伴生物」由掉落系统按 id 查表决定。" +
                 "示例：\"iron_ore\"（铁矿残块）、\"copper_ore\"（铜矿残块）…… 新增矿种无需改动 DigGrid / DrillVehicle / TileDefinition 的核心挖掘逻辑。")]
        public string dropId = "";

        [Header("DEV-004 特殊行为")]
        [Tooltip("Block 行为类型标记。承重岩(SupportRock)：被真正移除后，正上方同列的松散岩进入 Unstable→坍塌；" +
                 "松散岩(LooseRock)：正常静止，关键支撑被移除后由 BlockCollapseSystem 接管预警与落格。Normal=无环境行为。" +
                 "后续特殊 Block 在此枚举扩展（本 PR 不实现）。")]
        public BlockType blockType = BlockType.Normal;

        [Header("DEV-002 表现")]
        [Tooltip("视觉 profile（完整/裂纹1/2/3/崩碎帧的 Sprite）。空则 fallback 到 DEV-001 的纯色方块 + 裂纹调暗。" +
                 "正式美术就绪后，在这里拖入对应 Sprite 资产即可替换，无需改代码。")]
        public BlockVisualProfile visualProfile;
    }
}
