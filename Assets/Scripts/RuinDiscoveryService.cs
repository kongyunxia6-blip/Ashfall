using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-013：遗迹发现/调查状态的运行时记录器 + 只读异常信号查询（Issue §7/§10）。
    ///
    /// 职责（只提供信息，绝不写世界）：
    ///  - 只读扫描：只用 InBounds / GetTile，不生成/删除/修改任何 Ruin Block；
    ///  - 持有每座 RuinInstance 的「已发现 / 已调查」状态（V1 运行时生命周期，跨 Run 存活；
    ///    死亡/返航不重置 —— 防死亡无限刷首奖）；
    ///  - 远距离只给模糊 Anomalous Structure Signal；玩家接近到规则距离后标记 RuinDiscovered 并触发首发现事件；
    ///  - 不泄露完整房间轮廓与奖励坐标（不返回 rewardCells）；
    ///  - 不建第二套 CurrentDepth / Region（只读使用 grid/region 既有数据）。
    ///
    /// 正式运行时接线（Blocker1 修复）：
    ///  - 布局自动同步：Start/ReSyncFromGenerator 把 generator.Instances 全量登记，无需测试探针手动 RegisterAll；
    ///  - 异常查询入口：玩家扫描组件（OreScanner，持有 R 键手势与网格中心）在扫矿后调用
    ///    QueryAnomalyAround(center) 取文明异常信号并合入反馈 —— 资源扫描与文明扫描分层，但同一扫描动作都能得到异常。
    ///
    /// discovered（世界/探索发现）与 investigated（核心是否已调查）在 Issue §10 明确区分：
    /// Core 首奖幂等基于 investigated；本服务是两者状态的唯一真相源。
    /// </summary>
    public class RuinDiscoveryService : MonoBehaviour
    {
        [Tooltip("发现距离（Chebyshev 格）：玩家距某座 Ruin 的 bounds 边界 ≤ 本值 → 标记 discovered。")]
        [Min(1)] public int discoveryRadius = 6;

        [Tooltip("模糊信号距离上限（格）：bounds 边界超出本值 → 连模糊信号都不返回。")]
        [Min(1)] public int signalRange = 18;

        [Tooltip("遗迹生成器（DEV-013）。Start/ReSync 时自动把其 Instances 同步成本服务布局 —— 正式运行时闭环，不经测试探针手动 RegisterAll。")]
        public RuinGenerator generator;

        /// <summary>最近一次扫描结果（未扫为 null）。</summary>
        public RuinSignalResult LastSignal { get; private set; }

        /// <summary>每次首发现一座遗迹触发（幂等）。</summary>
        public event Action<RuinInstance> OnRuinFirstDiscovered;

        readonly Dictionary<string, bool> discovered = new Dictionary<string, bool>();
        readonly Dictionary<string, bool> investigated = new Dictionary<string, bool>();
        readonly List<RuinInstance> layout = new List<RuinInstance>();

        /// <summary>
        /// 正式运行时：DigGrid.Awake 生成在全部 Start 之前完成，因此本组件 Start 时
        /// generator.Instances 已由 Ruin pass 填好，可自动同步布局 —— 无需测试探针手动 RegisterAll。
        /// </summary>
        void Start()
        {
            ReSyncFromGenerator();
        }

        /// <summary>
        /// 把 generator.Instances 全量同步成本服务布局（幂等；重复调用按 instanceId 去重）。
        /// 网格 Regenerate（同 seed 确定性重建）后也可再调一次保持同步。
        /// </summary>
        public void ReSyncFromGenerator()
        {
            if (generator == null) return;
            RegisterAll(generator.Instances);
        }

        public void RegisterInstance(RuinInstance inst)
        {
            if (inst == null) return;
            if (!discovered.ContainsKey(inst.instanceId)) discovered[inst.instanceId] = false;
            if (!investigated.ContainsKey(inst.instanceId)) investigated[inst.instanceId] = false;
            if (!layout.Contains(inst)) layout.Add(inst);
        }

        public void RegisterAll(IEnumerable<RuinInstance> list)
        {
            if (list == null) return;
            foreach (var i in list) RegisterInstance(i);
        }

        /// <summary>清空全部运行时状态（测试/重建布局用；不用于 Run 结算）。</summary>
        public void ResetAll()
        {
            discovered.Clear();
            investigated.Clear();
            layout.Clear();
        }

        /// <summary>按核心格反查所属 RuinInstance（Core 命中时定位 instanceId）；未命中返回 null。</summary>
        public RuinInstance FindByCoreCell(Vector2Int coreCell)
        {
            for (int i = 0; i < layout.Count; i++)
                if (layout[i] != null && layout[i].coreCell == coreCell)
                    return layout[i];
            return null;
        }

        /// <summary>布局中登记的全部实例（只读）。</summary>
        public IReadOnlyList<RuinInstance> Instances => layout;

        // ---------- 状态查询 ----------
        public bool IsDiscovered(string instanceId) => discovered.TryGetValue(instanceId, out var v) && v;
        public bool IsInvestigated(string instanceId) => investigated.TryGetValue(instanceId, out var v) && v;
        public bool HasAnyRuin => discovered.Count > 0;

        /// <summary>Core 首奖命中后标记已调查（幂等；重复调用无害）。</summary>
        public void MarkInvestigated(string instanceId)
        {
            if (instanceId == null) return;
            investigated[instanceId] = true;
        }

        public int DiscoveredCount => CountTrue(discovered);
        public int InvestigatedCount => CountTrue(investigated);

        static int CountTrue(Dictionary<string, bool> d)
        {
            int n = 0;
            foreach (var kv in d) if (kv.Value) n++;
            return n;
        }

        // ---------- 只读扫描 ----------

        /// <summary>
        /// 以网格坐标为中心做一次只读信号扫描。返回最近一座（未发现或已发现）Ruins 的信号结果；
        /// 若某座 Ruin 的 bounds 边界距中心 ≤ discoveryRadius 且尚未 discovered → 触发发现并置 discovered=true。
        /// 不返回奖励坐标；绝不写世界。
        /// </summary>
        public RuinSignalResult ScanRuinAround(Vector2Int center)
        {
            // 遍历已注册布局实例，找最近一座在 signalRange 内的遗迹。
            RuinInstance nearest = null;
            int bestDist = int.MaxValue;
            for (int i = 0; i < layout.Count; i++)
            {
                var inst = layout[i];
                if (inst == null) continue;
                int d = DistToBounds(center, inst.bounds);
                if (d <= signalRange && d < bestDist)
                {
                    bestDist = d;
                    nearest = inst;
                }
            }

            var res = new RuinSignalResult();
            if (nearest == null)
            {
                res.description = "扫描：附近未检测到异常结构信号";
                res.hasSignal = false;
                LastSignal = res;
                return res;
            }

            // 触发发现（若足够近且未发现）
            bool wasDiscovered = IsDiscovered(nearest.instanceId);
            if (!wasDiscovered && bestDist <= discoveryRadius)
            {
                discovered[nearest.instanceId] = true;
                res.firstDiscovery = true;
                OnRuinFirstDiscovered?.Invoke(nearest);
            }

            bool nowDiscovered = IsDiscovered(nearest.instanceId);
            res.hasSignal = true;
            res.instanceId = nearest.instanceId;
            res.distance = bestDist;
            res.isDiscovered = nowDiscovered;
            res.isInvestigated = IsInvestigated(nearest.instanceId);
            res.ruinId = nearest.definition != null ? nearest.definition.stableId : "";
            string ruinName = nearest.definition != null ? nearest.definition.displayName : "古代遗迹";

            // 模糊 vs 明确：未发现只给方向/距离模糊信号，不露房间轮廓与奖励坐标
            if (!nowDiscovered)
                res.description = $"扫描：检测到{DescribeDirection(bestDist)}异常结构信号（文明遗存？）——需接近定位";
            else if (!res.isInvestigated)
                res.description = $"扫描：锁定异常结构 —— 「{ruinName}」（已发现，未调查）";
            else
                res.description = $"扫描：{ruinName}（已调查，无更多异常）";

            LastSignal = res;
            return res;
        }

        /// <summary>中心点距某 bounds 外接矩形的最短 Chebyshev 距离（0 = 在矩形内/边）。</summary>
        public static int DistToBounds(Vector2Int c, RectInt b)
        {
            int dx = c.x < b.x ? b.x - c.x : (c.x > b.xMax ? c.x - b.xMax : 0);
            int dy = c.y < b.y ? b.y - c.y : (c.y > b.yMax ? c.y - b.yMax : 0);
            return Mathf.Max(dx, dy);
        }

        static string DescribeDirection(int d)
        {
            // 网格 y 增大 = 更深（屏幕下方）。这里只给深度方向模糊提示。
            return d > 0 ? "更深处的" : "附近的";
        }
    }

    /// <summary>
    /// DEV-013：一次遗迹信号扫描的只读结果（供 HUD / 测试断言）。
    /// 不携带 rewardCells 等精确奖励坐标 —— 避免 Scanner 变透视作弊器。
    /// </summary>
    public class RuinSignalResult
    {
        public bool hasSignal;
        public bool firstDiscovery;      // 本次扫描是否触发了一次首发现
        public string instanceId = "";
        public string ruinId = "";
        public int distance = -1;        // 最近 bounds 边界 Chebyshev 距离（-1 = 无信号）
        public bool isDiscovered;
        public bool isInvestigated;
        public string description = "";
    }
}
