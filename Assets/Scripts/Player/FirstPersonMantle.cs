using System;
using StarterAssets;
using UnityEngine;

/// <summary>
/// Mantle add-on for the Starter Assets FirstPersonController. Put it on PlayerCapsule.
///
/// While in the air and holding forward into a ledge you can't jump onto (up to Max Ledge Height above your feet),
/// the player pulls up over it, as long as there's room to stand on top.
/// Looking around still works during the mantle.
/// </summary>
[RequireComponent(typeof(FirstPersonController))]
[DefaultExecutionOrder(-8)] // before FirstPersonMomentum and FirstPersonController
public class FirstPersonMantle : MonoBehaviour
{
    [Header("Reach")]
    [Tooltip("Lowest ledge (above your feet) worth mantling. Lower ones you just step or jump onto.")]
    [SerializeField] float minLedgeHeight = 0.5f;
    [Tooltip("Highest ledge (above your feet) you can grab. Standing height is 2.")]
    [SerializeField] float maxLedgeHeight = 2.3f;
    [Tooltip("How far in front of the capsule a wall can be to grab it.")]
    [SerializeField] float reachDistance = 0.5f;
    [Tooltip("Steepest ledge top you can stand on (degrees).")]
    [SerializeField] float maxLedgeSlope = 35f;
    [Tooltip("Layers that can be mantled. Defaults to everything.")]
    [SerializeField] LayerMask mantleLayers = ~0;

    [Header("Motion")]
    [Tooltip("How long you hang still after grabbing the ledge, before pulling up (sec). 0 = no pause.")]
    [SerializeField] float grabPause = 0.15f;
    [Tooltip("Speed of the upward pull (m/s).")]
    [SerializeField] float climbSpeed = 6f;
    [Tooltip("Speed of the move forward onto the ledge (m/s).")]
    [SerializeField] float forwardSpeed = 5f;
    [Tooltip("Give up if the mantle hasn't finished in this long (sec), e.g. if something blocks it.")]
    [SerializeField] float maxDuration = 1f;
    [Tooltip("Delay after a mantle before another can start (sec).")]
    [SerializeField] float cooldown = 0.3f;

    public bool IsMantling => phase != Phase.None;
    /// <summary>True during the grab pause, before the pull-up starts.</summary>
    public bool IsGrabbing => phase == Phase.Grab;
    public event Action MantleStarted;
    public event Action MantleEnded;

    enum Phase { None, Grab, Up, Forward }

    FirstPersonController fpc;
    CharacterController controller;
    StarterAssetsInputs input;

    Phase phase;
    Vector3 targetFeet;        // where the feet end up on top of the ledge
    float elapsed;
    float cooldownTimer;
    float feetOffset;
    readonly Collider[] overlapBuffer = new Collider[8];

    void Awake()
    {
        fpc = GetComponent<FirstPersonController>();
        controller = GetComponent<CharacterController>();
        input = GetComponent<StarterAssetsInputs>();
    }

    void OnDisable()
    {
        if (IsMantling) EndMantle();
    }

    Vector3 Feet => transform.position + Vector3.up * (controller.center.y - controller.height * 0.5f);

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return; // paused

        feetOffset = controller.center.y - controller.height * 0.5f;

        if (IsMantling)
        {
            UpdateMantle(dt);
            return;
        }

