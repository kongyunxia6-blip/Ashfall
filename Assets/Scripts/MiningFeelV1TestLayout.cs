using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-003：MiningFeelV1Test 场景的运行时布局重放器。
    ///
    /// 为什么需要：与 DEV-001 的 BlockV1TestLayout 同理 —— Builder 在 Editor 下做的 SetTile
    /// 只改 DigGrid 内存数组，Play 时 Awake→Generate() 会随机覆盖；布局必须在 Play 后重放。
    ///
    /// 布局（十字矿脉 + 深竖井，全部实例级耐久覆盖，不污染共享 SO）：
    ///  - 玩家锚定格 (centerX, anchorY)，站在竖井顶部的铁矿上；
    ///  - 竖井：正下方 columnLength 格铁矿（首格耐久 4 验证完整受击链，其余耐久 1 快速下挖 ≥10 格）；
    ///  - 左臂：4 格铁矿耐久 4（验证 4 击链 / 事件计数 / 4 方向单选）；
    ///  - 右臂：3 格泥土耐久 1（1 击崩碎；库中无普通岩，泥土 hardness=1 起始可挖）;
    ///  - 上方：1 格铁矿耐久 4（验证向上单格）；
    ///  - 玩家周身口袋清空（上方斜角两格 + 头顶再上一格），防止出生即被随机地形卡住。
    /// </summary>
    public class MiningFeelV1TestLayout : MonoBehaviour
    {
        [Tooltip("要覆盖的 DigGrid（默认取同一物体上的）")]
        public DigGrid grid;

        [Header("布局")]
        [Tooltip("玩家锚定行（grid y）。出生点 = 该行中心格")]
        public int anchorY = 6;
        [Tooltip("竖井深度（格）。≥10 用于「向下连续挖 ≥10 格」验收")]
        public int columnLength = 14;
        [Tooltip("左臂长度（格），铁矿耐久 4")]
        public int leftArmLength = 4;
        [Tooltip("右臂长度（格），普通岩耐久 1")]
        public int rightArmLength = 3;

        [Header("实例级耐久（Play 时生效，不修改共享 SO）")]
        public int ironDigHits = 4;
        public int rockDigHits = 1;

        /// <summary>玩家锚定格 X（场景中心列）。Builder 与测试脚本都读它。</summary>
        public int CenterX => grid != null ? grid.Width / 2 : 24;

        void Start()
        {
            if (grid == null) grid = GetComponent<DigGrid>();
            if (grid == null || grid.database == null)
            {
                Debug.LogWarning("[DEV-003] MiningFeelV1TestLayout: 未找到 DigGrid 或 database，跳过布局");
                return;
            }

            var iron = FindByDisplayName("Iron_铁矿");
            // 注意：库中没有「普通岩」。起始钻头 Lv1 只能挖 hardness≤1 的格
            // （HardRock_硬岩 hardness=3 挖不动，曾致右臂 HardnessLow）。
            // 右臂用 Dirt_泥土（hardness=1）承担「1 击崩碎 + 向右单选」验证。
            var rock = FindByDisplayName("Dirt_泥土");
            var empty = grid.database.emptyTile;
            if (iron == null || rock == null || empty == null)
            {
                Debug.LogWarning("[DEV-003] MiningFeelV1TestLayout: 找不到 Iron_铁矿 / HardRock_硬岩 / empty，跳过布局");
                return;
            }

            int cx = CenterX;
            int ay = anchorY;
            int filled = 0;

            // 1) 玩家周身口袋清空：锚定格本身 + 头顶两格 + 上斜两格（up 目标格除外，下面单独放）。
            //    锚定格必须清空——随机地形若在此放实心格，玩家会出生即卡进墙里被顶上up目标格（实测踩坑）。
            //    注意：左右相邻格不能清——它们是左右臂的首个挖掘目标格。
            grid.SetTile(cx, ay, empty);
            grid.SetTile(cx - 1, ay - 1, empty);
            grid.SetTile(cx + 1, ay - 1, empty);
            grid.SetTile(cx - 1, ay - 2, empty);
            grid.SetTile(cx, ay - 2, empty);
            grid.SetTile(cx + 1, ay - 2, empty);

            // 2) 上方目标：1 格铁矿耐久 4（验证向上单格挖掘）
            grid.SetTile(cx, ay - 1, iron, ironDigHits); filled++;

            // 3) 左臂：4 格铁矿耐久 4（验证 4 击链 / 事件计数 / 向左单选）
            for (int i = 1; i <= leftArmLength; i++)
            {
                grid.SetTile(cx - i, ay, iron, ironDigHits);
                filled++;
            }

            // 4) 右臂：3 格泥土耐久 1（1 击崩碎 + 向右单选；硬度1 起始钻头可挖）
            for (int i = 1; i <= rightArmLength; i++)
            {
                grid.SetTile(cx + i, ay, rock, rockDigHits);
                filled++;
            }

            // 5) 竖井：正下方 columnLength 格铁矿。
            //    首格耐久 4（验证向下 4 击链），其余耐久 1（快速连挖 ≥10 格验证目标稳定/不卡死）。
            for (int i = 1; i <= columnLength; i++)
            {
                int dur = (i == 1) ? ironDigHits : 1;
                grid.SetTile(cx, ay + i, iron, dur);
                filled++;
            }

            Debug.Log($"[DEV-003] MiningFeelV1TestLayout: 布局已应用（{filled} 格：上1 + 左{leftArmLength} + " +
                      $"右{rightArmLength} + 竖井{columnLength}，锚定 ({cx},{ay})；未修改共享 SO）");
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
