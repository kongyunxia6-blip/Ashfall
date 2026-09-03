using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-001：BlockV1Test 场景的运行时布局。
    ///
    /// 为什么需要这个组件：
    ///  BlockV1Builder 在 Editor 场景下做的 SetTile 只改 DigGrid 内存里的 grid 数组，
    ///  但 Play 时 DigGrid.Awake→Generate() 会重新随机生成，把精确布局覆盖掉。
    ///  因此布局必须在 Play 模式下、Awake 之后再次应用。本组件用 Start()（Awake 之后）做这件事。
    ///
    /// 挂在 DigGrid 同一物体上，Start() 时：
    ///  1. 用 SetTile 的 durability 参数做「实例级耐久覆盖」：铁矿耐久=ironDigHits、普通岩=rockDigHits
    ///  2. 把 startY~startY+LayoutHeight-1、startX~startX+LayoutWidth-1 填成 5:1 混合布局
    ///
    /// 重要：本组件**不再**在 Play 期修改共享 TileDefinition ScriptableObject 的 digHits/dropId——
    /// 测试耐久通过 SetTile 的实例级覆盖传入，避免主场景共享资产被运行时副作用污染。
    /// Editor 场景下不做事（Builder 已经填好了）。
    /// </summary>
    public class BlockV1TestLayout : MonoBehaviour
    {
        [Tooltip("要覆盖的 DigGrid（默认取同一物体上的）")]
        public DigGrid grid;

        [Header("布局")]
        public int layoutWidth = 12;
        public int layoutHeight = 4;
        public int startY = 5;
        [Tooltip("铁矿占比 = 5/6，普通岩占 1/6")]
        public int ironEveryN = 6;

        [Header("实例级耐久（Play 时生效，不修改共享 SO）")]
        [Tooltip("铁矿每格耐久（测试 6 态需要 ≥4）")]
        public int ironDigHits = 4;
        [Tooltip("普通岩每格耐久（1 击崩碎）")]
        public int rockDigHits = 1;

        TileDefinition ironDef;
        TileDefinition rockDef;

        void Start()
        {
            if (grid == null) grid = GetComponent<DigGrid>();
            if (grid == null || grid.database == null)
            {
                Debug.LogWarning("[DEV-001] BlockV1TestLayout: 未找到 DigGrid 或 database，跳过布局");
                return;
            }

            // 从 database 找 Iron 和 HardRock 定义
            ironDef = FindByDisplayName("Iron_铁矿");
            rockDef = FindByDisplayName("HardRock_硬岩");

            if (ironDef == null || rockDef == null)
            {
                Debug.LogWarning("[DEV-001] BlockV1TestLayout: 未找到 Iron_铁矿 / HardRock_硬岩 定义，跳过布局");
                return;
            }

            // 注意：不改共享 SO（ironDef.digHits / dropId）。测试耐久用 SetTile 的 durability 参数
            // 作为「实例级覆盖」传入，避免主场景共享资产被运行时副作用污染。

            int startX = grid.Width / 2 - layoutWidth / 2;
            int filled = 0;
            for (int ly = 0; ly < layoutHeight; ly++)
            {
                for (int lx = 0; lx < layoutWidth; lx++)
                {
                    int x = startX + lx;
                    int y = startY + ly;
                    if (!grid.InBounds(x, y)) continue;

                    bool isIron = (lx + ly * layoutWidth) % ironEveryN != (ironEveryN - 1);
                    if (isIron)
                        grid.SetTile(x, y, ironDef, ironDigHits);
                    else
                        grid.SetTile(x, y, rockDef, rockDigHits);
                    filled++;
                }
            }

            Debug.Log($"[DEV-001] BlockV1TestLayout: 布局已应用（{filled} 格，y={startY}~{startY + layoutHeight - 1}，" +
                      $"iron 耐久={ironDigHits}，rock 耐久={rockDigHits}；未修改共享 SO）");
        }

        TileDefinition FindByDisplayName(string name)
        {
            if (grid.database == null || grid.database.layers == null) return null;
            foreach (var layer in grid.database.layers)
            {
                if (layer?.tiles == null) continue;
                foreach (var tile in layer.tiles)
                    if (tile != null && tile.displayName == name) return tile;
            }
            return null;
        }
    }
}
