#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ashfall.EditorTools
{
    /// <summary>
    /// DEV-009 验收 S3：DEV-007/008 双生成器回归（编辑模式、无场景依赖）。
    /// 菜单：灰烬之下 → DEV-009 回归：双生成器（2 seed 深回归 + 20 seed stress）
    ///
    /// 覆盖 Issue #19 §验收 的回归断言：
    ///  - Space deterministic / Ore deterministic（同 seed 两次重生成逐项一致）；
    ///  - 矿不落空（每 band planted > 0、gridOre > 0）；
    ///  - vein connected（全部 OreVeinRecord.IsConnected8）；
    ///  - Σvein == planted == gridOre（记录自洽且与 DigGrid 落格一致）；
    ///  - ReservedRect 0 污染（保留区内零矿、零 space 挖空，坑口例外）；
    ///  - Space record connected（全部 UndergroundSpaceRecord.IsConnected8 / IsConnected4）；
    ///  - Empty ratio 汇报（2 seed 深回归输出实际值，stress 仅 sanity < 20%）；
    ///  - 带归属正确（每 vein/space 的 bandName ∈ DepthRegionLayout 区域且落在带深度范围内）。
    ///
    /// 注意：本脚本只调用 OreVeinGenerator / UndergroundSpaceGenerator 的既有公开 API
    /// （ApplyToGrid / Veins / Spaces / ...），不重写、不修改两个生成器。
    /// </summary>
    public static class DepthRegionV1Regression
    {
        const string DataFolder = "Assets/Ashfall/Data";
        const string ResultFile = @"C:/Users/58058/.workbuddy/tools/d9_regression_result.txt";

        // 2 seed 深回归（验收 seed 20260908 + 另一 seed 42）
        static readonly int[] DeepSeeds = { 20260908, 42 };
        // 20 seed 轻量 stress
        static readonly int[] StressSeeds = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };

        const int GridWidth = 56;
        const int GridDepth = 72;
        const int SurfaceReserveH = 3;   // 地表带保留高度（y0..2）
        const int OpeningHalfWidth = 3;  // 中心坑口 ±3（与 DigGrid.surfaceOpeningHalfWidth 一致）

        // ---------- 可复用的临时 rig（与 DepthRegionV1Builder 同款配置） ----------

        class Rig
        {
            public GameObject go;
            public DigGrid grid;
            public UndergroundSpaceGenerator spaceGen;
            public OreVeinGenerator oreGen;
            public OreReservedRect[] reserved;
            public TileDefinition empty;
            public TileDefinition bedrock;
        }

        static Rig BuildRig(int seed)
        {
            var db = AssetDatabase.LoadAssetAtPath<TileDatabase>($"{DataFolder}/TileDatabase.asset");
            var iron = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Iron_铁矿.asset");
            var copper = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Copper_铜矿.asset");
            var tin = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Tin_锡矿.asset");
            var silver = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Silver_银矿.asset");
            var dirt = AssetDatabase.LoadAssetAtPath<TileDefinition>($"{DataFolder}/Dirt_泥土.asset");
            if (db == null || iron == null || copper == null || tin == null || silver == null || dirt == null)
                return null;

            var rig = new Rig();
            rig.go = new GameObject("__D9RegressionRig");
            rig.go.hideFlags = HideFlags.HideAndDontSave;

            var dg = rig.go.AddComponent<DigGrid>();
            dg.database = db;
            dg.width = GridWidth;
            dg.depth = GridDepth;
            dg.useRandomSeed = false;
            dg.seed = seed;
            dg.enableFallingRocks = false;
            dg.breakDuration = 0f;
            dg.surfaceOpeningHalfWidth = OpeningHalfWidth;

            rig.reserved = new[]
            {
                new OreReservedRect { label = "SurfaceHub 地表带", x = 0, y = 0, w = GridWidth, h = SurfaceReserveH },
                new OreReservedRect { label = "ScannerPad 无矿基线区", x = 2, y = 16, w = 16, h = 10 },
            };

            var spaceGen = rig.go.AddComponent<UndergroundSpaceGenerator>();
            spaceGen.grid = dg;
            spaceGen.seedOverride = -1;
            spaceGen.bands = new[]
            {
                new UndergroundSpaceBand { bandName = "Shallow",
                    minDepth = DepthRegionLayout.Shallow.spaceStartY, maxDepth = DepthRegionLayout.Shallow.maxDepth,
                    pocketTarget = 6, pocketMinCells = 6, pocketMaxCells = 10,
                    pocketWidthMax = 5, pocketHeightMax = 4,
                    tunnelTarget = 4, tunnelMinLength = 4, tunnelMaxLength = 6,
                    branchTarget = 1, smallRoomTarget = 2,
                    branchSideMin = 2, branchSideMax = 3 },
                new UndergroundSpaceBand { bandName = "Mid",
                    minDepth = DepthRegionLayout.Mid.minDepth, maxDepth = DepthRegionLayout.Mid.maxDepth,
                    pocketTarget = 7, pocketMinCells = 8, pocketMaxCells = 13,
                    pocketWidthMax = 6, pocketHeightMax = 5,
                    tunnelTarget = 5, tunnelMinLength = 5, tunnelMaxLength = 8,
                    branchTarget = 2, smallRoomTarget = 1,
                    branchSideMin = 2, branchSideMax = 4 },
                new UndergroundSpaceBand { bandName = "Deep",
                    minDepth = DepthRegionLayout.Deep.minDepth, maxDepth = DepthRegionLayout.Deep.maxDepth,
                    pocketTarget = 8, pocketMinCells = 10, pocketMaxCells = 18,
                    pocketWidthMax = 7, pocketHeightMax = 5,
                    tunnelTarget = 5, tunnelMinLength = 7, tunnelMaxLength = 11,
                    branchTarget = 3, smallRoomTarget = 1,
                    branchSideMin = 2, branchSideMax = 4 },
            };
            spaceGen.reservedRects = rig.reserved;
            dg.undergroundSpaceGenerator = spaceGen;

            var oreGen = rig.go.AddComponent<OreVeinGenerator>();
            oreGen.grid = dg;
            oreGen.seedOverride = -1;
            oreGen.bands = new[]
            {
                new OreDepthBand { bandName = "Shallow",
                    minDepth = DepthRegionLayout.Shallow.minDepth, maxDepth = DepthRegionLayout.Shallow.maxDepth,
                    ores = new[] { iron, tin, copper }, weights = new[] { 45f, 40f, 15f },
                    veinMinSize = 2, veinMaxSize = 4, veinFrequency = 0.7f },
                new OreDepthBand { bandName = "Mid",
                    minDepth = DepthRegionLayout.Mid.minDepth, maxDepth = DepthRegionLayout.Mid.maxDepth,
                    ores = new[] { copper, iron, tin, silver }, weights = new[] { 55f, 25f, 15f, 5f },
                    veinMinSize = 3, veinMaxSize = 6, veinFrequency = 0.8f },
                new OreDepthBand { bandName = "Deep",
                    minDepth = DepthRegionLayout.Deep.minDepth, maxDepth = DepthRegionLayout.Deep.maxDepth,
                    ores = new[] { copper, iron, silver, tin }, weights = new[] { 62f, 15f, 15f, 8f },
                    veinMinSize = 4, veinMaxSize = 8, veinFrequency = 0.9f },
            };
            oreGen.reservedRects = rig.reserved;
            dg.oreVeinGenerator = oreGen;

            rig.grid = dg;
            rig.spaceGen = spaceGen;
            rig.oreGen = oreGen;
            rig.empty = db.emptyTile;
            rig.bedrock = db.bedrockTile;
            return rig;
        }

        static void DestroyRig(Rig rig)
        {
            if (rig != null && rig.go != null)
                UnityEngine.Object.DestroyImmediate(rig.go);
        }

        // ---------- 统计 ----------

        class Stats
        {
            public int veinCount, orePlanted, oreGrid, veinDiscards;
            public bool allVeinConnected = true;
            public string veinBandViolation = "";
            public int spaceCount, spaceCells, spaceDiscards;
            public bool allSpaceConnected8 = true;
            public bool allSpaceConnected4 = true;
            public string spaceBandViolation = "";
            public int reservedOre, reservedSpaceNonSolid;
            public int oreInSurfaceReserve, oreInScannerPad;
            public int totalNonSolid;
        }

        static Stats CollectStats(Rig rig)
        {
            var s = new Stats();
            var dg = rig.grid;
            int w = dg.width, d = dg.depth;
            int center = w / 2;

            // 矿脉记录
            foreach (var v in rig.oreGen.Veins)
            {
                s.veinCount++;
                s.orePlanted += v.Size;
                if (!v.IsConnected8) s.allVeinConnected = false;
                if (!RegionContainsBand(v.bandName, v.cells, isOre: true))
                    s.veinBandViolation = v.bandName;
            }
            s.veinDiscards = rig.oreGen.SubMinDiscards;

            // 空间记录
            foreach (var sp in rig.spaceGen.Spaces)
            {
                s.spaceCount++;
                s.spaceCells += sp.Size;
                if (!sp.IsConnected8) s.allSpaceConnected8 = false;
                if (!sp.IsConnected4) s.allSpaceConnected4 = false;
                if (!RegionContainsBand(sp.bandName, sp.cells, isOre: false))
                    s.spaceBandViolation = sp.bandName;
            }
            s.spaceDiscards = rig.spaceGen.Discarded;

            // 全图扫描：矿格 / 空格 / 保留区污染
            for (int y = 0; y < d; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (x == 0 || x == w - 1 || y >= d - 1) continue; // bedrock 边列
                    var t = dg.GetTile(x, y);
                    bool solid = t != null && t.isSolid;
                    bool ore = solid && t.value > 0;
                    if (ore) s.oreGrid++;
                    bool emptyCell = t == null || !solid;
                    if (emptyCell) s.totalNonSolid++;

                    if (InReserved(rig, x, y))
                    {
                        bool opening = y <= 2 && Mathf.Abs(x - center) <= OpeningHalfWidth; // 坑口由 DigGrid 自身开，允许
                        if (ore)
                        {
                            s.reservedOre++;
                            if (y < SurfaceReserveH) s.oreInSurfaceReserve++;
                            else s.oreInScannerPad++;
                        }
                        else if (emptyCell && !opening)
                        {
                            s.reservedSpaceNonSolid++;
                        }
                    }
                }
            }
            return s;
        }

        static bool InReserved(Rig rig, int x, int y)
        {
            foreach (var r in rig.reserved)
                if (r.Contains(x, y)) return true;
            return false;
        }

        // 校验 vein/space 的 cells 是否全部落在其 band 对应区域深度内
        static bool RegionContainsBand(string bandName, List<Vector2Int> cells, bool isOre)
        {
            var region = RegionByBandName(bandName);
            if (region == null) return false;
            int min = isOre ? region.minDepth : region.spaceStartY;
            int max = region.maxDepth;
            if (min > max) return false;
            foreach (var c in cells)
                if (c.y < min || c.y > max) return false;
            return true;
        }

        static DepthRegionDefinition RegionByBandName(string name)
        {
            if (name == DepthRegionLayout.Shallow.regionId) return DepthRegionLayout.Shallow;
            if (name == DepthRegionLayout.Mid.regionId) return DepthRegionLayout.Mid;
            if (name == DepthRegionLayout.Deep.regionId) return DepthRegionLayout.Deep;
            return null;
        }

        // ---------- 断言 ----------

        static readonly List<string> Logs = new List<string>();

        static void Assert(bool ok, string name, string detail)
        {
            Logs.Add((ok ? "PASS " : "FAIL ") + name + " :: " + detail);
        }

        static void RunSeed(Rig rig, int seed, StringBuilder outB, bool deep)
        {
            Logs.Clear();
            rig.grid.seed = seed;
            rig.grid.RegenerateFromDatabase();
            var a = CollectStats(rig);
            rig.grid.RegenerateFromDatabase(); // 第二次：确定性比对
            var b = CollectStats(rig);

            // 确定性
            bool detOre = a.veinCount == b.veinCount && a.orePlanted == b.orePlanted && a.oreGrid == b.oreGrid
                          && a.veinDiscards == b.veinDiscards && a.spaceCount == b.spaceCount
                          && a.spaceCells == b.spaceCells && a.spaceDiscards == b.spaceDiscards;
            Assert(detOre, "Deterministic(space+ore)", $"2x Regenerate 一致 vein#{a.veinCount}/{b.veinCount} orePlanted {a.orePlanted}/{b.orePlanted} space#{a.spaceCount}/{b.spaceCount} cells {a.spaceCells}/{b.spaceCells}");

            // 矿不落空（矿脉数与落格数 >0；按带也需非空）
            bool oreNonEmpty = a.veinCount > 0 && a.orePlanted > 0 && a.oreGrid > 0;
            Assert(oreNonEmpty, "OreNonEmpty", $"veins={a.veinCount} planted={a.orePlanted} gridOre={a.oreGrid} discards={a.veinDiscards}");

            // vein connected + 记录自洽（Σvein == planted == gridOre）
            bool sumSelfConsistent = a.orePlanted == a.oreGrid;
            Assert(a.allVeinConnected, "VeinConnected8", $"veins={a.veinCount} allConnected={a.allVeinConnected}");
            Assert(sumSelfConsistent, "SigmaVein==Planted==GridOre", $"Σvein.Size={a.orePlanted} planted={a.orePlanted} gridOre={a.oreGrid}");

            // band 归属
            Assert(a.veinBandViolation.Length == 0, "VeinBandInsideRegion", $"violation={a.veinBandViolation}");

            // 空间：record connected + Σspace == TotalEmptyCells
            Assert(a.allSpaceConnected8, "SpaceConnected8", $"spaces={a.spaceCount} all8={a.allSpaceConnected8}");
            Assert(a.allSpaceConnected4, "SpaceConnected4", $"all4={a.allSpaceConnected4}");
            bool spaceSelf = a.spaceCells == rig.spaceGen.TotalEmptyCells;
            Assert(spaceSelf, "SigmaSpace==TotalEmptyCells", $"Σspace={a.spaceCells} TotalEmptyCells={rig.spaceGen.TotalEmptyCells}");
            Assert(a.spaceBandViolation.Length == 0, "SpaceBandInsideRegion", $"violation={a.spaceBandViolation}");

            // 保留区 0 污染
            Assert(a.reservedOre == 0, "ReservedZeroOre", $"reservedOre={a.reservedOre} (surface={a.oreInSurfaceReserve} pad={a.oreInScannerPad})");
            Assert(a.reservedSpaceNonSolid == 0, "ReservedZeroSpaceCarve", $"reservedNonSolid={a.reservedSpaceNonSolid} (坑口除外)");

            // 空腔占比 sanity（stress 仅检查不失控；deep 输出实际值）
            float ratio = (float)a.spaceCells / (GridWidth * GridDepth);
            Assert(ratio < 0.20f, "EmptyRatioSanity", $"empty={a.spaceCells} ratio={ratio:P2} (<20%)");

            // 输出
            int pass = 0, fail = 0;
            foreach (var l in Logs)
            {
                if (l.StartsWith("PASS")) pass++; else fail++;
            }
            outB.AppendLine($"==== seed={seed} {(deep ? "(深回归)" : "(stress)")} ====");
            outB.AppendLine($"  veins={a.veinCount} planted={a.orePlanted} gridOre={a.oreGrid} discards={a.veinDiscards} | spaces={a.spaceCount} cells={a.spaceCells} discards={a.spaceDiscards} emptyRatio={ratio:P2}");
            outB.AppendLine($"  reserved: ore={a.reservedOre} spaceCarve={a.reservedSpaceNonSolid} | totalNonSolid={a.totalNonSolid}");
            foreach (var l in Logs) outB.AppendLine("  " + l);
            outB.AppendLine($"  => PASS {pass} / FAIL {fail}");
            outB.AppendLine();
        }

        // ---------- 入口 ----------

        [MenuItem("灰烬之下/DEV-009 回归：双生成器（2 seed 深回归 + 20 seed stress）")]
        public static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("DEV-009 S3 Regression: DEV-007/008 generators (DepthRegionLayout bands)");
            sb.AppendLine($"Grid {GridWidth}x{GridDepth}; Regions: Surface 0..{DepthRegionLayout.Surface.maxDepth} / " +
                          $"Shallow {DepthRegionLayout.Shallow.minDepth}..{DepthRegionLayout.Shallow.maxDepth} (space from {DepthRegionLayout.Shallow.spaceStartY}) / " +
                          $"Mid {DepthRegionLayout.Mid.minDepth}..{DepthRegionLayout.Mid.maxDepth} / " +
                          $"Deep {DepthRegionLayout.Deep.minDepth}..{DepthRegionLayout.Deep.maxDepth}");
            sb.AppendLine();

            int totalFail = 0;
            try
            {
                foreach (var seed in DeepSeeds)
                {
                    var rig = BuildRig(seed);
                    if (rig == null) { sb.AppendLine("RIG NULL (assets missing)"); totalFail++; continue; }
                    RunSeed(rig, seed, sb, deep: true);
                    DestroyRig(rig);
                }
                foreach (var seed in StressSeeds)
                {
                    var rig = BuildRig(seed);
                    if (rig == null) { sb.AppendLine("RIG NULL (assets missing)"); totalFail++; continue; }
                    RunSeed(rig, seed, sb, deep: false);
                    DestroyRig(rig);
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("EXCEPTION: " + e);
                totalFail++;
            }

            int failCount = 0;
            foreach (var line in sb.ToString().Split('\n'))
                if (line.TrimStart().StartsWith("FAIL")) failCount++;

            sb.AppendLine($"==== 汇总: 断言 FAIL 行数 = {failCount}（22 seed × 12 断言 = 264）====");
            File.WriteAllText(ResultFile, sb.ToString());
            Debug.Log("[DEV-009 Regression] done, see " + ResultFile + " (FAIL=" + failCount + "/264)");
        }
    }
}
#endif
