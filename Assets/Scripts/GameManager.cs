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
        public DrillVehicle Player { get; set; }
        public bool IsAtSurface { get; set; }
        public string LastServiceMessage { get; set; } = "";

        void Awake()
        {
            Instance = this;
            Upgrades = GetComponent<UpgradeSystem>();
            Cash = startingCash;

            if (Upgrades == null)
                Debug.LogWarning("[GameManager] 同一物体上未找到 UpgradeSystem，升级功能将不可用。");
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
        /// 死亡结算。三层轻惩罚：① 载荷全部损失 ② 现金扣打捞费 ③ 重生只恢复部分能源/服体（见 DrillVehicle）。
        /// 【堵住的漏洞】原来死亡只清货舱、不扣现金且满状态重生，于是最优解是
        /// 回程时主动把燃料烧光 = 免费传送回地表。现在主动自杀要多付打捞费 + 补油 + 等 3 分钟。
        /// </summary>
        public void OnPlayerDied(string reason, int deathDepth)
        {
            int cargoLost = Player != null ? Player.CargoValue : 0;

            int fee = Mathf.Min(
                Mathf.RoundToInt(Cash * salvageFeeCashRatio),
                Mathf.RoundToInt(salvageFeeBase + deathDepth * salvageFeePerDepth));
            SpendCash(fee);

            LastServiceMessage = $"服体失效 · 深度 {deathDepth} · 损失载荷 ¥{cargoLost} · 打捞费 ¥{fee}";

            Debug.Log($"[GameManager] 任务失败：{reason} → 深度 {deathDepth}，损失载荷 ¥{cargoLost}，" +
                      $"打捞费 ¥{fee}，{respawnDelay} 秒后于地表重生");

            Invoke(nameof(Respawn), respawnDelay);
        }

        void Respawn()
        {
            if (Player == null) return;
            Player.RespawnAtSurface(spawnPoint);
        }
    }
}
