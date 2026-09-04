using System;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// 一个深度分层：从 startDepth 开始，按权重随机生成该层的方块。
    /// </summary>
    [Serializable]
    public class DepthLayer
    {
        [Tooltip("该层的起始深度（格，0 = 地表）")]
        public int startDepth;

        [Tooltip("该层可能出现的方块")]
        public TileDefinition[] tiles;

        [Tooltip("与 tiles 一一对应的权重（不需要归一化，随便填比例即可）")]
        public float[] weights;

        [Tooltip("该层生成天然空洞的概率 0~1")]
        [Range(0f, 1f)] public float caveChance = 0.03f;
    }

    /// <summary>
    /// 方块 / 矿物总数据库 + 分层生成配置。
    /// 创建方式：右键 Project 面板 → Create → Ashfall → Tile Database
    /// </summary>
    [CreateAssetMenu(fileName = "TileDatabase", menuName = "Ashfall/Tile Database", order = 2)]
    public class TileDatabase : ScriptableObject
    {
        [Header("必备引用")]
        [Tooltip("代表「已挖空」的方块（isSolid 应为 false）")]
        public TileDefinition emptyTile;

        [Tooltip("基岩：地图最底部与左右边界，不可挖")]
        public TileDefinition bedrockTile;

        [Header("分层配置（请按 startDepth 从小到大排列）")]
        public DepthLayer[] layers;

        /// <summary>根据深度返回对应分层（取 startDepth ≤ depth 的最后一层）</summary>
        public DepthLayer GetLayerAtDepth(int depth)
        {
            if (layers == null || layers.Length == 0) return null;

            DepthLayer result = layers[0];
            for (int i = 0; i < layers.Length; i++)
            {
                if (depth >= layers[i].startDepth) result = layers[i];
                else break;
            }
            return result;
        }

        /// <summary>
        /// DEV-007：在指定分层内按权重随机取一个【纯填充物】（value == 0 的格，即非矿物）。
        /// 矿脉模式下基础地层只生成填充物，矿物全部交给 OreVeinGenerator 以矿脉形式叠加，
        /// 避免「随机散点矿 + 矿脉」两套来源混淆（保证矿脉是矿物的唯一主要来源）。
        /// 若该层没有任何 value==0 的格（极端：全矿层），回落到 PickRandom 兜底，不返回 null。
        /// </summary>
        public TileDefinition PickStrata(DepthLayer layer)
        {
            if (layer == null || layer.tiles == null || layer.tiles.Length == 0)
                return emptyTile;

            // 统计非矿格与权重
            float total = 0f;
            bool anyFill = false;
            for (int i = 0; i < layer.tiles.Length; i++)
            {
                var t = layer.tiles[i];
                if (t == null || t.value > 0) continue;
                anyFill = true;
                float w = (layer.weights != null && i < layer.weights.Length) ? layer.weights[i] : 1f;
                total += Mathf.Max(0f, w);
            }
            if (!anyFill || total <= 0f) return PickRandom(layer);

            float roll = UnityEngine.Random.Range(0f, total);
            for (int i = 0; i < layer.tiles.Length; i++)
            {
                var t = layer.tiles[i];
                if (t == null || t.value > 0) continue;
                float w = (layer.weights != null && i < layer.weights.Length) ? layer.weights[i] : 1f;
                roll -= Mathf.Max(0f, w);
                if (roll <= 0f) return t;
            }
            // 兜底：返回第一个非矿格
            for (int i = 0; i < layer.tiles.Length; i++)
            {
                var t = layer.tiles[i];
                if (t != null && t.value == 0) return t;
            }
            return layer.tiles[layer.tiles.Length - 1];
        }

        /// <summary>在指定分层内按权重随机取一个方块</summary>
        public TileDefinition PickRandom(DepthLayer layer)
        {
            if (layer == null || layer.tiles == null || layer.tiles.Length == 0)
                return emptyTile;

            float total = 0f;
            for (int i = 0; i < layer.tiles.Length; i++)
            {
                float w = (layer.weights != null && i < layer.weights.Length) ? layer.weights[i] : 1f;
                total += Mathf.Max(0f, w);
            }
            if (total <= 0f) return layer.tiles[0];

            float roll = UnityEngine.Random.Range(0f, total);
            for (int i = 0; i < layer.tiles.Length; i++)
            {
                float w = (layer.weights != null && i < layer.weights.Length) ? layer.weights[i] : 1f;
                roll -= Mathf.Max(0f, w);
                if (roll <= 0f) return layer.tiles[i];
            }
            return layer.tiles[layer.tiles.Length - 1];
        }
    }
}
