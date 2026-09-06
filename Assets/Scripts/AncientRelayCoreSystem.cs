using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-013：古代中继核心（AncientRelayCore）运行时交互（Issue §8）。
    ///
    /// 职责：当玩家命中房间内的 AncientRelayCore（装置点）时触发「调查」：
    ///  - 首次交互：标记该遗迹 investigated + 给出一次性文明资源奖励
    ///    （AncientDataFragment ×1 + 少量 AncientAlloy，依 RuinDefinition.rewardProfile）；
    ///  - 二次交互：只提示「已调查过」，不重复发首奖（幂等，经 RuinDiscoveryService.investigated）；
    ///  - 奖励统一进入现有 Inventory（DrillVehicle.Inventory.AddItem），走既有 Cargo 风险体系；
    ///    不绕过 Sell / RunRiskState；
    ///  - 本系统【不 HitBlock / 不崩碎 / 不消失】Core —— Core 是交互装置，不是可采矿；
    ///  - 死亡/返航不重置 investigated（防无限刷首奖）；只读查询由 RuinDiscoveryService 承载。
    ///
    /// 挂载：任意物体（推荐与 DigGrid 或 RuinGenerator 同物体），需引用 RuinDiscoveryService。
    /// 由 DrillVehicle 在命中 AncientRelayCore（RelayInteract hook）时转调 TryRelayInteract。
    /// </summary>
    public class AncientRelayCoreSystem : MonoBehaviour
    {
        [Tooltip("发现/调查状态唯一真相源（必填；Core 幂等依赖它）。")]
        public RuinDiscoveryService discovery;

        [Header("文明资源 Tile（Builder 注入真实资产；null 时该项奖励跳过）")]
        [Tooltip("古代数据碎片（AncientDataFragment）。")]
        public TileDefinition ancientDataTile;
        [Tooltip("古代合金（OreCatalog.AncientAlloy）。")]
        public TileDefinition ancientAlloyTile;

        /// <summary>最近一次交互结果（验收读取）。</summary>
        public string LastVerdict { get; private set; } = "none";

        /// <summary>最近一次实际发放的奖励简述（验收读取）。</summary>
        public string LastRewardText { get; private set; } = "";

        /// <summary>
        /// 玩家命中某格触发交互（DrillVehicle 在 RelayInteract hook 命中时调用）。
        /// 返回 true = 已由本系统处理（不 HitBlock）；false = 无对应核心/系统缺失 → 落回普通挖掘。
        /// </summary>
        public bool TryRelayInteract(Vector2Int cell, DrillVehicle vehicle)
        {
            if (discovery == null)
            {
                LastVerdict = "no_discovery_service";
                return false;
            }
            var inst = discovery.FindByCoreCell(cell);
            if (inst == null)
            {
                LastVerdict = "no_matching_core";
                return false;
            }

            if (discovery.IsInvestigated(inst.instanceId))
            {
                LastVerdict = "already_investigated";
                LastRewardText = "";
                PostMessage(inst, "这座古代中继核心已被调查过，内部文明能量已枯竭，没有更多发现。");
                return true;
            }

            // 首次调查：发一次性奖励。Blocker3：采用「先容量预检 → 全量发奖 → 才标 investigated」原子语义。
            // 背包不足（满/部分容量）→ 不标 investigated、不全发、不清空 —— 玩家腾空间后可重试，绝不静默吞/丢首奖。
            var profile = inst.definition != null ? inst.definition.rewardProfile : null;
            if (vehicle == null || vehicle.Inventory == null || profile == null)
            {
                LastVerdict = "reward_blocked_no_inventory";
                LastRewardText = "";
                PostMessage(inst, $"调查「{inst.definition.displayName}」，但载具数据链路异常，无法接收文明资源。请稍后再试。");
                return true;
            }

            var inv = vehicle.Inventory;
            // 预检：rewardProfile 每一项都要能【完整】装下
            bool canFitAll = true;
            for (int i = 0; i < profile.Length; i++)
            {
                var tile = ResolveRewardTile(profile[i].stableId);
                if (tile == null) continue;
                if (!inv.CanAcceptFull(tile, profile[i].count)) { canFitAll = false; break; }
            }

            if (!canFitAll)
            {
                // 容量不足：不标 investigated，不部分发放；给明确提示，玩家腾空间后回来重试。
                LastVerdict = "reward_blocked_no_cargo_space";
                LastRewardText = "";
                PostMessage(inst, $"调查「{inst.definition.displayName}」需要先腾出背包空间（文明资源将完整发放），当前载重/格子不足。请卸下部分货物后再来调查。");
                return true;
            }

            // 全量发奖（预检已保证全部装下，AddItem 返回 0）
            string got = "";
            for (int i = 0; i < profile.Length; i++)
            {
                var spec = profile[i];
                var tile = ResolveRewardTile(spec.stableId);
                if (tile == null) continue;
                int left = inv.AddItem(tile, spec.count);
                int accepted = Mathf.Max(0, spec.count - left);
                if (accepted > 0)
                    got += (got.Length > 0 ? "、" : "") + tile.displayName + "×" + accepted;
            }

            // 全量发放成功后才标记 investigated（幂等基准）
            discovery.MarkInvestigated(inst.instanceId);
            LastVerdict = "investigated_first_time";
            LastRewardText = got;
            PostMessage(inst, got.Length > 0
                ? $"调查「{inst.definition.displayName}」，提取古代文明数据，获得：{got}。"
                : $"调查「{inst.definition.displayName}」，未提取到有效资源。");
            return true;
        }

        /// <summary>依 stableId 解析真实奖励 Tile（缺字段 → null，该项奖励跳过）。</summary>
        TileDefinition ResolveRewardTile(string stableId)
        {
            switch (stableId)
            {
                case RuinCatalog.AncientDataFragment: return ancientDataTile;
                case OreCatalog.AncientAlloy:         return ancientAlloyTile;
                default: return null;
            }
        }

        void PostMessage(RuinInstance inst, string msg)
        {
            var gm = GameManager.Instance;
            if (gm != null) gm.LastServiceMessage = msg;
            Debug.Log($"[DEV-013] {msg}");
        }
    }
}
