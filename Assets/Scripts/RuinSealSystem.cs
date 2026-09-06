using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-013：遗迹封印（RuinSeal）运行时门控（Issue §6）。
    ///
    /// 设计（对齐 HotRock 的能力门范式）：
    ///  - RuinSeal 是「装备/能力是否准备好」的入口门，不是单纯高 HP 岩块；
    ///  - 无 RuinAccess：明确拒挖反馈（非 invisible wall），入口不打开、单格不变；
    ///  - 有 RuinAccess：放行本次单格挖掘（HitBlock 由 DigGrid 承载，把封印打穿形成入口）；
    ///  - 本系统只经 MiningCapabilityResolver.HasCapability(RuinAccess) 查询能力，
    ///    【禁止】直接查 RuinAccessKey 模块 enum；
    ///  - 不做 invisible wall（有权限即可打开，无权限给可见反馈）；
    ///  - 不复制第二套 HP/Breaking/Remove（耐久/命中仍由 DigGrid 唯一承载）；
    ///  - 每次动作仍只处理 1 Block，不产生 AoE / chain，不伤邻格。
    ///
    /// 挂载：任意物体（推荐与 DigGrid 或 DrillVehicle 同物体）。由 DrillVehicle 在命中
    /// RuinSeal 前经可选引用调用 AllowOpenSeal（null 则视为无封印系统 → 恒放行，零回归）。
    /// </summary>
    public class RuinSealSystem : MonoBehaviour
    {
        [Tooltip("无 RuinAccess 时玩家看到的明确反馈文案。")]
        public string rejectMessage = "遗迹封印被未知能量锁死——需要「遗迹密钥」（RuinAccess）才能开启！";

        /// <summary>最近一次门控判定结果（验收/日志读取）。</summary>
        public string LastVerdict { get; private set; } = "none";

        /// <summary>最近一次无权限拒挖的反馈文案（DrillVehicle 用它刷 HUD 消息）。</summary>
        public string LastRejectReason { get; private set; } = "";

        /// <summary>
        /// RuinSeal 命中前的门控查询（由 DrillVehicle 在命中前调用）。
        /// 返回 true = 放行本次单格挖掘（可打穿封印）；false = 拒挖（无 RuinAccess，入口不打开）。
        /// 只经 MiningCapabilityResolver 查 RuinAccess，不依赖任何具体模块。
        /// </summary>
        public bool AllowOpenSeal(bool hasRuinAccess)
        {
            if (hasRuinAccess)
            {
                LastVerdict = "open_allowed";
                LastRejectReason = "";
                return true;
            }

            // 无 RuinAccess：明确拒挖，给可见反馈。
            LastVerdict = "sealed_no_access";
            LastRejectReason = rejectMessage;
            return false;
        }

        /// <summary>重置门控状态（测试/新 Run 用）。</summary>
        public void ResetSeal()
        {
            LastVerdict = "reset";
            LastRejectReason = "";
        }
    }
}
