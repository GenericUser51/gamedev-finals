using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Platformer controller built on CharacterController (first- or third-person).
/// Features: camera-relative movement, acceleration/deceleration, sprint,
/// variable jump height, coyote time, jump buffering, air jumps, kept air momentum,
/// stronger fall gravity, ground snapping, moving-platform carry, and fall-out respawn.
///
/// Input comes from the project-wide Input System actions (Player/Move, Player/Jump, Player/Sprint).
/// Extra moves (crouch, slide) live in separate components such as CrouchSlide that use the hooks below.
/// </summary>
[RequireComponent(typeof(CharacterController))]
[DefaultExecutionOrder(100)] // run after moving platforms so we follow them without lag
public class PlayerController : MonoBehaviour
{
    [Header("Movement")]
    [Tooltip("Top speed while walking (units/sec).")]
    [SerializeField] float walkSpeed = 6f;
    [Tooltip("Top speed while holding Sprint (units/sec).")]
    [SerializeField] float sprintSpeed = 10f;
    [Tooltip("How fast you reach top speed on the ground.")]
    [SerializeField] float groundAcceleration = 60f;
    [Tooltip("How fast you stop on the ground when releasing input.")]
    [SerializeField] float groundDeceleration = 70f;
    [Tooltip("Steering strength in the air. Lower = more committed jumps.")]
    [SerializeField] float airAcceleration = 25f;
    [Tooltip("How fast horizontal speed bleeds off in the air with no input.")]
    [SerializeField] float airDeceleration = 8f;
    [Tooltip("On = first-person: the body always faces where the camera looks. " +
             "Off = third-person: the body turns toward its movement direction.")]
    [SerializeField] bool faceCameraDirection = true;
    [Tooltip("Third-person only: how fast the character turns to face its movement (degrees/sec).")]
    [SerializeField] float turnSpeed = 900f;

    [Header("Jumping")]
    [Tooltip("Peak height of a full ground jump (units).")]
    [SerializeField] float jumpHeight = 2.2f;
    [Tooltip("Extra jumps allowed in the air. 0 = no double jump.")]
    [SerializeField] int airJumps = 1;
    [Tooltip("Peak height of an air jump (units).")]
    [SerializeField] float airJumpHeight = 1.6f;
    [Tooltip("Grace period after walking off a ledge where you can still jump (sec).")]
    [SerializeField] float coyoteTime = 0.12f;
    [Tooltip("Jump presses this long before landing still count (sec).")]
    [SerializeField] float jumpBufferTime = 0.15f;
    [Tooltip("Upward speed is multiplied by this when Jump is released early. Lower = shorter taps.")]
    [SerializeField, Range(0f, 1f)] float jumpCutMultiplier = 0.45f;
    [Tooltip("In the air, speed above your normal top speed (from slides, bounce pads) bleeds off this fast (units/sec²).")]
    [SerializeField] float airMomentumDrag = 2f;

    [Header("Gravity")]
    [Tooltip("Gravity strength while rising (units/sec²). Unity's default physics is 9.81; platformers feel better higher.")]
    [SerializeField] float gravity = 30f;
    [Tooltip("Gravity is multiplied by this while falling, for snappier, less floaty jumps.")]
    [SerializeField] float fallGravityMultiplier = 1.6f;
    [Tooltip("Terminal falling speed (units/sec).")]
    [SerializeField] float maxFallSpeed = 30f;
    [Tooltip("Small downward push while grounded so we stay glued to slopes and steps.")]
    [SerializeField] float groundedStickForce = 2f;
    [Tooltip("When running or sliding over a slope crest or down stairs, snap back to ground within this distance " +
             "instead of briefly going airborne.")]
    [SerializeField] float groundSnapDistance = 0.5f;

    [Header("Respawn")]
    [Tooltip("Falling below this Y position respawns the player.")]
    [SerializeField] float killHeight = -25f;
    [Tooltip("Where to respawn. Leave empty to use the starting position. Checkpoints can set this.")]
    [SerializeField] Transform respawnPoint;

