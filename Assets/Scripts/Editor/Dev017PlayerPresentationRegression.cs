#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Spine.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Ashfall.EditorTools
{
    /// <summary>DEV-017：玩家 Spine 角色资源、场景接线与动画状态配置回归。</summary>
    public static class Dev017PlayerPresentationRegression
    {
        const string ScenePath = "Assets/Scenes/WorldGenV1.unity";
        const string SkeletonPath = "Assets/Art/Characters/Player/Spine/PlayerMiner_SkeletonData.asset";
        const string OutputPath = "C:/Users/58058/.workbuddy/tools/dev017_regression_result.txt";

        static int pass;
        static int fail;
        static readonly StringBuilder Log = new StringBuilder();

        static void Check(bool condition, string tag, string detail)
        {
            if (condition) { pass++; Log.AppendLine($"PASS  {tag}: {detail}"); }
            else { fail++; Log.AppendLine($"FAIL  {tag}: {detail}"); }
        }

        [MenuItem("灰烬之下/DEV-017 回归：玩家 Spine 表现 V1")]
        public static void Run()
        {
            pass = 0;
            fail = 0;
            Log.Clear();
            Log.AppendLine("DEV-017 Player Spine Presentation Regression V1");

            try
            {
                ValidateAssets();
                ValidateScene();
            }
            catch (Exception e)
            {
                Check(false, "EXCEPTION", e.ToString());
            }

            Log.AppendLine();
            Log.AppendLine($"==== 汇总: PASS {pass} / FAIL {fail} ====");
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            File.WriteAllText(OutputPath, Log.ToString());
            Debug.Log($"[DEV-017 Player Presentation Regression] done -> {OutputPath} (PASS {pass} / FAIL {fail})");
            if (fail > 0) throw new InvalidOperationException($"DEV-017 regression failed: {fail}");
        }

        static void ValidateAssets()
        {
            var skeletonAsset = AssetDatabase.LoadAssetAtPath<SkeletonDataAsset>(SkeletonPath);
            Check(skeletonAsset != null, "SkeletonData_Exists", SkeletonPath);
            if (skeletonAsset == null) return;

            var data = skeletonAsset.GetSkeletonData(true);
            Check(data != null, "SkeletonData_Valid", "Spine skeleton data can be loaded");
            if (data == null) return;

            string[] required =
            {
                PlayerSpineVisual.IdleAnimation,
                PlayerSpineVisual.WalkAnimation,
                PlayerSpineVisual.MiningAnimation,
                PlayerSpineVisual.FlyingAnimation
            };
            foreach (string animationName in required)
                Check(data.FindAnimation(animationName) != null, "Animation_" + animationName,
                    $"required animation '{animationName}' exists");

            Check(skeletonAsset.atlasAssets != null && skeletonAsset.atlasAssets.Length > 0 &&
                  skeletonAsset.atlasAssets.All(a => a != null), "Atlas_Bound", "SkeletonData has a valid atlas");
        }

        static void ValidateScene()
        {
            Check(File.Exists(Path.GetFullPath(ScenePath)), "WorldScene_Exists", ScenePath);
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var drivers = UnityEngine.Object.FindObjectsByType<PlayerSpineVisual>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var skeletons = UnityEngine.Object.FindObjectsByType<SkeletonAnimation>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var players = UnityEngine.Object.FindObjectsByType<DrillVehicle>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var mining = UnityEngine.Object.FindObjectsByType<MiningFeelController>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            Check(scene.IsValid() && scene.isLoaded, "WorldScene_Loaded", "WorldGenV1 opened successfully");
            Check(players.Length == 1, "Player_Count1", $"DrillVehicle count={players.Length}");
            Check(drivers.Length == 1, "SpineDriver_Count1", $"PlayerSpineVisual count={drivers.Length}");
            Check(skeletons.Length == 1, "SkeletonAnimation_Count1", $"SkeletonAnimation count={skeletons.Length}");
            Check(mining.Length == 1, "MiningController_Count1", $"MiningFeelController count={mining.Length}");
            if (players.Length != 1 || drivers.Length != 1 || skeletons.Length != 1 || mining.Length != 1) return;

            var driver = drivers[0];
            var visual = skeletons[0];
            var renderer = visual.GetComponent<MeshRenderer>();
            Check(driver.vehicle == players[0], "Vehicle_Reference", "visual driver references the scene DrillVehicle");
            Check(driver.mining == mining[0], "Mining_Reference", "visual driver references the scene MiningFeelController");
            Check(driver.transform.IsChildOf(players[0].transform), "Visual_ChildOfPlayer", "Spine visual is parented under Player");
            Check(visual.SkeletonDataAsset != null, "Scene_SkeletonBound", "SkeletonAnimation has SkeletonDataAsset");
            Check(visual.loop && visual.AnimationName == PlayerSpineVisual.IdleAnimation,
                "Scene_DefaultIdle", "scene starts in looping idle");
            Check(Mathf.Approximately(driver.transitionDuration, 0.12f), "TransitionMix", "animation transition duration=0.12s");
            Check(renderer != null && renderer.sortingOrder == 10, "SortingOrder", "player renders at sorting order 10");
            Check(driver.GetComponent<SpriteRenderer>() == null && players[0].GetComponent<SpriteRenderer>() == null,
                "NoPlaceholderSprite", "legacy placeholder SpriteRenderer is absent");

            ValidateRuntimeStates(driver, players[0], mining[0]);
        }

        static void ValidateRuntimeStates(PlayerSpineVisual driver, DrillVehicle vehicle, MiningFeelController mining)
        {
            var body = vehicle.GetComponent<Rigidbody2D>();
            Invoke(driver, "Awake");
            Invoke(driver, "Start");
            body.linearVelocity = Vector2.zero;
            SetAutoProperty(vehicle, "Grounded", true);
            SetAutoProperty(vehicle, "Jetting", false);
            SetAutoProperty(mining, "IsMining", false);
            Invoke(driver, "Update");
            Check(driver.CurrentAnimation == PlayerSpineVisual.IdleAnimation, "Runtime_Idle", "stationary player selects idle");

            body.linearVelocity = new Vector2(2f, 0f);
            Invoke(driver, "Update");
            Check(driver.CurrentAnimation == PlayerSpineVisual.WalkAnimation, "Runtime_Walk", "ground movement selects walk");
            Check(driver.Facing > 0f, "Runtime_FaceRight", "positive movement faces right");

            body.linearVelocity = new Vector2(-2f, 1f);
            SetAutoProperty(vehicle, "Grounded", false);
            SetAutoProperty(vehicle, "Jetting", true);
            Invoke(driver, "Update");
            Check(driver.CurrentAnimation == PlayerSpineVisual.FlyingAnimation, "Runtime_Fly", "jetting selects flying animation");
            Check(driver.Facing < 0f, "Runtime_FaceLeft", "negative movement faces left");

            SetField(mining, "currentDir", Vector2Int.right);
            SetAutoProperty(mining, "IsMining", true);
            Invoke(driver, "Update");
            Check(driver.CurrentAnimation == PlayerSpineVisual.MiningAnimation, "Runtime_MinePriority",
                "mining overrides flying and walking");
            Check(driver.Facing > 0f, "Runtime_MiningFacing", "mining direction controls facing while mining");
            Check(Mathf.Approximately(driver.GetComponent<SkeletonAnimation>().SkeletonDataAsset
                .GetAnimationStateData().DefaultMix, driver.transitionDuration), "Runtime_DefaultMix",
                "Spine AnimationStateData uses configured transition duration");
        }

        static void Invoke(object target, string methodName)
        {
            target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);
        }

        static void SetField(object target, string fieldName, object value)
        {
            target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        }

        static void SetAutoProperty(object target, string propertyName, object value)
        {
            SetField(target, $"<{propertyName}>k__BackingField", value);
        }
    }
}
#endif
