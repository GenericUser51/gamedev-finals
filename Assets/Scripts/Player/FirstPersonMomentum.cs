using StarterAssets;
using UnityEngine;

/// <summary>
/// Momentum add-on for the Starter Assets FirstPersonController. Put it on PlayerCapsule.
///
/// Replaces the controller's "snap toward input" movement with real velocity:
/// - Ground: accelerate up to walk/sprint speed, brake when you let go.
/// - Extra speed (from slides, ramps, slide-jumps) carries through the air, but on landing it's only kept
///   if you bunnyhop (jump again within the bunnyhop window); otherwise it's scrubbed off quickly.
///   Running downhill is the exception: gravity keeps feeding speed there.
/// - Air: limited steering, so you can't do instant 180° turns mid-jump.
///
/// Works alongside FirstPersonCrouchSlide (lets it drive during slides) and FirstPersonMantle.
/// Remove or disable this component and FirstPersonController moves exactly as before.
/// </summary>
[RequireComponent(typeof(FirstPersonController))]
[DefaultExecutionOrder(-5)] // after FirstPersonCrouchSlide (-10), before FirstPersonController (0)
public class FirstPersonMomentum : MonoBehaviour
{
    [Header("Ground")]
    [Tooltip("How fast you reach walk/sprint speed (m/s²).")]
    [SerializeField] float groundAcceleration = 40f;
    [Tooltip("How fast you stop when letting go of the movement keys (m/s²).")]
    [SerializeField] float groundDeceleration = 30f;
    [Tooltip("How fast speed above your normal top speed is scrubbed off on the ground once the bunnyhop window " +
             "has passed (m/s²). High = momentum only survives by bunnyhopping. Downhill is exempt.")]
    [SerializeField] float groundOverspeedFriction = 25f;
    [Tooltip("How fast you can steer while moving faster than normal top speed on the ground (degrees/sec).")]
    [SerializeField] float groundOverspeedTurnRate = 240f;

    [Header("Air")]
    [Tooltip("Steering strength in the air below normal top speed (m/s²). Lower = more committed jumps.")]
    [SerializeField] float airAcceleration = 12f;
    [Tooltip("How fast you can turn while carrying extra speed in the air (degrees/sec).")]
    [SerializeField] float airTurnRate = 90f;
    [Tooltip("How fast extra speed fades in the air (m/s²).")]
    [SerializeField] float airOverspeedDecay = 1.5f;
    [Tooltip("How fast you slow down in the air with no input (m/s²).")]
    [SerializeField] float airDrag = 1f;

    [Header("Slopes")]
    [Tooltip("How much of gravity speeds you up when moving downhill. Extra speed builds past your normal top speed. " +
             "Walking and sprinting uphill aren't slowed (only slides are, in FirstPersonCrouchSlide).")]
    [SerializeField] float downhillAcceleration = 0.6f;
    [Tooltip("Slopes gentler than this (degrees) count as flat.")]
    [SerializeField] float minSlopeAngle = 3f;
    [Tooltip("When running fast over a crest or down a slope, snap back to ground within this distance instead of lifting off.")]
    [SerializeField] float groundSnapDistance = 0.5f;

    [Header("Chaining")]
    [Tooltip("Bunnyhop window: after landing, speed is fully kept for this long. Jump within it to carry " +
             "momentum into the next jump; miss it and the extra speed is scrubbed off (sec).")]
    [SerializeField] float bunnyhopWindow = 0.2f;
    [Tooltip("Absolute top speed from any source (m/s).")]
    [SerializeField] float maxSpeed = 20f;

    public Vector3 HorizontalVelocity => velocity;
    public float Speed => velocity.magnitude;

    FirstPersonController fpc;
    CharacterController controller;
    StarterAssetsInputs input;
    FirstPersonCrouchSlide crouchSlide;   // optional

    Vector3 velocity;
    float timeSinceJump = 999f;
    float timeSinceLanding = 999f;
    bool wasGrounded = true;
    bool snapAfterMove; // we moved the player along the ground this frame; keep them on it
    bool touchingGroundLastFrame; // the capsule itself (not the looser Grounded check) was on the ground

