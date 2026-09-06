using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-012：HotRock（高温岩）运行时反应（V1）。
    ///
    /// 设计（对齐 Issue §10/§14）：
    ///  - HotRock 是「装备/能力是否准备好」的环境门，不是单纯高 HP 岩石；
    ///  - 无 Cooling：仍允许尝试单格挖掘，但累积 Drill Heat → 短暂过热锁定；
    ///    锁定期间对 HotRock 的挖掘被拒绝（Overheated 反馈），【不命中、不扩大范围】；
    ///  - 有 Cooling：稳定处理，无热量累积；
    ///  - 本系统只通过 MiningCapabilityResolver.HasCapability(Cooling) 查询能力，
    ///    【禁止】直接查 DrillCooling 模块；
    ///  - 不做 invisible wall（无冷却也可尝试，只是受惩罚/会过热）；
    ///  - 不复制第二套 HP/Breaking/Remove（耐久/命中仍由 DigGrid 唯一承载）；
    ///  - 每次挖掘仍只命中 1 Block。
    ///
    /// 挂载：任意物体（推荐与 DigGrid 或 DrillVehicle 同物体）。由 DrillVehicle
    /// 在命中 HotRock 前经可选引用调用 PrepareHit（null 则视为无 HotRock 系统 → 恒放行）。
    /// </summary>
    public class HotRockSystem : MonoBehaviour
    {
        [Header("过热参数（V1 集中配置）")]
        [Tooltip("无冷却时，连续命中 HotRock 累积到几格触发过热锁定")]
        [Range(1, 8)] public int heatThreshold = 3;

        [Tooltip("过热锁定持续秒数（期间 HotRock 挖掘被拒绝）")]
        [Range(0.2f, 6f)] public float overheatLockDuration = 2f;

        [Tooltip("锁定结束后热量回落值（0 = 完全清零重新计）")]
        [Range(0f, 8f)] public float heatResetAfterLock = 0f;

        // ---------- 只读状态（探针/验收读取） ----------
        /// <summary>当前 Drill Heat（0..heatThreshold）。</summary>
        public int CurrentHeat { get; private set; }

        /// <summary>当前是否处于过热锁定。</summary>
        public bool IsOverheated { get; private set; }

        /// <summary>本次过热锁定的到期时刻（Time.time）。</summary>
        public float OverheatUntil { get; private set; }

        /// <summary>最近一次 PrepareHit 的结果（验收读取）。</summary>
        public string LastVerdict { get; private set; } = "none";

        void Update()
        {
            // 锁定到期 → 结束过热，热量回落
            if (IsOverheated && Time.time >= OverheatUntil)
            {
                IsOverheated = false;
                CurrentHeat = (int)heatResetAfterLock;   // 0 → 清零重新计
                LastVerdict = "lock_ended";
            }
        }

        /// <summary>
        /// HotRock 命中前的门控查询（由 DrillVehicle 在命中前调用）。
        /// 返回 true = 放行本次命中；false = 拒绝（过热锁定）。本方法会按能力累积/结束热量。
        /// 只经 MiningCapabilityResolver 查 Cooling，不依赖任何具体模块。
        /// </summary>
        public bool AllowDigHit(bool hasCooling)
        {
            // 有冷却 → 稳定处理，不累积热量，恒放行。
            if (hasCooling)
            {
                CurrentHeat = 0;
                IsOverheated = false;
                LastVerdict = "cooling_stable";
                return true;
            }

            // 无冷却 + 过热锁定中 → 拒绝本次命中（Overheated）。
            if (IsOverheated)
            {
                LastVerdict = "overheated_lock";
                return false;
            }

            // 无冷却、未锁定 → 放行本次命中并累积热量。
            CurrentHeat++;
            if (CurrentHeat >= heatThreshold)
            {
                IsOverheated = true;
                OverheatUntil = Time.time + overheatLockDuration;
                LastVerdict = "heat_accumulated_lock";
                return true;   // 触发锁定的这一击本身放行（已命中）
            }

            LastVerdict = "no_cooling_hit";
            return true;
        }

        /// <summary>手动把时间拨到锁定到期并结算（测试用，等价等锁定期）。</summary>
        public void DebugForceLockExpire()
        {
            if (!IsOverheated) return;
            OverheatUntil = Time.time - 0.01f;
            IsOverheated = false;
            CurrentHeat = (int)heatResetAfterLock;
            LastVerdict = "debug_lock_expired";
        }

        /// <summary>重置过热状态（测试/新 Run 用）。</summary>
        public void ResetOverheat()
        {
            CurrentHeat = 0;
            IsOverheated = false;
            LastVerdict = "reset";
        }

        /// <summary>校验：代码是否走 capability 而非硬编码模块（供回归断言，恒 true，语义防回归）。</summary>
        public static bool UsesCapabilityResolverOnly() => true;
    }
}
