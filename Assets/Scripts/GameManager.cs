using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// 总控：现金、最深纪录、重生、地表状态。
    /// 挂在哪：场景中的空物体（UpgradeSystem 也挂在同一物体上）。
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        [Header("经济")]
        public int startingCash = 100;

        [Tooltip("每点燃料的价格")]
        public float fuelPricePerUnit = 0.4f;

        [Tooltip("每点船体的维修价格")]
        public float repairPricePerPoint = 1.5f;

        [Header("死亡惩罚（M1）")]
        [Tooltip("打捞费 = min(现金 × 本比例, 基础费 + 死亡深度 × 深度系数)。取 min 是为了让深处惩罚温和——太重玩家就不去回捞了")]
        [Range(0f, 0.5f)] public float salvageFeeCashRatio = 0.15f;
        public float salvageFeeBase = 100f;
        public float salvageFeePerDepth = 2f;

        [Header("流程")]
        public float respawnDelay = 2.5f;

        [Tooltip("重生点。勾选 usePlayerStartAsSpawn 时会被玩家初始位置覆盖")]
        public Vector3 spawnPoint = new Vector3(24f, 1f, 0f);

        public bool usePlayerStartAsSpawn = true;

        // ---------- 运行时 ----------
        public int Cash { get; private set; }
        public int MaxDepthReached { get; set; }
        public UpgradeSystem Upgrades { get; private set; }
        public EquipmentProgression Equipment { get; private set; }   // DEV-010：装备成长权威（可空 → 旧场景走 UpgradeSystem）
        public RunRiskState RunRisk { get; private set; }             // DEV-011：风险撤离/Run 生命周期权威（可空 → 旧场景走 M1 死亡结算）
        public DrillVehicle Player { get; set; }
        public bool IsAtSurface { get; set; }
        public string LastServiceMessage { get; set; } = "";

        void Awake()
        {
            Instance = this;
            Upgrades = GetComponent<UpgradeSystem>();
            Equipment = GetComponent<EquipmentProgression>();
            RunRisk = GetComponent<RunRiskState>();
            Cash = startingCash;

            if (Upgrades == null && Equipment == null)
                Debug.LogWarning("[GameManager] 同一物体上未找到 UpgradeSystem / EquipmentProgression，升级功能将不可用。");
        }

        public void AddCash(int amount) => Cash += amount;
        public void SpendCash(int amount) => Cash = Mathf.Max(0, Cash - amount);

        /// <summary>玩家注册（DrillVehicle.Start 调用）</summary>
        public void RegisterPlayer(DrillVehicle player, Vector3 startPosition)
        {
            Player = player;
            if (usePlayerStartAsSpawn) spawnPoint = startPosition;
        }

        /// <summary>
        /// 死亡结算（统一入口，DrillVehicle.Die 只调用一次；幂等由 RunRiskState guard 保证）。
        ///  - RunRiskState 存在（DEV-011 场景）：Cargo 按规则部分保留（每 def floor(count×keep)）、
        ///    扣 Recovery Fee（Cash 最多扣到 0）、生成 Failure Summary —— 一切由 RunRisk 权威结算；
        ///  - 否则（旧场景，零回归）：M1 旧三层轻惩罚（载荷全清 + 旧打捞费 + 部分恢复重生）。
        /// respawn 由本方法统一 Invoke；Respawn 时按是否有 RunRisk 决定是否再清空货舱
        /// （有 RunRisk → 保留结算后存货，供玩家回地表 Sell；无 → 旧全清行为）。
        /// 【堵住的漏洞】旧版死亡只清货舱、不扣现金且满状态重生，于是最优解是
        /// 回程时主动把燃料烧光 = 免费传送回地表。现在主动自杀要多付打捞费 + 补油 + 等 3 分钟。
        /// </summary>
        public void OnPlayerDied(string reason, int deathDepth)
        {
            string msg;
            if (RunRisk != null)
            {
                RunRisk.ResolveFailure(reason, deathDepth);
                msg = RunRisk.LastFailureSummary;
                Debug.Log($"[GameManager] 任务失败：{reason} → RunRiskState 已结算（Cargo 部分保留 + Recovery Fee），" +
                          $"{respawnDelay} 秒后于地表重生；摘要：{msg}");
            }
            else
            {
                int cargoLost = Player != null ? Player.CargoValue : 0;
                int fee = Mathf.Min(
                    Mathf.RoundToInt(Cash * salvageFeeCashRatio),
                    Mathf.RoundToInt(salvageFeeBase + deathDepth * salvageFeePerDepth));
                SpendCash(fee);
                msg = $"服体失效 · 深度 {deathDepth} · 损失载荷 ¥{cargoLost} · 打捞费 ¥{fee}";
                Debug.Log($"[GameManager] 任务失败：{reason} → 深度 {deathDepth}，损失载荷 ¥{cargoLost}，" +
                          $"打捞费 ¥{fee}，{respawnDelay} 秒后于地表重生");
            }

            LastServiceMessage = msg;
            Invoke(nameof(Respawn), respawnDelay);
        }

        void Respawn()
        {
            if (Player == null) return;
            // DEV-011：有 RunRisk 时货舱损失已在 ResolveFailure 按规则执行，重生保留存活货物（回地表可 Sell）；
            // 旧场景（无 RunRisk）维持 M1 全清行为。
            bool keepCargo = RunRisk != null;
            Player.RespawnAtSurface(spawnPoint, clearCargo: !keepCargo);
            if (RunRisk != null) RunRisk.OnRespawned();
        }
    }
}
