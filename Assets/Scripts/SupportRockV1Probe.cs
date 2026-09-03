using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-004：SupportRockV1Test 场景的验收探针。
    ///
    /// 挂在场景任意物体上（Builder 放 GameManager），订阅 DigGrid.OnTileDug +
    /// BlockCollapseSystem 四事件，记录计数供 MCP Play 实测读取验证：
    ///  - dugSupport / dugNormal：真正 dug 的承重岩 / 普通岩次数（验收：只有 SupportRock 触发检查）；
    ///  - supportCheck：支撑检查启动次数（验收：= dugSupport，普通岩 dug 不启动）；
    ///  - unstableCells：进入 Unstable 预警的去重格数；
    ///  - landedCells：实际落格次数（验收：A 组 4、B 组 2）；
    ///  - removedByCollapse：坍塌从原格移除的格数；
    ///  - minUnstableDuration：预警实测时长下限（验收：≥ warningDuration 附近，不瞬间消失）；
    ///  - doubleUnstable：同一格重复进入 Unstable 的次数（验收：=0 防重复触发）；
    ///  - chainSizeMax：单次检查最大链长（验收：≤ maxCollapseHeight）；
    ///  - landedOnSupport：落点 == 承重岩空位的次数（验收：落格真正填回支撑位）。
    ///
    /// 纯测试脚手架，不参与玩法逻辑。
    /// </summary>
    public class SupportRockV1Probe : MonoBehaviour
    {
        public static SupportRockV1Probe Instance { get; private set; }

        [Header("引用（默认自动查找）")]
        public DigGrid grid;
        public BlockCollapseSystem collapse;

        [Header("事件计数")]
        public int dugSupport;          // OnTileDug 且 def 为 SupportRock
        public int dugNormal;           // OnTileDug 且 def 为 Normal/其它
        public int supportCheck;        // BlockCollapseSystem.OnSupportCheck
        public int unstableCells;       // 进入 Unstable 的去重格总数（累计）
        public int landedCells;         // 落格次数（OnRockLanded）
        public int removedByCollapse;   // 坍塌移除格数（OnRockRemovedByCollapse）
        public int doubleUnstable;      // 同格重复 Unstable 次数（防重复触发；验收 = 0）
        public int landedOnSupport;     // 落点 == 承重岩原位的次数
        public int maxChainSeen;        // 单次支撑检查收集的最大 LooseRock 链长（验收 ≤ maxCollapseHeight）

        [Header("预警实测")]
        public float minUnstableDuration = float.MaxValue;   // 实测最短 Unstable→落格 时长
        public float maxUnstableDuration = 0f;

        readonly HashSet<Vector2Int> unstableSeen = new HashSet<Vector2Int>();
        readonly Dictionary<Vector2Int, float> unstableStart = new Dictionary<Vector2Int, float>();
        Vector2Int lastSupportCell;

        void OnEnable()
        {
            Instance = this;
            if (grid == null) grid = FindFirstObjectByType<DigGrid>();
            if (collapse == null) collapse = FindFirstObjectByType<BlockCollapseSystem>();

            if (grid != null) grid.OnTileDug += HandleTileDug;
            if (collapse != null)
            {
                collapse.OnSupportCheck += HandleSupportCheck;
                collapse.OnRockUnstable += HandleUnstable;
                collapse.OnRockLanded += HandleLanded;
                collapse.OnRockRemovedByCollapse += HandleRemoved;
            }
        }

        void OnDisable()
        {
            if (grid != null) grid.OnTileDug -= HandleTileDug;
            if (collapse != null)
            {
                collapse.OnSupportCheck -= HandleSupportCheck;
                collapse.OnRockUnstable -= HandleUnstable;
                collapse.OnRockLanded -= HandleLanded;
                collapse.OnRockRemovedByCollapse -= HandleRemoved;
            }
            if (Instance == this) Instance = null;
        }

        void HandleTileDug(Vector2Int cell, TileDefinition def)
        {
            if (def == null) { dugNormal++; return; }
            if (def.blockType == BlockType.SupportRock) dugSupport++;
            else dugNormal++;
        }

        void HandleSupportCheck(Vector2Int supportCell)
        {
            supportCheck++;
            lastSupportCell = supportCell;
            if (collapse != null && collapse.LastCollapseChainSize > maxChainSeen)
                maxChainSeen = collapse.LastCollapseChainSize;
        }

        void HandleUnstable(Vector2Int cell)
        {
            if (!unstableSeen.Add(cell)) doubleUnstable++;   // 同格重复 → 防重复失败
            unstableCells++;
            unstableStart[cell] = Time.time;
        }

        void HandleLanded(Vector2Int from, Vector2Int to)
        {
            landedCells++;
            if (to == lastSupportCell) landedOnSupport++;

            if (unstableStart.TryGetValue(from, out float start))
            {
                float dur = Time.time - start;
                if (dur < minUnstableDuration) minUnstableDuration = dur;
                if (dur > maxUnstableDuration) maxUnstableDuration = dur;
                unstableStart.Remove(from);
            }
        }

        void HandleRemoved(Vector2Int cell)
        {
            removedByCollapse++;
        }

        /// <summary>清零全部计数（测试分段用）。</summary>
        public void ResetCounts()
        {
            dugSupport = dugNormal = supportCheck = 0;
            unstableCells = landedCells = removedByCollapse = doubleUnstable = 0;
            landedOnSupport = 0;
            maxChainSeen = 0;
            minUnstableDuration = float.MaxValue;
            maxUnstableDuration = 0f;
            unstableSeen.Clear();
            unstableStart.Clear();
        }

        /// <summary>一行文本快照（MCP execute_code 直接 return 取回）。</summary>
        public string Snapshot()
        {
            var sb = new StringBuilder(256);
            sb.Append("dugSupport=").Append(dugSupport)
              .Append(" dugNormal=").Append(dugNormal)
              .Append(" check=").Append(supportCheck)
              .Append(" unstable=").Append(unstableCells)
              .Append(" landed=").Append(landedCells)
              .Append(" removed=").Append(removedByCollapse)
              .Append(" landedOnSupport=").Append(landedOnSupport)
              .Append(" double=").Append(doubleUnstable)
              .Append(" minUnstableDur=").Append(minUnstableDuration == float.MaxValue ? "n/a" : minUnstableDuration.ToString("F3"))
              .Append(" maxChain=").Append(maxChainSeen);
            return sb.ToString();
        }
    }
}
