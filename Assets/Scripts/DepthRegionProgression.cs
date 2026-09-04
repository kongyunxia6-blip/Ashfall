using System;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-009：深度 / 区域推进（Depth &amp; Region Progression V1）。
    ///
    /// 职责（只读地形/深度，绝不修改矿格或空格）：
    ///  - 根据玩家当前网格深度判定 CurrentRegion（边界来自 DepthRegionLayout 唯一来源）；
    ///  - 维护玩家可见深度 DisplayDepth（地表行及以上显示 0m，向下 1 格 = 1m）；
    ///  - 记录本次运行最大深度 MaxDepthThisRun / 最深区域 DeepestRegionThisRun（返航不回退）；
    ///  - 记录每个 Region 是否首次进入（只触发一次，区域内上下移动不重刷）；
    ///  - 触发「首次进入区域」提示（RegionEnterAnnounce + RegionFirstEntered 事件，供 HUD/未来系统）；
    ///  - 对外只读：CurrentRegion / MaxDepthEver（= GameManager.MaxDepthReached）/ Find 查询。
    ///
    /// 不做：深度锁门、奖励、补给、地图扫描、改格子。那是 DEV-010+ 之后的事。
    ///
    /// 挂载：场景任意物体（推荐 GameManager 同体）。vehicle/grid 未赋值时自动查找。
    /// 历史最深（MaxDepthEver）沿用 GameManager.MaxDepthReached（由 DrillVehicle.TrackDepth 维护），
    /// 本类不重复维护第二套「历史最深」。
    /// </summary>
    public class DepthRegionProgression : MonoBehaviour
    {
        [Header("引用（留空自动查找）")]
        [Tooltip("玩家载具（读其网格深度）")]
        public DrillVehicle vehicle;

        [Tooltip("地形网格（换算玩家网格深度）")]
        public DigGrid grid;

        [Header("区域集合（唯一来源，默认 = DepthRegionLayout.All）")]
        [Tooltip("按 order 升序。与 Builder 构造 Ore/Space band 使用同一集合")]
        public DepthRegionDefinition[] regions = DepthRegionLayout.All;

        [Header("首次进入提示")]
        [Tooltip("首次进入新区域提示的显示时长（秒）")]
        public float announceDuration = 2.2f;

        // ---------- 运行时状态（只读） ----------

        /// <summary>当前区域。Start 前为 null。</summary>
        public DepthRegionDefinition CurrentRegion { get; private set; }

        /// <summary>当前网格深度（DigGrid.WorldToGrid(pos).y，≥0）。</summary>
        public int CurrentGridY { get; private set; }

        /// <summary>玩家可见深度：地表行及以上 = 0，向下递增（1 Block = 1m）。</summary>
        public int DisplayDepth { get; private set; }

        /// <summary>本次运行最大深度（返航不回退；0 = 尚未下潜）。</summary>
        public int MaxDepthThisRun { get; private set; }

        /// <summary>本次运行到达过的最深区域（返航不回退）。未下潜时为 Surface。</summary>
        public DepthRegionDefinition DeepestRegionThisRun { get; private set; }

        /// <summary>历史最深深度（米，玩家可见语义：地表行按 0m 折算）。委托 GameManager.MaxDepthReached 只读包装。</summary>
        public int MaxDepthEver
        {
            get
            {
                if (GameManager.Instance == null) return 0;
                return DepthRegionLayout.DisplayDepthOf(GameManager.Instance.MaxDepthReached);
            }
        }

        /// <summary>当前首次进入提示文本（无提示时为 null/空）。HUD 按 AnnounceTimeLeft 决定是否显示。</summary>
        public string RegionEnterAnnounce { get; private set; }

        /// <summary>首次进入提示剩余显示时长（秒，>0 时 HUD 应显示 RegionEnterAnnounce）。</summary>
        public float AnnounceTimeLeft { get; private set; }

        /// <summary>是否已触发过首次进入事件（避免重复 announce；含 Surface 之外的区域）。</summary>
        public bool[] FirstEntered { get; private set; }

        /// <summary>首次进入某区域事件（order ≥ 1；Surface 不触发）。</summary>
        public event Action<DepthRegionDefinition> RegionFirstEntered;

        // ---------- 生命周期 ----------

        void Awake()
        {
            if (regions == null || regions.Length == 0) regions = DepthRegionLayout.All;
            FirstEntered = new bool[DepthRegionLayout.All.Length];
            DeepestRegionThisRun = DepthRegionLayout.Surface;
            Array.Sort(regions, (a, b) => a.order.CompareTo(b.order));
        }

        void Start()
        {
            if (grid == null) grid = GetComponent<DigGrid>();
            if (vehicle == null)
            {
                var gm = GameManager.Instance;
                vehicle = gm != null ? gm.Player : null;
            }
            if (vehicle == null && grid != null)
                vehicle = FindFirstObjectByType<DrillVehicle>();

            // 玩家恒从地表（Hub/坑口）出发：Surface 视为「已到过」，避免未来地下出生时误报首次进入 Surface
            int idx = IndexOf(DepthRegionLayout.Surface.regionId);
            if (idx >= 0) FirstEntered[idx] = true;
            Tick(true);
        }

        void LateUpdate()
        {
            if (vehicle == null || grid == null) return;
            Tick(false);

            if (AnnounceTimeLeft > 0f)
            {
                AnnounceTimeLeft -= Time.deltaTime;
                if (AnnounceTimeLeft <= 0f) RegionEnterAnnounce = null;
            }
        }

        // ---------- 主推进 ----------

        void Tick(bool initial)
        {
            var p = vehicle.transform.position;
            CurrentGridY = Mathf.Max(0, grid.WorldToGrid(p).y);
            DisplayDepth = DepthRegionLayout.DisplayDepthOf(CurrentGridY);

            var prev = CurrentRegion;
            var cur = FindRegion(CurrentGridY);
            CurrentRegion = cur;

            if (DisplayDepth > MaxDepthThisRun)
                MaxDepthThisRun = DisplayDepth;

            if (cur.order > DeepestRegionThisRun.order)
                DeepestRegionThisRun = cur;

            // 首次进入：仅当横跨边界进入一个此前从未进入过的区域时触发一次
            if (!initial && cur != null && cur.order > 0 && cur != prev)
            {
                int idx = IndexOf(cur.regionId);
                if (idx >= 0 && !FirstEntered[idx])
                {
                    FirstEntered[idx] = true;
                    RegionEnterAnnounce = "进入：" + cur.displayName;
                    AnnounceTimeLeft = announceDuration;
                    try { RegionFirstEntered?.Invoke(cur); }
                    catch (Exception e) { Debug.LogWarning("[DEV-009] RegionFirstEntered 订阅者异常：" + e.Message); }
                }
            }
        }

        // ---------- 查询 ----------

        /// <summary>按网格深度查区域（clamp 安全，不抛异常）。</summary>
        public DepthRegionDefinition FindRegion(int gridY)
        {
            if (regions == null || regions.Length == 0) return DepthRegionLayout.FindByGridY(gridY);
            for (int i = 0; i < regions.Length; i++)
                if (gridY >= regions[i].minDepth && gridY <= regions[i].maxDepth)
                    return regions[i];
            return gridY < regions[0].minDepth ? regions[0] : regions[regions.Length - 1];
        }

        /// <summary>某区域是否已首次进入（order ≥ 1 才有意义）。</summary>
        public bool HasEntered(string regionId)
        {
            int idx = IndexOf(regionId);
            return idx >= 0 && FirstEntered != null && FirstEntered[idx];
        }

        int IndexOf(string regionId)
        {
            for (int i = 0; i < DepthRegionLayout.All.Length; i++)
                if (DepthRegionLayout.All[i].regionId == regionId) return i;
            return -1;
        }

        // ---------- Run 生命周期 ----------

        /// <summary>
        /// 重置「本次运行」统计（最大深度/最深区域/首次进入提示）。
        /// DEV-009 不自动接入死亡/返航（项目暂无明确 Run 生命周期），
        /// 由未来 Run 系统或 MCP 验收在需要时显式调用。
        /// 注意：不重置 MaxDepthEver（历史最深跨 Run 保留）。
        /// </summary>
        public void ResetRun()
        {
            MaxDepthThisRun = DisplayDepth > 0 ? DisplayDepth : 0;
            DeepestRegionThisRun = CurrentRegion != null && CurrentRegion.order > 0
                ? CurrentRegion : DepthRegionLayout.Surface;
            RegionEnterAnnounce = null;
            AnnounceTimeLeft = 0f;
            if (FirstEntered != null)
                for (int i = 0; i < FirstEntered.Length; i++) FirstEntered[i] = false;
            var gm = GameManager.Instance;
            if (gm != null && gm.IsAtSurface)
            {
                int si = IndexOf(DepthRegionLayout.Surface.regionId);
                if (si >= 0) FirstEntered[si] = true;
            }
        }
    }
}
