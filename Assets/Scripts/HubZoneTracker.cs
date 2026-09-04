using UnityEngine;

namespace Ashfall
{
    /// <summary>地表 Hub 交互区种类。Lander=登陆舱（返航安全区，无按键交互，仅进出计数供 HUD 显示上下文）。</summary>
    public enum HubZoneKind { Sell, Workbench, Fuel, Lander }

    /// <summary>
    /// DEV-006：地表 Hub 交互范围的跨实例 / 跨场景安全计数。
    ///
    /// 解决 DEV-005 SellTerminal.PlayerInRange 的静态生命周期隐患：
    ///  - 计数按 kind 全局共享：多个同类终端/功能点时，离开其中一个不会错误清掉另一个仍在场的状态；
    ///  - 每个实例经 ZonePresence 持有本地计数，仅在 0↔1 边界增减全局计数（进出配对、重复 Enter 免疫）；
    ///  - 实例 OnDisable / OnDestroy 经 ZonePresence.ReleaseAll 一次性归还（场景卸载 / 对象禁用 /
    ///    销毁后不残留 true；多 Collider 玩家 localCount&gt;1 时也一次清空，全局只退一次）；
    ///  - [RuntimeInitializeOnLoadMethod(SubsystemRegistration)] 在进 Play（域重载或关闭 Domain Reload）时归零。
    ///
    /// 用法：各功能组件（SellTerminal / UpgradeWorkbench / FuelStation / SurfaceBase）在自己的
    /// OnTriggerEnter2D/Exit2D 里调 presence.Enter() / presence.Exit()，
    /// 并在 OnDisable/OnDestroy 调 presence.ReleaseAll()。
    /// 对外只读 in-range 状态 = HubZoneTracker.InRange(kind)。
    /// </summary>
    public static class HubZoneTracker
    {
        static readonly int[] counts = new int[4];

        /// <summary>玩家当前是否在任一同类功能区的交互范围内。</summary>
        public static bool InRange(HubZoneKind kind) => counts[(int)kind] > 0;

        /// <summary>某 kind 的全局在场数（&gt;0 = 有玩家在该类功能区；测试/断言用）。</summary>
        public static int Count(HubZoneKind kind) => counts[(int)kind];

        /// <summary>玩家进入一个该 kind 的实例范围（由实例在 0→1 边界调用）。</summary>
        public static void Enter(HubZoneKind kind) => counts[(int)kind]++;

        /// <summary>玩家离开一个该 kind 的实例范围（由实例在 1→0 边界调用）。</summary>
        public static void Exit(HubZoneKind kind) => counts[(int)kind] = Mathf.Max(0, counts[(int)kind] - 1);

        /// <summary>归零全部计数（实例 OnDisable/OnDestroy 已各自归还后，通常已为 0；兜底用）。</summary>
        public static void ResetAll()
        {
            for (int i = 0; i < counts.Length; i++) counts[i] = 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnDomainReload() => ResetAll();
    }

    /// <summary>
    /// DEV-006 第二轮：单实例「玩家在场」计数器（替代各组件手写 int localCount）。
    ///
    /// 生命周期规则（评审修正要求）：
    ///  - Enter / Exit 成对调用，逐 Collider 配对（一个 DrillVehicle 有多个 Collider 时，
    ///    每个 Collider 各进各出、各自配对）；
    ///  - 全局 kind 计数只在本实例 0↔1 边界增减一次（多 Collider 也只占一个全局位）；
    ///  - ReleaseAll 供 OnDisable / OnDestroy / 场景卸载：一次性归还本实例全部本地占用
    ///    （local 直接清 0，全局只 Exit 一次）——修复旧版「ReleaseLocal 每次仅 -1，
    ///    同一实例 localCount&gt;1 时禁用/销毁会残留全局计数」的问题；
    ///  - 正常 OnTriggerExit 仍走 Exit()（单次配对递减，与 Enter 对称）。
    /// </summary>
    public sealed class ZonePresence
    {
        readonly HubZoneKind kind;
        int local;

        public ZonePresence(HubZoneKind kind) { this.kind = kind; }

        /// <summary>本实例范围内玩家 Collider 数。</summary>
        public int LocalCount => local;

        /// <summary>本实例是否正有玩家在范围内。</summary>
        public bool Present => local > 0;

        /// <summary>一个玩家 Collider 进入本实例范围（0→1 边界才动全局计数）。</summary>
        public void Enter()
        {
            if (local == 0) HubZoneTracker.Enter(kind);
            local++;
        }

        /// <summary>一个玩家 Collider 离开本实例范围（逐次配对递减；1→0 边界才动全局计数）。</summary>
        public void Exit()
        {
            if (local <= 0) return;
            local--;
            if (local == 0) HubZoneTracker.Exit(kind);
        }

        /// <summary>一次性释放本实例全部占用（禁用/销毁/场景卸载时调用；幂等）。</summary>
        public void ReleaseAll()
        {
            if (local <= 0) return;
            local = 0;
            HubZoneTracker.Exit(kind);
        }
    }
}
