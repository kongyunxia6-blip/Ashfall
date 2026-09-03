using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ashfall
{
    /// <summary>
    /// 格子背包面板（UGUI，纯代码构建，零预制体 / 零美术依赖）。
    /// 挂在哪：场景任意物体 —— 会自建 Canvas 与 EventSystem，挂上就能用。
    ///
    /// 操作：
    ///   I          = 开关面板（打开时暂停游戏，Motherload 的 INV 也是暂停的：
    ///                地下整理背包不该被落石砸、被高温烤）
    ///   左键点格子 = 丢弃 1 件
    ///   右键点格子 = 丢弃整堆
    ///
    /// 【为什么必须能主动丢弃】重量会让「装太重 → 爬不动 → 燃料耗尽」形成死亡螺旋。
    /// 没有主动丢弃这个安全阀，玩家满载下潜后会被自己背的矿石困死，且毫无补救手段。
    ///
    /// 图标现阶段直接用 TileDefinition.color 生成的纯色块（零美术），
    /// 后续换成 128×128 贴图只需改 RefreshSlots 里的 icon.sprite 赋值。
    /// </summary>
    public class InventoryPanel : MonoBehaviour
    {
        [Header("开关")]
        public KeyCode toggleKey = KeyCode.I;

        [Header("外观")]
        public float slotSize = 48f;
        public float slotSpacing = 6f;
        public int padding = 14;

        /// <summary>面板是否打开（其他脚本可用它屏蔽输入）</summary>
        public static bool IsOpen { get; private set; }

        Canvas canvas;
        GameObject panelRoot;
        RectTransform gridRoot;
        Text titleText;
        Text detailText;
        Text hintText;

        DrillVehicle player;
        readonly List<SlotView> slotViews = new List<SlotView>();
        int builtRows = -1;
        static Font cachedFont;

        class SlotView
        {
            public Image background;
            public Image icon;
            public Text count;
            public int index;
        }

        // ---------- 生命周期 ----------

        void Awake() => BuildUI();

        void Start()
        {
            if (GameManager.Instance != null) Bind(GameManager.Instance.Player);
        }

        void OnDestroy()
        {
            Bind(null);
            if (IsOpen) { IsOpen = false; Time.timeScale = 1f; }
        }

        void Update()
        {
            if (player == null && GameManager.Instance != null)
                Bind(GameManager.Instance.Player);

            if (Input.GetKeyDown(toggleKey)) SetOpen(!IsOpen);
        }

        void Bind(DrillVehicle vehicle)
        {
            if (player == vehicle) return;

            if (player != null) player.Inventory.OnChanged -= Refresh;
            player = vehicle;
            if (player != null) player.Inventory.OnChanged += Refresh;
        }

        // ---------- 开关 ----------

        void SetOpen(bool open)
        {
            if (IsOpen == open) return;

            IsOpen = open;
            panelRoot.SetActive(open);
            Time.timeScale = open ? 0f : 1f;

            if (open) Refresh();
        }

        // ---------- 构建 UI ----------

        void BuildUI()
        {
            EnsureEventSystem();

            var canvasGo = new GameObject("InventoryCanvas");
            canvasGo.transform.SetParent(transform, false);

            canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            canvasGo.AddComponent<GraphicRaycaster>();

            panelRoot = new GameObject("Panel");
            panelRoot.transform.SetParent(canvasGo.transform, false);

            var panelImage = panelRoot.AddComponent<Image>();
            panelImage.color = new Color(0.07f, 0.08f, 0.10f, 0.96f);

            var panelRect = panelRoot.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.anchoredPosition = Vector2.zero;

            var layout = panelRoot.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.spacing = 8;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var fitter = panelRoot.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var font = GetFont();
            titleText = MakeText("Title", panelRoot.transform, font, 15, TextAnchor.MiddleCenter, Color.white);
            detailText = MakeText("Detail", panelRoot.transform, font, 12, TextAnchor.MiddleCenter, new Color(0.75f, 0.78f, 0.85f));
            hintText = MakeText("Hint", panelRoot.transform, font, 12, TextAnchor.MiddleCenter, new Color(0.6f, 0.63f, 0.7f));

            var gridGo = new GameObject("Grid");
            gridGo.transform.SetParent(panelRoot.transform, false);

            var gridLayout = gridGo.AddComponent<GridLayoutGroup>();
            gridLayout.cellSize = new Vector2(slotSize, slotSize);
            gridLayout.spacing = new Vector2(slotSpacing, slotSpacing);
            gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            gridLayout.constraintCount = InventoryGrid.Columns;
            gridLayout.childAlignment = TextAnchor.MiddleCenter;

            var gridFitter = gridGo.AddComponent<ContentSizeFitter>();
            gridFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            gridFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            gridRoot = gridGo.GetComponent<RectTransform>();

            panelRoot.SetActive(false);
        }

        void BuildSlots(int rows)
        {
            for (int i = 0; i < slotViews.Count; i++)
                if (slotViews[i].background != null) Destroy(slotViews[i].background.gameObject);
            slotViews.Clear();

            var font = GetFont();
            int total = InventoryGrid.Columns * rows;

            for (int i = 0; i < total; i++)
            {
                var slotGo = new GameObject("Slot" + i);
                slotGo.transform.SetParent(gridRoot, false);

                var bg = slotGo.AddComponent<Image>();
                bg.color = new Color(0.17f, 0.18f, 0.21f, 1f);

                var iconGo = new GameObject("Icon");
                iconGo.transform.SetParent(slotGo.transform, false);
                var icon = iconGo.AddComponent<Image>();
                icon.color = new Color(0f, 0f, 0f, 0f);
                var iconRect = icon.GetComponent<RectTransform>();
                iconRect.anchorMin = new Vector2(0.14f, 0.14f);
                iconRect.anchorMax = new Vector2(0.86f, 0.86f);
                iconRect.offsetMin = Vector2.zero;
                iconRect.offsetMax = Vector2.zero;

                var countText = MakeText("Count", slotGo.transform, font, 12, TextAnchor.LowerRight, Color.white);
                var countRect = countText.GetComponent<RectTransform>();
                countRect.anchorMin = Vector2.zero;
                countRect.anchorMax = Vector2.one;
                countRect.offsetMin = new Vector2(2f, 1f);
                countRect.offsetMax = new Vector2(-4f, -1f);

                int index = i;
                var btn = slotGo.AddComponent<Button>();
                btn.onClick.AddListener(() => Drop(index, false));

                // 右键丢整堆。Button 只响应左键，所以额外挂 EventTrigger 判右键。
                var trigger = slotGo.AddComponent<EventTrigger>();

                var rightClick = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
                rightClick.callback.AddListener(data =>
                {
                    var pointer = data as PointerEventData;
                    if (pointer != null && pointer.button == PointerEventData.InputButton.Right)
                        Drop(index, true);
                });
                trigger.triggers.Add(rightClick);

                var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
                enter.callback.AddListener(_ => ShowDetail(index));
                trigger.triggers.Add(enter);

                var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
                exit.callback.AddListener(_ => ShowDetail(-1));
                trigger.triggers.Add(exit);

                slotViews.Add(new SlotView
                {
                    background = bg,
                    icon = icon,
                    count = countText,
                    index = i
                });
            }

            builtRows = rows;
        }

        // ---------- 刷新 ----------

        void Refresh()
        {
            if (player == null) return;

            var inv = player.Inventory;
            if (inv.Rows != builtRows) BuildSlots(inv.Rows);

            for (int i = 0; i < slotViews.Count; i++)
            {
                var slot = inv.GetSlot(i);
                var view = slotViews[i];

                if (slot == null || !slot.IsOccupied)
                {
                    view.background.color = new Color(0.17f, 0.18f, 0.21f, 1f);
                    view.icon.color = new Color(0f, 0f, 0f, 0f);
                    view.count.text = "";
                    continue;
                }

                if (slot.isPrimary)
                {
                    view.background.color = new Color(0.26f, 0.28f, 0.33f, 1f);
                    view.icon.color = slot.def.color;
                    view.count.text = slot.count.ToString();
                }
                else
                {
                    // 大件物品的附属格：半透明表示「被左边那格占用」
                    var c = slot.def.color;
                    view.background.color = new Color(0.21f, 0.22f, 0.26f, 1f);
                    view.icon.color = new Color(c.r, c.g, c.b, 0.35f);
                    view.count.text = "";
                }
            }

            titleText.text = $"货舱   载重 {inv.TotalWeight:F1} / {inv.MaxWeight:F0}   ·   格 {inv.UsedSlots} / {inv.Capacity}";
            hintText.text = $"总价值 ${inv.TotalValue}   ·   左键丢 1 件 · 右键丢整堆 · {toggleKey} 关闭";
            ShowDetail(-1);
        }

        void ShowDetail(int index)
        {
            // 没悬停时保留一句占位提示：空字符串会让 Text 的 preferredWidth 塌成 0，
            // 面板宽度随悬停内容反复跳动。
            const string placeholder = "悬停格子查看物品详情";

            if (player == null || index < 0)
            {
                detailText.text = placeholder;
                return;
            }

            var inv = player.Inventory;
            var slot = inv.GetSlot(index);
            if (slot == null) { detailText.text = placeholder; return; }

            int primary = slot.isPrimary ? index : slot.ownerIndex;
            var target = inv.GetSlot(primary);
            if (target == null || target.IsEmpty) { detailText.text = placeholder; return; }

            var def = target.def;
            float density = def.weight > 0f ? def.value / def.weight : 0f;
            detailText.text = $"{def.displayName}   ×{target.count}   ·   单价 ${def.value}   ·   重量 {def.weight:F1}   ·   密度 {density:F0}";
        }

        // ---------- 交互 ----------

        void Drop(int index, bool wholeStack)
        {
            if (player == null) return;

            var inv = player.Inventory;
            var slot = inv.GetSlot(index);
            if (slot == null) return;

            int primary = slot.isPrimary ? index : slot.ownerIndex;
            var target = inv.GetSlot(primary);
            if (target == null || target.IsEmpty) return;

            inv.RemoveAt(primary, wholeStack ? target.count : 1);
        }

        // ---------- 工具 ----------

        static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;

            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }

        static Text MakeText(string name, Transform parent, Font font, int fontSize,
                             TextAnchor anchor, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = fontSize;
            text.color = color;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = false;

            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(320f, fontSize + 8f);

            return text;
        }

        static Font GetFont()
        {
            if (cachedFont != null) return cachedFont;

            // Unity 6 移除了 Arial.ttf，对它 Resources.GetBuiltinResource 不再返回 null，
            // 而是直接 ArgumentException —— 所以必须先尝试 LegacyRuntime，否则会抛异常
            // 导致 Awake/BuildUI 中断，整个面板组件被 Unity 自动禁用。
            cachedFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (cachedFont == null) cachedFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (cachedFont == null)
                Debug.LogWarning("[InventoryPanel] 找不到任何内置字体（LegacyRuntime/Arial 都没找到），文字将不显示");
            return cachedFont;
        }
    }
}
