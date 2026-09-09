using System;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-003 / DEV-018.1：着地限定的「前方双格」单格采矿交互与挖掘手感。
    ///
    /// 职责（只做「着地判定 → 前方目标解析 → 命中节奏」，不碰 Block 数据层）：
    ///  - 【DEV-018.1】只在稳定着地时挖矿：以 DrillVehicle.Grounded 为着地权威，
    ///    Jetting==true 或离地即禁止锁定 / 挖矿动画 / 命中（离地当帧清目标与高亮，无空中宽限）。
    ///  - 【DEV-018.1】每个朝向只有前方竖排两格合法：front（身体/镐头同高正前方格）
    ///    与 frontDown（front 正下方一格）；面向由最近一次有效左右输入决定的权威 Facing 判定；
    ///    正上 / 身后 / 正脚下 / 斜上 / 两格以外全部不可挖。
    ///  - 【DEV-018.1】输入映射：A/左、D/右 只请求对应朝向 front（front 空也不自动改挖 frontDown）；
    ///    S/下 请求当前朝向 frontDown，但 front 仍实心时先锁挖 front；W/上 或空输入不挖。
    ///  - 按住连续挖掘：攻击间隔门控，一次攻击 tick 最多调一次 DrillVehicle.TryDigHit；
    ///    命中节奏参数集中在本组件（attackInterval / hitStop / recovery / breakExtraPause）。
    ///  - 单格命中手感：极短 hit-stop（冻结载具速度，不动 Time.timeScale、不影响 MovementMode）；
    ///    轻量目标提示：只高亮当前目标 1 格（描边 overlay），H 键开关；
    ///    工具效率接口：EffectiveMultiplier × equipment/upgrades 钻速，未来升级只让挖掘更快。
    ///
    /// 设计规则（对齐 Issue #6 + DEV-018.1 玩法规则）：
    ///  - 耐久 / Breaking / 裂纹阶段仍以 DigGrid 为唯一真相源，本组件不维护第二套 HP；
    ///  - 命中路径复用 DrillVehicle.TryDigHit（硬度检查 + HUD 进度 + 燃料），不复制挖掘逻辑；
    ///  - 不实现任何 AoE / 范围挖掘；同输入只解析 0 或 1 个相邻目标格。
    ///
    /// 挂载：玩家物体（与 DrillVehicle 同物体）。存在且 enabled 时，DrillVehicle 的
    /// 单击挖掘会让位给本组件（单击 = 短按住，走同一状态机）。
    /// </summary>
    [DefaultExecutionOrder(100)]   // FixedUpdate 在 DrillVehicle 之后跑：hit-stop 才能覆盖当帧速度
    public class MiningFeelController : MonoBehaviour
    {
        [Header("引用（默认自动取同物体 DrillVehicle 的 grid）")]
        public DigGrid grid;

        [Header("挖掘节奏参数（集中配置，无散落 magic number）")]
        [Tooltip("攻击间隔（秒）：两次 HitBlock 之间的最小时间。实际间隔 = 本值 / 工具效率倍率")]
        [Min(0.05f)] public float attackInterval = 0.28f;

        [Tooltip("命中停顿 hit-stop（秒）：命中瞬间冻结载具速度的时长，制造「打中了」的顿挫。原型建议 0.03~0.05")]
        [Range(0f, 0.12f)] public float hitStopDuration = 0.04f;

        [Tooltip("下一击恢复锁定（秒）：命中后在此窗口内不允许下一击（recovery）。实际下一击窗口 = max(攻击间隔, 本值)")]
        [Range(0f, 0.3f)] public float recoveryTime = 0.05f;

        [Tooltip("Break 额外停顿（秒）：方块崩碎后在攻击间隔上追加的恢复窗口（原型区间 0.08~0.18），崩碎更有重量")]
        [Range(0f, 0.3f)] public float breakExtraPause = 0.12f;

        [Tooltip("Break 命中停顿倍率：崩碎那一下的 hit-stop 时长 = hitStopDuration × 本值")]
        [Range(1f, 4f)] public float breakHitStopMultiplier = 2f;

        [Header("工具效率接口（预留升级入口；永远只 Hit 单格）")]
        [Tooltip("工具效率倍率：>1 挖得更快（等比缩短攻击间隔）。未来装备/升级树从这里注入，不改变单格规则")]
        [Min(0.1f)] public float miningSpeedMultiplier = 1f;

        [Tooltip("是否叠加 UpgradeSystem.DrillSpeedMultiplier（引擎等级带来的既有钻速成长）")]
        public bool useUpgradeDrillSpeed = true;

        [Header("目标解析阈值")]
        [Tooltip("方向输入死区：|x| 或 |y| 低于它视为无该轴输入（防按键噪声触发挖矿）")]
        [Range(0.05f, 0.5f)] public float directionDeadZone = 0.2f;

        [Header("目标提示（轻量 V1，H 键开关）")]
        [Tooltip("是否显示单格目标描边提示。运行中可按 H 切换")]
        public bool showTargetHighlight = true;

        [Tooltip("目标提示颜色（半透明黄描边，不遮挡裂纹 Sprite）")]
        public Color highlightColor = new Color(1f, 0.9f, 0.2f, 0.85f);

        // ---------- 对外只读状态（HUD / 测试） ----------

        /// <summary>当前锁定的目标格（null = 无有效目标）。一次只会有 0 或 1 个。</summary>
        public Vector2Int? CurrentTarget { get; private set; }

        /// <summary>
        /// 当前挖掘请求方向（世界空间，zero = 无有效挖掘请求）。
        /// front 挖掘 = (±1, 0)；frontDown 挖掘 = (0, -1)。注意 DigGrid 网格 y 向下为正。
        /// 仅供诊断/提示读取；目标真正由 Facing + 实心度解析，不直接用本值当偏移。
        /// </summary>
        public Vector2Int CurrentDirection => currentDir;

        /// <summary>
        /// 权威水平朝向（+1 右 / -1 左）。只由最近一次有效左右输入更新；S/W 竖直键不改变朝向。
        /// front / frontDown 都相对本朝向判定，挖矿动画朝向也以此为准。
        /// </summary>
        public int Facing { get; private set; } = 1;

        /// <summary>是否正在采矿（稳定着地 + 按住输入 + 有有效目标）。</summary>
        public bool IsMining { get; private set; }

        /// <summary>实际生效的工具效率倍率（含升级/装备系统；永远只 Hit 单格）。</summary>
        public float EffectiveMultiplier
        {
            get
            {
                float m = miningSpeedMultiplier;
                // DEV-010：装备成长存在时以其挖速倍率为准；否则沿用旧 UpgradeSystem.DrillSpeedMultiplier。
                if (equipment != null)
                    m *= equipment.EffectiveDigSpeedMultiplier;
                else if (useUpgradeDrillSpeed && upgrades != null)
                    m *= upgrades.DrillSpeedMultiplier;
                return Mathf.Max(0.1f, m);
            }
        }

        /// <summary>实际生效的攻击间隔（秒）= attackInterval / 效率倍率。</summary>
        public float EffectiveInterval => attackInterval / EffectiveMultiplier;

        /// <summary>距下一次允许 Hit 的剩余秒数（0 = 现在可打）。供测试验证攻击间隔门控。</summary>
        public float NextHitIn => Mathf.Max(0f, nextHitTime - Time.time);

        /// <summary>一次成功命中后触发（cell, broke）。broke=true 表示这一击使方块进入崩碎。</summary>
        public event Action<Vector2Int, bool> OnMiningHit;

        // ---------- 内部状态 ----------

        DrillVehicle vehicle;
        Rigidbody2D rb;
        UpgradeSystem upgrades;
        /// <summary>DEV-010：装备成长权威组件（可空 → 空则沿用旧 UpgradeSystem 钻速倍率）。</summary>
        EquipmentProgression equipment;

        Vector2Int currentDir = Vector2Int.zero;   // 当前挖掘请求方向（front = (±1,0)；frontDown = (0,-1)）
        float nextHitTime;                         // 攻击间隔门控（一次 tick 最多一击的唯一闸门）
        float hitStopUntil;                        // hit-stop 窗口
        bool heldLastFrame;                        // 释放检测（立即停止）

        // 测试驱动钩子（MCP Play 实测用；见 DebugDriveInput / DebugDriveFor）
        bool debugOverride;
        Vector2 debugDir;
        bool debugHeld;
        float debugDriveUntil;                     // 一次性驱动截止时刻（0 = 持续驱动模式）
        string debugReport;                        // 一次性驱动停止帧快照（DebugGetReport 读取）

        // 目标提示（运行时懒生成的单个描边对象，不是每格永久 GameObject）
        GameObject highlightGo;
        SpriteRenderer highlightSr;
        Sprite highlightSprite;

        void Awake()
        {
            vehicle = GetComponent<DrillVehicle>();
            rb = GetComponent<Rigidbody2D>();
        }

        void Start()
        {
            if (grid == null && vehicle != null) grid = vehicle.grid;
            if (grid == null) grid = FindFirstObjectByType<DigGrid>();
            var gm = GameManager.Instance;
            upgrades = gm != null ? gm.Upgrades : null;
            equipment = gm != null ? gm.Equipment : null;
            if (vehicle == null)
                Debug.LogWarning("[DEV-003] MiningFeelController 需要与 DrillVehicle 同物体挂载");
        }

        void Update()
        {
            if (vehicle == null || grid == null || vehicle.IsDead)
            {
                CurrentTarget = null;
                UpdateHighlight(null);
                IsMining = false;
                return;
            }

            // 一次性测试驱动到期：自动停止，让位真实输入。
            if (debugOverride && debugDriveUntil > 0f && Time.time >= debugDriveUntil)
            {
                debugDriveUntil = 0f;
                DebugClearDrive();
            }

            // H = 目标提示开关（轻量 V1，正式 UI 不在本任务范围）
            if (Input.GetKeyDown(KeyCode.H))
                showTargetHighlight = !showTargetHighlight;

            // ---- 输入源：测试驱动优先，否则读硬件 ----
            Vector2 raw;
            bool held;
            if (debugOverride)
            {
                raw = debugDir;
                held = debugHeld;
            }
            else
            {
                raw = ReadDirectionKeys();
                held = Input.GetMouseButton(0) && !DrillVehicle.IsPointerOverUI();
            }

            TickMining(raw, held);
        }

        /// <summary>
        /// 一帧的采矿状态机：着地判定 → 朝向 → 前方目标解析 → 高亮 → 攻击门控命中。
        /// Update 每帧调用；测试驱动同步调用一次保证状态即时就位（不依赖帧调度）。
        ///
        /// DEV-018.1 着地权威：只有稳定着地（vehicle.Grounded && !vehicle.Jetting）才能
        /// 锁定/高亮/命中；离地当帧清 CurrentTarget 与高亮、IsMining=false，无空中宽限。
        /// </summary>
        void TickMining(Vector2 raw, bool held)
        {
            // 释放输入 → 立即停止（不完成"最后一击"）
            if (!held && heldLastFrame) IsMining = false;
            heldLastFrame = held;

            // ---- 朝向权威：只由最近一次有效左右输入决定（S/W 竖直键不改左右朝向）----
            if (raw.x > directionDeadZone) Facing = 1;
            else if (raw.x < -directionDeadZone) Facing = -1;

            // ---- 挖掘请求方向（front = 水平；frontDown = 竖直向下）----
            currentDir = ComputeDigRequest(raw);

            // ---- 1. 着地判定（权威 Gate）：离地/喷气 = 不可挖，立即清目标与高亮 ----
            bool stanceOk = vehicle != null && vehicle.Grounded && !vehicle.Jetting;
            if (!stanceOk)
            {
                CurrentTarget = null;
                UpdateHighlight(null);
                IsMining = false;
                return;                              // 不产生任何目标/高亮/命中
            }

            // ---- 2. 前方单格目标解析（front 优先 frontDown；front 空不自动下沉）----
            Vector2Int? target = ResolveTarget();
            CurrentTarget = target;
            UpdateHighlight(target);

            IsMining = held && target != null;

            // ---- 3. 攻击门控：一次 tick 最多一次 HitBlock ----
            if (!IsMining) return;
            if (Time.time < nextHitTime) return;

            var res = vehicle.TryDigHit(target.Value);
            if (res == DigHitResult.Hit || res == DigHitResult.Broken)
            {
                bool broke = res == DigHitResult.Broken;
                // Break 后追加 breakExtraPause：保证「崩碎 → 下一格」之间有可感知的重量窗口，
                // 同时从机制上杜绝「同一攻击 tick 穿透命中下一格」。
                float interval = EffectiveInterval + (broke ? breakExtraPause : 0f);
                nextHitTime = Time.time + Mathf.Max(interval, recoveryTime);
                hitStopUntil = Time.time + hitStopDuration * (broke ? breakHitStopMultiplier : 1f);
                OnMiningHit?.Invoke(target.Value, broke);
            }
            else if (res == DigHitResult.NotSolid)
            {
                // 目标在命中瞬间失效（如被落石/其他系统移走）：清目标，下帧重选
                CurrentTarget = null;
                UpdateHighlight(null);
            }
            // HardnessLow：提示由 TryDigHit 内部刷了，给一个退避避免每帧刷
            else if (res == DigHitResult.HardnessLow)
            {
                nextHitTime = Time.time + EffectiveInterval;
            }
        }

        void FixedUpdate()
        {
            // hit-stop：极短冻结载具速度（局部攻击节奏暂停），不动 Time.timeScale、
            // 不改 MovementMode 任何逻辑。40ms 级窗口对物理无感，只给手感一个「顿」。
            if (Time.time < hitStopUntil && rb != null && vehicle != null && !vehicle.IsDead)
                rb.linearVelocity = Vector2.zero;
        }

        // ---------- 输入与方向 ----------

        static Vector2 ReadDirectionKeys()
        {
            float h = 0f, v = 0f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) h -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) h += 1f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) v += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) v -= 1f;
            return new Vector2(h, v);
        }

        /// <summary>
        /// 把原始输入解析成唯一「挖掘请求方向」（世界空间）。
        /// DEV-018.1 映射（front/frontDown 相对 Facing）：
        ///  - 水平主轴（|x|>|y|）→ front：返回 (±1, 0)，符号 = 该轴水平方向；
        ///  - 竖直向下（|y|>|x| 且 y<0，即 S/下）→ frontDown：返回 (0, -1)；
        ///  - W/上、死区内、或水平竖直强度相等（歧义）→ 返回 zero（不挖）。
        /// 朝向外改由 TickMining 从 raw.x 独立维护，这里不再决定左右。
        /// </summary>
        Vector2Int ComputeDigRequest(Vector2 raw)
        {
            float ax = Mathf.Abs(raw.x), ay = Mathf.Abs(raw.y);
            if (ax < directionDeadZone && ay < directionDeadZone)
                return Vector2Int.zero;             // 无有效方向输入 → 不挖

            if (ax > ay)                            // 水平主轴 → front（面前格）
                return new Vector2Int(raw.x > 0f ? 1 : -1, 0);

            if (ay > ax && raw.y < 0f)              // S/下 → frontDown（面前下方格）
                return new Vector2Int(0, -1);

            return Vector2Int.zero;                  // W/上、或水平竖直等强歧义 → 不挖
        }

        // ---------- 前方单格目标解析 ----------

        /// <summary>
        /// 解析当前目标格（DEV-018.1：每个朝向只有前方竖排两格合法）。
        ///  - pc = 玩家身体所在格（WorldToGrid 世界坐标）；
        ///  - front  = pc + (Facing, 0) —— 身体/镐头同高正前方格；
        ///  - frontDown = pc + (Facing, +1) —— front 正下方一格（网格 y 向下为正）。
        ///  规则：
        ///   ・水平请求（front）：只锁 front；front 为空（空气/崩碎/界外）→ null，不自动改挖 frontDown；
        ///   ・向下请求（frontDown）：front 仍实心 → 先锁 front；front 已清空 → 才锁 frontDown；
        ///   ・正上 / 身后 / 正脚下 / 斜上 / 两格以外天然不在 front/frontDown 集合 → 永不选中；
        ///   ・只解析 0 或 1 格，绝不多格 / AoE。
        ///  目标由「Facing + 当前实心度」每帧确定性重算，无迟滞锁格 —— 但 DigGrid 耐久单调递减，
        ///  实心未崩时同朝向每帧都锁同一格，等同稳定锁定。
        /// </summary>
        Vector2Int? ResolveTarget()
        {
            if (currentDir == Vector2Int.zero)
                return null;                        // 无有效挖掘请求

            Vector2Int pc = grid.WorldToGrid(transform.position);
            Vector2Int front = pc + new Vector2Int(Facing, 0);       // 面前格（身体同高）

            if (currentDir.x != 0)
            {
                // 水平请求 → 只挖 front；front 空绝不自动改挖 frontDown
                return IsCellValidTarget(front) ? front : (Vector2Int?)null;
            }

            // 竖直向下请求 → frontDown，但 front 仍实心时先挖 front
            if (IsCellValidTarget(front))
                return front;
            Vector2Int frontDown = pc + new Vector2Int(Facing, 1);
            return IsCellValidTarget(frontDown) ? frontDown : (Vector2Int?)null;
        }

        /// <summary>该格是否可作为挖掘目标：实心、在界内、且不在崩碎中（崩碎中不可重复 Hit）。</summary>
        bool IsCellValidTarget(Vector2Int c)
        {
            if (!grid.InBounds(c.x, c.y)) return false;
            if (grid.IsBreaking(c.x, c.y)) return false;
            return grid.IsSolid(c.x, c.y);
        }

        // ---------- 目标提示（轻量 V1） ----------

        void UpdateHighlight(Vector2Int? target)
        {
            if (!showTargetHighlight || target == null)
            {
                if (highlightSr != null) highlightSr.enabled = false;
                return;
            }
            EnsureHighlight();
            if (highlightSr == null) return;
            highlightSr.enabled = true;
            highlightGo.transform.position = grid.GridToWorld(target.Value.x, target.Value.y);
        }

        /// <summary>
        /// 懒生成唯一一个目标描边对象（全场景最多 1 个，不是每格永久 GameObject）。
        /// 描边 = 128×128 仅边缘像素的中空框（全局规格见 BlockSpec：1 Block = 1×1 世界 = 128px/PPU128），
        /// 盖在前景 Tilemap 之上但不遮挡格子内部的裂纹 Sprite。
        /// </summary>
        void EnsureHighlight()
        {
            if (highlightGo != null) return;

            if (highlightSprite == null)
            {
                const int N = BlockSpec.PixelSize;   // 128px 画布
                const int B = 12;                    // 边框 12px（等比于原 3px@32px 画布）
                var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Point;
                for (int x = 0; x < N; x++)
                    for (int y = 0; y < N; y++)
                    {
                        bool edge = x < B || y < B || x >= N - B || y >= N - B;
                        tex.SetPixel(x, y, edge ? Color.white : Color.clear);
                    }
                tex.Apply();
                highlightSprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), BlockSpec.PPU);
                highlightSprite.name = "DEV003_TargetOutline";
            }

            highlightGo = new GameObject("DEV003_TargetHighlight");
            highlightGo.transform.SetParent(transform, false);
            highlightSr = highlightGo.AddComponent<SpriteRenderer>();
            highlightSr.sprite = highlightSprite;
            highlightSr.color = highlightColor;
            highlightSr.sortingOrder = 15;   // 前景 Tilemap(1) 之上、崩碎 FX(20) 之下
            highlightSr.enabled = false;
        }

        // ---------- 测试驱动钩子（MCP Play 实测用） ----------

        /// <summary>
        /// 测试驱动激活中（仅 MCP 实测置位；手玩恒 false）。DrillVehicle.ReadInput 在激活期间
        /// 以 DebugMove 作为移动输入 → 位移同样走真实 FixedUpdate/physics，与挖掘驱动同源。
        /// </summary>
        public bool DebugActive => debugOverride;

        /// <summary>测试驱动当前方向（同 DebugActive 语义，供 DrillVehicle 复用同一份驱动输入）。</summary>
        public Vector2 DebugMove => debugDir;

        /// <summary>
        /// 测试驱动：覆盖硬件输入，模拟「方向 + 按住」。传入后 Update 走与真实输入完全相同的状态机。
        /// 仅用于自动化实测（MCP 无法注入 OS 级键鼠）；手玩时勿调用。
        /// </summary>
        public void DebugDriveInput(Vector2 rawDir, bool held)
        {
            debugOverride = true;
            debugDir = rawDir;
            debugHeld = held;
            debugDriveUntil = 0f;   // 持续驱动模式（无自动停止）
        }

        /// <summary>
        /// 一次性测试驱动：给定输入立即同步执行一次采矿状态机 tick（方向解析 → 目标解析 →
        /// 高亮 → 门控命中），并留存状态快照（DebugGetReport 读取）。驱动保持 seconds 秒后
        /// 自动让位真实输入。快照在调用时同步生成，不依赖帧调度（编辑器失焦停帧也可用）。
        /// 快照 = 本 tick 结束后的即时状态；若后续真实帧继续挖掘改变了目标格，以探针/二次读为准。
        /// </summary>
        public void DebugDriveFor(Vector2 rawDir, bool held, float seconds)
        {
            debugOverride = true;
            debugDir = rawDir;
            debugHeld = held;
            debugDriveUntil = Time.time + Mathf.Max(0.05f, seconds);
            TickMining(rawDir, held);          // 同步 tick：状态即时就位
            debugReport = CaptureDebugSnapshot();
        }

        /// <summary>读取最近一次一次性驱动的停止帧快照（未跑过或未到期时返回提示文本）。</summary>
        public string DebugGetReport()
        {
            return debugReport ?? "NO_REPORT（尚未跑一次性驱动，或驱动尚未到期停止）";
        }

        /// <summary>停止帧快照：方向 / 目标格 / 高亮状态与位置匹配 / 高亮对象计数 / 是否采矿中。</summary>
        string CaptureDebugSnapshot()
        {
            string tgt = CurrentTarget.HasValue ? CurrentTarget.Value.ToString() : "null";

            int hlCount = 0;
            var trs = FindObjectsByType<Transform>(FindObjectsSortMode.None);
            foreach (var t in trs)
                if (t.name == "DEV003_TargetHighlight") hlCount++;

            string hl;
            bool posMatch = false;
            if (highlightGo == null)
            {
                hl = "not-created";
            }
            else
            {
                hl = highlightSr.enabled ? "enabled" : "disabled";
                if (CurrentTarget.HasValue)
                {
                    Vector3 hp = highlightGo.transform.position;
                    Vector3 tp = grid.GridToWorld(CurrentTarget.Value.x, CurrentTarget.Value.y);
                    posMatch = (hp - tp).sqrMagnitude < 0.0001f;
                }
            }
            return $"dir={CurrentDirection} target={tgt} hl={hl} hl.count={hlCount} hl.posMatch={posMatch} mining={IsMining}";
        }

        /// <summary>清除测试驱动，恢复硬件输入。</summary>
        public void DebugClearDrive()
        {
            debugOverride = false;
            debugDir = Vector2.zero;
            debugHeld = false;
            debugDriveUntil = 0f;
        }
    }
}
