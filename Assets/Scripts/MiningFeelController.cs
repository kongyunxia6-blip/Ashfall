using System;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-003：单格采矿交互与挖掘手感 V1。
    ///
    /// 职责（只做「输入 → 单格目标 → 命中节奏」，不碰 Block 数据层）：
    ///  - 4 方向单格目标锁定：同一输入只解析出 1 个相邻目标格，带换向/锁格双重迟滞防抖动；
    ///  - 按住连续挖掘：攻击间隔门控，一次攻击 tick 最多调一次 DigGrid.HitBlock；
    ///    当前格消失后方向保持则自动锁到紧邻下一格；释放输入立即停止；
    ///  - 挖掘节奏参数集中在本组件（attackInterval / hitStop / recovery / breakExtraPause）；
    ///  - 单格命中手感：极短 hit-stop（冻结载具速度，不动 Time.timeScale、不影响 MovementMode），
    ///    Break 那一下 hit-stop × 倍率 + 追加 breakExtraPause，更有重量但不整屏震动；
    ///  - 轻量目标提示：只高亮当前目标 1 格（描边 overlay，不遮挡裂纹 Sprite），H 键开关；
    ///  - 工具效率接口：miningSpeedMultiplier × UpgradeSystem.DrillSpeedMultiplier，
    ///    未来升级只让挖掘更快，永远只 Hit 1 个 Block。
    ///
    /// 设计规则（对齐 Issue #6）：
    ///  - 耐久 / Breaking / 裂纹阶段仍以 DigGrid 为唯一真相源，本组件不维护第二套 HP；
    ///  - 命中路径复用 DrillVehicle.TryDigHit（硬度检查 + HUD 进度 + 燃料），不复制挖掘逻辑；
    ///  - 不实现任何 AoE / 范围挖掘。
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

        [Header("目标锁定（防抖动）")]
        [Tooltip("换向迟滞：新方向主轴强度须达到该值才切换方向（防斜按/抖动导致目标在相邻格之间乱跳）")]
        [Range(0.1f, 0.9f)] public float directionSwitchThreshold = 0.45f;

        [Tooltip("锁定格最大邻接距离（世界单位）：锁定格中心与玩家任一轴距离超过它就放弃锁定重选")]
        [Min(0.6f)] public float maxTargetDistance = 1.65f;

        [Header("目标提示（轻量 V1，H 键开关）")]
        [Tooltip("是否显示单格目标描边提示。运行中可按 H 切换")]
        public bool showTargetHighlight = true;

        [Tooltip("目标提示颜色（半透明黄描边，不遮挡裂纹 Sprite）")]
        public Color highlightColor = new Color(1f, 0.9f, 0.2f, 0.85f);

        // ---------- 对外只读状态（HUD / 测试） ----------

        /// <summary>当前锁定的目标格（null = 无有效目标）。一次只会有 0 或 1 个。</summary>
        public Vector2Int? CurrentTarget { get; private set; }

        /// <summary>当前锁定的 4 方向（世界空间，zero = 无方向输入）。注意 DigGrid 网格 y 向下为正，用网格坐标时需 y 取反。</summary>
        public Vector2Int CurrentDirection => currentDir;

        /// <summary>是否正在采矿（按住输入且有有效目标）。</summary>
        public bool IsMining { get; private set; }

        /// <summary>实际生效的工具效率倍率（含升级系统）。</summary>
        public float EffectiveMultiplier
        {
            get
            {
                float m = miningSpeedMultiplier;
                if (useUpgradeDrillSpeed && upgrades != null) m *= upgrades.DrillSpeedMultiplier;
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

        Vector2Int currentDir = Vector2Int.zero;   // 锁定的 4 方向
        Vector2Int? lockedCell;                    // 迟滞锁定格（部分挖掘过的格不随便换）
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
            upgrades = GameManager.Instance != null ? GameManager.Instance.Upgrades : null;
            if (vehicle == null)
                Debug.LogWarning("[DEV-003] MiningFeelController 需要与 DrillVehicle 同物体挂载");
        }

        void Update()
        {
            if (vehicle == null || grid == null || vehicle.IsDead)
            {
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
        /// 一帧的采矿状态机：方向解析 → 单格目标解析 → 高亮 → 攻击门控命中。
        /// Update 每帧调用；测试驱动同步调用一次保证状态即时就位（不依赖帧调度）。
        /// </summary>
        void TickMining(Vector2 raw, bool held)
        {
            // 释放输入 → 立即停止（不完成"最后一击"）
            if (!held && heldLastFrame) IsMining = false;
            heldLastFrame = held;

            // ---- 2. 方向解析（带换向迟滞） ----
            UpdateDirection(raw);

            // ---- 3. 单格目标解析（严格相邻 + 锁格迟滞） ----
            Vector2Int? target = ResolveTarget();
            CurrentTarget = target;
            UpdateHighlight(target);

            IsMining = held && target != null;

            // ---- 4. 攻击门控：一次 tick 最多一次 HitBlock ----
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
                // 目标在命中瞬间失效（如被落石/其他系统移走）：清锁定，下帧重选
                lockedCell = null;
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
        /// 把原始输入解析成唯一 4 方向。规则：
        ///  - 双轴都有输入时取【主轴】（|x|>|y| 取水平，否则取竖直），同输入永远只出一个方向；
        ///  - 已有方向时，新主轴强度须 ≥ directionSwitchThreshold 才换向（迟滞防跳）。
        /// </summary>
        void UpdateDirection(Vector2 raw)
        {
            float ax = Mathf.Abs(raw.x), ay = Mathf.Abs(raw.y);
            if (ax < 0.2f && ay < 0.2f)
            {
                currentDir = Vector2Int.zero;   // 松开方向键 = 无方向（目标随之隐藏）
                return;
            }

            Vector2Int newDir = ax > ay
                ? (raw.x > 0f ? Vector2Int.right : Vector2Int.left)
                : (raw.y > 0f ? Vector2Int.up : Vector2Int.down);

            if (currentDir == Vector2Int.zero || newDir == currentDir)
            {
                currentDir = newDir;
                return;
            }

            float strength = newDir.x != 0 ? ax : ay;
            if (strength >= directionSwitchThreshold)
                currentDir = newDir;
        }

        // ---------- 单格目标解析 ----------

        /// <summary>
        /// 解析当前目标格。规则：
        ///  - 新目标严格 = 玩家所在格 + 当前方向（只此 1 格，绝不打斜对角或第二格）；
        ///  - 若锁定格（挖了一半的格）仍实心、仍在邻接距离内、且仍在当前方向轴上，
        ///    继续保持锁定 —— 玩家踩在格边界轻微抖动时目标不在相邻格之间跳；
        ///  - 当前方向无可挖 Block → 返回 null（目标提示隐藏），不外扩搜索。
        /// </summary>
        Vector2Int? ResolveTarget()
        {
            if (currentDir == Vector2Int.zero)
            {
                lockedCell = null;
                return null;
            }

            Vector2Int pc = grid.WorldToGrid(transform.position);
            // 世界方向 → 网格方向：DigGrid 网格 y 向下为正（越深越大），世界 y 向上为正，故 y 取反
            Vector2Int gridDir = new Vector2Int(currentDir.x, -currentDir.y);

            if (lockedCell.HasValue)
            {
                Vector2Int lc = lockedCell.Value;
                Vector3 lw = grid.GridToWorld(lc.x, lc.y);
                bool near = Mathf.Abs(lw.x - transform.position.x) <= maxTargetDistance
                         && Mathf.Abs(lw.y - transform.position.y) <= maxTargetDistance;
                Vector2Int rel = lc - pc;
                // 仍在方向轴上：沿挖掘方向分量仍朝前（允许横向偏移 1 格内的边界抖动，
                // 但锁定格一旦脱离方向轴正前方 ±1 格带就放弃）
                bool onAxis = gridDir.x != 0
                    ? (rel.x == gridDir.x && Mathf.Abs(rel.y) <= 1)
                    : (rel.y == gridDir.y && Mathf.Abs(rel.x) <= 1);

                if (near && onAxis && IsCellValidTarget(lc))
                    return lc;

                lockedCell = null;
            }

            Vector2Int desired = pc + gridDir;
            if (IsCellValidTarget(desired))
            {
                lockedCell = desired;
                return desired;
            }
            return null;
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
        /// 描边 = 32×32 仅边缘像素的中空框，盖在前景 Tilemap 之上但不遮挡格子内部的裂纹 Sprite。
        /// </summary>
        void EnsureHighlight()
        {
            if (highlightGo != null) return;

            if (highlightSprite == null)
            {
                const int N = 32, B = 3;
                var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Point;
                for (int x = 0; x < N; x++)
                    for (int y = 0; y < N; y++)
                    {
                        bool edge = x < B || y < B || x >= N - B || y >= N - B;
                        tex.SetPixel(x, y, edge ? Color.white : Color.clear);
                    }
                tex.Apply();
                highlightSprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), N);
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
