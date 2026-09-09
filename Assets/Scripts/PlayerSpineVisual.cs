using System.Collections.Generic;
using Spine.Unity;
using UnityEngine;

namespace Ashfall
{
    /// <summary>
    /// Drives the player's Spine presentation from authoritative gameplay state.
    /// The component is visual-only: movement, collision and mining remain on DrillVehicle.
    /// </summary>
    [RequireComponent(typeof(SkeletonAnimation))]
    public sealed class PlayerSpineVisual : MonoBehaviour
    {
        public const string IdleAnimation = "idle";
        public const string WalkAnimation = "walk";
        public const string MiningAnimation = "skili01";
        public const string FlyingAnimation = "skill02";

        [Header("Gameplay references")]
        public DrillVehicle vehicle;
        public MiningFeelController mining;

        [Header("Animation thresholds")]
        [Min(0f)] public float moveThreshold = 0.08f;
        [Min(0f)] public float airborneSpeedThreshold = 0.12f;
        [Range(0f, 0.5f)] public float transitionDuration = 0.12f;

        public string CurrentAnimation => currentAnimation;
        public float Facing => facing;

        SkeletonAnimation skeletonAnimation;
        Rigidbody2D body;
        string currentAnimation;
        float facing = 1f;
        readonly HashSet<string> reportedMissingAnimations = new HashSet<string>();

        void Awake()
        {
            skeletonAnimation = GetComponent<SkeletonAnimation>();
            ResolveReferences();
        }

        void Start()
        {
            skeletonAnimation.Initialize(false);
            if (skeletonAnimation.valid)
                skeletonAnimation.SkeletonDataAsset.GetAnimationStateData().DefaultMix = transitionDuration;
            Play(IdleAnimation, true);
        }

        void Update()
        {
            ResolveReferences();
            if (skeletonAnimation == null || !skeletonAnimation.valid || vehicle == null || body == null)
                return;

            Vector2 velocity = body.linearVelocity;
            bool isMining = mining != null && mining.enabled && mining.IsMining;
            if (isMining && mining.CurrentDirection.x != 0)
                facing = Mathf.Sign(mining.CurrentDirection.x);
            else if (Mathf.Abs(velocity.x) > moveThreshold)
                facing = Mathf.Sign(velocity.x);
            skeletonAnimation.Skeleton.ScaleX = Mathf.Abs(skeletonAnimation.Skeleton.ScaleX) * facing;

            bool isFlying = !isMining && (vehicle.Jetting || (!vehicle.Grounded && velocity.sqrMagnitude > airborneSpeedThreshold * airborneSpeedThreshold));
            bool isWalking = !isMining && !isFlying && Mathf.Abs(velocity.x) > moveThreshold;

            if (isMining) Play(MiningAnimation, true);
            else if (isFlying) Play(FlyingAnimation, true);
            else if (isWalking) Play(WalkAnimation, true);
            else Play(IdleAnimation, true);
        }

        void ResolveReferences()
        {
            if (vehicle == null) vehicle = GetComponentInParent<DrillVehicle>();
            if (mining == null && vehicle != null) mining = vehicle.GetComponent<MiningFeelController>();
            if (body == null && vehicle != null) body = vehicle.GetComponent<Rigidbody2D>();
            if (skeletonAnimation == null) skeletonAnimation = GetComponent<SkeletonAnimation>();
        }

        void Play(string animationName, bool loop)
        {
            if (currentAnimation == animationName || skeletonAnimation == null || !skeletonAnimation.valid)
                return;

            if (skeletonAnimation.Skeleton.Data.FindAnimation(animationName) == null)
            {
                if (reportedMissingAnimations.Add(animationName))
                    Debug.LogError($"[PlayerSpineVisual] Missing Spine animation: {animationName}", this);
                if (animationName != IdleAnimation)
                    Play(IdleAnimation, true);
                return;
            }

            skeletonAnimation.AnimationState.SetAnimation(0, animationName, loop);
            currentAnimation = animationName;
        }
    }
}