    [Header("References")]
    [Tooltip("Camera used for camera-relative movement. Defaults to Camera.main.")]
    [SerializeField] Transform cameraTransform;
    [Tooltip("Lock and hide the mouse cursor while playing. Esc unlocks, click relocks.")]
    [SerializeField] bool lockCursor = true;
    [Tooltip("Movement input weaker than this is ignored, so worn gamepad sticks don't make the player creep.")]
    [SerializeField, Range(0f, 0.5f)] float moveDeadzone = 0.15f;

    // ---- Public state (handy for animation, UI, sound) ----
    public bool IsGrounded { get; private set; }
    public Vector3 Velocity => horizontalVelocity + Vector3.up * verticalVelocity;
    public float HorizontalSpeed => horizontalVelocity.magnitude;
    public bool IsSprinting { get; private set; }
    public bool IsGroundedOrCoyote => IsGrounded || coyoteTimer > 0f;
    public Vector3 GroundNormal => groundNormal;
    public float Gravity => gravity;
    public float WalkSpeed => walkSpeed;
    public event Action Jumped;     // fires on every jump (ground or air)
    public event Action AirJumped;  // fires only on air jumps
    public event Action Landed;     // fires when touching ground after being airborne
    public event Action Respawned;
    public event Action Teleported; // fires on any Teleport, including respawns
    public event Action<float> PlatformRotated; // yaw degrees a spinning platform turned us this frame

    // ---- Hooks for add-on components on the same object (e.g. CrouchSlide) ----
    /// <summary>Horizontal velocity. Add-ons may read or overwrite it.</summary>
    public Vector3 HorizontalVelocity { get => horizontalVelocity; set => horizontalVelocity = value; }
    /// <summary>While true, this controller leaves horizontal velocity to an add-on (e.g. during a slide).</summary>
    public bool HorizontalControlOverridden { get; set; }
    /// <summary>When above 0, replaces walk/sprint top speed and disables sprinting (e.g. crouch speed).</summary>
    public float SpeedLimitOverride { get; set; }

    CharacterController controller;
    InputAction moveAction, jumpAction, sprintAction;

    float feetOffset; // CharacterController center.y minus half its height (0 when the pivot is at the feet)
    Vector3 groundNormal = Vector3.up;

    Vector3 horizontalVelocity;
    float verticalVelocity;
    float coyoteTimer;
    float jumpBufferTimer;
    int airJumpsLeft;
    bool isJumping; // true while rising from a jump the player is still allowed to cut short

    Vector3 spawnPosition;
    Quaternion spawnRotation;

    // Moving platform tracking
    Transform groundTransform;
    Transform groundCandidate;
    Vector3 localPointOnGround;
    Quaternion lastGroundRotation;

    void Reset()
    {
        // Sensible capsule when the component is first added (pivot at the feet).
        var cc = GetComponent<CharacterController>();
        if (cc == null) return;
        cc.height = 2f;
        cc.radius = 0.5f;
        cc.center = new Vector3(0f, 1f, 0f);
        cc.stepOffset = 0.3f;
        cc.slopeLimit = 45f;
        cc.skinWidth = 0.08f;
    }

    void Awake()
    {
        controller = GetComponent<CharacterController>();

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        InputActionAsset actions = InputSystem.actions;
        if (actions == null)
        {
            Debug.LogError("PlayerController: no project-wide Input Actions found. " +
                           "Set one in Project Settings > Input System Package.", this);
            enabled = false;
            return;
        }
        moveAction = actions.FindAction("Player/Move", true);
        jumpAction = actions.FindAction("Player/Jump", true);
        sprintAction = actions.FindAction("Player/Sprint", true);
        feetOffset = controller.center.y - controller.height * 0.5f;

        spawnPosition = transform.position;
        spawnRotation = transform.rotation;
        airJumpsLeft = airJumps;
    }

