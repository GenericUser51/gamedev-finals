using System;
using StarterAssets;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Crouch and slide add-on for the Starter Assets FirstPersonController.
/// Put it on the same GameObject as FirstPersonController (PlayerCapsule).
///
/// Crouch: hold the Crouch action (C / Left Ctrl / gamepad B) to shrink the capsule, lower the camera and move slower.
/// Slide:  press Crouch while sprinting (or, with Slide On Landing, land while holding Crouch with enough speed).
///         Slides speed up downhill, slow down uphill, and jumping out of one keeps the speed in the air.
///
/// Remove or disable this component and FirstPersonController works exactly as before.
/// </summary>
[RequireComponent(typeof(FirstPersonController))]
[DefaultExecutionOrder(-10)] // before FirstPersonController, which then moves using what we set here
public class FirstPersonCrouchSlide : MonoBehaviour
{
    [Header("Crouch")]
    [Tooltip("Capsule height while crouched. Must be at least 2x the CharacterController radius.")]
    [SerializeField] float crouchHeight = 1.1f;
    [Tooltip("Top speed while crouched (m/s). Starter Assets walk speed is 4.")]
    [SerializeField] float crouchSpeed = 2f;
    [Tooltip("How fast the capsule shrinks/grows when crouching/standing (m/s).")]
    [SerializeField] float crouchTransitionSpeed = 8f;

    [Header("Slide")]
    [Tooltip("Must be moving at least this fast when pressing Crouch to slide. It's always at least " +
             "walk speed + 1, so you have to sprint (or carry momentum) to slide whatever the walk speed is.")]
    [SerializeField] float slideMinSpeed = 5f;
    [Tooltip("Speed added when a slide starts.")]
    [SerializeField] float slideBoost = 3f;
    [Tooltip("The start boost can't push you above this speed (existing speed above it is kept). " +
             "It's always at least sprint speed + Slide Boost, so a slide from a sprint always gets the full boost.")]
    [SerializeField] float slideBoostCap = 9f;
    [Tooltip("Absolute top speed while sliding, e.g. down long slopes.")]
    [SerializeField] float maxSlideSpeed = 20f;
    [Tooltip("Constant slowdown of a slide on flat ground (m/s²). Lower = longer slides. " +
             "With the defaults, a slide from sprint covers about 6 m.")]
    [SerializeField] float slideBaseFriction = 4f;
    [Tooltip("Extra slowdown on flat ground and uphill that grows with speed, so fast slides (e.g. off a ramp) " +
             "bleed speed quickly and ease into a stop. 0.35 at 20 m/s = 7 m/s² extra. Lower = longer slides.")]
    [SerializeField] float slideSpeedDrag = 0.35f;
    [Tooltip("The slide ends when speed drops below this.")]
    [SerializeField] float slideEndSpeed = 2.5f;
    [Tooltip("How far you can steer a slide (degrees/sec).")]
    [SerializeField] float slideSteering = 60f;
    [Tooltip("How much of gravity speeds up a slide going downhill. Higher = faster slides down slopes.")]
    [SerializeField] float slideDownhillAcceleration = 1.6f;
    [Tooltip("How much of gravity slows a slide going uphill.")]
    [SerializeField] float slideUphillDeceleration = 1.2f;
    [Tooltip("Slide friction is multiplied by this while going downhill (lower = slides keep more speed on slopes).")]
    [SerializeField, Range(0f, 1f)] float downhillFrictionMultiplier = 0.3f;
    [Tooltip("Delay after a slide ends before another can start (sec).")]
    [SerializeField] float slideCooldown = 0.4f;
    [Tooltip("Landing while holding Crouch with enough speed starts a slide automatically. " +
             "Off = slides only start from a Crouch press, so jump momentum is only kept by bunnyhopping.")]
    [SerializeField] bool slideOnLanding = false;
    [Tooltip("How long a slide survives being briefly airborne (bumps, slope seams) before it ends (sec).")]
    [SerializeField] float slideAirGraceTime = 0.3f;
    [Tooltip("While sliding down slopes, snap back to ground within this distance instead of lifting off.")]
    [SerializeField] float groundSnapDistance = 0.6f;

