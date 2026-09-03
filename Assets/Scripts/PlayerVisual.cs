using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// 玩家外观：运行时程序生成圆点贴图，零美术依赖。
    /// 挂在哪：玩家物体（需要 SpriteRenderer）。
    /// </summary>
    [RequireComponent(typeof(SpriteRenderer))]
    public class PlayerVisual : MonoBehaviour
    {
        [Header("外观")]
        public Color bodyColor = new Color(0.35f, 0.88f, 1f);

        [Tooltip("贴图分辨率（像素）")]
        public int size = 48;

        [Tooltip("外圈警示环颜色，用于体现「服体完整度」")]
        public Color ringColor = new Color(1f, 0.85f, 0.3f);

        void Awake()
        {
            var sr = GetComponent<SpriteRenderer>();
            if (sr == null) return;

            var tex = MakeDotTexture(size, bodyColor, ringColor);
            sr.sprite = Sprite.Create(tex, new Rect(0, 0, size, size),
                                      new Vector2(0.5f, 0.5f), size);
            sr.sortingOrder = 10;
        }

        static Texture2D MakeDotTexture(int size, Color body, Color ring)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float c = (size - 1) * 0.5f;
            float outer = c * 0.95f;
            float ringInner = c * 0.74f;
            float inner = c * 0.62f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - c;
                    float dy = y - c;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);

                    Color col = Color.clear;

                    if (d <= outer)
                    {
                        // 抗锯齿边缘
                        float edge = Mathf.Clamp01(outer - d);

                        if (d <= inner)
                            col = new Color(body.r, body.g, body.b, edge);
                        else if (d <= ringInner)
                        {
                            // 渐变到警示环
                            float k = (d - inner) / Mathf.Max(0.0001f, ringInner - inner);
                            col = Color.Lerp(body, ring, k);
                            col.a = edge;
                        }
                        else
                        {
                            float k = (d - ringInner) / Mathf.Max(0.0001f, outer - ringInner);
                            col = Color.Lerp(ring, body, k * 0.6f);
                            col.a = edge;
                        }
                    }

                    tex.SetPixel(x, y, col);
                }
            }

            tex.Apply();
            tex.filterMode = FilterMode.Point;
            return tex;
        }
    }
}
