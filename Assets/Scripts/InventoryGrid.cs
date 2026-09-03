using System;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// 背包中的一格。
    /// 主槽（isPrimary）持有真实数量；大件物品（gridWidth &gt; 1）会额外占用右侧若干格，
    /// 那些附属槽 isPrimary=false 且用 ownerIndex 指回主槽，只用于占位，不重复计数。
    /// </summary>
    [Serializable]
    public class InventorySlot
    {
        public TileDefinition def;
        public int count;

        /// <summary>true = 本格是物品起点（有数量）；false = 被左侧大件占用</summary>
        public bool isPrimary = true;

        /// <summary>附属槽指回主槽下标；主槽为 -1</summary>
        public int ownerIndex = -1;

        public bool IsEmpty => def == null || count <= 0;

        /// <summary>被占用（含大件的附属槽）</summary>
        public bool IsOccupied => def != null;

        public int Value => (isPrimary && def != null) ? def.value * count : 0;

        public float Weight => (isPrimary && def != null) ? def.weight * count : 0f;
    }

    /// <summary>
    /// 格子背包：6 列固定，行数随货舱等级增长。
    ///
    /// 【两套约束，各管一件事】
    ///   格子 = 体积，限制「能带多少种 / 多少件」（大件残骸占 2 格，很快吃满）。
    ///   重量 = 载重，限制「总共能带多少」（MaxCarryWeight，超了就装不进去）。
    /// 矿物重而小（重量先到顶），残骸大而轻（格子先到顶）—— 两者抢同一个背包，
    /// 于是「这趟装矿还是装残骸」本身就是一个取舍。
    ///
    /// 【为什么重量才是硬约束】Motherload 的 cargo bay 按块计数（7~120），
    /// 换成格子背包后若不设重量上限，24 格 × 16 堆叠 = 384 件，是原设计的 19 倍，
    /// 经济系统会直接崩掉。所以格子只当容器，真正的容量由 MaxCarryWeight 把关。
    ///
    /// 占位只做横向连续（占 1 格或 2 格），不做俄罗斯方块式 2D 形状 —— 后者要旋转、
    /// 要复杂放置算法，收益不划算。
    /// </summary>
    public class InventoryGrid
    {
        public const int Columns = 6;

        /// <summary>货舱升级后格子数变化时的回调（UI 需要重建格子）</summary>
        public event Action OnChanged;

        InventorySlot[] slots = Array.Empty<InventorySlot>();
        int rows = 2;
        float maxWeight = 24f;

        public int Rows => rows;
        public int ColumnCount => Columns;
        public int Capacity => Columns * rows;
        public float MaxWeight => maxWeight;

        public InventorySlot GetSlot(int index) =>
            (index >= 0 && index < slots.Length) ? slots[index] : null;

        /// <summary>已占用的格数（含大件附属槽）</summary>
        public int UsedSlots
        {
            get
            {
                int n = 0;
                for (int i = 0; i < slots.Length; i++)
                    if (slots[i].IsOccupied) n++;
                return n;
            }
        }

        public float TotalWeight
        {
            get
            {
                float sum = 0f;
                for (int i = 0; i < slots.Length; i++)
                    sum += slots[i].Weight;
                return sum;
            }
        }

        public int TotalValue
        {
            get
            {
                int sum = 0;
                for (int i = 0; i < slots.Length; i++)
                    sum += slots[i].Value;
                return sum;
            }
        }

        /// <summary>载重比 0~1。用于换算爬升惩罚与油耗惩罚</summary>
        public float LoadRatio => maxWeight > 0f ? Mathf.Clamp01(TotalWeight / maxWeight) : 0f;

        public bool IsFullByWeight => TotalWeight >= maxWeight - 0.0001f;

        // ---------- 容量 ----------

        /// <summary>
        /// 随货舱升级扩容。已有物品尽量保留：格子数变少时，超出范围的格子里的物品会被丢弃。
        /// </summary>
        public void Resize(int newRows, float newMaxWeight)
        {
            rows = Mathf.Max(1, newRows);
            maxWeight = Mathf.Max(1f, newMaxWeight);

            int size = Columns * rows;
            if (slots.Length != size)
            {
                var old = slots;
                slots = new InventorySlot[size];
                for (int i = 0; i < size; i++)
                {
                    slots[i] = (i < old.Length) ? old[i] : new InventorySlot();
                    if (slots[i] == null) slots[i] = new InventorySlot();
                }
            }
            Notify();
        }

        // ---------- 增删 ----------

        /// <summary>
        /// 放入物品。先填同类未满的堆，再开新格；受堆叠上限与载重上限双重限制。
        /// 返回「没装下的数量」——满舱时交给调用方决定是替换还是丢弃。
        /// </summary>
        public int AddItem(TileDefinition def, int amount)
        {
            if (def == null || amount <= 0) return amount;
            if (slots.Length == 0) return amount;

            int width = Mathf.Max(1, def.gridWidth);
            int limit = Mathf.Max(1, def.stackLimit);
            float total = TotalWeight;

            // 1. 先填已有的同类堆
            for (int i = 0; i < slots.Length && amount > 0; i++)
            {
                var s = slots[i];
                if (!s.isPrimary || s.def != def || s.count >= limit) continue;

                while (amount > 0 && s.count < limit)
                {
                    if (total + def.weight > maxWeight + 0.0001f)
                    {
                        Notify();
                        return amount;
                    }
                    s.count++;
                    total += def.weight;
                    amount--;
                }
            }

            // 2. 再开新格（横向连续 width 格）
            while (amount > 0)
            {
                int start = FindFreeRun(width);
                if (start < 0) break;

                int want = Mathf.Min(amount, limit);
                int placed = 0;
                while (placed < want && total + def.weight <= maxWeight + 0.0001f)
                {
                    placed++;
                    total += def.weight;
                }
                if (placed <= 0) break;

                slots[start].def = def;
                slots[start].count = placed;
                slots[start].isPrimary = true;
                slots[start].ownerIndex = -1;

                for (int k = 1; k < width; k++)
                {
                    int idx = start + k;
                    if (idx >= slots.Length) break;
                    slots[idx].def = def;
                    slots[idx].count = 0;
                    slots[idx].isPrimary = false;
                    slots[idx].ownerIndex = start;
                }

                amount -= placed;
            }

            Notify();
            return amount;
        }

        /// <summary>从指定格（或其所属主格）取出若干件，返回实际取出数量</summary>
        public int RemoveAt(int index, int amount)
        {
            int primary = PrimaryIndexOf(index);
            if (primary < 0 || amount <= 0) return 0;

            var s = slots[primary];
            int removed = Mathf.Min(amount, s.count);
            s.count -= removed;
            if (s.count <= 0) ReleaseRun(primary);

            Notify();
            return removed;
        }

        public void Clear()
        {
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i].def = null;
                slots[i].count = 0;
                slots[i].isPrimary = true;
                slots[i].ownerIndex = -1;
            }
            Notify();
        }

        // ---------- 查询 ----------

        /// <summary>
        /// 找出「价值密度」最低的一格主槽，用于满舱时替换。
        /// 只在它比 incoming 更差时返回下标，否则返回 -1（不该替换）。
        /// 价值密度 = 单价 ÷ 重量：这才是 Motherload 式的取舍（丢铁矿换钻石），
        /// 而不是按绝对单价（那样会把一整堆铁矿当成比一颗钻石更值钱）。
        /// </summary>
        public int IndexOfLowestValueDensity(TileDefinition incoming)
        {
            if (incoming == null) return -1;

            float incomingDensity = Density(incoming);
            int best = -1;
            float bestDensity = float.MaxValue;

            for (int i = 0; i < slots.Length; i++)
            {
                var s = slots[i];
                if (!s.isPrimary || s.IsEmpty) continue;

                float d = Density(s.def);
                if (d < bestDensity)
                {
                    bestDensity = d;
                    best = i;
                }
            }

            return (best >= 0 && bestDensity < incomingDensity) ? best : -1;
        }

        static float Density(TileDefinition def)
        {
            if (def == null) return 0f;
            return def.value / Mathf.Max(0.0001f, def.weight);
        }

        // ---------- 内部 ----------

        /// <summary>把下标换算到主槽（点到大件的附属槽时，等价于点它的主槽）</summary>
        int PrimaryIndexOf(int index)
        {
            if (index < 0 || index >= slots.Length) return -1;

            var s = slots[index];
            if (s.isPrimary) return s.IsEmpty ? -1 : index;
            return (s.ownerIndex >= 0 && s.ownerIndex < slots.Length) ? s.ownerIndex : -1;
        }

        /// <summary>找同行内连续 width 个空格的起点，找不到返回 -1</summary>
        int FindFreeRun(int width)
        {
            if (width <= 1)
            {
                for (int i = 0; i < slots.Length; i++)
                    if (!slots[i].IsOccupied) return i;
                return -1;
            }

            for (int row = 0; row < rows; row++)
            {
                int rowStart = row * Columns;
                for (int col = 0; col + width <= Columns; col++)
                {
                    int idx = rowStart + col;
                    bool free = true;
                    for (int k = 0; k < width; k++)
                    {
                        if (slots[idx + k].IsOccupied) { free = false; break; }
                    }
                    if (free) return idx;
                }
            }
            return -1;
        }

        void ReleaseRun(int primary)
        {
            var def = slots[primary].def;
            int width = def != null ? Mathf.Max(1, def.gridWidth) : 1;

            for (int k = 0; k < width; k++)
            {
                int idx = primary + k;
                if (idx >= slots.Length) break;

                slots[idx].def = null;
                slots[idx].count = 0;
                slots[idx].isPrimary = true;
                slots[idx].ownerIndex = -1;
            }
        }

        void Notify() => OnChanged?.Invoke();
    }
}