    void Awake()
    {
        fpc = GetComponent<FirstPersonController>();
        controller = GetComponent<CharacterController>();
        input = GetComponent<StarterAssetsInputs>();
        crouchSlide = GetComponent<FirstPersonCrouchSlide>();
    }

    void OnEnable() => fpc.Jumped += OnJumped;

    void OnDisable()
    {
        fpc.Jumped -= OnJumped;
        // Hand movement back unless another add-on is using it.
        if (crouchSlide == null || !crouchSlide.IsDrivingMovement) fpc.HorizontalControlOverridden = false;
    }

    void OnJumped()
    {
        timeSinceJump = 0f;
        snapAfterMove = false; // don't pull a jump back down
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return; // paused

        timeSinceJump += dt;
        timeSinceLanding += dt;

        // Start from what actually happened last frame, so walls and slopes eat speed naturally.
        // Not controller.velocity: ground snapping moves the capsule again afterwards, which would read
        // as a near-stop every other frame and make movement stutter.
        velocity = fpc.LastMoveVelocity;
        velocity.y = 0f;

        // Starter Assets' ground check still reads "grounded" for a moment after a jump; treat that as air.
        bool grounded = fpc.Grounded && timeSinceJump > 0.15f;
        if (grounded && !wasGrounded) timeSinceLanding = 0f;
        wasGrounded = grounded;
        snapAfterMove = false;

        // A slide, slide-jump or mantle is in charge right now: just keep tracking.
        if (fpc.MovementSuspended || (crouchSlide != null && crouchSlide.isActiveAndEnabled && crouchSlide.IsDrivingMovement))
            return;

        Vector2 move = input != null ? input.move : Vector2.zero;
        bool hasInput = move.sqrMagnitude > 0.0001f;
        Vector3 wishDir = hasInput ? (transform.right * move.x + transform.forward * move.y).normalized : Vector3.zero;
        float inputMagnitude = input != null && input.analogMovement ? Mathf.Clamp01(move.magnitude) : 1f;

        float topSpeed = fpc.SpeedLimitOverride > 0f
            ? fpc.SpeedLimitOverride
            : (input != null && input.sprint ? fpc.SprintSpeed : fpc.MoveSpeed);
        float wishSpeed = topSpeed * inputMagnitude;

        velocity = grounded
            ? GroundMove(velocity, wishDir, hasInput, wishSpeed, dt)
            : AirMove(velocity, wishDir, hasInput, wishSpeed, dt);
        velocity = Vector3.ClampMagnitude(velocity, maxSpeed);

        fpc.HorizontalControlOverridden = true;
        fpc.OverrideHorizontalVelocity = velocity;
        // Only keep the player on ground they were really standing on. Never snap while landing:
        // the Grounded check turns true ~0.3 m above the floor, and snapping then is a visible jolt.
        snapAfterMove = grounded && touchingGroundLastFrame;
    }

    void LateUpdate()
    {
        // FirstPersonController has moved by now. Running fast down a slope outruns its small downward push
        // and lifts off, which would stop slope speed-up, so pull back onto the ground instead.
        if (snapAfterMove && !controller.isGrounded && !fpc.MovementSuspended) SnapToGround();
        snapAfterMove = false;
        touchingGroundLastFrame = controller.isGrounded;
    }

