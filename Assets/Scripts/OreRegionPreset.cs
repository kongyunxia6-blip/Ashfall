using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-015 Blocker2 根治：矿脉 / 经济分层的【唯一权威配置源】（ScriptableObject 资产化）。
    ///
    /// 背景：DEV-015 首轮验收指出「经济模拟器(EconomyBalanceV1Sim)内部硬编码了一套矿种权重 /
    /// vein 尺寸 / 频率，未从正式 OreVeinGenerator 权威配置读取」；且 OreVeinGenerator.bands 原本是
    /// 挂在各场景 GameObject 上的手写字段（DEV-007 测试用，深层甚至没放金/铂/钻石，与经济设计断层），
    /// 不存在可被回归 / 模拟读取的单一权威资产。
    ///
    /// 本类把「一个深度区域内出什么矿、矿脉多大、多密」固化成可资产化的权威表：
    ///  - OreVeinGenerator 可持有一个 preset（bands 为空时从 preset 读，避免手写）；
    ///  - EconomyBalanceV1Sim / 回归只从 preset 读取，不再第二份硬编码；
    ///  - preset 内容 = 经济设计意图的完整带分布（Deep 含金/铂/祖母绿/红宝石/钻石等高值矿），
    ///    使「越深单趟越值钱」在经济与生成层面自洽。
    ///
    /// 创建方式：右键 Project → Create → Ashfall → Ore Region Preset。
    /// </summary>
    [CreateAssetMenu(fileName = "OreRegionPreset", menuName = "Ashfall/Ore Region Preset", order = 3)]
    public class OreRegionPreset : ScriptableObject
    {
        [Tooltip("矿脉带（按深度从小到大排列，复用 OreDepthBand 序列化定义）。" +
                 "命中与 OreVeinGenerator.bands 同构：含 ores/weights/veinMin-MaxSize/veinFrequency")]
        public OreDepthBand[] bands;

        [Tooltip("可复现种子（默认跟随 DigGrid.seed）；仅统计/模拟参考")]
        public int referenceSeed = -1;

        /// <summary>是否含至少一个带、且每带至少 1 种矿（有效性校验）。</summary>
        public bool IsValid
        {
            get
            {
                if (bands == null || bands.Length == 0) return false;
                foreach (var b in bands)
                    if (b != null && b.ores != null && b.ores.Length > 0) return true;
                return false;
            }
        }

        /// <summary>按 bandName 取带（找不到返回 null）。</summary>
        public OreDepthBand FindBand(string bandName)
        {
            if (bands == null) return null;
            foreach (var b in bands)
                if (b != null && b.bandName == bandName) return b;
            return null;
        }
    }
}