        cooldownTimer -= dt;
        bool holdingForward = input != null && input.move.y > 0.5f;
        if (cooldownTimer <= 0f && !fpc.Grounded && holdingForward && TryFindLedge(out Vector3 feetOnLedge))
            StartMantle(feetOnLedge);
    }

    // ---- Detection ----

    bool TryFindLedge(out Vector3 feetOnLedge)
    {
        feetOnLedge = default;
        float radius = controller.radius;
        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        Vector3 feet = Feet;

        // 1. A wall right in front of the chest.
        Vector3 chest = feet + Vector3.up * Mathf.Min(1.0f, controller.height * 0.5f);
        if (!Physics.SphereCast(chest, radius * 0.5f, forward, out RaycastHit wallHit, radius * 0.5f + reachDistance,
                                mantleLayers, QueryTriggerInteraction.Ignore))
            return false;
        if (IsSelf(wallHit.collider)) return false;
        if (Mathf.Abs(wallHit.normal.y) > 0.3f) return false;               // not a wall
        if (Vector3.Dot(-wallHit.normal, forward) < 0.5f) return false;     // not facing it

        // 2. The top of that wall, within reach.
        Vector3 overLedge = wallHit.point - wallHit.normal * (radius + 0.1f);
        Vector3 downFrom = new Vector3(overLedge.x, feet.y + maxLedgeHeight + 0.1f, overLedge.z);
        float downDistance = maxLedgeHeight - minLedgeHeight + 0.1f;
        if (!Physics.Raycast(downFrom, Vector3.down, out RaycastHit topHit, downDistance,
                             mantleLayers, QueryTriggerInteraction.Ignore))
            return false;
        if (IsSelf(topHit.collider)) return false;
        if (topHit.normal.y < Mathf.Cos(maxLedgeSlope * Mathf.Deg2Rad)) return false;

        float ledgeHeight = topHit.point.y - feet.y;
        if (ledgeHeight < minLedgeHeight || ledgeHeight > maxLedgeHeight) return false;

        // 3. Room to stand on top, and room above us to climb straight up.
        Vector3 standFeet = new Vector3(topHit.point.x, topHit.point.y + 0.05f, topHit.point.z);
        if (!CapsuleIsClear(standFeet)) return false;
        Vector3 raisedFeet = new Vector3(feet.x, standFeet.y, feet.z);
        if (!CapsuleIsClear(raisedFeet)) return false;

        feetOnLedge = standFeet;
        return true;
    }

    bool CapsuleIsClear(Vector3 feet)
    {
        float radius = controller.radius;
        Vector3 bottom = feet + Vector3.up * (radius + 0.05f);
        Vector3 top = feet + Vector3.up * (controller.height - radius);
        int count = Physics.OverlapCapsuleNonAlloc(bottom, top, radius * 0.95f, overlapBuffer,
                                                   mantleLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
            if (!IsSelf(overlapBuffer[i])) return false;
        return true;
    }

    bool IsSelf(Collider other) => other == controller || other.transform.IsChildOf(transform);

    // ---- Mantle ----

    void StartMantle(Vector3 feetOnLedge)
    {
        targetFeet = feetOnLedge;
        phase = grabPause > 0f ? Phase.Grab : Phase.Up;
        elapsed = 0f;
        fpc.MovementSuspended = true;
        MantleStarted?.Invoke();
    }

    void UpdateMantle(float dt)
    {
        elapsed += dt;
        Vector3 feet = Feet;

        // Hang on the ledge for a moment before pulling up.
        if (phase == Phase.Grab)
        {
            if (elapsed >= grabPause) phase = Phase.Up;
            return;
        }

        if (phase == Phase.Up)
        {
            float remaining = targetFeet.y - feet.y;
            if (remaining <= 0.01f) phase = Phase.Forward;
            else controller.Move(Vector3.up * Mathf.Min(climbSpeed * dt, remaining));
        }

        if (phase == Phase.Forward)
        {
            Vector3 toTarget = targetFeet - Feet;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;
            if (distance <= 0.02f)
            {
                EndMantle();
                return;
            }
            // Small downward push keeps the capsule settled on the ledge as it moves on.
            Vector3 step = toTarget / distance * Mathf.Min(forwardSpeed * dt, distance) + Vector3.down * 0.01f;
            controller.Move(step);
        }

        if (elapsed > maxDuration + grabPause) EndMantle();
    }

    void EndMantle()
    {
        phase = Phase.None;
        cooldownTimer = cooldown;
        fpc.SetVerticalVelocity(-2f); // like standing on ground, no sudden drop
        fpc.MovementSuspended = false;
        MantleEnded?.Invoke();
    }
}
