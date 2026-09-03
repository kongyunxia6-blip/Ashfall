using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// DEV-002：Block 受击 / 崩碎表现层控制器。
    ///
    /// 职责（只做表现，不碰数据层）：
    ///  - 监听 DigGrid.OnBlockHit：单格命中反馈（极短闪亮 + 少量碎屑）+ 命中音效事件入口；
    ///  - 监听 DigGrid.OnBlockBreakStart：生成短生命周期崩碎对象，播放 breakFrames 序列 + 碎屑；
    ///  - 监听 DigGrid.OnTileDug：销毁对应格子的崩碎对象（崩碎对象与 RemoveBlock 同步消失）。
    ///
    /// 设计规则（对齐 Issue #4）：
    ///  - 裂纹阶段 / 耐久一律读 DigGrid，本组件不维护第二套 HP；
    ///  - 崩碎表现接 DEV-001 的 OnBlockBreakStart / breakDuration / RemoveBlock 管线，不另写挖穿逻辑；
    ///  - 崩碎对象是短生命周期动态对象，随 OnTileDug 销毁，不变成每格永久 GameObject；
    ///  - 不做整屏震动、不做范围挖掘、不做多格 AoE 爽感。
    ///
    /// 挂载：任意物体（推荐与 DigGrid 同物体或 GameManager）。拖入 grid 引用即可。
    /// </summary>
    public class BlockVisualController : MonoBehaviour
    {
        [Tooltip("要监听的 DigGrid（默认取同物体或场景内第一个）")]
        public DigGrid grid;

        [Header("崩碎表现")]
        [Tooltip("崩碎碎屑数量（OnBlockBreakStart 时撒出）")]
        [Min(0)] public int breakDebrisCount = 6;

        [Tooltip("崩碎帧播放总时长覆盖（秒）。0 = 用 grid.breakDuration；>0 = 强制该时长（供 breakDuration=0 时仍可观察崩碎动画）")]
        [Min(0f)] public float breakAnimDurationOverride = 0f;

        [Header("命中反馈")]
        [Tooltip("单格命中碎屑数量（OnBlockHit 时撒出，0 = 关闭）")]
        [Min(0)] public int hitDebrisCount = 2;

        [Tooltip("命中闪亮持续帧数（1 = 闪 1 帧）")]
        [Min(0)] public int hitFlashFrames = 2;

        [Header("音效接口（V1 占位，不实现正式音效）")]
        [Tooltip("命中音效事件入口：在 Inspector / 代码里订阅后，单格被命中时回调格子坐标")]
        public UnityEngine.Events.UnityEvent<Vector2Int> onHitSound;

        [Tooltip("崩碎音效事件入口：方块崩碎时回调格子坐标")]
        public UnityEngine.Events.UnityEvent<Vector2Int> onBreakSound;

        /// <summary>当前格子 → 正在播放的崩碎对象（供 OnTileDug 销毁）。</summary>
        readonly Dictionary<Vector2Int, GameObject> activeBreaks = new Dictionary<Vector2Int, GameObject>();

        Sprite debrisSprite;   // 共享的白色小方块碎屑 Sprite（懒生成）

        void OnEnable()
        {
            if (grid == null) grid = GetComponent<DigGrid>();
            if (grid == null) grid = FindFirstObjectByType<DigGrid>();
            if (grid == null)
            {
                Debug.LogWarning("[DEV-002] BlockVisualController: 未找到 DigGrid，无法监听表现事件");
                return;
            }

            grid.OnBlockHit += OnBlockHit;
            grid.OnBlockBreakStart += OnBlockBreakStart;
            grid.OnTileDug += OnTileDug;
        }

        void OnDisable()
        {
            if (grid == null) return;
            grid.OnBlockHit -= OnBlockHit;
            grid.OnBlockBreakStart -= OnBlockBreakStart;
            grid.OnTileDug -= OnTileDug;
        }

        // ---------- 命中反馈 ----------

        void OnBlockHit(Vector2Int cell, TileDefinition def)
        {
            // 1) 极短闪亮（等价单格命中反馈，不整屏震动）
            var profile = def != null ? def.visualProfile : null;
            if (hitFlashFrames > 0)
                StartCoroutine(Co_HitFlash(cell, profile));

            // 2) 少量碎屑
            if (hitDebrisCount > 0)
                SpawnDebris(GridCenter(cell), profile != null ? profile.hitFlashColor : def.color, hitDebrisCount, 1.5f);

            // 3) 音效事件入口（V1 占位）
            onHitSound?.Invoke(cell);
        }

        // ---------- 崩碎表现 ----------

        void OnBlockBreakStart(Vector2Int cell, TileDefinition def)
        {
            var profile = def != null ? def.visualProfile : null;

            // 1) 崩碎对象：仅当有 breakFrames 或 crack3 Sprite 时才生成动态对象；
            //    否则崩碎视觉由 DigGrid 的 fallback（裂纹3 调暗）承担。
            if (profile != null && (profile.HasBreakFrames || profile.crack3 != null))
                SpawnBreakObject(cell, def, profile);

            // 2) 崩碎碎屑（无论有无 Sprite 都撒，模拟崩裂飞屑）
            if (breakDebrisCount > 0)
                SpawnDebris(GridCenter(cell), def != null ? def.color : Color.gray, breakDebrisCount, 3.0f);

            // 3) 音效事件入口（V1 占位）
            onBreakSound?.Invoke(cell);
        }

        void OnTileDug(Vector2Int cell, TileDefinition def)
        {
            if (activeBreaks.TryGetValue(cell, out var go))
            {
                if (go != null) Destroy(go);
                activeBreaks.Remove(cell);
            }
        }

        // ---------- 崩碎对象 ----------

        void SpawnBreakObject(Vector2Int cell, TileDefinition def, BlockVisualProfile profile)
        {
            var go = new GameObject("BlockBreakFX");
            go.transform.position = GridCenter(cell);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = 20;   // 盖在前景 Tilemap 之上

            activeBreaks[cell] = go;
            StartCoroutine(Co_PlayBreak(go, sr, cell, def, profile));
        }

        System.Collections.IEnumerator Co_PlayBreak(GameObject go, SpriteRenderer sr, Vector2Int cell, TileDefinition def, BlockVisualProfile profile)
        {
            // 崩碎帧序列：breakFrames 优先，否则用 crack3 单帧
            Sprite[] frames = profile.HasBreakFrames ? profile.breakFrames
                : (profile.crack3 != null ? new Sprite[] { profile.crack3 } : null);

            if (frames == null || frames.Length == 0)
            {
                // 理论上不会走到（调用方已判断），兜底纯色
                sr.color = def.color;
                yield break;
            }

            float total = breakAnimDurationOverride > 0f
                ? breakAnimDurationOverride
                : (grid.breakDuration > 0f ? grid.breakDuration : 0.2f);
            float perFrame = Mathf.Max(0.02f, total / frames.Length);

            for (int i = 0; i < frames.Length; i++)
            {
                sr.sprite = frames[i];
                yield return new WaitForSeconds(perFrame);
            }

            // 播完定格最后一帧，等待 OnTileDug 销毁
            if (go != null) sr.sprite = frames[frames.Length - 1];
        }

        // ---------- 命中闪亮 ----------

        System.Collections.IEnumerator Co_HitFlash(Vector2Int cell, BlockVisualProfile profile)
        {
            if (grid.tilemap == null) yield break;

            var c = new Vector3Int(cell.x, -cell.y, 0);
            Color original = grid.tilemap.GetColor(c);
            Color flash = (profile != null) ? profile.hitFlashColor : Color.white;

            int frames = Mathf.Max(1, hitFlashFrames);
            for (int i = 0; i < frames; i++)
            {
                grid.tilemap.SetColor(c, Color.Lerp(original, flash, 0.65f));
                yield return null;
            }
            grid.tilemap.SetColor(c, original);
        }

        // ---------- 碎屑 ----------

        void SpawnDebris(Vector2 worldPos, Color color, int count, float speed)
        {
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("Debris");
                go.transform.position = worldPos + (Vector3)(UnityEngine.Random.insideUnitCircle * 0.2f);
                go.transform.localScale = Vector3.one * UnityEngine.Random.Range(0.06f, 0.16f);

                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = GetDebrisSprite();
                sr.color = color;
                sr.sortingOrder = 30;

                var rb = go.AddComponent<Rigidbody2D>();
                rb.gravityScale = 4f;
                float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                rb.velocity = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * UnityEngine.Random.Range(0.5f, 1f) * speed;
                rb.angularVelocity = UnityEngine.Random.Range(-360f, 360f);

                StartCoroutine(Co_FadeDebris(go, sr, UnityEngine.Random.Range(0.25f, 0.5f)));
            }
        }

        System.Collections.IEnumerator Co_FadeDebris(GameObject go, SpriteRenderer sr, float life)
        {
            float t = 0f;
            Color c = sr.color;
            while (t < life)
            {
                t += Time.deltaTime;
                if (go == null) yield break;
                sr.color = new Color(c.r, c.g, c.b, Mathf.Lerp(1f, 0f, t / life));
                yield return null;
            }
            if (go != null) Destroy(go);
        }

        Sprite GetDebrisSprite()
        {
            if (debrisSprite != null) return debrisSprite;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            for (int x = 0; x < 2; x++)
                for (int y = 0; y < 2; y++)
                    tex.SetPixel(x, y, Color.white);
            tex.Apply();
            debrisSprite = Sprite.Create(tex, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f), 16f); // ppu=16 → 约 0.125 unit
            debrisSprite.name = "Ashfall_DebrisPixel";
            return debrisSprite;
        }

        Vector3 GridCenter(Vector2Int cell)
        {
            return grid != null ? grid.GridToWorld(cell.x, cell.y) : new Vector3(cell.x, -cell.y, 0f);
        }
    }
}
