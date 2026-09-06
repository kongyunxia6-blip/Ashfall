using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-011：风险撤离 / 失败与死亡代价 V1 —— 集中式参数表（唯一来源，禁止散落 magic number）。
    ///
    /// V1 采用静态集中定义（同 DEV-010 EquipmentCatalog 风格，不强制 ScriptableObject 化）：
    ///  - cargoKeepRatio：死亡后每种货物保留比例。默认 0.5 → 保留 floor(count × 0.5)，
    ///    其余为本次 Run 的损失。规则确定性、可解释、可测试（无随机）。
    ///  - Recovery Fee：fee = BaseRecoveryFee + floor(MaxDepthThisRun / DepthStep) × DepthStepFee。
    ///    - 未下潜（MaxDepthThisRun=0，地表/测试异常死亡）→ 0 费；
    ///    - Cash 不足时最多扣到 0，不产生债务（由 GameManager.SpendCash clamp 保证）；
    ///    - MaxRecoveryFee &gt; 0 时封顶（V1 默认 60：62m 深处约 $40，封顶不触发，仅防数值膨胀）。
    ///
    /// 经济量级对齐（DEV-005 出厂 100 现金、铁 w1/$12、每格矿价 ≤ $12）：
    ///  - Base $10 = 半趟浅层收益量级（约 1 块铁的价值），失败有体感但不伤筋动骨；
    ///  - 每 10m +$5：50m 失事 ≈ $35（Issue 示例原值），60m 深 ≈ $40，远低于满载货物价值，
    ///    玩家不会因一次失败破产，但仍会认真权衡「这趟值不值得继续贪」。
    /// </summary>
    public static class RiskExtractionCatalog
    {
        // ---------- Cargo Loss ----------

        /// <summary>死亡后保留的货物数量比例（每 def：keep = floor(count × keepRatio)）。</summary>
        public const float CargoKeepRatio = 0.5f;

        /// <summary>保留比例的文字（HUD「失事保留约 xx%」用）。</summary>
        public static string KeepRatioText => $"{Mathf.RoundToInt(CargoKeepRatio * 100f)}%";

        /// <summary>按比例保留后的件数（keep = floor(count × keepRatio)，确定性下取整）。</summary>
        public static int KeepCount(int count) => Mathf.FloorToInt(count * CargoKeepRatio);

        /// <summary>按比例损失件数（loss = count - keep = ceil(count × (1-keepRatio))）。</summary>
        public static int LostCount(int count) => count - KeepCount(count);

        /// <summary>
        /// 按确定性保留规则估算当前货舱的失事损失价值（只读，不修改货舱；HUD 风险行用）。
        /// </summary>
        public static int LostValueOf(InventoryGrid inv)
            => inv != null ? inv.EstimatedLossValue(CargoKeepRatio) : 0;

        // ---------- Recovery Fee ----------

        /// <summary>打捞费基础费（$）。0m 死 = 0 费；下潜过即至少付本费。</summary>
        public const int BaseRecoveryFee = 10;

        /// <summary>深度步长（m）。每满一个步长追加一次 StepFee。</summary>
        public const int DepthStep = 10;

        /// <summary>每满一个步长的追加费（$）。</summary>
        public const int DepthStepFee = 5;

        /// <summary>打捞费封顶（$）。0 = 不封顶。V1 默认 60（62m 理论 $40，不触发，仅防膨胀）。</summary>
        public const int MaxRecoveryFee = 60;

        /// <summary>
        /// Recovery Fee 公式（单一来源）：
        ///   maxDepth &lt;= 0（未下潜/地表死亡）→ 0；
        ///   否则 = min(MaxRecoveryFee(可选), Base + floor(maxDepth / DepthStep) × StepFee)。
        /// maxDepth 使用 DisplayDepth 语义（Surface 显示 0m；下潜 1 格 = 1m），
        /// 与 DepthRegionProgression.MaxDepthThisRun / MaxDepthEver 同一套深度语义。
        /// </summary>
        public static int RecoveryFee(int maxDepthThisRun)
        {
            if (maxDepthThisRun <= 0) return 0;
            int raw = BaseRecoveryFee + (maxDepthThisRun / DepthStep) * DepthStepFee;
            return MaxRecoveryFee > 0 ? Mathf.Min(raw, MaxRecoveryFee) : raw;
        }

        /// <summary>打捞费公式说明（PR / HUD 展示用）。</summary>
        public static string FeeFormulaText =>
            $"$" + BaseRecoveryFee + " + 每 " + DepthStep + "m +$" + DepthStepFee +
            (MaxRecoveryFee > 0 ? $"（封顶 ${MaxRecoveryFee}）" : "");
    }
}
