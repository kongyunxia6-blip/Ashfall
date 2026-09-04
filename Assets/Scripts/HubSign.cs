using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-006：地表 Hub 世界空间标牌。
    ///
    /// 把文字投影到屏幕绘制（IMGUI，零字体/纹理资产依赖，与 GameHUD 同技术栈），
    /// 挂在功能区上方作 placeholder 标牌：「登陆舱/安全区」「出售终端」「装备工作台」
    /// 「能源补给」「下矿入口」。首次进入场景即可读图理解各功能位置。
    /// </summary>
    public class HubSign : MonoBehaviour
    {
        [Tooltip("标牌文字")]
        [TextArea(1, 3)] public string text = "标牌";

        [Tooltip("文字颜色（与地表功能区分色，便于辨识）")]
        public Color color = Color.white;

        [Tooltip("相对本物体的显示偏移（世界单位，默认偏上）")]
        public Vector3 offset = new Vector3(0f, 1.3f, 0f);

        GUIStyle style;

        void OnGUI()
        {
            var cam = Camera.main;
            if (cam == null) return;

            Vector3 wp = transform.position + offset;
            Vector3 sp = cam.WorldToScreenPoint(wp);
            if (sp.z < 0f) return;   // 在相机背后不画

            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 14,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.white }
                };
            }

            var r = new Rect(sp.x - 170f, Screen.height - sp.y - 13f, 340f, 26f);

            // 先黑描边再上色，保证任何背景可读
            Color prev = style.normal.textColor;
            style.normal.textColor = new Color(0f, 0f, 0f, 0.9f);
            GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), text, style);
            style.normal.textColor = color;
            GUI.Label(r, text, style);
            style.normal.textColor = prev;
        }
    }
}
