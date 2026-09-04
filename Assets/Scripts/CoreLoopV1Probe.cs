using System.Text;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-005：CoreLoopV1Test 场景的验收探针。
    ///
    /// 订阅 DigGrid.OnTileDug 统计挖除计数（按矿种），其余断言（Cash/Fuel/背包/出售/升级）
    /// 由测试脚本直接读 GameManager / DrillVehicle / UpgradeSystem / MiningFeelController 的
    /// 公开属性 —— 探针只做「挖到矿」的确定性计数 + 提供一行快照。
    ///
    /// 纯测试脚手架，不参与玩法逻辑。
    /// </summary>
    public class CoreLoopV1Probe : MonoBehaviour
    {
        public static CoreLoopV1Probe Instance { get; private set; }

        [Header("引用（默认自动查找）")]
        public DigGrid grid;

        [Header("矿种资产（Builder 注入，供按矿种判定）")]
        public TileDefinition iron;
        public TileDefinition copper;
        public TileDefinition tin;

        [Header("挖掘计数")]
        public int dugTotal;       // 挖除格数（含土/矿）
        public int dugIron;        // 挖到铁矿数
        public int dugCopper;      // 挖到铜矿数
        public int dugTin;         // 挖到锡矿数

        void OnEnable()
        {
            Instance = this;
            if (grid == null) grid = FindFirstObjectByType<DigGrid>();
            if (grid != null) grid.OnTileDug += HandleTileDug;
        }

        void OnDisable()
        {
            if (grid != null) grid.OnTileDug -= HandleTileDug;
            if (Instance == this) Instance = null;
        }

        void HandleTileDug(Vector2Int cell, TileDefinition def)
        {
            if (def == null) return;
            dugTotal++;
            if (def == iron) dugIron++;
            else if (def == copper) dugCopper++;
            else if (def == tin) dugTin++;
        }

        /// <summary>清零全部计数（测试分段用）。</summary>
        public void ResetCounts()
        {
            dugTotal = dugIron = dugCopper = dugTin = 0;
        }

        /// <summary>一行文本快照（MCP execute_code 直接 return 取回）。</summary>
        public string Snapshot()
        {
            var sb = new StringBuilder(128);
            sb.Append("dugTotal=").Append(dugTotal)
              .Append(" iron=").Append(dugIron)
              .Append(" copper=").Append(dugCopper)
              .Append(" tin=").Append(dugTin);
            return sb.ToString();
        }
    }
}
