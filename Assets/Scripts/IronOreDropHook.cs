using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-001：铁矿专属掉落事件预留钩子。
    ///
    /// 职责：
    ///  - 监听 DigGrid.OnTileDug（崩碎事件），只对 TileDefinition.dropsIronOre=true 的方块打 DEV-001 日志；
    ///  - 未来 IronOre 需要走特殊掉落路径（掉"铁矿残块"、掉地上、数量波动、伴生物）时在这里扩展，
    ///    不需要修改玩家挖掘核心逻辑（DrillVehicle / DigGrid 不变）。
    ///
    /// 挂载位置：任意 MonoBehaviour 场景物体（推荐挂在 GameManager 或 DigGrid 同一物体）。
    /// 拖拽 grid 引用（DigGrid），运行时自动订阅 OnTileDug。
    ///
    /// V1 行为：只打日志（Dev001 标记），不改变游戏数值——背包添加仍走 DrillVehicle.HandleTileDug。
    /// </summary>
    public class IronOreDropHook : MonoBehaviour
    {
        [Tooltip("要监听的 DigGrid（崩碎事件来源）")]
        public DigGrid grid;

        void OnEnable()
        {
            if (grid == null) grid = FindFirstObjectByType<DigGrid>();
            if (grid == null)
            {
                Debug.LogWarning("[DEV-001] IronOreDropHook: 未找到 DigGrid，无法监听 OnTileDug");
                return;
            }
            grid.OnTileDug += OnBlockShattered;
        }

        void OnDisable()
        {
            if (grid != null) grid.OnTileDug -= OnBlockShattered;
        }

        void OnBlockShattered(Vector2Int cell, TileDefinition def)
        {
            if (def == null || !def.dropsIronOre) return;

            // V1：预留掉落事件——仅日志，不改游戏数值。
            // 未来在这里扩展：掉"铁矿残块"、掉地上、数量波动、伴生矿等。
            Debug.Log($"[DEV-001] IronOre shattered @ {cell} (dropsIronOre=true). " +
                      $"Future: custom drop path goes here.");
        }
    }
}
