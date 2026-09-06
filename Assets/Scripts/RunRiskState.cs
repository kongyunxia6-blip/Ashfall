using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-011：风险撤离 / Run 生命周期 / 失败与死亡代价 V1 —— 单一权威状态机。
    ///
    /// 职责（对齐 Issue #23）：
    ///  - Run 生命周期：首次离开地表（离开 SurfaceHubZone）→ RunActive；回地表未 Sell 风险仍在；
    ///    完成 Sell → RunSecured；下一次真正离开地表 → BeginRun（reset 本次 Run 统计，MaxDepthEver 永不清）；
    ///  - 未结算风险估算：PendingCargoValue / EstimatedLostValue / EstimatedRecoveryFee 实时只读；
    ///  - 失败结算 ResolveFailure：只执行一次（FailureResolved guard，同帧多次 Die / 重复调用幂等），
    ///    Cargo 按 RiskExtractionCatalog.CargoKeepRatio 部分保留（每 def floor(count×keep)），
    ///    扣 Recovery Fee（Cash 最多扣到 0），生成 Failure Summary，respawn 由 GameManager 统一调度；
    ///  - respawn 后不清空保留货舱（玩家可把存活货物带回 SellTerminal 出售，Loop C）；
    ///  - 新 Run 开始点正式接入 DepthRegionProgression.ResetRun()（唯一调用方）；
    ///  - 永久成长（Equipment/Module/等级/Cash 已卖部分）一律不丢。
    ///
    /// 不做：修改 DigGrid / Ore / 地形；改装备；改 Scanner；改 HUD 绘制（只读展示）；死亡原因扩展。
    ///
    /// 挂载：GameManager 同物体（GameManager.RunRisk 自动 GetComponent）。
    /// 旧场景（无本组件）→ GameManager 走 M1 旧死亡结算（全清 + 旧打捞费），零回归。
    /// </summary>
    public class RunRiskState : MonoBehaviour
    {
        [Header("引用（默认自动查找）")]
        public GameManager gameManager;
        public DepthRegionProgression regionProgression;
        public DrillVehicle vehicle;

        // ---------- 只读状态 ----------

        /// <summary>本次 Run 是否进行中（已离开地表，或已回地表但尚未 Sell 结算）。</summary>
        public bool RunActive { get; private set; }

        /// <summary>本次 Run 的货物是否已通过 SellTerminal 安全结算（风险清零）。</summary>
        public bool RunSecured { get; private set; }

        /// <summary>当前 Run 是否已发生失败并结算完毕（幂等 guard；respawn 后复位）。</summary>
        public bool FailureResolved { get; private set; }

        /// <summary>Run 编号（从 1 起；成功开始一次 = +1）。日志/验收展示用。</summary>
        public int RunNumber { get; private set; }

        /// <summary>最近一次失败结算摘要（respawn 后仍可读；下一次 BeginRun 时清空）。</summary>
        public string LastFailureSummary { get; private set; } = "";

        /// <summary>最近一次失败发生时的最深区域（Summary 前可读；未失败为 null）。</summary>
        public DepthRegionDefinition LastFailureRegion { get; private set; }

        // ---------- 未结算风险估算（只读，实时） ----------

        /// <summary>尚未结算的货物价值（= 当前货舱价值；RunSecured 后应为 0 或玩家新挖未卖）。</summary>
        public int PendingCargoValue => vehicle != null ? vehicle.CargoValue : 0;

        /// <summary>按当前货舱明细逐种估算的失事损失价值（keep = floor(count×keep)，loss = count-keep）。</summary>
        public int EstimatedLostValue
        {
            get
            {
                if (vehicle == null) return 0;
                return RiskExtractionCatalog.LostValueOf(vehicle.Inventory);
            }
        }

        /// <summary>当前最大深度（DisplayDepth 语义，委托 DepthRegionProgression）。</summary>
        public int MaxDepthThisRun
            => regionProgression != null ? regionProgression.MaxDepthThisRun : 0;

        /// <summary>失事预计打捞费（按本次 Run 最大深度）。</summary>
        public int EstimatedRecoveryFee
            => RiskExtractionCatalog.RecoveryFee(MaxDepthThisRun);

        // ---------- 事件 ----------

        /// <summary>Run 状态变化（BeginRun / Sell 结算 / 失败结算 / respawn 复位）后触发（HUD 即时刷新）。</summary>
        public event Action OnRunStateChanged;

        void Awake()
        {
            if (gameManager == null) gameManager = GetComponent<GameManager>();
        }

        void Start()
        {
            if (gameManager == null) gameManager = GameManager.Instance;
            if (regionProgression == null && gameManager != null)
                regionProgression = gameManager.GetComponent<DepthRegionProgression>();
            if (vehicle == null && gameManager != null) vehicle = gameManager.Player;
            if (vehicle == null) vehicle = FindFirstObjectByType<DrillVehicle>();

            // DEV-011：进入 Mid/Deep 的一次性风险提示（HUD 只读展示；每 Run 由 ResetRun 清 first-entered 后只会再触发一次）
            if (regionProgression != null)
                regionProgression.RegionFirstEntered += OnRegionFirstEntered;
        }

        void OnDestroy()
        {
            if (regionProgression != null)
                regionProgression.RegionFirstEntered -= OnRegionFirstEntered;
        }

        void LateUpdate()
        {
            var gm = gameManager != null ? gameManager : GameManager.Instance;
            if (gm == null) return;

            // 离开地表（Hub 区外）= Run 开始。仅在 RunActive=false 时触发一次：
            //  - 出生/卖完货后再下矿 → BeginRun（新 Run）；
            //  - 失败结算后 RunActive=false，respawn 回到 Hub 区（IsAtSurface=true）不触发；
            //    玩家再次下矿 → BeginRun（新 Run，Issue §4「下一次下矿重新开始」）；
            //  - Sell 结算后再下矿 → BeginRun（新 Run）。
            // 回地表但未 Sell（IsAtSurface=true）：RunActive 保持，风险仍在（左上「未结算」不消失）。
            if (!gm.IsAtSurface && !RunActive)
                BeginRun();
        }

        // ---------- Run 生命周期 ----------

        /// <summary>
        /// 开始一次新 Run：RunNumber+1、清空上次失败摘要、正式 reset 本次深度统计。
        /// 唯一会调用 DepthRegionProgression.ResetRun() 的地方（DEV-011 接入点）；
        /// MaxDepthEver 永不 reset（ResetRun 语义保证）。
        /// </summary>
        public void BeginRun()
        {
            RunNumber++;
            RunActive = true;
            RunSecured = false;
            FailureResolved = false;
            LastFailureSummary = "";
            LastFailureRegion = null;
            if (regionProgression != null)
                regionProgression.ResetRun();
            Debug.Log($"[RunRiskState] Run #{RunNumber} 开始 —— 离开地表，风险结算已重置（MaxDepthEver 保留）");
            NotifyChanged();
        }

        /// <summary>
        /// 本 Run 货物已安全出售（SellTerminal 结算成功后调用）。
        /// RunSecured=true → 未结算风险清零；玩家再次离开地表才开新 Run。
        /// </summary>
        public void NotifyCargoSecured(int earned)
        {
            if (earned > 0)
            {
                RunSecured = true;
                Debug.Log($"[RunRiskState] 本次货物已安全结算 +${earned}（RunSecured=true，风险清零）");
                NotifyChanged();
            }
        }

        /// <summary>
        /// respawn 复位：允许下一次失败再次结算（FailureResolved=false）。
        /// 保留 LastFailureSummary 供 HUD 持续显示（直到下次 BeginRun 清空）。
        /// 注意：本方法不重置 RunActive —— 死亡即 Run 结束，respawn 后回到 Hub 区，
        /// 玩家必须再次离开地表（BeginRun）才会开启新 Run。
        /// </summary>
        public void OnRespawned()
        {
            FailureResolved = false;
            NotifyChanged();
        }

        // ---------- 失败结算（幂等） ----------

        /// <summary>
        /// 结算一次失败。全部死亡来源（Fuel/Hull/其他 Die 路径）最终都汇聚到这里，且只结算一次：
        ///  - FailureResolved guard：同帧 Fuel/Hull 同时 &lt;=0、或 Die/ResolveFailure 被重复调用 → 后到者直接返回；
        ///  - Cargo：每 def 保留 floor(count × keepRatio)，其余损失（确定性，按槽位稳定顺序）;
        ///  - Recovery Fee：按本次 Run 最大深度（prog.MaxDepthThisRun），Cash 最多扣到 0；
        ///  - 失败摘要记录在 LastFailureSummary（含损失明细/打捞费/保留价值），respawn 后仍可读。
        /// 永久成长（装备/模块/等级/已卖 Cash）不受影响；MaxDepthEver 不回退。
        /// 实际 respawn 由 GameManager 统一 Invoke（本方法不直接搬动玩家）。
        /// </summary>
        public void ResolveFailure(string reason, int deathDepth)
        {
            if (FailureResolved)
            {
                Debug.LogWarning($"[RunRiskState] ResolveFailure 重复调用已忽略（幂等 guard）：{reason}");
                return;
            }
            FailureResolved = true;
            RunActive = false;
            RunSecured = false;

            var gm = gameManager != null ? gameManager : GameManager.Instance;
            var v = vehicle != null ? vehicle : (gm != null ? gm.Player : null);

            // 深度快照（结算用）：本次 Run 最大深度（DisplayDepth）+ 最深区域（Summary 前可读）
            int maxDepth = regionProgression != null ? regionProgression.MaxDepthThisRun : deathDepth;
            LastFailureRegion = regionProgression != null ? regionProgression.DeepestRegionThisRun : null;

            // 1) Cargo 部分保留：每 def floor(count×keep)，其余损失（确定性明细）
            int cargoValueBefore = v != null ? v.CargoValue : 0;
            var lost = v != null ? v.Inventory.ApplyFractionalLoss(RiskExtractionCatalog.CargoKeepRatio) : null;
            int keptValue = v != null ? v.CargoValue : 0;

            // 2) Recovery Fee（Cash ≥ 0 clamp；fee 0 也允许 —— Surface/0m 死亡无费）
            int fee = RiskExtractionCatalog.RecoveryFee(maxDepth);
            int feeApplied = 0;
            if (gm != null && fee > 0)
            {
                feeApplied = Mathf.Min(fee, gm.Cash);
                if (feeApplied > 0)
                    gm.SpendCash(feeApplied);
            }

            // 3) 失败摘要（LossLine 转文本；UI 直接显示本字符串）
            LastFailureSummary = BuildSummary(reason, maxDepth, lost, keptValue, feeApplied, fee);

            Debug.Log($"[RunRiskState] 失败结算：{reason} @depth {maxDepth}m" +
                      $" · Cargo ${cargoValueBefore} → 保留 ${keptValue}（loss {DescribeLost(lost)}）" +
                      $" · 打捞费 ${feeApplied}/{fee}（cash {gm.Cash}）" +
                      $" · 装备/模块永久成长保留 · MaxDepthEver 不回退");
            NotifyChanged();
        }

        string BuildSummary(string reason, int maxDepth, LossLine[] lost, int keptValue, int feeApplied, int feeRaw)
        {
            string lostText = DescribeLost(lost);
            string feeText = feeRaw > 0
                ? (feeApplied < feeRaw ? $"打捞费 ${feeApplied}（现金不足，应付 ${feeRaw}）" : $"打捞费 ${feeApplied}")
                : "打捞费 $0";
            if (string.IsNullOrEmpty(lostText)) lostText = "无";
            return $"打捞完成（{reason}）· 损失货物 {lostText} · {feeText} · 保留货物价值 ${keptValue}";
        }

        static string DescribeLost(LossLine[] lost)
        {
            if (lost == null || lost.Length == 0) return "";
            var parts = new List<string>();
            foreach (var l in lost)
                parts.Add(l.def != null ? $"{l.def.displayName} ×{l.lost}" : $"×{l.lost}");
            return string.Join(" / ", parts.ToArray());
        }

        // ---------- 区域风险提示（HUD 展示，一次性） ----------

        void OnRegionFirstEntered(DepthRegionDefinition region)
        {
            // Mid(order2)/Deep(order3) 首次进入：轻量提示一次（不反复刷）
            if (region.order >= 2)
            {
                var gm = gameManager != null ? gameManager : GameManager.Instance;
                if (gm != null)
                    gm.LastServiceMessage = $"继续深入将提高未结算风险（当前未结算 ${PendingCargoValue}）";
            }
        }

        void NotifyChanged()
        {
            try { OnRunStateChanged?.Invoke(); }
            catch (Exception e) { Debug.LogWarning("[RunRiskState] OnRunStateChanged 订阅者异常：" + e.Message); }
        }
    }
}
