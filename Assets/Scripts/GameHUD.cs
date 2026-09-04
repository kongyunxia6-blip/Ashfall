using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// 原型 HUD：用 IMGUI 绘制，零 UI 依赖 —— 挂到场景任意物体上就能看到全部状态。
    /// 后续可替换为 uGUI / TextMeshPro（保持读取同样的公开属性即可）。
    /// 操作：U 开关升级商店（DEV-006 起需靠近装备工作台；场景无工作台时回退地表任意位置），1~6 购买对应部件。
    /// 右侧面板按当前所在功能区显示对应提示（卖矿 / 升级 / 补给），不串台。
    /// </summary>
    public class GameHUD : MonoBehaviour
    {
        [Header("开关")]
        public bool showHelp = true;
        public KeyCode shopToggleKey = KeyCode.U;

        [Header("DEV-009 深度/区域（留空 = 场景无区域系统，回退旧「当前深度」显示）")]
        [Tooltip("区域推进系统。赋值时左上显示『深度 x m | 区域：xx』与首次进入横幅")]
        public DepthRegionProgression regionProgression;

        bool shopOpen;
        bool stylesReady;
        GUIStyle labelStyle;
        GUIStyle barLabelStyle;
        GUIStyle centerStyle;
        GUIStyle panelStyle;

        // ---------- 输入 ----------

        void Update()
        {
            var gm = GameManager.Instance;
            bool canShop = gm != null && gm.IsAtSurface
                && (UpgradeWorkbench.PlayerInRange || !UpgradeWorkbench.AnyExists);

            if (Input.GetKeyDown(shopToggleKey))
            {
                if (canShop) shopOpen = !shopOpen;
                else
                {
                    if (gm != null && gm.IsAtSurface)
                        gm.LastServiceMessage = "升级需靠近装备工作台";
                    shopOpen = false;
                }
            }
            else if (shopOpen && !canShop)
            {
                shopOpen = false;   // 离开工作台范围 / 离开地表：自动关面板
            }

            if (!shopOpen) return;
            if (gm == null || gm.Upgrades == null) return;

            if (Input.GetKeyDown(KeyCode.Alpha1)) TryBuy(UpgradePart.Drill);
            if (Input.GetKeyDown(KeyCode.Alpha2)) TryBuy(UpgradePart.Hull);
            if (Input.GetKeyDown(KeyCode.Alpha3)) TryBuy(UpgradePart.Engine);
            if (Input.GetKeyDown(KeyCode.Alpha4)) TryBuy(UpgradePart.FuelTank);
            if (Input.GetKeyDown(KeyCode.Alpha5)) TryBuy(UpgradePart.Radiator);
            if (Input.GetKeyDown(KeyCode.Alpha6)) TryBuy(UpgradePart.CargoBay);
        }

        void TryBuy(UpgradePart part)
        {
            var gm = GameManager.Instance;
            if (gm == null || gm.Upgrades == null) return;

            var up = gm.Upgrades;
            int cost = up.GetCost(part);

            if (!up.TryBuy(part))
                gm.LastServiceMessage = $"现金不足：{up.GetDisplayName(part)} 需要 ${cost}";
            else
                gm.LastServiceMessage = $"已升级 {up.GetDisplayName(part)} → Lv{up.GetLevel(part)}";
        }

        // ---------- 绘制 ----------

        void InitStyles()
        {
            if (stylesReady) return;

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };

            barLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };

            centerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.yellow }
            };

            panelStyle = new GUIStyle(GUI.skin.box);
            panelStyle.normal.background = MakeSolidTexture(new Color(0f, 0f, 0f, 0.75f));

            stylesReady = true;
        }

        void OnGUI()
        {
            InitStyles();

            var gm = GameManager.Instance;
            var p = gm != null ? gm.Player : null;

            // 左上：经济与深度
            GUILayout.BeginArea(new Rect(12, 12, 300, 120));
            GUILayout.Label($"现金   ${(gm != null ? gm.Cash : 0)}", labelStyle);
            var prog = regionProgression != null ? regionProgression
                : (gm != null ? gm.GetComponent<DepthRegionProgression>() : null);
            if (prog != null)
            {
                // DEV-009：区域语义显示（地表显示 0m；向下单调增加；本次最深/最深纪录并列）
                var regName = prog.CurrentRegion != null ? prog.CurrentRegion.displayName : "-";
                GUILayout.Label($"深度   {prog.DisplayDepth} m | 区域：{regName}", labelStyle);
                GUILayout.Label($"本次最深   {prog.MaxDepthThisRun} m · 最深纪录   {prog.MaxDepthEver} m", labelStyle);
            }
            else
            {
                // 旧场景回退（无 DepthRegionProgression）
                GUILayout.Label($"当前深度   {(p != null ? p.CurrentDepth : 0)} m", labelStyle);
                GUILayout.Label($"最深纪录   {(gm != null ? gm.MaxDepthReached : 0)} m", labelStyle);
            }
            GUILayout.EndArea();

            // DEV-009：首次进入区域横幅（只出现一次，持续 announceDuration 秒；不遮挡核心画面）
            if (prog != null && prog.AnnounceTimeLeft > 0f && !string.IsNullOrEmpty(prog.RegionEnterAnnounce))
            {
                float fade = Mathf.Clamp01(prog.AnnounceTimeLeft / 0.4f);
                GUI.color = new Color(1f, 0.9f, 0.35f, fade);
                GUI.Label(new Rect(0, Screen.height * 0.16f, Screen.width, 36f),
                    prog.RegionEnterAnnounce, centerStyle);
                GUI.color = Color.white;
            }

            // 左下：三条状态
            if (p != null)
            {
                float y = Screen.height - 40f;
                DrawBar(new Rect(12, y - 68f, 240f, 24f), p.Fuel, p.MaxFuel,
                    new Color(1f, 0.72f, 0.1f), $"燃料 {p.Fuel:F0} / {p.MaxFuel:F0}");
                DrawBar(new Rect(12, y - 40f, 240f, 24f), p.Hull, p.MaxHull,
                    new Color(0.92f, 0.28f, 0.28f), $"船体 {p.Hull:F0} / {p.MaxHull:F0}");
                // 载重条：这才是真正的容量（格子只是容器）。接近满载转暖色，提示「该回去了」
                var cargoColor = p.LoadRatio > 0.85f
                    ? new Color(1f, 0.55f, 0.15f)
                    : new Color(0.3f, 0.8f, 0.92f);
                DrawBar(new Rect(12, y - 12f, 240f, 24f), p.CargoWeight, p.MaxCarryWeight,
                    cargoColor, $"载重 {p.CargoWeight:F1}/{p.MaxCarryWeight:F0} · {p.CargoCount}格 · ${p.CargoValue}");
            }

            // 正下方：钻探进度与提示
            if (p != null && p.IsDigging)
            {
                float w = 200f;
                var rect = new Rect(Screen.width * 0.5f - w * 0.5f, Screen.height - 96f, w, 14f);
                DrawBar(rect, p.DigProgress01, 1f, new Color(0.4f, 0.95f, 0.45f), "");
            }

            string msg = "";
            if (p != null && !string.IsNullOrEmpty(p.LastMessage)) msg = p.LastMessage;
            else if (gm != null && !string.IsNullOrEmpty(gm.LastServiceMessage)) msg = gm.LastServiceMessage;

            if (!string.IsNullOrEmpty(msg))
                GUI.Label(new Rect(0, Screen.height - 128f, Screen.width, 30f), msg, centerStyle);

            // 死亡提示
            if (p != null && p.IsDead)
                GUI.Label(new Rect(0, Screen.height * 0.35f, Screen.width, 40f), "任务失败 — 正在返回地表…", centerStyle);

            // 右侧：帮助 / 升级商店
            DrawRightPanel(gm, p);
        }

        void DrawRightPanel(GameManager gm, DrillVehicle p)
        {
            if (gm == null) return;

            float w = 300f;
            float x = Screen.width - w - 12f;

            GUILayout.BeginArea(new Rect(x, 12f, w, Screen.height - 24f));

            if (gm.IsAtSurface)
            {
                GUI.color = new Color(0.4f, 1f, 0.5f);
                GUILayout.Label("【地表据点 Surface Hub】", labelStyle);
                GUI.color = Color.white;
                GUILayout.Space(4);

                int cargoVal = p != null ? p.CargoValue : 0;
                int fuelNow = p != null ? Mathf.RoundToInt(p.Fuel) : 0;
                int fuelMax = p != null ? Mathf.RoundToInt(p.MaxFuel) : 0;
                bool anyZone = false;

                if (SurfaceBase.PlayerInRange)
                {
                    GUILayout.Label("◆ 登陆舱 / 返航安全区（已安全返回）", labelStyle);
                    anyZone = true;
                }
                if (SellTerminal.PlayerInRange)
                {
                    GUILayout.Label($"◆ 出售终端：按 {KeyCode.E} 出售全部（估值 ${cargoVal}）", labelStyle);
                    anyZone = true;
                }
                if (UpgradeWorkbench.PlayerInRange)
                {
                    GUILayout.Label($"◆ 装备工作台：按 {shopToggleKey} {(shopOpen ? "关闭" : "打开")}升级商店", labelStyle);
                    anyZone = true;
                }
                if (FuelStation.PlayerInRange)
                {
                    GUILayout.Label($"◆ 能源补给点：按 {KeyCode.E} 加油补给", labelStyle);
                    anyZone = true;
                }

                GUILayout.Space(2);
                GUILayout.Label($"货舱估值 ${cargoVal} · 现金 ${gm.Cash} · 燃料 {fuelNow}/{fuelMax}", labelStyle);
                if (!anyZone)
                    GUILayout.Label("移动至功能区：卖矿 → 补给 → 升级；右侧为下矿入口", labelStyle);
            }
            else
            {
                GUI.color = new Color(0.7f, 0.7f, 0.7f);
                GUILayout.Label("【地下】回到地表才能补给与升级", labelStyle);
                GUI.color = Color.white;
            }

            if (showHelp)
            {
                GUILayout.Space(8);
                GUILayout.Label("WASD / 方向键：移动", labelStyle);
                GUILayout.Label("朝方块按住方向键：钻探", labelStyle);
            }

            if (shopOpen && gm.Upgrades != null)
            {
                GUILayout.Space(12);
                GUILayout.Label("── 升级商店（按 1~6 购买）──", labelStyle);

                var up = gm.Upgrades;
                DrawShopRow(1, UpgradePart.Drill, up, gm.Cash);
                DrawShopRow(2, UpgradePart.Hull, up, gm.Cash);
                DrawShopRow(3, UpgradePart.Engine, up, gm.Cash);
                DrawShopRow(4, UpgradePart.FuelTank, up, gm.Cash);
                DrawShopRow(5, UpgradePart.Radiator, up, gm.Cash);
                DrawShopRow(6, UpgradePart.CargoBay, up, gm.Cash);

                GUILayout.Space(8);
                GUILayout.Label($"钻头等级决定你能挖穿的硬度（当前 Lv{up.DrillLevel}）", labelStyle);
                GUILayout.Label($"货舱 {up.CargoCapacity}  ·  油箱 {up.MaxFuel:F0}  ·  船体 {up.MaxHull:F0}", labelStyle);
            }

            GUILayout.EndArea();
        }

        void DrawShopRow(int hotkey, UpgradePart part, UpgradeSystem up, int cash)
        {
            // 满级：显示"已满级"而不是"$0"（GetCost 满级返回 0，直接显示会误导成免费）
            if (up.IsMaxLevel(part))
            {
                GUI.color = new Color(0.55f, 0.55f, 0.55f);
                GUILayout.Label($"[{hotkey}] {up.GetDisplayName(part)}  Lv{up.GetLevel(part)}  已满级", labelStyle);
                GUI.color = Color.white;
                return;
            }

            int cost = up.GetCost(part);
            bool affordable = cash >= cost;

            GUI.color = affordable ? Color.white : new Color(1f, 0.45f, 0.45f);
            GUILayout.Label($"[{hotkey}] {up.GetDisplayName(part)}  Lv{up.GetLevel(part)}  →  ${cost}", labelStyle);
            GUI.color = Color.white;
        }

        // ---------- 工具 ----------

        void DrawBar(Rect rect, float value, float max, Color color, string label)
        {
            // 底槽
            GUI.color = new Color(0.12f, 0.12f, 0.12f, 0.9f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);

            // 填充
            float ratio = max > 0f ? Mathf.Clamp01(value / max) : 0f;
            var fill = new Rect(rect.x + 2f, rect.y + 2f, (rect.width - 4f) * ratio, rect.height - 4f);
            GUI.color = color;
            GUI.DrawTexture(fill, Texture2D.whiteTexture);

            // 文字
            GUI.color = Color.white;
            if (!string.IsNullOrEmpty(label)) GUI.Label(rect, label, barLabelStyle);
        }

        static Texture2D MakeSolidTexture(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }
    }
}
