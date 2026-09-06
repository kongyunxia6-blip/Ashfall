using System.Collections.Generic;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-013：遗迹生成器（Issue §5）。确定性薄层，负责把小型人工结构落到现有 DigGrid。
    ///
    /// 设计：
    ///  - 只依附/写入现有 DigGrid（SetTile），不建第二套 Grid；
    ///  - 不接管 OreVeinGenerator / UndergroundSpaceGenerator —— 由 DigGrid.Generate 在
    ///    vein pass 之后以明确顺序调用（Base → Space → Vein → Ruin 后置覆盖）；
    ///  - 固定 seed（grid.seed 或 seedOverride）→ 确定性布局；
    ///  - 选合法候选区：仅放入 definition.allowedRegionId 的深度带，避开基岩边 / Surface 坑口 /
    ///    边界；footprint 不越界；
    ///  - 写入人工结构 Block/标记：四周人工墙、房间内腔挖空、入口 RuinSeal、核心 AncientRelayCore、
    ///    少量奖励矿节点；
    ///  - 登记 RuinInstance（只读）供 RuinDiscoveryService / AncientRelayCore / 验收查询。
    ///
    /// tile 资产引用由场景 Builder 注入（wall/seal/core/reward 真实 .asset）；缺失时回退安全策略（不生成）。
    /// 生成后 Scanner / Discovery 只读，绝不由本类或扫描改动世界。
    /// </summary>
    public class RuinGenerator : MonoBehaviour
    {
        [Tooltip("目标 DigGrid（常与挂载物体相同）。")]
        public DigGrid grid;

        [Tooltip("-1 = 跟随 grid.seed；否则用本覆盖值（确定性测试）。")]
        public int seedOverride = -1;

        [Header("人工结构 tile（Builder 注入真实资产）")]
        [Tooltip("人工墙（破损金属/石质复合墙，占位色）。null → 中止生成。")]
        public TileDefinition wallTile;
        [Tooltip("入口封印（blockType=RuinSeal）。null → 中止生成。")]
        public TileDefinition sealTile;
        [Tooltip("中继核心（blockType=AncientRelayCore）。null → 中止生成。")]
        public TileDefinition coreTile;
        [Tooltip("房间内奖励矿（古代合金等）。null → 不铺奖励节点。")]
        public TileDefinition rewardTile;

        [Tooltip("每轮最多尝试多少个候选锚点后放弃（防死循环）。")]
        public int maxPlacementAttempts = 64;

        /// <summary>本 pass 生成的遗迹（只读布局；Scanner/Discovery 查询）。</summary>
        public readonly List<RuinInstance> Instances = new List<RuinInstance>();

        /// <summary>本次实际使用的 seed（确定性验证用）。</summary>
        public int EffectiveSeed { get; private set; }

        /// <summary>是否登记了至少一座遗迹。</summary>
        public bool HasInstances => Instances.Count > 0;

        /// <summary>最近一次 Run 的结果（ok / reason），验收读取。</summary>
        public string LastVerdict { get; private set; } = "none";

        /// <summary>清空上一次生成结果（供 Regenerate 前调用）。</summary>
        public void ClearInstances() => Instances.Clear();

        /// <summary>当前 grid 的 seed（seedOverride 优先）。</summary>
        public int ResolveSeed()
        {
            if (seedOverride >= 0) return seedOverride;
            return grid != null ? grid.seed : 20260906;
        }

        /// <summary>
        /// 执行遗迹生成 pass。返回成功（登记了 ≥1 座）。由 DigGrid.Generate 在 vein pass 之后调用。
        /// 确定性：相同 seed + 相同输入 → 相同布局。
        /// </summary>
        public bool ApplyToGrid(DigGrid target)
        {
            grid = target;
            Instances.Clear();
            if (grid == null || grid.database == null)
            {
                LastVerdict = "fail:no_grid_or_db";
                return false;
            }
            if (wallTile == null || sealTile == null || coreTile == null)
            {
                LastVerdict = "fail:missing_tiles";
                return false;
            }

            EffectiveSeed = ResolveSeed();
            var rng = new System.Random(EffectiveSeed);

            var def = RuinCatalog.AncientRelay;
            var region = FindRegion(def.allowedRegionId);
            if (region == null)
            {
                LastVerdict = "fail:no_region_" + def.allowedRegionId;
                return false;
            }

            // 可选锚点：在 region 的深度带内随机 x0/y0，循环找合法位。
            int w = def.footprintWidth, h = def.footprintHeight;
            int centerX = grid.width / 2;
            bool placed = false;
            for (int attempt = 0; attempt < maxPlacementAttempts && !placed; attempt++)
            {
                int x0 = rng.Next(2, Mathf.Max(3, grid.width - 2 - w));
                // y0：顶部留至少 1 行实心便于上行返回；底部留 ≥2 行不碰 bedrock。
                int yMin = region.minDepth;
                int yMax = Mathf.Min(region.maxDepth, grid.depth - 2 - h);
                if (yMax < yMin) break;
                int y0 = yMin + rng.Next(0, Mathf.Max(1, yMax - yMin + 1));

                if (!IsLegalAnchor(x0, y0, w, h, centerX)) continue;
                placed = true;

                // 确定性写入房间（fixed interior 形状见 PlaceRoom）
                var inst = new RuinInstance
                {
                    definition = def,
                    instanceId = $"Ruin_{def.stableId}_x{x0}_y{y0}",
                    usedSeed = EffectiveSeed,
                };
                inst.bounds = new RectInt(x0, y0, w, h);
                PlaceRoom(x0, y0, w, h, def, inst);
                Instances.Add(inst);
            }

            LastVerdict = placed ? $"ok:{Instances.Count}" : "fail:no_legal_anchor";
            return placed;
        }

        /// <summary>房间布局锚点合法性：不碰基岩左右边、不盖 Surface 坑口、不越界。</summary>
        bool IsLegalAnchor(int x0, int y0, int w, int h, int centerX)
        {
            // 左右留 ≥1 格内缩（不覆盖 x==0 / width-1 基岩列）
            if (x0 < 1 || x0 + w - 1 > grid.width - 2) return false;
            if (y0 < 1 || y0 + h - 1 > grid.depth - 2) return false;
            // 房间横向范围不得覆盖地表坑口列（玩家初始下潜竖井），避免出生即见遗迹
            int halfOpening = grid.surfaceOpeningHalfWidth;
            int openingXmin = centerX - halfOpening - 2, openingXmax = centerX + halfOpening + 2;
            if (x0 <= openingXmax && x0 + w - 1 >= openingXmin) return false;
            return true;
        }

        /// <summary>固定把一座 AncientRelayRoom 布局写入 grid（8×4：四周墙 + 6×2 内腔 + seal/core/reward）。</summary>
        void PlaceRoom(int x0, int y0, int w, int h, RuinDefinition def, RuinInstance inst)
        {
            var db = grid.database;
            var empty = db.emptyTile;
            int lastX = x0 + w - 1, lastY = y0 + h - 1;

            // 四周人工墙（先整块铺墙，再挖内腔与门）
            for (int y = y0; y <= lastY; y++)
                for (int x = x0; x <= lastX; x++)
                    grid.SetTile(x, y, wallTile);

            // 内腔：挖空中间 6×2（x0+1..lastX-1，y0+1..lastY-1 若高>3）
            // 通用化：内腔 = 去掉 4 周一圈后的空心；但 AncientRelayRoom 8×4 → 内腔 6×2 过高只有 2 行，
            // 我们保留 y0+1 与 y0+2 两行为内腔，顶部/底部墙各 1 行，左右墙各 1 列。
            for (int y = y0 + 1; y <= lastY - 1; y++)
            {
                for (int x = x0 + 1; x <= lastX - 1; x++)
                {
                    grid.SetTile(x, y, empty);
                    inst.interiorCells.Add(new Vector2Int(x, y));
                }
            }

            // 核心：放在内腔靠内底行（x0+2, y0+2）
            int coreX = x0 + 2, coreY = y0 + 2;
            grid.SetTile(coreX, coreY, coreTile);
            inst.coreCell = new Vector2Int(coreX, coreY);

            // 奖励节点：放在内腔右侧（x0+5, y0+1）若在腔内
            if (rewardTile != null && x0 + 5 <= lastX - 1)
            {
                int rx = x0 + 5, ry = y0 + 1;
                grid.SetTile(rx, ry, rewardTile);
                inst.rewardCells.Add(new Vector2Int(rx, ry));
            }

            // 入口 RuinSeal：房间右墙、内腔同高行（lastX, y0+1）—— Seal 左侧紧邻内腔。
            int sx = lastX, sy = y0 + 1;
            grid.SetTile(sx, sy, sealTile);
            inst.entranceCell = new Vector2Int(sx, sy);

            // 校正 interiorCells 不含 core/reward 覆盖格（core/reward 是实体，不应算可通行）
            inst.interiorCells.RemoveAll(c => c.x == coreX && c.y == coreY);
            if (rewardTile != null) inst.interiorCells.RemoveAll(c => c.x == x0 + 5 && c.y == y0 + 1);
        }

        static DepthRegionDefinition FindRegion(string regionId)
        {
            foreach (var r in DepthRegionLayout.All)
                if (r.regionId == regionId) return r;
            return null;
        }
    }
}
