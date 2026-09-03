using UnityEngine;

namespace Ashfall
{
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

        [Header("DEV-001 预留")]
        [Tooltip("铁矿专属掉落事件预留：true 时崩碎后会走铁矿特殊掉落路径（IronOreDropHook 接）。" +
                 "普通岩 / 其他矿石保持 false，走通用 OnTileDug 流程")]
        public bool dropsIronOre = false;
    }
}
