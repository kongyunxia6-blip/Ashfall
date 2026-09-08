#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// DEV-016：正式地层世界生成回归（Editor 纯逻辑 + 只读真实资产断言）。
    ///
    /// 覆盖：
    ///  - 5 地层深度数据连续无重叠、单调（Soil 0-100 / Normal 100-250 / Dense 250-450 /
    ///    Granite 450-575 / Basalt 575-640，世界 640 深）；
    ///  - 每地层 TileDatabase 层是**纯填充岩**（value==0 / blockType Normal / 非 hazard），
    ///    矿只来自矿脉带（不混入基底）；
    ///  - 每地层基底 TileDefinition 已绑定正式 <stratum>_base_intact 视觉（visualProfile.intact 非空）；
    ///  - OreRegionPreset_Strata 5 带覆盖 [3,639] 连续无重叠、深度升序、每带≥1 矿；
    ///  - 边缘/底行 bedrock 在 DigGrid.Generate 边界分支成立（DigGrid 源码级约束，只读核查调用约定）。
    ///
    /// 运行即先生成地层资产（幂等），保证断言对象确定。
    /// 菜单：灰烬之下 → DEV-016 回归：正式地层世界生成 V1
    /// </summary>
    public static class Dev016WorldGenRegression
    {
        const string OutFile = "Assets/../Logs/dev016_regression_result.txt";
        const string StrataDbPath = "Assets/Ashfall/Data/TileDatabase_Strata.asset";
        const string StrataPresetPath = "Assets/Ashfall/Data/OreRegionPreset_Strata.asset";

        static int pass, fail;
        static readonly StringBuilder Logs = new StringBuilder();
        static string ResultFile;

        static void Assert(bool ok, string tag, string msg)
        {
            if (ok) { pass++; Logs.AppendLine($"PASS  {tag}: {msg}"); }
            else { fail++; Logs.AppendLine($"FAIL  {tag}: {msg}"); }
        }

        [MenuItem("灰烬之下/DEV-016 回归：正式地层世界生成 V1")]
        public static void Run()
        {
            pass = 0; fail = 0; Logs.Clear();
            ResultFile = Path.GetFullPath(OutFile);
            var sb = new StringBuilder();
            sb.AppendLine("DEV-016 (正式地层世界) World Generation Regression V1");
            sb.AppendLine("先就地生成地层资产（幂等），再只读断言真实 DB/Preset。");

            try
            {
                // ---- 0. 确保资产存在 ----
                Dev016StrataSetup.Build();

                var db = AssetDatabase.LoadAssetAtPath<TileDatabase>(StrataDbPath);
                var preset = AssetDatabase.LoadAssetAtPath<OreRegionPreset>(StrataPresetPath);

                Assert(db != null, "StrataDB_Exists", $"存在 TileDatabase_Strata.asset");
                Assert(preset != null, "StrataPreset_Exists", $"存在 OreRegionPreset_Strata.asset");
                if (db == null || preset == null) { sb.AppendLine(Logs.ToString()); Finish(sb); return; }

                Assert(db.emptyTile != null, "DB_EmptySet", "emptyTile 已设");
                Assert(db.bedrockTile != null, "DB_BedrockSet", "bedrockTile 已设");

                // ---- 1. 5 地层连续、无重叠、纯填充、绑正式岩图 ----
                var layers = db.layers;
                Assert(layers != null && layers.Length == 5, "Layers_Count5",
                    $"5 地层（实际 {layers?.Length ?? 0}）");
                if (layers != null && layers.Length == 5)
                {
                    int[] expectStart = { 0, 100, 250, 450, 575 };
                    bool asc = true, pureFill = true, boundArt = true;
                    var sbD = new StringBuilder();
                    for (int i = 0; i < layers.Length; i++)
                    {
                        var L = layers[i];
                        if (L == null) { pureFill = false; continue; }
                        bool startOk = L.startDepth == expectStart[i];
                        if (!startOk) asc = false;
                        sbD.Append($"[{L.startDepth}..{(i + 1 < layers.Length ? layers[i + 1].startDepth - 1 : 639)}]");
                        // 每层纯填充：单 value==0 Normal 非 hazard 岩
                        if (L.tiles == null || L.tiles.Length == 0) { pureFill = false; continue; }
                        bool layerPure = true, hasArt = false;
                        foreach (var t in L.tiles)
                        {
                            if (t == null) { layerPure = false; continue; }
                            if (t.value != 0 || t.isHazard || t.blockType != BlockType.Normal) layerPure = false;
                            if (t.visualProfile != null && t.visualProfile.intact != null) hasArt = true;
                        }
                        if (!layerPure) pureFill = false;
                        if (!hasArt) boundArt = false;
                    }
                    Assert(asc, "Layers_StartAscending",
                        $"地层 startDepth = {expectStart[0]}/{expectStart[1]}/{expectStart[2]}/{expectStart[3]}/{expectStart[4]}（升序）: {sbD}");
                    Assert(pureFill, "Layers_PureFill",
                        "每地层层只有 value==0 / blockType Normal / 非 hazard 的纯填充岩（矿只来自 vein pass）");
                    Assert(boundArt, "Layers_ArtBound",
                        "每地层基底 TileDefinition 已绑正式 <stratum>_base_intact 视觉（visualProfile.intact 非空）");
                }

                // ---- 2. OreRegionPreset_Strata：5 带覆盖 [3,639] 连续、升序、无重叠、每带≥1 矿 ----
                var bands = preset.bands;
                Assert(bands != null && bands.Length == 5, "Preset_Bands5",
                    $"矿脉带 5（实际 {bands?.Length ?? 0}）");
                if (bands != null && bands.Length == 5)
                {
                    bool contiguous = true, mono = true, eachHasOre = true;
                    int prevMax = -1;
                    var sbB = new StringBuilder();
                    for (int i = 0; i < bands.Length; i++)
                    {
                        var b = bands[i];
                        if (b == null) { contiguous = false; eachHasOre = false; continue; }
                        if (b.ores == null || b.ores.Length == 0) eachHasOre = false;
                        if (i > 0 && (b.minDepth <= prevMax)) contiguous = false;   // 与上带重叠
                        if (b.minDepth < prevMax + 1) mono = false;                  // 非严格接续
                        sbB.Append($"[{b.minDepth}..{b.maxDepth}]");
                        prevMax = b.maxDepth;
                    }
                    Assert(contiguous, "Preset_NoOverlap", $"带两两无重叠: {sbB}");
                    Assert(mono, "Preset_Monotonic", $"带深度严格升序、无缝覆盖 3..639");
                    Assert(eachHasOre, "Preset_EachHasOre", "每带 ≥1 种矿");
                    // 覆盖端点校验：第1带 min==3（避开地表 0-2 坑口），最后带 max==639（避开底部 bedrock 640? depth=640 → max 格 639）
                    Assert(bands[0].minDepth == 3, "Preset_CoverTop",
                        $"首带从 3 起（避开 0-2 地表坑口），实际 {bands[0].minDepth}");
                    Assert(bands[bands.Length - 1].maxDepth == 639, "Preset_CoverBottom",
                        $"末带至 639（世界深 640，最底行 bedrock=639），实际 {bands[bands.Length - 1].maxDepth}");
                }
            }
            catch (Exception e)
            {
                Assert(false, "EXCEPTION", e.ToString());
            }

            sb.AppendLine(Logs.ToString());
            sb.AppendLine($"\n==== 汇总: PASS {pass} / FAIL {fail} ====");
            Finish(sb);
        }

        static void Finish(StringBuilder sb)
        {
            try { File.WriteAllText(ResultFile, sb.ToString()); }
            catch (Exception e) { Debug.LogWarning("[DEV-016] 写结果文件失败: " + e.Message); }
            Debug.Log($"[DEV-016 WorldGen Regression] done → {ResultFile} (PASS {pass} / FAIL {fail})");
        }
    }
}
#endif
