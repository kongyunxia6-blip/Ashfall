using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-003：MiningFeelV1Test 场景的验收探针。
    ///
    /// 挂在场景任意物体上（Builder 放 GameManager），订阅 DigGrid 三事件 +
    /// MiningFeelController.OnMiningHit，记录计数、每帧命中数、命中时间戳，
    /// 供 MCP Play 实测脚本读取验证：
    ///  - 一次攻击 tick 是否只命中 1 格（maxHitsInOneFrame 必须 == 1）；
    ///  - 攻击间隔门控（相邻命中时间差 ≥ EffectiveInterval）；
    ///  - Break 后是否追加了 breakExtraPause；
    ///  - 铁矿 4 耐久的事件计数（hit=3 / break=1 / dug=1）。
    ///
    /// 纯测试脚手架，不参与玩法逻辑（与 BlockV1TestLayout 同类）。
    /// </summary>
    public class MiningFeelV1Probe : MonoBehaviour
    {
        public static MiningFeelV1Probe Instance { get; private set; }

        [Header("引用（默认自动查找）")]
        public DigGrid grid;
        public MiningFeelController feel;

        [Header("事件计数")]
        public int gridHitCount;       // DigGrid.OnBlockHit（未崩碎的命中）
        public int gridBreakCount;     // DigGrid.OnBlockBreakStart
        public int gridDugCount;       // DigGrid.OnTileDug（真正移除）
        public int miningHitCount;     // 控制器成功命中次数
        public int miningBreakHitCount;// 控制器命中且崩碎次数

        [Header("节奏测量")]
        public int maxHitsInOneFrame;  // 任何单帧内控制器命中的最大次数（验收：必须 == 1）
        public float lastHitTime = -1f;
        public float prevHitTime = -1f;
        public float minHitGap = float.MaxValue;   // 相邻命中最小时间差（验收：≥ 攻击间隔）
        public float lastBreakHitTime = -1f;       // 最近一次「崩碎击」的时间
        public float minGapAfterBreak = float.MaxValue; // 崩碎击→下一击的最小间隔（验收：≥ 间隔+breakExtraPause）

        /// <summary>最近 16 次命中的时间戳（秒），用于测试脚本做完整间隔序列分析。</summary>
        public readonly List<float> hitTimes = new List<float>(16);

        int hitsThisFrame;
        int lastHitFrame = -1;

        void OnEnable()
        {
            Instance = this;
            if (grid == null) grid = FindFirstObjectByType<DigGrid>();
            if (feel == null) feel = FindFirstObjectByType<MiningFeelController>();

            if (grid != null)
            {
                grid.OnBlockHit += HandleGridHit;
                grid.OnBlockBreakStart += HandleGridBreak;
                grid.OnTileDug += HandleGridDug;
            }
            if (feel != null) feel.OnMiningHit += HandleMiningHit;
        }

        void OnDisable()
        {
            if (grid != null)
            {
                grid.OnBlockHit -= HandleGridHit;
                grid.OnBlockBreakStart -= HandleGridBreak;
                grid.OnTileDug -= HandleGridDug;
            }
            if (feel != null) feel.OnMiningHit -= HandleMiningHit;
            if (Instance == this) Instance = null;
        }

        void HandleGridHit(Vector2Int cell, TileDefinition def) => gridHitCount++;
        void HandleGridBreak(Vector2Int cell, TileDefinition def) => gridBreakCount++;
        void HandleGridDug(Vector2Int cell, TileDefinition def) => gridDugCount++;

        void HandleMiningHit(Vector2Int cell, bool broke)
        {
            miningHitCount++;
            if (broke) miningBreakHitCount++;

            // 每帧命中数（验收：一次攻击 tick 只命中 1 格）
            if (Time.frameCount == lastHitFrame) hitsThisFrame++;
            else { lastHitFrame = Time.frameCount; hitsThisFrame = 1; }
            if (hitsThisFrame > maxHitsInOneFrame) maxHitsInOneFrame = hitsThisFrame;

            // 间隔测量
            float t = Time.time;
            if (lastHitTime >= 0f)
            {
                float gap = t - lastHitTime;
                if (gap < minHitGap) minHitGap = gap;
                if (Mathf.Approximately(lastHitTime, lastBreakHitTime) && gap < minGapAfterBreak)
                    minGapAfterBreak = gap;
            }
            prevHitTime = lastHitTime;
            lastHitTime = t;
            if (broke) lastBreakHitTime = t;

            hitTimes.Add(t);
            if (hitTimes.Count > 16) hitTimes.RemoveAt(0);
        }

        /// <summary>清零全部计数（测试分段用）。</summary>
        public void ResetCounts()
        {
            gridHitCount = gridBreakCount = gridDugCount = 0;
            miningHitCount = miningBreakHitCount = 0;
            maxHitsInOneFrame = 0;
            lastHitTime = prevHitTime = lastBreakHitTime = -1f;
            minHitGap = minGapAfterBreak = float.MaxValue;
            hitTimes.Clear();
            hitsThisFrame = 0;
            lastHitFrame = -1;
        }

        /// <summary>一行文本快照（MCP execute_code 直接 return 取回）。</summary>
        public string Snapshot()
        {
            var sb = new StringBuilder(256);
            sb.Append("gridHit=").Append(gridHitCount)
              .Append(" gridBreak=").Append(gridBreakCount)
              .Append(" gridDug=").Append(gridDugCount)
              .Append(" miningHit=").Append(miningHitCount)
              .Append(" miningBreak=").Append(miningBreakHitCount)
              .Append(" maxHitsInOneFrame=").Append(maxHitsInOneFrame)
              .Append(" minHitGap=").Append(minHitGap == float.MaxValue ? "n/a" : minHitGap.ToString("F3"))
              .Append(" minGapAfterBreak=").Append(minGapAfterBreak == float.MaxValue ? "n/a" : minGapAfterBreak.ToString("F3"));
            return sb.ToString();
        }
    }
}