    [Header("Slide Jump")]
    [Tooltip("Jumping out of a slide keeps its speed in the air instead of dropping straight to sprint speed.")]
    [SerializeField] bool keepSlideMomentumInAir = true;
    [Tooltip("Steering while carrying slide momentum in the air (degrees/sec).")]
    [SerializeField] float airSteering = 120f;
    [Tooltip("How fast that extra air speed bleeds back down to sprint speed (m/s²).")]
    [SerializeField] float airMomentumDrag = 2f;

    // ---- Public state (handy for animation, UI, sound) ----
    public bool IsCrouching { get; private set; } // true while crouched or sliding
    public bool IsSliding => isSliding;
    /// <summary>Current slide speed (m/s), 0 when not sliding.</summary>
    public float SlideSpeed => isSliding ? velocity.magnitude : 0f;
    /// <summary>True while this component is steering the player (sliding, or carrying slide speed in the air).</summary>
    public bool IsDrivingMovement => isSliding || isCarrying;
    public event Action SlideStarted;
    public event Action SlideEnded;

    FirstPersonController fpc;
    CharacterController controller;
    StarterAssetsInputs input;
    PlayerInput playerInput;
    InputAction crouchAction;
    FirstPersonMomentum momentum; // optional: when present, it carries slide-jump speed instead of us

    Transform cameraRoot;          // PlayerCameraRoot: the camera follows it, so lowering it lowers the view
    float cameraRootStandingY;
    float standingHeight;
    float feetOffset;              // CharacterController center.y minus half its height

    bool isSliding;
    bool isCarrying;               // carrying slide momentum through the air after a slide jump
    float carryTime;
    float slideCooldownTimer;
    float airborneTime;
    bool wasGrounded;
    bool touchingGroundLastFrame;   // the capsule itself (not the looser Grounded check) was on the ground
    float timeSinceDownhill = 999f; // how long since the slide was last heading downhill
    Vector3 velocity;              // horizontal velocity we hand to FirstPersonController while overriding
    readonly Collider[] overlapBuffer = new Collider[8];

    void Awake()
    {
        fpc = GetComponent<FirstPersonController>();
        controller = GetComponent<CharacterController>();
        input = GetComponent<StarterAssetsInputs>();
        playerInput = GetComponent<PlayerInput>();
        momentum = GetComponent<FirstPersonMomentum>();

        standingHeight = controller.height;
        feetOffset = controller.center.y - controller.height * 0.5f;
        crouchHeight = Mathf.Clamp(crouchHeight, controller.radius * 2f + 0.01f, standingHeight);

        if (fpc.CinemachineCameraTarget != null)
        {
            cameraRoot = fpc.CinemachineCameraTarget.transform;
            cameraRootStandingY = cameraRoot.localPosition.y;
        }
    }

    void Start()
    {
        // PlayerInput sets up its actions by Start.
        if (playerInput != null && playerInput.actions != null)
            crouchAction = playerInput.actions.FindAction("Crouch", false);

        if (crouchAction == null)
        {
            Debug.LogWarning("FirstPersonCrouchSlide: no 'Crouch' action in the PlayerInput's actions asset, " +
                             "crouch and slide are disabled.", this);
            enabled = false;
        }
    }

    void OnEnable() => fpc.Jumped += OnJumped;

    void OnDisable()
    {
        fpc.Jumped -= OnJumped;

        // Hand full control back to FirstPersonController and stand up.
        isSliding = false;
        isCarrying = false;
        IsCrouching = false;
        fpc.HorizontalControlOverridden = false;
        fpc.SpeedLimitOverride = 0f;
        if (standingHeight > 0f) SetHeight(standingHeight);
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return; // paused

        bool grounded = fpc.Grounded;
        airborneTime = grounded ? 0f : airborneTime + dt;
        bool crouchHeld = crouchAction.IsPressed();
        bool crouchPressed = crouchAction.WasPressedThisFrame();
        bool justLanded = grounded && !wasGrounded;
        wasGrounded = grounded;
        slideCooldownTimer -= dt;

        // What we actually moved last frame (accounts for walls, slopes and steps). Not controller.velocity:
        // ground snapping moves the capsule again afterwards, which would read as a near-stop and stutter.
        Vector3 actual = fpc.LastMoveVelocity;
        actual.y = 0f;

        if (isCarrying) UpdateCarry(actual, grounded, dt);

        // Slide = press Crouch while moving fast (or land holding Crouch, if Slide On Landing is on).
        float minSlideSpeed = Mathf.Max(slideMinSpeed, fpc.MoveSpeed + 1f); // never slide from a plain walk
        if (!isSliding && grounded && slideCooldownTimer <= 0f && actual.magnitude >= minSlideSpeed &&
            (crouchPressed || (slideOnLanding && justLanded && crouchHeld)))
        {
            StartSlide(actual);
        }
        else if (isSliding)
        {
            UpdateSlide(actual, grounded, crouchHeld, dt);
        }

        UpdateCrouchHeight(crouchHeld, dt);

        // Tell FirstPersonController what to do this frame.
        fpc.HorizontalControlOverridden = isSliding || isCarrying;
        fpc.OverrideHorizontalVelocity = velocity;
        fpc.SpeedLimitOverride = IsCrouching && !isSliding ? crouchSpeed : 0f;
    }

