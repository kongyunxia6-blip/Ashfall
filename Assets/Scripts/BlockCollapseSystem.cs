using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-004：承重岩 / 局部坍塌系统 V1。
    ///
    /// 职责（环境系统，不碰玩家单格挖掘核心）：
    ///  - 监听 DigGrid.OnTileDug —— 只有被移除的 Block 是 SupportRock（承重岩）才启动支撑检查；
    ///  - 检查承重岩正上方同列的连续 LooseRock（松散岩）段，最多 maxCollapseHeight 格；
    ///    遇空格 / 普通稳定块 / Bedrock / 新支撑 / 越界即停止，不做全图 flood fill；
    ///  - LooseRock 进入可见 Unstable 预警（tilemap 颜色闪烁，时长可配），预警由本系统维护，
    ///    不伪造成再次 HitBlock；
    ///  - 预警结束后整段下移 1 格落格（写回 DigGrid：清空原格 → 从 support 位置逐格回填），
    ///    落点调用 DigGrid.NotifyRockFell 复用玩家砸伤通道（玩家侧零改动）；
    ///  - 防重复：同一 SupportRock 只在真正 dug 时检查一次；已在 Unstable/落格中的 LooseRock
    ///    不重复入队；执行前逐格校验仍为 LooseRock（防玩家抢先挖掉导致的竞态）。
    ///
    /// 设计规则（对齐 Issue #8）：
    ///  - 耐久 / Breaking / 移除仍以 DigGrid 为唯一真相源，本系统只做「移除后重排」，不复制耐久；
    ///  - 不依赖每格永久 GameObject；预警闪烁走 tilemap 颜色，零对象；
    ///  - 坍塌范围有限、确定性、可配置，不做整片雪崩；
    ///  - MiningFeelController / DrillVehicle 不知道承重岩怎么塌。
    ///
    /// 挂载：任意物体（推荐与 DigGrid 同物体或 GameManager）。拖入 grid 引用。
    /// </summary>
    public class BlockCollapseSystem : MonoBehaviour
    {
        [Header("引用（默认取同物体 DigGrid）")]
        public DigGrid grid;

        [Header("坍塌参数（集中配置）")]
        [Tooltip("单次支撑检查向上扫描的最大格数（Issue 区间 4~6）。超出部分视为未受直接影响，不处理")]
        [Range(1, 12)] public int maxCollapseHeight = 5;

        [Tooltip("Unstable 预警时长（秒）：LooseRock 进入闪烁到真正落格的窗口（Issue 区间 0.25~0.6）")]
        [Range(0.1f, 1f)] public float warningDuration = 0.4f;

        [Tooltip("预警闪烁颜色（叠加在方块原色上做呼吸闪烁）")]
        public Color warningFlashColor = new Color(1f, 0.45f, 0.1f);

        [Tooltip("预警闪烁频率（次/秒）")]
        [Range(1f, 12f)] public float flashRate = 6f;

        [Tooltip("落格是否触发 DigGrid.OnRockFell（复用 DrillVehicle 的落石砸伤通道）。0 关闭")]
        public bool damageOnLanding = true;

        // ---------- 事件（探针 / 表现 / 未来音效） ----------

        /// <summary>一次支撑检查启动（cell = 被移除的承重岩位置）。</summary>
        public event Action<Vector2Int> OnSupportCheck;

        /// <summary>某格 LooseRock 进入 Unstable 预警（cell）。</summary>
        public event Action<Vector2Int> OnRockUnstable;

        /// <summary>某格 LooseRock 完成落格（cell = 原格；to = 落点）。</summary>
        public event Action<Vector2Int, Vector2Int> OnRockLanded;

        /// <summary>某格 LooseRock 因坍塌被从原格移除（cell；落格链最顶端的空格位）。</summary>
        public event Action<Vector2Int> OnRockRemovedByCollapse;

        // ---------- 内部状态 ----------

        class ActiveChain
        {
            public Vector2Int supportCell;      // 被挖掉的承重岩格（已 dug 空）
            public readonly List<Vector2Int> cells = new List<Vector2Int>(); // 自下而上（最贴近 support 在前）
            public float unstableUntil;          // 预警截止
            public bool done;
        }

        readonly List<ActiveChain> chains = new List<ActiveChain>();
        readonly HashSet<Vector2Int> unstableCells = new HashSet<Vector2Int>();

        // ---------- 生命周期 ----------

        void OnEnable()
        {
            if (grid == null) grid = GetComponent<DigGrid>();
            if (grid == null) grid = FindFirstObjectByType<DigGrid>();
            if (grid == null)
            {
                Debug.LogWarning("[DEV-004] BlockCollapseSystem: 未找到 DigGrid，无法监听支撑移除");
                return;
            }
            grid.OnTileDug += HandleTileDug;
        }

        void OnDisable()
        {
            if (grid != null) grid.OnTileDug -= HandleTileDug;
        }

        // ---------- 触发：只有 SupportRock 真正移除才检查 ----------

        void HandleTileDug(Vector2Int cell, TileDefinition def)
        {
            if (def == null || def.blockType != BlockType.SupportRock) return;

            // 只检查真正移除的承重岩；dug 只发生一次 → 天然只触发一次
            BeginSupportCheck(cell);
        }

        /// <summary>从承重岩位置向上收集连续 LooseRock 段并进入预警。</summary>
        void BeginSupportCheck(Vector2Int support)
        {
            if (grid == null) return;

            var chain = new ActiveChain { supportCell = support };
            for (int dy = 1; dy <= maxCollapseHeight; dy++)
            {
                int y = support.y - dy;   // 网格 y 向下为正，上方 = y 减小
                if (!grid.InBounds(support.x, y)) break;

                var t = grid.GetTile(support.x, y);
                // 停止条件：空格 / 非 LooseRock 的稳定块（普通岩、基岩、另一个承重岩…）
                if (t == null || !t.isSolid || t.blockType != BlockType.LooseRock) break;
                // 防重复：已在预警/落格中的格不重复入队
                if (unstableCells.Contains(new Vector2Int(support.x, y))) break;

                chain.cells.Add(new Vector2Int(support.x, y));
            }

            if (chain.cells.Count == 0) { LastCollapseChainSize = 0; return; }

            LastCollapseChainSize = chain.cells.Count;
            chain.unstableUntil = Time.time + warningDuration;
            chains.Add(chain);

            OnSupportCheck?.Invoke(support);
            foreach (var c in chain.cells)
            {
                unstableCells.Add(c);
                OnRockUnstable?.Invoke(c);
            }
        }

        // ---------- Update：预警闪烁 + 到期落格 ----------

        void Update()
        {
            if (grid == null) return;

            for (int i = chains.Count - 1; i >= 0; i--)
            {
                var ch = chains[i];
                if (ch.done) { chains.RemoveAt(i); continue; }

                if (Time.time >= ch.unstableUntil)
                {
                    ExecuteCollapse(ch);
                    ch.done = true;
                    chains.RemoveAt(i);
                    continue;
                }

                // 预警闪烁（tilemap 颜色呼吸，零 GameObject）
                float k = (Mathf.Sin(Time.time * flashRate * Mathf.PI * 2f) + 1f) * 0.5f;
                Color flash = Color.Lerp(Color.white, warningFlashColor, k * 0.7f);
                for (int j = 0; j < ch.cells.Count; j++)
                {
                    var c = ch.cells[j];
                    var t = grid.GetTile(c.x, c.y);
                    if (t == null) continue;   // 已挖空：无颜色残留
                    if (!t.isSolid || t.blockType != BlockType.LooseRock)
                    {
                        // 被改写但仍存在：恢复原色（该格不参与落格，由 ExecuteCollapse 跳过）
                        grid.RefreshCell(c.x, c.y);
                        continue;
                    }
                    if (grid.tilemap != null)
                        grid.tilemap.SetColor(new Vector3Int(c.x, -c.y, 0), flash);
                }
            }
        }

        /// <summary>
        /// 执行落格：整段 LooseRock 下移 1 格。
        /// 目标 = 承重岩空位开始逐格上移回填；原段最顶端腾空。
        /// warning 期间被玩家抢先挖掉 / 改写的格不参与落格。
        /// </summary>
        void ExecuteCollapse(ActiveChain ch)
        {
            if (grid == null) return;

            var defs = new List<TileDefinition>(ch.cells.Count);
            var liveCells = new List<Vector2Int>(ch.cells.Count);

            try
            {
                // 1) 快照每格 def，并清空仍存活的格（从原格移除）
                for (int i = 0; i < ch.cells.Count; i++)
                {
                    var c = ch.cells[i];
                    var t = grid.GetTile(c.x, c.y);
                    if (t == null || !t.isSolid || t.blockType != BlockType.LooseRock)
                    {
                        // 竞态：已被玩家抢先挖掉 / 改写成其它内容 → 不参与落格。
                        // 若该格仍存在（被替换成其它实心块），刷新单格视觉恢复原色，
                        // 防预警期间叠加的橙色在 tilemap 上残留。
                        if (t != null && t.isSolid) grid.RefreshCell(c.x, c.y);
                        continue;
                    }

                    defs.Add(t);
                    liveCells.Add(c);
                }

                if (liveCells.Count == 0) return;   // 整段都被抢先挖光：仅清状态，无落格

                // 清空全部 live 格（不触发 dug/掉落 —— 环境移除不产出矿物）
                var empty = grid.database != null ? grid.database.emptyTile : null;
                for (int i = 0; i < liveCells.Count; i++)
                {
                    var c = liveCells[i];
                    if (empty != null) grid.SetTile(c.x, c.y, empty);
                    else grid.SetTile(c.x, c.y, null);
                    grid.RefreshCell(c.x, c.y);
                    OnRockRemovedByCollapse?.Invoke(c);
                }

                // 2) 从承重岩空位起逐格回填（整段下移 1 格：live[0] → support 位，live[i] → live[i-1] 原位）
                Vector2Int target = ch.supportCell;
                for (int i = 0; i < liveCells.Count; i++)
                {
                    var c = liveCells[i];
                    grid.SetTile(target.x, target.y, defs[i]);
                    grid.RefreshCell(target.x, target.y);
                    OnRockLanded?.Invoke(c, target);
                    if (damageOnLanding) grid.NotifyRockFell(target);
                    target = c;   // 下一个落在当前格的原位
                }
            }
            finally
            {
                // 统一收尾：无论链中哪些格存活 / 被抢先挖掉，整条链全部退出 Unstable。
                // （不能只清理 liveCells —— 被挖掉/改写而跳过的格坐标会残留在
                //   unstableCells，未来该坐标重放 LooseRock 时会被 BeginSupportCheck 的
                //   Contains 检查误断链，形成不可再次坍塌的隐蔽状态。）
                foreach (var c in ch.cells) unstableCells.Remove(c);
            }
        }

        // ---------- 测试辅助 ----------

        /// <summary>最近一次支撑检查收集到的 LooseRock 链长（探针/测试读取，验收 ≤ maxCollapseHeight）。</summary>
        public int LastCollapseChainSize { get; private set; }

        /// <summary>当前处于 Unstable/未完成落格中的格数（测试验证防重复用）。</summary>
        public int PendingChainCount
        {
            get
            {
                int n = 0;
                foreach (var ch in chains) if (!ch.done) n += ch.cells.Count;
                return n;
            }
        }

        /// <summary>对指定格手动触发一次支撑检查（测试用；正常路径由 OnTileDug 驱动）。</summary>
        public void DebugTriggerCheck(Vector2Int supportCell) => BeginSupportCheck(supportCell);

        /// <summary>立即结算所有已到期链（测试用；等价等一帧）。</summary>
        public void DebugFlushExpired()
        {
            for (int i = chains.Count - 1; i >= 0; i--)
            {
                var ch = chains[i];
                if (ch.done) { chains.RemoveAt(i); continue; }
                if (Time.time >= ch.unstableUntil)
                {
                    ExecuteCollapse(ch);
                    ch.done = true;
                    chains.RemoveAt(i);
                }
            }
        }

        /// <summary>当前仍登记在 Unstable 状态的格数（探针/测试读取；链结算后应为 0，验证无残留）。</summary>
        public int UnstableCellCount => unstableCells.Count;

        /// <summary>立即强制结算指定承重岩位置的链（测试用；等价把该链 unstableUntil 拨到过去，不依赖真实帧）。</summary>
        public void DebugForceCollapse(Vector2Int supportCell)
        {
            for (int i = chains.Count - 1; i >= 0; i--)
            {
                var ch = chains[i];
                if (ch.done) { chains.RemoveAt(i); continue; }
                if (ch.supportCell != supportCell) continue;
                ExecuteCollapse(ch);
                ch.done = true;
                chains.RemoveAt(i);
                return;
            }
        }
    }
}
