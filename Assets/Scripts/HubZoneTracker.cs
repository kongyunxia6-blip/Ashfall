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
    ///  - 每实例持有 local 计数，仅在 0↔1 边界增减全局计数（进出配对、重复 Enter 免疫）；
    ///  - 实例 OnDisable / OnDestroy 主动归还（场景卸载 / 对象禁用 / 销毁后不残留 true）；
    ///  - [RuntimeInitializeOnLoadMethod(SubsystemRegistration)] 在进 Play（域重载或关闭 Domain Reload）时归零。
    ///
    /// 用法：各功能组件（SellTerminal / UpgradeWorkbench / FuelStation / SurfaceBase）在自己的
    /// OnTriggerEnter2D/Exit2D 里调 Enter/Exit，并在 OnDisable/OnDestroy 归还本实例计数。
    /// 对外只读 in-range 状态 = HubZoneTracker.InRange(kind)。
    /// </summary>
    public static class HubZoneTracker
    {
        static readonly int[] counts = new int[4];

        /// <summary>玩家当前是否在任一同类功能区的交互范围内。</summary>
        public static bool InRange(HubZoneKind kind) => counts[(int)kind] > 0;

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
}
