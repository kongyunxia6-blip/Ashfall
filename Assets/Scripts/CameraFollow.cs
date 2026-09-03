using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// 相机跟随：平滑追踪玩家，深度越深自动略微拉远视野。
    /// 挂在哪：Main Camera。
    /// </summary>
    public class CameraFollow : MonoBehaviour
    {
        [Header("跟随")]
        public Transform target;

        [Tooltip("跟随刚度，越大越跟手（1 = 很松，20 = 几乎硬跟）")]
        public float stiffness = 10f;

        [Tooltip("相机与目标的 Z 偏移（2D 一般 -10）")]
        public float zOffset = -10f;

        [Header("视野")]
        public float orthoSize = 12f;

        [Tooltip("到达该深度时，视野线性放大到 maxOrthoSize")]
        public float zoomStartDepth = 200f;

        public float maxOrthoSize = 16f;

        Camera cam;

        void Awake()
        {
            cam = GetComponent<Camera>();
            if (cam != null)
            {
                cam.orthographic = true;
                cam.orthographicSize = orthoSize;
            }
        }

        void LateUpdate()
        {
            if (target == null) return;

            Vector3 want = target.position;
            want.z = zOffset;

            // 指数平滑：与帧率无关
            float t = 1f - Mathf.Exp(-stiffness * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, want, t);

            if (cam != null)
            {
                float depth = Mathf.Max(0f, -target.position.y);
                float k = Mathf.Clamp01(depth / Mathf.Max(1f, zoomStartDepth));
                cam.orthographicSize = Mathf.Lerp(orthoSize, maxOrthoSize, k);
            }
        }
    }
}