    void Start()
    {
        if (lockCursor) SetCursorLocked(true);
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return; // paused

        HandleCursor();
        FollowMovingPlatform();

        bool wasGrounded = IsGrounded;
        IsGrounded = controller.isGrounded;

        if (IsGrounded && !wasGrounded) Landed?.Invoke();

        // --- Timers ---
        if (IsGrounded)
        {
            coyoteTimer = coyoteTime;
            airJumpsLeft = airJumps;
        }
        else
        {
            coyoteTimer -= dt;
        }

        if (jumpAction.WasPressedThisFrame()) jumpBufferTimer = jumpBufferTime;
        else jumpBufferTimer -= dt;

        // --- Horizontal movement ---
        Vector3 wishDir = GetMoveDirection();
        bool hasInput = wishDir.sqrMagnitude > 0.0001f;

        if (HorizontalControlOverridden)
        {
            IsSprinting = false; // an add-on (e.g. a slide) is driving horizontal velocity
        }
        else
        {
            bool speedLimited = SpeedLimitOverride > 0f;
            IsSprinting = sprintAction.IsPressed() && hasInput && !speedLimited;
            float targetSpeed = speedLimited ? SpeedLimitOverride : IsSprinting ? sprintSpeed : walkSpeed;
            float currentSpeed = HorizontalSpeed;

            if (!IsGrounded && hasInput && currentSpeed > targetSpeed)
            {
                // Keep extra momentum in the air (slide jumps, bounce pads): steer freely, lose speed slowly.
                Vector3 steered = Vector3.MoveTowards(horizontalVelocity, wishDir.normalized * currentSpeed,
                                                      airAcceleration * dt);
                horizontalVelocity = steered.normalized *
                                     Mathf.MoveTowards(currentSpeed, targetSpeed, airMomentumDrag * dt);
            }
            else
            {
                float accel = IsGrounded
                    ? (hasInput ? groundAcceleration : groundDeceleration)
                    : (hasInput ? airAcceleration : airDeceleration);
                horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, wishDir * targetSpeed, accel * dt);
            }
        }

        if (faceCameraDirection && cameraTransform != null)
        {
            Vector3 cameraForward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up);
            if (cameraForward.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(cameraForward, Vector3.up);
        }
        else if (hasInput)
        {
            Quaternion targetRotation = Quaternion.LookRotation(wishDir, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, turnSpeed * dt);
        }

        // --- Vertical: ground stick / gravity ---
        if (IsGrounded && verticalVelocity <= 0f)
        {
            verticalVelocity = -groundedStickForce;
            isJumping = false;
        }
        else
        {
            float g = verticalVelocity < 0f ? gravity * fallGravityMultiplier : gravity;
            verticalVelocity = Mathf.Max(verticalVelocity - g * dt, -maxFallSpeed);
        }

        // --- Jumping ---
        if (jumpBufferTimer > 0f)
        {
            if (coyoteTimer > 0f)
            {
                DoJump(jumpHeight);
                coyoteTimer = 0f;
            }
            else if (airJumpsLeft > 0)
            {
                DoJump(airJumpHeight);
                airJumpsLeft--;
                AirJumped?.Invoke();
            }
        }

        // Variable jump height: releasing Jump while rising cuts the jump short.
        if (isJumping && verticalVelocity > 0f && !jumpAction.IsPressed())
        {
            verticalVelocity *= jumpCutMultiplier;
            isJumping = false;
        }

        // --- Move ---
        bool groundedBeforeMove = IsGrounded;
        groundCandidate = null;
        groundNormal = Vector3.up;
        CollisionFlags flags = controller.Move(Velocity * dt);

        // Bonk: hitting a ceiling kills upward speed.
        if ((flags & CollisionFlags.Above) != 0 && verticalVelocity > 0f)
        {
            verticalVelocity = 0f;
            isJumping = false;
        }

        // Hitting a wall shouldn't keep building speed into it.
        if ((flags & CollisionFlags.Sides) != 0)
        {
            horizontalVelocity = Vector3.ProjectOnPlane(controller.velocity, Vector3.up);
        }

        // Stick to slopes and stairs instead of briefly launching off them.
        // (After the wall check, since this extra Move overwrites controller.velocity.)
        if (groundedBeforeMove && !controller.isGrounded && verticalVelocity <= 0f) SnapToGround();

        UpdateGroundTracking();