    void LateUpdate()
    {
        // FirstPersonController has moved by now. Sliding fast down a slope outruns its small downward
        // push and briefly lifts off, which would end the slide, so pull back onto the ground instead.
        // Only for ground we were really on last frame: snapping while landing is a visible jolt.
        if (isSliding && touchingGroundLastFrame && !controller.isGrounded) SnapToGround();
        touchingGroundLastFrame = controller.isGrounded;
    }

    void SnapToGround()
    {
        float radius = controller.radius;
        Vector3 origin = transform.position + Vector3.up * (feetOffset + radius);
        float distance = groundSnapDistance + controller.skinWidth;

        if (Physics.SphereCast(origin, radius * 0.95f, Vector3.down, out RaycastHit hit, distance,
                               fpc.GroundLayers, QueryTriggerInteraction.Ignore) &&
            hit.normal.y >= Mathf.Cos(controller.slopeLimit * Mathf.Deg2Rad))
        {
            controller.Move(Vector3.down * (hit.distance + 0.01f));
        }
    }

    // ---- Slide ----

    void StartSlide(Vector3 actual)
    {
        float speed = actual.magnitude;
        // Boost up to the cap, but never take away speed you already had.
        float boostCap = Mathf.Max(slideBoostCap, fpc.SprintSpeed + slideBoost);
        float boosted = Mathf.Max(speed, Mathf.Min(speed + slideBoost, boostCap));
        velocity = actual / speed * boosted;
        isSliding = true;
        isCarrying = false;
        SlideStarted?.Invoke();
    }

    void EndSlide()
    {
        isSliding = false;
        slideCooldownTimer = slideCooldown;
        SlideEnded?.Invoke();
    }

    void UpdateSlide(Vector3 actual, bool grounded, bool crouchHeld, float dt)
    {
        float speed = actual.magnitude;
        Vector3 direction = speed > 0.001f ? actual / speed : transform.forward;

        // Limited steering, so a slide commits you to a direction.
        Vector3 wishDir = WishDirection();
        if (wishDir != Vector3.zero)
            direction = Vector3.RotateTowards(direction, wishDir, slideSteering * Mathf.Deg2Rad * dt, 0f);

        // Slopes: which way is downhill, and are we heading down (+) or up (-) it?
        Vector3 downhill = Vector3.zero;
        float alignment = 0f;
        if (grounded)
        {
            downhill = Vector3.ProjectOnPlane(Vector3.down, GroundNormal()); // length grows with steepness
            downhill.y = 0f;
            if (downhill.sqrMagnitude > 0.0025f) alignment = Vector3.Dot(direction, downhill.normalized);
            else downhill = Vector3.zero; // flat enough to ignore
        }

        bool headingDownhill = alignment > 0.1f;
        timeSinceDownhill = headingDownhill ? 0f : timeSinceDownhill + dt;

        // Friction: much lower heading downhill; on flat ground and uphill it grows with speed, so fast
        // slides decelerate hard at first and ease into a stop. None during a brief hop off a bump.
        if (grounded)
        {
            float friction = headingDownhill
                ? slideBaseFriction * downhillFrictionMultiplier
                : slideBaseFriction + slideSpeedDrag * speed;
            speed = Mathf.MoveTowards(speed, 0f, friction * dt);
        }
        velocity = direction * speed;

        // Gravity along the slope: downhill speeds you up, uphill slows you down.
        if (downhill != Vector3.zero)
        {
            float factor = alignment >= 0f ? slideDownhillAcceleration : slideUphillDeceleration;
            velocity += downhill * (Mathf.Abs(fpc.Gravity) * factor * dt);
        }
        velocity = Vector3.ClampMagnitude(velocity, maxSlideSpeed);

        // Heading downhill (or just were, e.g. hopping off a bump on the slope): only letting go of Crouch ends it.
        if (timeSinceDownhill < slideAirGraceTime)
        {
            if (!crouchHeld) EndSlide();
            return;
        }

        bool tooSlow = velocity.magnitude < slideEndSpeed;
        bool airborne = airborneTime > slideAirGraceTime; // slid off a ledge, not just a bump
        if (tooSlow || !crouchHeld || airborne) EndSlide();
    }

