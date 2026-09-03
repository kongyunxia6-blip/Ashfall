using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-002：Block 视觉配置（把「Block 状态」和「Block 怎么画」解耦）。
    ///
    /// 一个 Block 类型可以引用一套本 profile，为它的完整/裂纹/崩碎各状态指定 Sprite。
    /// 正式美术缺失时允许留空——留空的状态会 fallback 到 DEV-001 的「纯色方块 + 裂纹调暗」，
    /// 因此不会出现空洞，也不依赖把具体矿物 Sprite 写死在代码里。
    ///
    /// 创建方式：右键 Project 面板 → Create → Ashfall → Block Visual Profile
    ///
    /// 状态映射（由 DigGrid.GetCrackStage 决定，表现层不自行维护 HP）：
    ///   stage 0 = 完整  → intact
    ///   stage 1 = 裂纹1 → crack1
    ///   stage 2 = 裂纹2 → crack2
    ///   stage 3 = 裂纹3 → crack3
    ///   崩碎（OnBlockBreakStart）→ breakFrames（短序列，允许 1 帧或多帧）
    /// </summary>
    [CreateAssetMenu(fileName = "BlockVisualProfile", menuName = "Ashfall/Block Visual Profile", order = 3)]
    public class BlockVisualProfile : ScriptableObject
    {
        [Header("状态 Sprite（可留空，空则 fallback 纯色方块）")]
        [Tooltip("完整（stage 0）")]
        public Sprite intact;

        [Tooltip("裂纹1（stage 1）")]
        public Sprite crack1;

        [Tooltip("裂纹2（stage 2）")]
        public Sprite crack2;

        [Tooltip("裂纹3（stage 3）")]
        public Sprite crack3;

        [Tooltip("崩碎帧序列（OnBlockBreakStart 后播放，允许 1 帧或多帧）。" +
                 "空则用 crack3 作为崩碎视觉；含 1 帧 = 静止崩碎图；含多帧 = 逐帧崩碎动画")]
        public Sprite[] breakFrames;

        [Header("命中反馈（DEV-002）")]
        [Tooltip("单格被命中（耐久-1 但未崩碎）时，目标格短暂闪亮的时长（秒）。0 = 关闭")]
        [Min(0f)] public float hitFlashDuration = 0.08f;

        [Tooltip("命中闪亮颜色（默认白色，会被短暂 lerp 到该色再恢复）")]
        public Color hitFlashColor = Color.white;

        /// <summary>按裂纹阶段取 Sprite。无对应 Sprite 时返回 null（由调用方 fallback）。</summary>
        public Sprite GetStageSprite(int stage)
        {
            switch (stage)
            {
                case 0: return intact;
                case 1: return crack1;
                case 2: return crack2;
                case 3: return crack3;
                default: return intact;
            }
        }

        /// <summary>是否至少配置了一个状态 Sprite（用于判断是否走 Sprite 渲染路径）。</summary>
        public bool HasAnySprite =>
            intact != null || crack1 != null || crack2 != null || crack3 != null;

        /// <summary>是否有崩碎帧序列。</summary>
        public bool HasBreakFrames => breakFrames != null && breakFrames.Length > 0;
    }
}