        if (transform.position.y < killHeight) Respawn();
    }

    void DoJump(float height)
    {
        verticalVelocity = Mathf.Sqrt(2f * gravity * height);
        jumpBufferTimer = 0f;
        isJumping = true;
        IsGrounded = false;
        groundTransform = null; // leave any moving platform
        Jumped?.Invoke();
    }

    /// <summary>The direction the player is asking to move this frame, relative to the camera (length 0–1).</summary>
    public Vector3 GetMoveDirection()
    {
        if (moveAction == null) return Vector3.zero;
        Vector2 input = moveAction.ReadValue<Vector2>();
        // Ignore tiny stick drift so the player doesn't creep on its own.
        if (input.magnitude < moveDeadzone) return Vector3.zero;
        return CameraRelative(input);
    }

    Vector3 CameraRelative(Vector2 input)
    {
        if (input.sqrMagnitude < 0.0001f) return Vector3.zero;

        Vector3 forward = Vector3.forward;
        Vector3 right = Vector3.right;
        if (cameraTransform != null)
        {
            forward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.0001f) // camera looking straight up/down
                forward = Vector3.ProjectOnPlane(cameraTransform.up, Vector3.up);
            forward.Normalize();
            right = Vector3.Cross(Vector3.up, forward);
        }

        return Vector3.ClampMagnitude(forward * input.y + right * input.x, 1f);
    }

    void SnapToGround()
    {
        float radius = controller.radius;
        Vector3 origin = transform.position + Vector3.up * (feetOffset + radius);
        float distance = groundSnapDistance + controller.skinWidth;

        if (Physics.SphereCast(origin, radius * 0.95f, Vector3.down, out RaycastHit hit, distance,
                               ~0, QueryTriggerInteraction.Ignore) &&
            hit.normal.y >= Mathf.Cos(controller.slopeLimit * Mathf.Deg2Rad))
        {
            controller.Move(Vector3.down * (hit.distance + 0.01f));
        }
    }

    // ---- Moving platforms ----

    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        // Only count surfaces we're standing on, not walls.
        if (hit.normal.y > 0.6f)
        {
            groundCandidate = hit.collider.transform;
            groundNormal = hit.normal;
        }
    }

    void UpdateGroundTracking()
    {
        groundTransform = controller.isGrounded ? groundCandidate : null;
        if (groundTransform != null)
        {
            localPointOnGround = groundTransform.InverseTransformPoint(transform.position);
            lastGroundRotation = groundTransform.rotation;
        }
    }

    void FollowMovingPlatform()
    {
        if (groundTransform == null) return;

        Vector3 targetPosition = groundTransform.TransformPoint(localPointOnGround);
        Vector3 delta = targetPosition - transform.position;
        if (delta.sqrMagnitude > 1e-8f) controller.Move(delta);

        // Rotate with spinning platforms (yaw only).
        Quaternion rotationDelta = groundTransform.rotation * Quaternion.Inverse(lastGroundRotation);
        float yaw = Mathf.DeltaAngle(0f, rotationDelta.eulerAngles.y);
        if (Mathf.Abs(yaw) > 0.001f)
        {
            transform.rotation = Quaternion.Euler(0f, yaw, 0f) * transform.rotation;
            PlatformRotated?.Invoke(yaw); // lets a first-person camera turn with us
        }
    }

    // ---- Respawn / teleport ----

    /// <summary>Set where the player respawns (e.g. from a checkpoint trigger).</summary>
    public void SetRespawnPoint(Transform point) => respawnPoint = point;

    public void Respawn()
    {
        if (respawnPoint != null) Teleport(respawnPoint.position, respawnPoint.rotation);
        else Teleport(spawnPosition, spawnRotation);
        Respawned?.Invoke();
    }

    /// <summary>Instantly move the player, clearing all momentum.</summary>
    public void Teleport(Vector3 position, Quaternion rotation)
    {
        // CharacterController overrides transform changes while enabled.
        controller.enabled = false;
        transform.SetPositionAndRotation(position, rotation);
        controller.enabled = true;

        horizontalVelocity = Vector3.zero;
        verticalVelocity = 0f;
        isJumping = false;
        jumpBufferTimer = 0f;
        groundTransform = null;
        Teleported?.Invoke();
    }

    /// <summary>Launch the player upward, e.g. from a bounce pad. Restores air jumps.</summary>
    public void Launch(float height)
    {
        DoJump(height);
        isJumping = false; // can't cut a bounce-pad launch short
        airJumpsLeft = airJumps;
    }

    // ---- Cursor ----

    void HandleCursor()
    {
        if (!lockCursor) return;
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            SetCursorLocked(false);
        else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame &&
                 Cursor.lockState != CursorLockMode.Locked)
            SetCursorLocked(true);
    }

    static void SetCursorLocked(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }
}
