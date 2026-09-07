using System.Collections.Generic;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-014 Blocker1 修复：地下发现节点的只读信号查询服务（镜像 RuinDiscoveryService 风格）。
    ///
    /// Issue #30 §Acceptance「看到异常 → 判断是否绕路」的第一环：Discovery Node 必须能被正式
    /// 玩家扫描动作只读地发现，并返回粗粒度模糊异常提示。本组件不建第二套 Scanner V2：
    ///  - 作为 OreScanner 的第二个可选字段（同 ruinDiscovery），在玩家同一个扫描动作里并查；
    ///  - 只读：只用 InBounds / GetTile / 布局记录，绝不写任何 Block / 不改 world；
    ///  - 模糊：只返回该节点类型的 ScanHint（abandoned / thermal / ancient / unstable）+
    ///    粗略深度方向与距离档，【不泄露】bounds / rewardCells / 精确奖励坐标；
    ///  - 布局自动同步：Start / ReSyncFromGenerator 把 generator.Nodes 全量登记（正式运行时闭环，
    ///    不经测试探针手动 RegisterAll）；
    ///  - 发现状态（discovered）记录在运行时（跨 scan 去重首发现提示），不持有玩法结算。
    ///
    /// 运行时接线（Blocker1 修复）：
    ///  - 场景 Builder 在 GameManager 物体挂本组件，赋 discoveryNodeGenerator = nodeGen；
    ///  - OreScanner.discoveryNodeDiscovery = 本组件 → ScanAndReportAround 在扫矿 + 文明信号后，
    ///    再查本服务的节点异常信号并合入同一句反馈。
    /// </summary>
    public class DiscoveryNodeDiscoveryService : MonoBehaviour
    {
        [Tooltip("发现距离（Chebyshev 格）：玩家距某节点 bounds 边界 ≤ 本值 → 标记该节点已发现（首发现提示幂等）。")]
        [Min(1)] public int discoveryRadius = 5;

        [Tooltip("模糊信号距离上限（格）：bounds 边界超出本值 → 连模糊信号都不返回。")]
        [Min(1)] public int signalRange = 16;

        [Tooltip("发现节点生成器（DEV-014）。Start/ReSync 时自动把其 Nodes 同步成本服务布局。")]
        public DiscoveryNodeGenerator discoveryNodeGenerator;

        /// <summary>最近一次节点扫描结果（未扫为 null）。</summary>
        public DiscoverySignalResult LastSignal { get; private set; }

        readonly Dictionary<string, bool> discovered = new Dictionary<string, bool>();
        readonly List<DiscoveryNodeInstance> layout = new List<DiscoveryNodeInstance>();

        /// <summary>正式运行时：DigGrid.Awake 生成在全部 Start 之前完成，本组件 Start 可自动同步布局。</summary>
        void Start()
        {
            ReSyncFromGenerator();
        }

        /// <summary>把 discoveryNodeGenerator.Nodes 全量同步成本服务布局（幂等；按 instanceId 去重）。</summary>
        public void ReSyncFromGenerator()
        {
            if (discoveryNodeGenerator == null) return;
            if (discoveryNodeGenerator.Nodes == null) return;
            for (int i = 0; i < discoveryNodeGenerator.Nodes.Count; i++)
                RegisterInstance(discoveryNodeGenerator.Nodes[i]);
        }

        public void RegisterInstance(DiscoveryNodeInstance inst)
        {
            if (inst == null) return;
            if (!discovered.ContainsKey(inst.instanceId)) discovered[inst.instanceId] = false;
            if (!layout.Contains(inst)) layout.Add(inst);
        }

        public void RegisterAll(IEnumerable<DiscoveryNodeInstance> list)
        {
            if (list == null) return;
            foreach (var i in list) RegisterInstance(i);
        }

        /// <summary>清空全部运行时状态（测试/重建布局用）。</summary>
        public void ResetAll()
        {
            discovered.Clear();
            layout.Clear();
        }

        /// <summary>布局中登记的全部实例（只读）。</summary>
        public IReadOnlyList<DiscoveryNodeInstance> Instances => layout;

        public bool IsDiscovered(string instanceId) => discovered.TryGetValue(instanceId, out var v) && v;

        public int DiscoveredCount
        {
            get
            {
                int n = 0;
                foreach (var kv in discovered) if (kv.Value) n++;
                return n;
            }
        }

        /// <summary>
        /// 以网格坐标为中心做一次只读信号扫描。返回最近一座在 signalRange 内的发现节点的模糊信号；
        /// 若节点 bounds 边界距中心 ≤ discoveryRadius 且尚未 discovered → 标记已发现并触发首发现提示。
        /// 只读：绝不写世界；不返回 bounds / rewardCells / 精确奖励坐标。
        /// </summary>
        public DiscoverySignalResult ScanDiscoveryAround(Vector2Int center)
        {
            // 遍历已登记布局实例，找最近一座在 signalRange 内的节点
            DiscoveryNodeInstance nearest = null;
            int bestDist = int.MaxValue;
            for (int i = 0; i < layout.Count; i++)
            {
                var inst = layout[i];
                if (inst == null) continue;
                int d = RuinDiscoveryService.DistToBounds(center, inst.bounds);
                if (d <= signalRange && d < bestDist)
                {
                    bestDist = d;
                    nearest = inst;
                }
            }

            var res = new DiscoverySignalResult();
            if (nearest == null)
            {
                res.description = "";
                res.hasSignal = false;
                LastSignal = res;
                return res;
            }

            bool wasDiscovered = IsDiscovered(nearest.instanceId);
            if (!wasDiscovered && bestDist <= discoveryRadius)
                discovered[nearest.instanceId] = true;

            bool nowDiscovered = IsDiscovered(nearest.instanceId);
            res.hasSignal = true;
            res.type = nearest.type;
            res.distance = bestDist;
            res.isDiscovered = nowDiscovered;

            // 模糊：只给 ScanHint + 粗略深度方向；已发现再补类型显示名。绝不返回 bounds/rewardCells。
            string hint = DiscoveryNodeTypeInfo.ScanHint(nearest.type);
            string depthWord = bestDist > 0 ? "更深处的" : "附近的";
            if (!nowDiscovered)
                res.description = "侦测到" + depthWord + hint;
            else
                res.description = hint + "（已发现：" + DiscoveryNodeTypeInfo.DisplayName(nearest.type) + "）";

            LastSignal = res;
            return res;
        }
    }

    /// <summary>DEV-014 Blocker1：一次发现节点信号扫描的只读结果。不携带 bounds/rewardCells 等精确坐标。</summary>
    public class DiscoverySignalResult
    {
        public bool hasSignal;
        public DiscoveryNodeType type;
        public int distance = -1;
        public bool isDiscovered;
        public string description = "";
    }
}
