namespace Ashfall
{
    /// <summary>
    /// 全局 Block 视觉规格（2026-09-03 评审锁定的项目规范）：
    ///  1 Block = 世界单位 1×1；每块砖美术 = 128×128 px；Block Sprite PPU = 128。
    /// 
    /// 集中声明原则：凡涉及 Block 贴图像素尺寸 / PPU 的代码一律引用本类常量，
    /// 禁止把 32/128 作为尺寸语义散落在各脚本里。日后更换像素规格只改这里。
    /// </summary>
    public static class BlockSpec
    {
        /// <summary>单格贴图像素边长（px）。</summary>
        public const int PixelSize = 128;

        /// <summary>Sprite pixels-per-unit：PixelSize / PPU = 1 → 世界尺寸恒为 1×1 unit = 1 格。</summary>
        public const int PPU = 128;
    }
}
