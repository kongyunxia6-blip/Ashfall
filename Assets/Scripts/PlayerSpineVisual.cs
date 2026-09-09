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

        SkeletonAnimation skeletonAnimation;
        Rigidbody2D body;
        string currentAnimation;
        float facing = 1f;

        void Awake()
        {
            skeletonAnimation = GetComponent<SkeletonAnimation>();
            ResolveReferences();
        }

        void Start()
        {
            skeletonAnimation.Initialize(false);
            Play(IdleAnimation, true);
        }

        void Update()
        {
            ResolveReferences();
            if (skeletonAnimation == null || !skeletonAnimation.valid || vehicle == null || body == null)
                return;

            Vector2 velocity = body.linearVelocity;
            if (Mathf.Abs(velocity.x) > moveThreshold)
                facing = Mathf.Sign(velocity.x);
            skeletonAnimation.Skeleton.ScaleX = Mathf.Abs(skeletonAnimation.Skeleton.ScaleX) * facing;

            bool isMining = mining != null && mining.enabled && mining.IsMining;
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
                Debug.LogWarning($"[PlayerSpineVisual] Missing Spine animation: {animationName}", this);
                return;
            }

            skeletonAnimation.AnimationState.SetAnimation(0, animationName, loop);
            currentAnimation = animationName;
        }
    }
}
