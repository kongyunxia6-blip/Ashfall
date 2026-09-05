using System.Collections.Generic;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-007：一次 Scanner 扫描的结果（只读快照，供 HUD 显示与测试断言）。
    /// </summary>
    public class OreScanResult
    {
        /// <summary>扫描中心（网格坐标）。</summary>
        public Vector2Int center;

        /// <summary>扫描半径（Chebyshev 距离，含边界）。</summary>
        public int radius;

        /// <summary>半径内矿物格总数（0 = 附近无矿）。</summary>
        public int totalSignals;

        /// <summary>半径内矿物位置清单（供方向提示 / 测试；不暴露精确坐标给玩家 UI，测试可读）。</summary>
        public readonly List<Vector2Int> signalCells = new List<Vector2Int>();

        /// <summary>半径内出现的矿种（去重，顺序 = 首次发现顺序；只作展示顺序，计数请看 oreCounts）。</summary>
        public readonly List<TileDefinition> oreTypes = new List<TileDefinition>();

        /// <summary>
        /// 半径内各矿种的【真实格数】（Key = 矿种，Value = 该矿种出现的格数）。
        /// 与 totalSignals 一致（Σ oreCounts == totalSignals），供 HUD 显示与测试断言；
        /// 修正：早期版误把去重后的 oreTypes 当计数用（5 铁 + 3 铜会错显示成 Iron×1 / Copper×1）。
        /// </summary>
        public readonly Dictionary<TileDefinition, int> oreCounts = new Dictionary<TileDefinition, int>();

        /// <summary>最近矿的网格坐标（无矿时为 null）。</summary>
        public Vector2Int? nearestCell;

        /// <summary>最近矿的类型（无矿时为 null）。</summary>
        public TileDefinition nearestOre;

        /// <summary>最近矿的 Chebyshev 距离（无矿时为 -1）。</summary>
        public int nearestDistance = -1;

        /// <summary>汇总人类可读描述（HUD / 日志用）。</summary>
        public string Summary { get; private set; } = "";

        /// <summary>方向提示（以网格 y 向下为正约定；仅在最近矿距离 &gt; 0 时非空）。</summary>
        public string DirectionHint { get; private set; } = "";

        public void BuildSummary()
        {
            if (totalSignals <= 0)
            {
                Summary = "扫描：附近没有检测到矿物反应";
                return;
            }

            // 各矿种真实格数（Key 顺序无关；用 oreTypes 的顺序保证展示稳定）
            var parts = new List<string>();
            foreach (var t in oreTypes)
            {
                if (t == null) continue;
                int n = 0;
                if (oreCounts != null) oreCounts.TryGetValue(t, out n);
                parts.Add($"{t.displayName}×{n}");
            }
            string types = string.Join(" / ", parts);

            if (nearestCell.HasValue)
            {
                int dx = nearestCell.Value.x - center.x;
                int dy = nearestCell.Value.y - center.y;   // 网格 y 向下为正
                DirectionHint = DescribeDirection(dx, dy);
                string oreName = nearestOre != null ? nearestOre.displayName : "矿物";
                Summary = $"扫描：附近检测到 {totalSignals} 个矿物信号（{types}）· " +
                          $"最近 {oreName} 在{DescribeDistance(nearestDistance)}{DirectionHint}";
            }
            else
            {
                Summary = $"扫描：附近检测到 {totalSignals} 个矿物信号（{types}）";
            }
        }

        static string DescribeDistance(int dist) => dist <= 0 ? "脚下" : $"{dist} 格外";

        static string DescribeDirection(int dx, int dy)
        {
            // 玩家（世界坐标）习惯：屏幕上方 = 网格 y 减小（更浅）；屏幕下方 = 网格 y 增大（更深）
            string v = dy < 0 ? "上" : (dy > 0 ? "下" : "");
            string h = dx < 0 ? "左" : (dx > 0 ? "右" : "");
            return (v + h) == "" ? "正前方" : (v + h) + "方";
        }
    }

    /// <summary>
    /// DEV-007：基础扫描 / 邻近矿物提示 V1（只读，方案 A+B：计数 + 方向提示）。
    ///
    /// 职责（只提供信息，绝不代替采矿）：
    ///  - 以玩家所在格为中心、有限半径（Chebyshev）扫描 DigGrid；
    ///  - 统计半径内矿物信号（value &gt; 0 的实心格）数量 / 矿种 / 最近矿方向；
    ///  - 输出 OreScanResult（HUD 显示 / 测试断言），完全不写 Block / Durability / Cargo / Fuel；
    ///  - 不自动挖矿、不自动拾取、不显示整张地图、不改变矿物位置。
    ///
    /// 操作：R 键触发一次扫描（原型 Debug 键，方便后续接入正式模块系统）。0 消耗。
    ///
    /// 挂载：玩家同物体（自动取同物体 DrillVehicle 的 grid；否则拖入）。
    /// </summary>
    public class OreScanner : MonoBehaviour
    {
        [Tooltip("扫描半径（Chebyshev 距离；3~5 格即可，V1 有限范围不透视全图）")]
        [Min(1)] public int scanRadius = 4;

        [Header("DEV-010：外部半径加成（勘探传感器装备时由 EquipmentProgression 实时提供）")]
        [Tooltip("实际生效半径 = scanRadius + 外部加成。Scanner 本身仍只读。")]
        public int EffectiveRadius
        {
            get
            {
                var gm = GameManager.Instance;
                var eq = gm != null ? gm.Equipment : null;
                int bonus = eq != null ? eq.EffectiveScannerRadiusBonus : 0;
                return scanRadius + bonus;
            }
        }

        [Tooltip("触发扫描的按键")]
        public KeyCode scanKey = KeyCode.R;

        [Tooltip("扫描结果写进 GameManager.LastServiceMessage（HUD 中央消息）。关掉则只存 LastResult")]
        public bool surfaceToServiceMessage = true;

        DigGrid grid;
        DrillVehicle vehicle;

        /// <summary>最近一次扫描结果（未扫过为 null）。</summary>
        public OreScanResult LastResult { get; private set; }

        void Awake()
        {
            vehicle = GetComponent<DrillVehicle>();
        }

        void Start()
        {
            if (grid == null && vehicle != null) grid = vehicle.grid;
            if (grid == null) grid = FindFirstObjectByType<DigGrid>();
            if (grid == null)
                Debug.LogWarning("[DEV-007] OreScanner: 未找到 DigGrid，无法扫描");
        }

        void Update()
        {
            if (grid == null) return;
            if (!Input.GetKeyDown(scanKey)) return;
            if (vehicle == null || vehicle.IsDead) return;

            var result = ScanAround(vehicle.transform.position);
            LastResult = result;
            if (surfaceToServiceMessage && GameManager.Instance != null)
                GameManager.Instance.LastServiceMessage = result.Summary;
            Debug.Log("[OreScanner] " + result.Summary);
        }

        /// <summary>以世界坐标为中心扫描（转网格坐标后调 ScanAt）。半径 = 基础 + 外部加成（只读）。</summary>
        public OreScanResult ScanAround(Vector3 worldPos)
        {
            return ScanAt(grid.WorldToGrid(worldPos), EffectiveRadius);
        }

        /// <summary>
        /// 以网格坐标为中心、给定半径做 Chebyshev 扫描。纯只读：只用 InBounds/GetTile。
        /// 判定矿物 = isSolid 且 value &gt; 0（泥土/硬岩/熔岩 value=0 不算；bedrock/empty 不算）。
        /// </summary>
        public OreScanResult ScanAt(Vector2Int center, int radius)
        {
            var res = new OreScanResult { center = center, radius = radius };

            int best = int.MaxValue;
            int minX = center.x - radius, maxX = center.x + radius;
            int minY = center.y - radius, maxY = center.y + radius;
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!grid.InBounds(x, y)) continue;
                    var def = grid.GetTile(x, y);
                    if (def == null || !def.isSolid || def.value <= 0) continue;

                    res.signalCells.Add(new Vector2Int(x, y));
                    res.totalSignals++;
                    if (!res.oreTypes.Contains(def)) res.oreTypes.Add(def);
                    int curCnt = 0;
                    res.oreCounts.TryGetValue(def, out curCnt);
                    res.oreCounts[def] = curCnt + 1;

                    int dist = Mathf.Max(Mathf.Abs(x - center.x), Mathf.Abs(y - center.y));
                    if (dist < best)
                    {
                        best = dist;
                        res.nearestCell = new Vector2Int(x, y);
                        res.nearestOre = def;
                        res.nearestDistance = dist;
                    }
                }
            }

            res.BuildSummary();
            return res;
        }
    }
}
