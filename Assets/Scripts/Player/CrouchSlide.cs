using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Crouch and slide add-on for PlayerController. Put it on the same GameObject as PlayerController.
///
/// Crouch: hold the Player/Crouch action to shrink the capsule and move slower.
/// Slide:  press Crouch while moving fast (sprinting), or land while holding Crouch with enough speed.
///         Slides keep momentum into jumps, speed up downhill and slow down uphill.
///
/// Remove or disable this component and PlayerController works exactly as before.
/// </summary>
[RequireComponent(typeof(PlayerController))]
[DefaultExecutionOrder(90)] // just before PlayerController (100), which then moves using what we set here
public class CrouchSlide : MonoBehaviour
{
    [Header("Crouch")]
    [Tooltip("Capsule height while crouched. Must be at least 2x the CharacterController radius.")]
    [SerializeField] float crouchHeight = 1.1f;
    [Tooltip("Top speed while crouched (units/sec).")]
    [SerializeField] float crouchSpeed = 3f;
    [Tooltip("How fast the capsule shrinks/grows when crouching/standing (units/sec).")]
    [SerializeField] float crouchTransitionSpeed = 8f;

    [Header("Slide")]
    [Tooltip("Must be moving at least this fast when pressing Crouch to slide (above walk speed = sprint to slide).")]
    [SerializeField] float slideMinSpeed = 7f;
    [Tooltip("Speed added when a slide starts.")]
    [SerializeField] float slideBoost = 3f;
    [Tooltip("The start boost can't push you above this speed (existing speed above it is kept).")]
    [SerializeField] float slideBoostCap = 13f;
    [Tooltip("Absolute top speed while sliding, e.g. down long slopes.")]
    [SerializeField] float slideMaxSpeed = 18f;
    [Tooltip("How fast a slide slows down on flat ground (units/sec²).")]
    [SerializeField] float slideFriction = 6f;
    [Tooltip("The slide ends when speed drops below this.")]
    [SerializeField] float slideEndSpeed = 3.5f;
    [Tooltip("How far you can steer a slide (degrees/sec).")]
    [SerializeField] float slideSteering = 60f;
    [Tooltip("How strongly slopes speed up (downhill) or slow down (uphill) a slide. 0 = slopes don't matter.")]
    [SerializeField] float slideSlopeAcceleration = 1f;
    [Tooltip("Delay after a slide ends before another can start (sec).")]
    [SerializeField] float slideCooldown = 0.4f;

    // ---- Public state (handy for animation, UI, sound) ----
    public bool IsCrouching { get; private set; } // true while crouched or sliding
    public bool IsSliding => isSliding;
    public event Action SlideStarted;
    public event Action SlideEnded;

    PlayerController player;
    CharacterController controller;
    InputAction crouchAction;

    float standingHeight;
    float feetOffset; // CharacterController center.y minus half its height (0 when the pivot is at the feet)
    bool isSliding;
    float slideCooldownTimer;
    bool wasGrounded;
    readonly Collider[] overlapBuffer = new Collider[8];

    void Awake()
    {
        player = GetComponent<PlayerController>();
        controller = GetComponent<CharacterController>();

        InputActionAsset actions = InputSystem.actions;
        crouchAction = actions != null ? actions.FindAction("Player/Crouch", false) : null;
        if (crouchAction == null)
        {
            Debug.LogWarning("CrouchSlide: Player/Crouch action not found, crouch and slide are disabled.", this);
            enabled = false;
            return;
        }

        standingHeight = controller.height;
        feetOffset = controller.center.y - controller.height * 0.5f;
        crouchHeight = Mathf.Clamp(crouchHeight, controller.radius * 2f + 0.01f, standingHeight);
    }

    void OnEnable()
    {
        player.Jumped += OnJumped;
        player.Teleported += OnTeleported;
    }

    void OnDisable()
    {
        player.Jumped -= OnJumped;
        player.Teleported -= OnTeleported;

        // Hand full control back to PlayerController.
        if (isSliding) EndSlide();
        player.SpeedLimitOverride = 0f;
        IsCrouching = false;
        if (controller != null && standingHeight > 0f) SetHeight(standingHeight);
    }

    void OnJumped()
    {
        if (isSliding) EndSlide(); // slide jump: horizontal momentum is kept by PlayerController
    }

    void OnTeleported()
    {
        if (isSliding) EndSlide();
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return; // paused

        bool grounded = controller.isGrounded;
        bool crouchHeld = crouchAction.IsPressed();
        bool crouchPressed = crouchAction.WasPressedThisFrame();
        bool justLanded = grounded && !wasGrounded;
        wasGrounded = grounded;
        slideCooldownTimer -= dt;

        // Slide = press Crouch while moving fast, or land while holding Crouch with enough speed.
        if (!isSliding && grounded && slideCooldownTimer <= 0f && player.HorizontalSpeed >= slideMinSpeed &&
            (crouchPressed || (justLanded && crouchHeld)))
        {
            StartSlide();
        }

        if (isSliding) UpdateSlide(grounded, crouchHeld, dt);

        UpdateCrouchHeight(crouchHeld, dt);

        // Tell PlayerController what to do this frame.
        player.HorizontalControlOverridden = isSliding;
        player.SpeedLimitOverride = IsCrouching && !isSliding ? crouchSpeed : 0f;
    }

    // ---- Slide ----

    void StartSlide()
    {
        isSliding = true;
        Vector3 velocity = player.HorizontalVelocity;
        float speed = velocity.magnitude;
        // Boost up to the cap, but never take away speed you already had (e.g. from a long fall onto a slope).
        float boosted = Mathf.Max(speed, Mathf.Min(speed + slideBoost, slideBoostCap));
        player.HorizontalVelocity = velocity.normalized * boosted;
        player.HorizontalControlOverridden = true;
        SlideStarted?.Invoke();
    }

    void EndSlide()
    {
        isSliding = false;
        slideCooldownTimer = slideCooldown;
        player.HorizontalControlOverridden = false;
        SlideEnded?.Invoke();
    }

    void UpdateSlide(bool grounded, bool crouchHeld, float dt)
    {
        Vector3 velocity = player.HorizontalVelocity;
        float speed = velocity.magnitude;
        Vector3 direction = speed > 0.001f ? velocity / speed : transform.forward;

        // Limited steering, so a slide commits you to a direction.
        Vector3 wishDir = player.GetMoveDirection();
        if (wishDir.sqrMagnitude > 0.0001f)
            direction = Vector3.RotateTowards(direction, wishDir.normalized, slideSteering * Mathf.Deg2Rad * dt, 0f);

        speed = Mathf.MoveTowards(speed, 0f, slideFriction * dt);
        velocity = direction * speed;

        // Slopes: downhill speeds you up, uphill slows you down.
        if (grounded && slideSlopeAcceleration > 0f)
        {
            Vector3 downhill = Vector3.ProjectOnPlane(Vector3.down, player.GroundNormal); // length = sin(slope angle)
            downhill.y = 0f;
            velocity += downhill * (player.Gravity * slideSlopeAcceleration * dt);
        }
        player.HorizontalVelocity = Vector3.ClampMagnitude(velocity, slideMaxSpeed);

        bool tooSlow = player.HorizontalSpeed < slideEndSpeed;
        bool airborne = !player.IsGroundedOrCoyote;
        if (tooSlow || !crouchHeld || airborne) EndSlide();
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

    // Shrink/grow from the top so the feet stay planted.
    void SetHeight(float height)
    {
        controller.height = height;
        Vector3 center = controller.center;
        center.y = feetOffset + height * 0.5f;
        controller.center = center;
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