    void OnJumped()
    {
        if (!isSliding) return;
        EndSlide();

        // FirstPersonMomentum (if present) keeps slide-jump speed on its own from next frame on.
        bool momentumHandlesIt = momentum != null && momentum.isActiveAndEnabled;
        if (keepSlideMomentumInAir && !momentumHandlesIt)
        {
            isCarrying = true;
            carryTime = 0f;
        }

        // Jumped fires inside FirstPersonController's Update, before it moves. Keep this frame's movement
        // at slide speed; whoever drives next frame takes it from there.
        fpc.HorizontalControlOverridden = isCarrying || momentumHandlesIt;
        fpc.OverrideHorizontalVelocity = velocity;
    }

    void UpdateCarry(Vector3 actual, bool grounded, float dt)
    {
        carryTime += dt;
        // Grounded can stay true for a moment right after jumping, so only count landings after a short delay.
        if (grounded && carryTime > 0.2f)
        {
            isCarrying = false;
            return;
        }

        float speed = actual.magnitude;
        Vector3 direction = speed > 0.001f ? actual / speed : transform.forward;
        Vector3 wishDir = WishDirection();
        if (wishDir != Vector3.zero)
            direction = Vector3.RotateTowards(direction, wishDir, airSteering * Mathf.Deg2Rad * dt, 0f);

        speed = Mathf.MoveTowards(speed, fpc.SprintSpeed, airMomentumDrag * dt);
        velocity = direction * speed;

        // Back to normal speed: hand control back to FirstPersonController.
        if (speed <= fpc.SprintSpeed + 0.05f) isCarrying = false;
    }

    Vector3 WishDirection()
    {
        Vector2 move = input != null ? input.move : Vector2.zero;
        if (move.sqrMagnitude < 0.0001f) return Vector3.zero;
        return (transform.right * move.x + transform.forward * move.y).normalized;
    }

    Vector3 GroundNormal()
    {
        Vector3 origin = transform.position + Vector3.up * (feetOffset + 0.3f);
        return Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 0.8f, fpc.GroundLayers,
                               QueryTriggerInteraction.Ignore)
            ? hit.normal
            : Vector3.up;
    }

    // ---- Crouch ----

    void UpdateCrouchHeight(bool crouchHeld, float dt)
    {
        bool wantCrouch = crouchHeld || isSliding;
        // Stay crouched under low ceilings even after letting go of Crouch.
        if (!wantCrouch && controller.height < standingHeight - 0.001f && !CanStandUp()) wantCrouch = true;
        IsCrouching = wantCrouch;

        float targetHeight = wantCrouch ? crouchHeight : standingHeight;
        if (!Mathf.Approximately(controller.height, targetHeight))
            SetHeight(Mathf.MoveTowards(controller.height, targetHeight, crouchTransitionSpeed * dt));
    }

    // Shrink/grow from the top so the feet stay planted, and lower the camera by the same amount.
    void SetHeight(float height)
    {
        controller.height = height;
        Vector3 center = controller.center;
        center.y = feetOffset + height * 0.5f;
        controller.center = center;

        if (cameraRoot != null)
        {
            Vector3 rootPosition = cameraRoot.localPosition;
            rootPosition.y = cameraRootStandingY - (standingHeight - height);
            cameraRoot.localPosition = rootPosition;
        }
    }

    bool CanStandUp()
    {
        float radius = controller.radius;
        Vector3 feet = transform.position + Vector3.up * feetOffset;
        Vector3 bottom = feet + Vector3.up * (radius + 0.1f); // lifted so the floor doesn't count
        Vector3 top = feet + Vector3.up * (standingHeight - radius);

        int count = Physics.OverlapCapsuleNonAlloc(bottom, top, radius * 0.95f, overlapBuffer,
                                                   ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Collider other = overlapBuffer[i];
            if (other == controller || other.transform.IsChildOf(transform)) continue; // ourselves
            return false;
        }
        return true;
    }
}