    Vector3 GroundMove(Vector3 v, Vector3 wishDir, bool hasInput, float wishSpeed, float dt)
    {
        float speed = v.magnitude;
        bool inLandingGrace = timeSinceLanding < bunnyhopWindow;

        // Which way is downhill, and are we heading down (+1), across (0) or up (-1) it?
        GetSlope(out Vector3 downhillDir, out float steepness);
        Vector3 travelDir = speed > 0.1f ? v / speed : wishDir;
        float alignment = steepness > 0f ? Vector3.Dot(travelDir, downhillDir) : 0f;

        // Uphill doesn't slow walking or sprinting; only downhill matters (it feeds extra speed).
        bool goingDownhill = alignment > 0.1f;

        if (!hasInput)
        {
            // No keys held: come to a halt. Extra speed brakes harder still, so landing from a slide-jump
            // stops you within a couple of metres. (A buffered jump fires on the landing frame itself,
            // so bunnyhops don't need any grace time here.)
            float brake = speed > wishSpeed ? groundDeceleration + groundOverspeedFriction : groundDeceleration;
            return Vector3.MoveTowards(v, Vector3.zero, brake * dt);
        }

        Vector3 result;
        if (speed <= wishSpeed + 0.01f)
        {
            result = Vector3.MoveTowards(v, wishDir * wishSpeed, groundAcceleration * dt);
        }
        else
        {
            // Faster than normal: steer the extra speed and let it fade instead of dropping it instantly.
            // Downhill it doesn't fade at all; gravity keeps feeding it.
            Vector3 direction = Vector3.RotateTowards(v / speed, wishDir, groundOverspeedTurnRate * Mathf.Deg2Rad * dt, 0f);
            float newSpeed = inLandingGrace || goingDownhill
                ? speed
                : Mathf.MoveTowards(speed, wishSpeed, groundOverspeedFriction * dt);
            result = direction * newSpeed;
        }

        // Gravity along the slope speeds you up going down (going up, it doesn't slow you).
        if (alignment > 0f)
        {
            float slopeAccel = Mathf.Abs(fpc.Gravity) * steepness * alignment * downhillAcceleration;
            float resultSpeed = result.magnitude;
            if (resultSpeed > 0.01f) result *= Mathf.Max(0f, resultSpeed + slopeAccel * dt) / resultSpeed;
        }
        return result;
    }

    /// <summary>Horizontal downhill direction and steepness (sine of the slope angle; 0 on flat ground).</summary>
    void GetSlope(out Vector3 downhillDir, out float steepness)
    {
        downhillDir = Vector3.zero;
        steepness = 0f;

        Vector3 origin = transform.position + Vector3.up * (controller.center.y - controller.height * 0.5f + 0.3f);
        if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 0.8f, fpc.GroundLayers,
                             QueryTriggerInteraction.Ignore))
            return;

        Vector3 down = Vector3.ProjectOnPlane(Vector3.down, hit.normal);
        down.y = 0f;
        if (down.sqrMagnitude < 1e-6f) return; // flat

        float sine = Mathf.Sqrt(Mathf.Max(0f, 1f - hit.normal.y * hit.normal.y));
        if (sine < Mathf.Sin(minSlopeAngle * Mathf.Deg2Rad)) return;

        downhillDir = down.normalized;
        steepness = sine;
    }

    void SnapToGround()
    {
        float radius = controller.radius;
        Vector3 origin = transform.position + Vector3.up * (controller.center.y - controller.height * 0.5f + radius);
        float distance = groundSnapDistance + controller.skinWidth;

        if (Physics.SphereCast(origin, radius * 0.95f, Vector3.down, out RaycastHit hit, distance,
                               fpc.GroundLayers, QueryTriggerInteraction.Ignore) &&
            hit.normal.y >= Mathf.Cos(controller.slopeLimit * Mathf.Deg2Rad))
        {
            controller.Move(Vector3.down * (hit.distance + 0.01f));
        }
    }

    Vector3 AirMove(Vector3 v, Vector3 wishDir, bool hasInput, float wishSpeed, float dt)
    {
        float speed = v.magnitude;

        if (!hasInput) return Vector3.MoveTowards(v, Vector3.zero, airDrag * dt);

        if (speed <= wishSpeed + 0.01f)
            return Vector3.MoveTowards(v, wishDir * wishSpeed, airAcceleration * dt);

        Vector3 direction = Vector3.RotateTowards(v / speed, wishDir, airTurnRate * Mathf.Deg2Rad * dt, 0f);
        return direction * Mathf.MoveTowards(speed, wishSpeed, airOverspeedDecay * dt);
    }
}
