using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Ashfall
{
    /// <summary>
    /// 玩家钻地舱：移动、钻探、燃料、船体、货舱、高温伤害。
    /// 挂在哪：玩家物体（需要 Rigidbody2D + Collider2D），并把 DigGrid 拖进 grid 字段。
    /// 操作：A/D + ←/→ 移动；挖矿 = 手动——按住方向键选方向 + 单击鼠标左键，
    /// 每种方格需打满 digHits 击才挖穿（移动撞墙只会停住、不会自动钻）；空格 = 喷气开关。
    /// 三模式：Hover = 直驱悬浮（原型）；Gravity = 重力滑翔 + 喷气；Walk = Motherload 式
    /// 地面行走 + 重力坠落 + 力驱动喷气（无跳跃、无爬台阶，上不去只能飞或挖）。
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    public class DrillVehicle : MonoBehaviour
    {
        /// <summary>
        /// 运动模式（共存方案）。
        /// Hover   = 悬浮（gravityScale=0，8 向直驱），即原型现状手感，永远能飞；
        /// Gravity = 「喷气重力」模式：默认关喷气 → 受引擎重力滑翔（只能左右 + S 下潜，
        ///            会落地、不会自己爬升）；按空格开喷气 → 反重力悬浮。再按空格关喷气 → 恢复下落。
        /// Walk    = 「Motherload 行走」模式：关喷气 → 脚踏实地行走（地面加速度/摩擦，
        ///            站稳 vy=0；走出边缘/脚下被挖空 → 重力坠落 + 落地冲击）；按空格开喷气 →
        ///            力驱动悬浮（hoverAccel/hoverDecel 惯性对抗，非直驱）。M3 地层规则按层切换。
        /// </summary>
        public enum MovementMode { Hover, Gravity, Walk }

        [Header("引用（必填）")]
        public DigGrid grid;

        [Header("运动模式")]
        [Tooltip("初始运动模式。Game.unity 默认 Hover（手感不变）；重力层测试/玩法设 Gravity")]
        public MovementMode startMode = MovementMode.Hover;

        [Header("重力模式手感（仅 startMode=Gravity 生效）")]
        [Tooltip("喷气【关闭】时的引擎重力系数（乘 Physics2D.gravity）。1 ≈ 正常重力；0 = 无重力漂移")]
        [Range(0f, 3f)] public float fallGravity = 1f;
        [Tooltip("喷气【关闭】时按住 S/↓ 的附加下潜加速度（m/s²），叠加在引擎重力上，下坠更可控")]
        public float sinkAccel = 8f;
        [Tooltip("下落终端速度上限（m/s）：重力加速到这个值就封顶，下坠有加速过程但不会无限变快、落地不突兀")]
        public float maxFallSpeed = 7f;
        [Tooltip("进入 Gravity 模式时的初始喷气状态。false = 默认关喷气（受重力，按空格才起飞）；true = 一开始就反重力悬浮")]
        public bool startJetting = false;

        [Header("悬浮移动手感（悬浮开启 / Hover 模式）")]
        [Tooltip("悬浮移动加速度（m/s²）：按住方向后从零加速到满速的快慢。越大越跟手，越小越有漂移惯性")]
        public float hoverAccel = 14f;
        [Tooltip("悬浮松键缓冲减速（m/s²）：松开方向键后滑行减速到停的快慢。越小滑得越远、缓冲感越强")]
        public float hoverDecel = 10f;

        [Header("行走模式手感（仅 Walk · 喷气关闭时）")]
        [Tooltip("地面起步加速度（m/s²）。比悬浮大，走路跟手不肉")]
        public float walkAccel = 25f;
        [Tooltip("地面松键摩擦减速（m/s²）。比悬浮大很多，走两步就停，不像滑冰")]
        public float walkFriction = 30f;
        [Tooltip("空中水平控制系数：离地后左右推动乘以它。1 = 空中与地面一样灵活，0 = 空中完全失控")]
        [Range(0f, 1f)] public float airControl = 0.55f;
        [Tooltip("脚下落地探测余量（世界单位）：站定后允许脚下最多悬空这么远仍算着地")]
        public float groundProbe = 0.06f;
        [Tooltip("重落地提示阈值（m/s）：下落速度达到它才提示「重重落地」。0 = 不提示")]
        public float hardLandingSpeed = 5f;

        [Header("手感")]
        [Tooltip("手动挖掘的扫描射程（世界单位，超出 1.2 的探测余量）。调大 = 能隔空挖到更远的格")]
        public float probeDistance = 0.7f;

        [Tooltip("未升级时的基础移动速度")]
        public float baseMoveSpeed = 4f;

        [Header("碰撞")]
        [Tooltip("玩家碰撞半径（单位：格）。用于网格阻挡，需略小于 0.5 才不会卡在通道口")]
        public float playerRadius = 0.36f;

        [Header("燃料消耗（每秒）")]
        public float fuelIdleDrain = 0.5f;
        public float fuelMoveDrain = 1.1f;
        public float fuelDrillDrain = 2.0f;

        [Tooltip("向上移动（对抗重力）的燃料倍率")]
        public float upwardFuelMultiplier = 2.2f;

        [Header("危险")]
        [Tooltip("落石砸中的伤害")]
        public float rockDamage = 12f;

        [Tooltip("基础安全深度，超过后开始过热。仅在拿不到 UpgradeSystem 时作为兜底；正常走 UpgradeSystem.SafeDepth（v2：250 + 70/级）")]
        public float baseSafeDepth = 250f;

        [Tooltip("每超出 1 格安全深度，每秒受到的伤害")]
        public float heatDamagePerDepth = 0.06f;

        [Header("重生恢复比例（堵死「免费传送」漏洞）")]
        [Tooltip("重生后燃料恢复比例。设为 1 = 免费满油，会让「回程主动烧光燃料」变成最优解，核心张力失效")]
        [Range(0f, 1f)] public float respawnFuelRatio = 0.6f;
        [Tooltip("重生后服体完整度恢复比例")]
        [Range(0f, 1f)] public float respawnHullRatio = 0.5f;

        [Header("载重惩罚（Motherload 核心张力）")]
        [Tooltip("满载时【上升】推力的衰减比例。只惩罚向上 —— 下潜永远轻松，回程才是结算时刻。\n" +
                 "设为 0 则重量只影响油耗")]
        [Range(0f, 0.9f)] public float weightClimbPenalty = 0.45f;

        [Tooltip("满载时【向上】油耗的增加比例")]
        [Range(0f, 2f)] public float weightFuelPenalty = 0.35f;

        [Header("提示节流")]
        [Tooltip("同一条提示的重复弹出冷却（秒）。钻探每 0.3s 一格，不节流会满屏刷提示")]
        public float messageThrottle = 1.5f;

        // ---------- 运行时状态（供 HUD / 地表读取） ----------
        public float Fuel { get; private set; }
        public float MaxFuel { get; private set; } = 100f;
        public float Hull { get; private set; }
        public float MaxHull { get; private set; } = 100f;
        /// <summary>已占用的背包格数（含大件附属格）</summary>
        public int CargoCount { get; private set; }

        /// <summary>背包总格数（6 列 × 行数）。只是容器上限，真正的容量看 MaxCarryWeight</summary>
        public int CargoCapacity { get; private set; } = InventoryGrid.Columns * 2;

        public int CargoValue { get; private set; }

        /// <summary>当前载重</summary>
        public float CargoWeight { get; private set; }

        /// <summary>载重上限 —— 这才是真正的容量</summary>
        public float MaxCarryWeight { get; private set; } = 24f;

        /// <summary>载重比 0~1。1 = 满载，爬升最慢、油耗最高</summary>
        public float LoadRatio { get; private set; }

        public bool IsDead { get; private set; }
        public int CurrentDepth { get; private set; }

        /// <summary>喷气（反重力悬浮）是否开启。Hover 恒为悬浮；Gravity/Walk 由空格切换。</summary>
        public bool Jetting { get; private set; }

        /// <summary>是否脚踏实地站在方块上（Walk 模式核心状态；其余模式近似维护）。</summary>
        public bool Grounded { get; private set; }

        /// <summary>落地瞬间触发（参数 = 落地前的下落速度，正值）。用于音效 / 镜头震动 / 坠落伤害。</summary>
        public event Action<float> OnLanded;

        /// <summary>当前钻探进度 0~1（HUD 用于画进度条）</summary>
        public float DigProgress01 => digTargetTime > 0f ? Mathf.Clamp01(digProgress / digTargetTime) : 0f;

        /// <summary>最近一次操作提示（HUD 显示）</summary>
        public string LastMessage { get; private set; } = "";

        Rigidbody2D rb;
        UpgradeSystem upgrades;
        MovementMode currentMode;

        /// <summary>
        /// 格子背包：存每格的【种类 + 数量】，而不是只存总价值。
        /// 存明细才能按种类丢弃、显示清单、让 M4 残骸有落脚点。
        /// 容量由「格数 + 载重上限」双重约束，见 InventoryGrid 类注释。
        /// </summary>
        readonly InventoryGrid inventory = new InventoryGrid();

        /// <summary>格子背包（供背包 UI 读取与操作）</summary>
        public InventoryGrid Inventory => inventory;

        // 多击挖掘进度（复用原进度条字段，HUD 兼容）
        Vector2Int digCell;       // 当前敲的目标格
        bool isDigging;           // 正在敲（HUD 显示进度条）
        float digProgress;        // 已击次数
        float digTargetTime;      // 该格总击打次数（TileDefinition.digHits）
        float messageTimer;

        string lastThrottledMessage = "";
        float throttleCooldown;

        void Awake()
        {
            rb = GetComponent<Rigidbody2D>();
            rb.freezeRotation = true;
            rb.interpolation = RigidbodyInterpolation2D.Interpolate;
            SetMovementMode(startMode);   // 取代硬编码 gravityScale=0，支持 Hover/Gravity 共存
        }

        void Start()
        {
            upgrades = GameManager.Instance != null ? GameManager.Instance.Upgrades : null;
            ApplyUpgradeStats();
            FullRestore();

            if (grid != null)
            {
                grid.OnRockFell += HandleRockFell;
                grid.OnTileDug += HandleTileDug;
            }

            // 背包任何变动（挖掘入包、手动丢弃、容量变化）都自动同步对外只读属性，
            // 这样背包 UI 直接改 inventory 即可，不必记得调本类的刷新方法。
            inventory.OnChanged += SyncCargoStats;

            if (GameManager.Instance != null) GameManager.Instance.RegisterPlayer(this, transform.position);
        }

        void OnDestroy()
        {
            if (grid != null)
            {
                grid.OnRockFell -= HandleRockFell;
                grid.OnTileDug -= HandleTileDug;
            }
            inventory.OnChanged -= SyncCargoStats;
        }

        /// <summary>
        /// 切换运动模式。
        /// Hover = 悬浮（gravityScale=0，8 向直驱，现状）；Gravity = 喷气重力模式
        /// （初始喷气状态由 startJetting 决定，之后用 SetJetting/ToggleJetting 切）。
        /// </summary>
        public void SetMovementMode(MovementMode mode)
        {
            currentMode = mode;
            if (rb == null) rb = GetComponent<Rigidbody2D>();
            rb.linearDamping = 0f;

            if (mode == MovementMode.Hover)
            {
                Jetting = false;                 // Hover 没有喷气概念
                rb.gravityScale = 0f;
            }
            else
            {
                Jetting = startJetting;          // 进入重力层时按 startJetting 决定初始是否悬浮
                ApplyJetPhysics();
            }
        }

        /// <summary>玩家开/关喷气（反重力悬浮）。Gravity / Walk 有效，会刷提示。</summary>
        public void ToggleJetting()
        {
            if (currentMode == MovementMode.Hover) return;
            SetJetting(!Jetting);
        }

        /// <summary>设置喷气状态。silent=true 时用于脚本/模式切换强制，不刷提示；玩家手动切会提示。</summary>
        public void SetJetting(bool on, bool silent = false)
        {
            if (currentMode == MovementMode.Hover) return;
            if (Jetting == on) return;
            Jetting = on;
            ApplyJetPhysics();
            if (!silent)
                ShowMessage(on ? "喷气开启 · 反重力悬浮" : "喷气关闭 · 引擎重力", 0.8f);
        }

        /// <summary>把当前喷气状态同步到 Rigidbody2D：悬浮时抵消重力，否则交给引擎重力（Hover 永远无重力）。</summary>
        void ApplyJetPhysics()
        {
            rb.gravityScale = (!Jetting && currentMode != MovementMode.Hover) ? fallGravity : 0f;
        }

        // ---------- 主循环 ----------

        void Update()
        {
            if (IsDead || grid == null) return;

            if (Fuel <= 0f) { Die("燃料耗尽，困死地下"); return; }
            if (Hull <= 0f) { Die("船体损毁"); return; }

            // 空格 = 喷气开关（Gravity / Walk；Hover 本来就悬浮，空格无意义）
            if (currentMode != MovementMode.Hover && Input.GetKeyDown(KeyCode.Space))
                ToggleJetting();

            Vector2 input = ReadInput();

            // 手动挖掘（纯手动）：单击鼠标左键 = 朝当前按住的移动键方向，立即挖穿一格。
            // 不再有「按住方向键碰墙自动钻」——移动撞墙只停住，想挖就点左键。
            // 指针停在 UI 上（背包面板）时不挖 —— 否则「点格子丢弃」会顺带挖穿一格方块。
            if (Input.GetMouseButtonDown(0) && !IsPointerOverUI())
                TryManualDig(input);

            DrainFuel(input);
            ApplyHeat();
            ApplyHazard();
            TrackDepth();

            if (messageTimer > 0f)
            {
                messageTimer -= Time.deltaTime;
                if (messageTimer <= 0f) LastMessage = "";
            }

            if (throttleCooldown > 0f)
            {
                throttleCooldown -= Time.deltaTime;
                if (throttleCooldown <= 0f) lastThrottledMessage = "";
            }
        }

        /// <summary>
        /// 鼠标是否停在 UGUI 元素上。
        /// Input.GetMouseButtonDown 是底层 API，不受 UI 事件系统拦截，
        /// 所以点了背包格子仍会穿透触发挖掘 —— 必须自己问 EventSystem。
        /// 注意：用 `using UnityEngine.EventSystems;` 引入类型后，要写 `EventSystem.current`，
        /// 不能写 `EventSystems.EventSystem.current` —— `using` 不会把嵌套命名空间本身作为标识符暴露。
        /// </summary>
        static bool IsPointerOverUI()
        {
            var es = EventSystem.current;
            return es != null && es.IsPointerOverGameObject();
        }

        void FixedUpdate()
        {
            if (IsDead) { rb.linearVelocity = Vector2.zero; return; }

            Vector2 input = ReadInput();
            float speed = upgrades != null ? upgrades.MoveSpeed : baseMoveSpeed;

            // 四种物理行为分流：
            //   Hover          = 直驱悬浮（原型手感，Game.unity 用，保持原样）
            //   喷气开启       = 力驱动悬浮（Motherload 惯性对抗，Gravity / Walk 共用）
            //   Walk 关喷气    = 脚踏实地行走（地面加速度/摩擦 + 重力坠落）
            //   Gravity 关喷气 = 引擎重力滑翔（原样）
            if (currentMode == MovementMode.Hover)
                HoverMove(input, speed);
            else if (Jetting)
                JetMove(input, speed);
            else if (currentMode == MovementMode.Walk)
                WalkMove(input, speed);
            else
                GlideMove(input, speed);
        }

        /// <summary>Hover 模式：直驱悬浮。网格阻挡分轴清零，玩家被顶住后靠手动挖掘开路。</summary>
        void HoverMove(Vector2 input, float speed)
        {
            Vector2 vel = input * speed;
            if (grid != null && vel.sqrMagnitude > 0.0001f)
            {
                Vector2 next = rb.position + vel * Time.fixedDeltaTime;
                if (BlockedByTerrain(new Vector2(next.x, rb.position.y))) vel.x = 0f;
                if (BlockedByTerrain(new Vector2(rb.position.x, next.y))) vel.y = 0f;
            }
            rb.linearVelocity = vel;
            Grounded = false;
        }

        /// <summary>
        /// 喷气悬浮（Gravity / Walk 开喷气）：力驱动，Motherload 手感核心。
        /// 速度按 hoverAccel 趋近目标、松键按 hoverDecel 滑行减速 —— 不是瞬变的。
        /// 下落中点火：先被惯性带着继续冲一段，才被推力慢慢拉回上升（约 0.5s 对抗期）。
        /// </summary>
        void JetMove(Vector2 input, float speed)
        {
            Vector2 target = input * speed;
            float acc = input.sqrMagnitude > 0.01f ? hoverAccel : hoverDecel;

            // 载重惩罚：【只惩罚向上】—— 下潜永远轻松，回程才是结算时刻。
            // 这是 Motherload 节奏感的来源：下去时一路顺畅，满载爬升时才感到沉重。
            // 水平移动与下落完全不受影响，所以「装满」从不影响探索，只影响回家。
            if (target.y > 0.05f)
                acc *= 1f - LoadRatio * weightClimbPenalty;

            Vector2 vel = Vector2.MoveTowards(rb.linearVelocity, target, acc * Time.fixedDeltaTime);

            if (grid != null && vel.sqrMagnitude > 0.0001f)
            {
                Vector2 next = rb.position + vel * Time.fixedDeltaTime;
                if (BlockedByTerrain(new Vector2(next.x, rb.position.y))) vel.x = 0f;
                if (BlockedByTerrain(new Vector2(rb.position.x, next.y))) vel.y = 0f;
            }
            rb.linearVelocity = vel;
            Grounded = false;
        }

        /// <summary>
        /// Walk 模式 · 关喷气：脚踏实地行走。
        /// 地面（Grounded）：水平按 walkAccel/walkFriction 趋近目标，vy 清零并吸附格顶（站稳不抖）；
        /// 空中：引擎重力累积下落 + 终端限速 + 落地顶回（带冲击反馈）。
        /// input.y 完全不参与位移 —— 只喂给 TryManualDig 当挖掘方向（按住 S+左键 = 向下挖）。
        /// 不做爬台阶：一格高的墙也照撞停，想上去只能开喷气或挖掉。
        /// </summary>
        void WalkMove(Vector2 input, float speed)
        {
            float dt = Time.fixedDeltaTime;
            float r = playerRadius;

            // 1. 落地探测：脚下 groundProbe 距离内有实心格顶面，且【不是正在明显下落】。
            //    vy > -0.5：站稳时 vy 每帧被清零、最多累积一帧重力约 -0.2；下落中很快跌破 -0.5。
            //    没有这条的话，高速下落经过地面附近会被误判 grounded → 直接吸附 vy=0，
            //    跳过 else 分支的落地检测 → Land 永不触发，落地冲击反馈丢失（实测踩坑）。
            float footY = rb.position.y - r;
            float groundTop = grid != null ? GroundTopAt(rb.position.x, footY - groundProbe)
                                           : float.NegativeInfinity;
            bool grounded = !float.IsNegativeInfinity(groundTop) && rb.linearVelocity.y > -0.5f;

            // 2. 水平：地面加速度起步 / 摩擦停步；离地后操控被削弱
            float target = input.x * speed;
            float acc = Mathf.Abs(input.x) > 0.1f ? walkAccel : walkFriction;
            if (!grounded) acc *= airControl;
            float vx = Mathf.MoveTowards(rb.linearVelocity.x, target, acc * dt);

            // 3. 前方墙阻挡：一格高也照撞，不爬台阶
            if (Mathf.Abs(vx) > 0.0001f)
            {
                Vector2 probeX = rb.position + new Vector2(vx * dt, 0f);
                if (BlockedByTerrain(probeX)) vx = 0f;
            }

            // 4. 竖直：站稳 / 坠落
            float vy = rb.linearVelocity.y;
            if (grounded)
            {
                vy = 0f;
                rb.position = new Vector2(rb.position.x, groundTop + r + 0.001f); // 吸附格顶防微沉
            }
            else
            {
                vy = Mathf.Max(vy, -maxFallSpeed);
                if (grid != null && vy < 0f)
                {
                    float yLowNext = footY + vy * dt;
                    float topY = GroundTopAt(rb.position.x, yLowNext);
                    if (!float.IsNegativeInfinity(topY))
                    {
                        float impact = -vy;
                        rb.position = new Vector2(rb.position.x, topY + r + 0.001f);
                        vy = 0f;
                        grounded = true;
                        Land(impact);
                    }
                }
            }

            rb.linearVelocity = new Vector2(vx, vy);
            Grounded = grounded;
        }

        /// <summary>Gravity 模式 · 关喷气：引擎重力滑翔（原实现，仅补落地冲击反馈）。</summary>
        void GlideMove(Vector2 input, float speed)
        {
            float vx = input.x * speed;
            if (grid != null && Mathf.Abs(vx) > 0.0001f)
            {
                Vector2 probeX = rb.position + new Vector2(vx * Time.fixedDeltaTime, 0f);
                if (BlockedByTerrain(probeX)) vx = 0f;
            }

            float vy = rb.linearVelocity.y;                     // 引擎重力已累积的下落速度
            if (input.y < -0.1f) vy -= sinkAccel * Time.fixedDeltaTime;   // S/↓ 主动下潜
            vy = Mathf.Max(vy, -maxFallSpeed);                  // 终端速度：下坠有加速过程但封顶，缓冲落地

            // 落地阻挡（修复穿地 bug 的实现，保持原样）：
            // 每帧用【下缘三点】探测下一帧下缘位置是否进入实心格，命中就把玩家
            // 直接顶回该格顶面之上。不依赖「中间安全点」，已入地也能自恢复，天然防高速穿地。
            if (grid != null && vy < 0f)
            {
                float r = playerRadius;
                float yLowNext = rb.position.y - r + vy * Time.fixedDeltaTime;
                float topY = GroundTopAt(rb.position.x, yLowNext);
                if (!float.IsNegativeInfinity(topY))
                {
                    float impact = -vy;
                    rb.position = new Vector2(rb.position.x, topY + r + 0.001f); // 微间隙防浮点再入
                    vy = 0f;
                    Land(impact);
                }
            }

            rb.linearVelocity = new Vector2(vx, vy);
            Grounded = grid != null && vy == 0f
                && !float.IsNegativeInfinity(GroundTopAt(rb.position.x, rb.position.y - playerRadius - 0.02f));
        }

        /// <summary>落地冲击：Motherload 的「墩」感。广播事件（后续接音效/镜头震/坠落伤害），超阈值刷提示。</summary>
        void Land(float impactSpeed)
        {
            OnLanded?.Invoke(impactSpeed);
            if (hardLandingSpeed > 0f && impactSpeed >= hardLandingSpeed)
                ShowMessageThrottled("重重落地！", 0.6f);
            Debug.Log($"[DrillVehicle] 落地 impact={impactSpeed:F2} grounded={Grounded}");
        }

        /// <summary>
        /// 以玩家包围盒四角采样，判断该中心位置是否会卡进实心格。
        /// 下缘两点抬高 groundSkin（0.02）：贴地时引擎重力每帧会把玩家下压约 0.004 格，
        /// 不抬的话下角会误判穿进脚下格顶面 → 贴地水平移动被误当「卡墙」每帧清零
        /// （这正是「移动键没反应」的真根因，曾误诊为贴墙）。真墙是整格高，0.02 余量不会穿透。
        /// </summary>
        bool BlockedByTerrain(Vector2 center)
        {
            float r = playerRadius;
            const float groundSkin = 0.02f;
            return grid.IsSolidAtWorld(center + new Vector2(-r, -r + groundSkin))
                || grid.IsSolidAtWorld(center + new Vector2(r, -r + groundSkin))
                || grid.IsSolidAtWorld(center + new Vector2(-r, r))
                || grid.IsSolidAtWorld(center + new Vector2(r, r));
        }

        /// <summary>
        /// 落地探测：检查世界高度 yLow（玩家下缘）在水平 x 处是否已进入实心格。
        /// 取 x-r / x / x+r 三个下缘点中【最浅】的阻挡格，返回其顶面世界 y（格 y 占世界
        /// [-gy-1, -gy]，顶面 = -gy）；三处都未入地则返回 -Infinity。
        /// 用三点是为了玩家半格悬空站在坑口/平台边缘时也能找到脚下真实支撑，不会卡墙。
        /// </summary>
        float GroundTopAt(float x, float yLow)
        {
            float best = float.NegativeInfinity;
            for (int k = -1; k <= 1; k++)
            {
                Vector2Int c = grid.WorldToGrid(new Vector2(x + k * playerRadius, yLow));
                if (grid.IsSolid(c.x, c.y))
                {
                    float top = -c.y;                 // 该实心格的顶面世界 y
                    if (top > best) best = top;
                }
            }
            return best;
        }

        Vector2 ReadInput()
        {
            // 直接读按键：不依赖 InputManager.asset 里的 Horizontal/Vertical 轴向配置，
            // 新建工程即使没生成输入配置也能立刻操作。
            float h = 0f, v = 0f;

            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) h -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) h += 1f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) v += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) v -= 1f;

            var dir = new Vector2(h, v);
            return dir.sqrMagnitude > 1f ? dir.normalized : dir;
        }

        // ---------- 手动挖掘 ----------

        /// <summary>
        /// 手动挖一格（多击制）：单击左键 = 敲一击。
        /// 前方"碰撞体" = 从玩家表面沿输入方向步进扫描的探测带，碰到【最近的实心格】
        /// 该格即成为挖掘目标；每敲一击 digProgress+1，打满 TileDefinition.digHits 才挖穿。
        /// 目标格切换或松开方向键 → 进度清零重计。硬度门槛照旧（钻头不足打不动）。
        /// </summary>
        void TryManualDig(Vector2 input)
        {
            if (input.sqrMagnitude < 0.01f)
            {
                ShowMessageThrottled("按住方向键选择挖掘方向", 0.6f);
                return;
            }

            Vector2 dir = input.normalized;
            Vector2Int? hit = null;
            // 前方碰撞体探测：从玩家表面起，沿方向步进采样，找第一个实心格。
            // 扫描上界 = probeDistance + 1.2：够到「紧贴玩家那一格」之后的一格，
            // 保证玩家站格边缘贴墙时也能命中隔壁墙格（探针不再因 0.7<1 而够不到墙）。
            float maxD = probeDistance + 1.2f;
            for (float d = playerRadius + 0.05f; d <= maxD; d += 0.2f)
            {
                Vector2Int c = grid.WorldToGrid((Vector2)transform.position + dir * d);
                var def = grid.GetTile(c.x, c.y);
                if (def != null && def.isSolid) { hit = c; break; }
            }

            if (hit == null)
            {
                ShowMessageThrottled("这个方向没有可挖的墙", 0.5f);
                return;
            }

            Vector2Int cell = hit.Value;
            var tdef = grid.GetTile(cell.x, cell.y);
            int drillLevel = upgrades != null ? upgrades.DrillLevel : 1;
            if (tdef.hardness > drillLevel)
            {
                ResetDig();
                ShowMessageThrottled($"钻头不足：需要 Lv{tdef.hardness}（当前 Lv{drillLevel}）", 0.5f);
                return;
            }

            // DEV-001：耐久由 DigGrid 承载。玩家每次点击 = 调 HitBlock 一次。
            // digProgress/digTargetTime 保留给 HUD 显示，但数据源 = DigGrid.curDurability
            if (!isDigging || cell != digCell)
            {
                digCell = cell;
                isDigging = true;
            }

            digTargetTime = Mathf.Max(1, tdef.digHits);

            if (grid.HitBlock(cell.x, cell.y, out var dug))
            {
                int curAfter = grid.GetDurability(cell.x, cell.y);
                digProgress = digTargetTime - Mathf.Max(0, curAfter);   // 已击数 = max - cur

                if (dug != null)
                {
                    // 崩碎：走 OnTileDug → HandleTileDug（含背包添加）
                }
                else if (digTargetTime > 1f)
                {
                    ShowMessageThrottled($"{tdef.displayName}：{Mathf.RoundToInt(digProgress)}/{Mathf.RoundToInt(digTargetTime)} 击", 0.35f);
                }
            }
        }

        void ResetDig()
        {
            isDigging = false;
            digProgress = 0f;
            digTargetTime = 0f;
        }

        void HandleTileDug(Vector2Int cell, TileDefinition def)
        {
            ResetDig();

            if (def == null || def.value <= 0) return;

            int left = inventory.AddItem(def, 1);

            if (left > 0)
            {
                // 装不下（超重 或 格子满）：若新矿的【价值密度】高于舱内最差的那一格，
                // 就丢掉那一格的东西换进来 —— 这就是 Motherload 的「丢铁矿换钻石」。
                //
                // 为什么比密度而不是比单价：一整堆铁矿总价可能高于一颗钻石，
                // 但它占掉的载重能换回更多钻石，所以该丢的仍然是铁矿。
                //
                // 为什么要循环：丢一件铁矿只腾出 1.0 载重，而钻石要 3.5，
                // 得连续丢几件才塞得下。guard 只是防死循环。
                bool replaced = false;
                int guard = 0;
                while (guard++ < 64)
                {
                    int worst = inventory.IndexOfLowestValueDensity(def);
                    if (worst < 0) break;                      // 舱内没有更差的了，不换
                    if (inventory.RemoveAt(worst, 1) <= 0) break;
                    replaced = true;
                    if (inventory.AddItem(def, 1) == 0) break; // 腾够了
                }

                if (replaced) ShowMessageThrottled("载重已满：已丢弃价值密度最低的物资", 1.0f);
                else ShowMessageThrottled("背包空间或载重已满，物资被丢弃！", 0.8f);
            }

            SyncCargoStats();
        }

        /// <summary>把背包状态同步到对外只读属性（供 HUD 与地表结算读取）</summary>
        void SyncCargoStats()
        {
            CargoCount = inventory.UsedSlots;
            CargoValue = inventory.TotalValue;
            CargoWeight = inventory.TotalWeight;
            LoadRatio = inventory.LoadRatio;
        }

        /// <summary>是否正在钻探（HUD 用于显示进度条）</summary>
        public bool IsDigging => isDigging;

        // ---------- 资源消耗 ----------

        void DrainFuel(Vector2 input)
        {
            float drain = fuelIdleDrain;

            if (input.sqrMagnitude > 0.01f)
            {
                drain = fuelMoveDrain;
                // 向上对抗重力更费油；背得越重，爬升烧得越快
                if (input.y > 0.1f) drain *= upwardFuelMultiplier * (1f + LoadRatio * weightFuelPenalty);
                if (isDigging) drain += fuelDrillDrain;
            }
            else if (isDigging)
            {
                drain += fuelDrillDrain;
            }

            Fuel = Mathf.Max(0f, Fuel - drain * Time.deltaTime);
        }

        void ApplyHeat()
        {
            // 安全深度由 UpgradeSystem 单点定义（v2：250 + 70/级），
            // 不再由本类用冷却系数二次换算。拿不到 UpgradeSystem 时才回退到 baseSafeDepth。
            float safe = upgrades != null ? upgrades.SafeDepth : baseSafeDepth;
            if (CurrentDepth <= safe) return;

            float over = CurrentDepth - safe;
            Damage(over * heatDamagePerDepth * Time.deltaTime);
        }

        void ApplyHazard()
        {
            var cell = grid.WorldToGrid(transform.position);
            var here = grid.GetTile(cell.x, cell.y);
            if (here != null && here.isHazard)
                Damage(here.hazardDamage * Time.deltaTime);
        }

        void TrackDepth()
        {
            CurrentDepth = Mathf.Max(0, grid.DepthOf(transform.position));
            if (GameManager.Instance != null && CurrentDepth > GameManager.Instance.MaxDepthReached)
                GameManager.Instance.MaxDepthReached = CurrentDepth;
        }

        void HandleRockFell(Vector2Int cell)
        {
            if (IsDead) return;
            if (grid.WorldToGrid(transform.position) == cell)
            {
                Damage(rockDamage);
                ShowMessage("被落石砸中！", 1f);
            }
        }

        // ---------- 状态变更 ----------

        public void Damage(float amount)
        {
            if (IsDead) return;
            Hull = Mathf.Max(0f, Hull - amount);
        }

        public void ApplyUpgradeStats()
        {
            if (upgrades == null)
            {
                MaxFuel = 100f; MaxHull = 100f;
                CargoCapacity = InventoryGrid.Columns * 2;
                MaxCarryWeight = 24f;
                inventory.Resize(2, MaxCarryWeight);
                SyncCargoStats();
                return;
            }

            MaxFuel = upgrades.MaxFuel;
            MaxHull = upgrades.MaxHull;
            CargoCapacity = upgrades.CargoCapacity;
            MaxCarryWeight = upgrades.MaxCarryWeight;

            // 货舱升级 = 加一行格子 + 提高载重上限
            inventory.Resize(upgrades.CargoRows, MaxCarryWeight);
            SyncCargoStats();

            Fuel = Mathf.Min(Fuel, MaxFuel);
            Hull = Mathf.Min(Hull, MaxHull);
        }

        public void FullRestore()
        {
            ApplyUpgradeStats();
            Fuel = MaxFuel;
            Hull = MaxHull;
            IsDead = false;
        }

        /// <summary>清空货舱并返回其价值</summary>
        public int UnloadCargo()
        {
            int value = CargoValue;
            inventory.Clear();
            SyncCargoStats();
            return value;
        }

        /// <summary>按可用现金加油：能加多少加多少，返回实际花费（钱不够只加一部分）</summary>
        public int Refuel(float pricePerUnit, int availableCash)
        {
            float need = MaxFuel - Fuel;
            if (need <= 0f) return 0;

            float affordable = pricePerUnit > 0f ? availableCash / pricePerUnit : need;
            float toAdd = Mathf.Min(need, affordable);
            if (toAdd <= 0f) return 0;

            Fuel = Mathf.Min(MaxFuel, Fuel + toAdd);
            return Mathf.CeilToInt(toAdd * pricePerUnit);
        }

        /// <summary>按可用现金维修：能修多少修多少，返回实际花费</summary>
        public int Repair(float pricePerPoint, int availableCash)
        {
            float need = MaxHull - Hull;
            if (need <= 0f) return 0;

            float affordable = pricePerPoint > 0f ? availableCash / pricePerPoint : need;
            float toAdd = Mathf.Min(need, affordable);
            if (toAdd <= 0f) return 0;

            Hull = Mathf.Min(MaxHull, Hull + toAdd);
            return Mathf.CeilToInt(toAdd * pricePerPoint);
        }

        void Die(string reason)
        {
            if (IsDead) return;
            IsDead = true;
            rb.linearVelocity = Vector2.zero;
            ShowMessage(reason, 3f);
            Debug.Log($"[DrillVehicle] 任务失败：{reason}");

            if (GameManager.Instance != null) GameManager.Instance.OnPlayerDied(reason, CurrentDepth);
        }

        /// <summary>
        /// 失败后回到地表重生：货舱清空（损失），现金扣打捞费（GameManager 处理），
        /// 燃料与服体【只部分恢复】。
        /// 【为什么不再满状态】原实现是 FullRestore 满油满血且不扣现金，
        /// 于是产生最优解：回程货舱空了就主动把燃料烧完 = 免费传送回地表 + 满状态，
        /// 还省了爬上去的油钱和时间。而「回程燃料够不够」正是全部紧张感的来源。
        /// </summary>
        public void RespawnAtSurface(Vector3 spawnPos)
        {
            transform.position = spawnPos;
            inventory.Clear();
            SyncCargoStats();

            ApplyUpgradeStats();
            Fuel = MaxFuel * respawnFuelRatio;
            Hull = Mathf.Max(1f, MaxHull * respawnHullRatio);   // 至少留 1 点，避免一落地就再次判定死亡
            IsDead = false;
            ResetDig();
        }

        void ShowMessage(string msg, float duration)
        {
            LastMessage = msg;
            messageTimer = duration;
        }

        /// <summary>
        /// 节流版提示：同一条消息在冷却期内不重复弹出。
        /// 钻一格只要 0.3 秒，而提示持续 1.2 秒 —— 不节流的话满舱提示会常驻刷屏。
        /// </summary>
        void ShowMessageThrottled(string msg, float duration)
        {
            if (msg == lastThrottledMessage && throttleCooldown > 0f) return;
            lastThrottledMessage = msg;
            throttleCooldown = Mathf.Max(duration, messageThrottle);
            ShowMessage(msg, duration);
        }
    }
}
