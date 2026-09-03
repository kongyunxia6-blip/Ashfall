using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-001：通用掉落派发器（由 IronOreDropHook 改名而来，去掉矿种专属逻辑）。
    ///
    /// 职责：
    ///  - 监听 DigGrid.OnTileDug（方块被挖穿/移除事件），读取 TileDefinition.dropId（通用掉落标识）；
    ///  - dropId 非空 → 派发给对应掉落逻辑（V1 仅打日志占位，不改游戏数值）；
    ///  - dropId 为空 → 不做事（该方块走 DrillVehicle.HandleTileDug 的默认入包流程）。
    ///
    /// 设计要点：核心 Block 定义只携带一个通用 dropId 字符串，不感知矿种；
    ///   具体「掉什么 / 掉几个 / 概率 / 伴生物」由本掉落系统（或未来 drop table 资产）按 id 查表决定，
    ///   新增矿种不需要修改 DigGrid / DrillVehicle / TileDefinition 的核心挖掘逻辑。
    ///
    /// 挂载位置：任意 MonoBehaviour（推荐 GameManager 或 DigGrid 同一物体）。
    /// 拖拽 grid 引用（DigGrid），运行时自动订阅 OnTileDug。
    /// </summary>
    public class BlockDropHook : MonoBehaviour
    {
        [Tooltip("要监听的 DigGrid（挖穿事件来源）")]
        public DigGrid grid;

        void OnEnable()
        {
            if (grid == null) grid = FindFirstObjectByType<DigGrid>();
            if (grid == null)
            {
                Debug.LogWarning("[DEV-001] BlockDropHook: 未找到 DigGrid，无法监听 OnTileDug");
                return;
            }
            grid.OnTileDug += OnBlockDug;
        }

        void OnDisable()
        {
            if (grid != null) grid.OnTileDug -= OnBlockDug;
        }

        void OnBlockDug(Vector2Int cell, TileDefinition def)
        {
            if (def == null || string.IsNullOrEmpty(def.dropId)) return;

            // V1：预留掉落派发——仅日志，不改游戏数值。
            // 未来在这里扩展：按 def.dropId 查 drop table（掉什么/掉几个/概率/伴生矿），
            // 或转交给独立掉落系统；核心挖掘逻辑（DigGrid/DrillVehicle/TileDefinition）保持不变。
            Debug.Log($"[DEV-001] Block dug @ {cell} (dropId=\"{def.dropId}\"). " +
                      $"Future: dispatch custom drop via drop table here.");
        }
    }
}
