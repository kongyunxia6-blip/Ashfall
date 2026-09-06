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
    /// DEV-011：一笔货物损失的明细行（def + 损失件数）。供 ApplyFractionalLoss 返回、失败摘要展示。
    /// </summary>
    [Serializable]
    public struct LossLine
    {
        public TileDefinition def;
        public int lost;
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

        /// <summary>
        /// Blocker3（DEV-013）：只读预检 —— AddItem(def, amount) 能否【全部】装下（不修改任何状态）。
        /// 与 AddItem 完全同构：先填同类未满堆，再开新格；受堆叠上限与载重上限双重限制。
        /// 返回 true = 全部装入；false = 部分/全部装不下（调用方应拒绝发放，避免"部分奖励被静默吞"）。
        /// </summary>
        public bool CanAcceptFull(TileDefinition def, int amount)
        {
            if (def == null || amount <= 0) return true;
            if (slots == null || slots.Length == 0) return false;

            int width = Mathf.Max(1, def.gridWidth);
            int limit = Mathf.Max(1, def.stackLimit);
            float total = TotalWeight;
            int remaining = amount;

            // 1) 先填同类未满堆（只读估算：不修改 s.count，仅用堆余量 + 载重判定吸收量）
            for (int i = 0; i < slots.Length && remaining > 0; i++)
            {
                var s = slots[i];
                if (!s.isPrimary || s.def != def || s.count >= limit) continue;
                int room = limit - s.count;
                int absorb = 0;
                while (absorb < room && remaining > 0 && total + def.weight <= maxWeight + 0.0001f)
                {
                    absorb++;
                    total += def.weight;
                    remaining--;
                }
                if (remaining > 0 && total + def.weight > maxWeight + 0.0001f) return false; // 载重满
            }

            // 2) 再开新格（横向连续 width 空槽）
            while (remaining > 0)
            {
                int start = FindFreeRun(width);
                if (start < 0) return false;   // 格子满
                int want = Mathf.Min(remaining, limit);
                int placed = 0;
                while (placed < want && total + def.weight <= maxWeight + 0.0001f)
                {
                    placed++;
                    total += def.weight;
                }
                if (placed <= 0) return false; // 载重满，无法再开新堆
                remaining -= placed;
            }
            return true;
        }

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

        /// <summary>
        /// DEV-011：确定性「按比例保留」损失 —— 死亡/失败时对每种货物保留 keep = floor(count × keepRatio)，
        /// 其余（loss = count - keep）按稳定顺序移除。
        ///
        /// 规则（可解释、可测试、无随机）：
        ///  - 同种 def 跨多堆时先聚合总数再整体取 keep（避免逐堆 floor 造成隐性多损）；
        ///  - 移除顺序 = 槽下标升序，先清空靠前堆再动后面的（确定性）；
        ///  - 绝不生成负数量；不会把整舱清空后随机重加；
        ///  - 返回每种货物损失件数明细（LossLine[]，无损失返回空数组）。
        /// 调用方若持有 CargoValue/CargoWeight 等缓存，请在返回后重新同步（本类会发 OnChanged）。
        /// </summary>
        public LossLine[] ApplyFractionalLoss(float keepRatio)
        {
            if (slots.Length == 0 || slots == null) return Array.Empty<LossLine>();
            keepRatio = Mathf.Clamp01(keepRatio);

            // 1) 按槽升序首次出现顺序聚合每 def 总数
            var totals = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<TileDefinition, int>>();
            for (int i = 0; i < slots.Length; i++)
            {
                var s = slots[i];
                if (!s.isPrimary || s.IsEmpty) continue;
                bool found = false;
                for (int k = 0; k < totals.Count; k++)
                {
                    if (totals[k].Key == s.def)
                    {
                        totals[k] = new System.Collections.Generic.KeyValuePair<TileDefinition, int>(
                            totals[k].Key, totals[k].Value + s.count);
                        found = true;
                        break;
                    }
                }
                if (!found) totals.Add(new System.Collections.Generic.KeyValuePair<TileDefinition, int>(s.def, s.count));
            }

            // 2) 逐 def 移除超额部分（确定性：槽升序，先清空靠前堆）
            var lostList = new System.Collections.Generic.List<LossLine>();
            bool anyChange = false;
            for (int t = 0; t < totals.Count; t++)
            {
                var def = totals[t].Key;
                int total = totals[t].Value;
                int keep = Mathf.FloorToInt(total * keepRatio);
                int toRemove = total - keep;
                if (toRemove <= 0) continue;

                int removed = 0;
                for (int i = 0; i < slots.Length && removed < toRemove; i++)
                {
                    var s = slots[i];
                    if (!s.isPrimary || s.def != def || s.count <= 0) continue;
                    int take = Mathf.Min(s.count, toRemove - removed);
                    s.count -= take;
                    removed += take;
                    if (s.count <= 0) ReleaseRun(i);
                }
                if (removed > 0)
                {
                    anyChange = true;
                    lostList.Add(new LossLine { def = def, lost = removed });
                }
            }

            if (anyChange) Notify();
            return lostList.ToArray();
        }

        /// <summary>
        /// DEV-011：按确定性保留规则估算损失价值（只读，不修改货舱）。用于 HUD 预估展示。
        ///
        /// 【聚合规则必须与 ApplyFractionalLoss 完全一致（DEV-011 fix）】：
        /// 先按 def 聚合每种货物的总件数，再整体 keep = floor(total × keepRatio)，
        /// 损失 = total - keep，价值 = Σ(损失件数 × def.value)。
        /// 严禁逐堆 floor —— 否则同种矿跨多个 stack 时，估算损失会大于实际结算损失
        /// （例：iron×1 分两堆、keepRatio=0.5：实际聚合 total=2→keep=1→损1；
        ///   逐堆则每堆各损1 → 估算成损2，与 ApplyFractionalLoss 结算不一致）。
        /// </summary>
        public int EstimatedLossValue(float keepRatio)
        {
            keepRatio = Mathf.Clamp01(keepRatio);

            // 逐主槽累加，但每种 def 只在【首次出现】时按聚合总数一次性结算，避免重复计损。
            var counted = new System.Collections.Generic.HashSet<TileDefinition>();
            int lossValue = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                var s = slots[i];
                if (!s.isPrimary || s.IsEmpty) continue;
                if (!counted.Add(s.def)) continue;   // 该 def 已按聚合总数算过，跳过后续堆

                // 聚合该 def 的全部主槽总件数（跨多个 stack）
                int defTotal = 0;
                for (int j = 0; j < slots.Length; j++)
                {
                    var s2 = slots[j];
                    if (s2 != null && s2.isPrimary && s2.def == s.def && !s2.IsEmpty)
                        defTotal += s2.count;
                }

                int loss = defTotal - Mathf.FloorToInt(defTotal * keepRatio);
                lossValue += loss * s.def.value;
            }
            return lossValue;
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
