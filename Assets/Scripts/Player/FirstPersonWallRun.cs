using System;
using StarterAssets;
using UnityEngine;

/// <summary>
/// Wall running for the Starter Assets FirstPersonController. Put it on PlayerCapsule.
///
/// Start: in the air, moving fast (Min Speed) mostly along a wall on your left or right, holding forward.
/// Run:   you keep your speed along the wall, with a small upward boost and light gravity.
/// End:   after Max Duration, or when you let go of forward, run off the end of the wall, or touch the ground.
/// Jump:  Space during a wall run kicks you up and away from the wall with a small speed boost.
///        Bounce between facing walls to chain runs (each jump adds the boost, up to momentum's max speed).
///
/// Works with FirstPersonMomentum (speed carries out of the run) and FirstPersonMantle.
/// </summary>
[RequireComponent(typeof(FirstPersonController))]
[DefaultExecutionOrder(-7)] // after FirstPersonMantle (-8), before FirstPersonMomentum (-5) and FirstPersonController (0)
public class FirstPersonWallRun : MonoBehaviour
{
    [Header("Starting")]
    [Tooltip("Horizontal speed needed to start a wall run (m/s). Walk is 6, sprint 9.")]
    [SerializeField] float minSpeed = 6f;
    [Tooltip("You must be at least this high above the ground to start (so you don't wall run from a standstill).")]
    [SerializeField] float minHeightAboveGround = 0.8f;
    [Tooltip("How far beyond the capsule a wall can be to grab it.")]
    [SerializeField] float wallCheckDistance = 0.6f;
    [Tooltip("Walls leaning more than this from vertical (degrees) can't be run on.")]
    [SerializeField] float maxWallLean = 15f;
    [Tooltip("Layers that can be wall run on. Defaults to everything.")]
    [SerializeField] LayerMask wallLayers = ~0;

    [Header("Running")]
    [Tooltip("Longest a single wall run lasts (sec).")]
    [SerializeField] float maxDuration = 1.75f;
    [Tooltip("Upward speed given when a wall run starts (m/s), so runs arc up a little before dropping.")]
    [SerializeField] float upwardBoost = 2.5f;
    [Tooltip("Gravity while wall running (m/s²). Normal gravity is 16.5; lower = longer, flatter runs.")]
    [SerializeField] float wallGravity = 4f;
    [Tooltip("Fastest you slide down the wall (m/s).")]
    [SerializeField] float maxSlideDownSpeed = 3f;
    [Tooltip("Small push into the wall so you follow it around gentle curves (m/s).")]
    [SerializeField] float stickForce = 1f;

    [Header("Wall Jump")]
    [Tooltip("Height of a jump off the wall (m).")]
    [SerializeField] float wallJumpHeight = 1.2f;
    [Tooltip("How hard a wall jump pushes you away from the wall (m/s).")]
    [SerializeField] float wallJumpPushAway = 6f;
    [Tooltip("Extra speed a wall jump adds in the direction you were running along the wall (m/s). " +
             "Momentum carries it through the air; bunnyhop on landing to keep it.")]
    [SerializeField] float wallJumpSpeedBoost = 2.5f;
    [Tooltip("After leaving a wall, you can't re-grab that same wall for this long (sec). Other walls are fine.")]
    [SerializeField] float sameWallCooldown = 0.6f;

    public bool IsWallRunning => running;
    /// <summary>Which side the wall is on: +1 right, -1 left, 0 not wall running.</summary>
    public int WallSide => running ? side : 0;
    public event Action WallRunStarted;
    public event Action WallRunEnded;
    public event Action WallJumped;

    FirstPersonController fpc;
    CharacterController controller;
    StarterAssetsInputs input;
    FirstPersonMantle mantle; // optional

    bool running;
    int side;
    Vector3 wallNormal;
    Vector3 wallForward;   // along the wall, in the direction we're running
    float speed;           // horizontal speed along the wall
    float verticalSpeed;
    float runTime;

    bool resumeNextFrame;  // after a wall jump, hand control back one frame later (see WallJump)
    float resumeVerticalSpeed;

    Vector3 lastWallNormal;
    float lastWallTime = -999f;

    void Awake()
    {
        fpc = GetComponent<FirstPersonController>();
        controller = GetComponent<CharacterController>();
        input = GetComponent<StarterAssetsInputs>();
        mantle = GetComponent<FirstPersonMantle>();
    }

    void OnDisable()
    {
        if (running || resumeNextFrame) Release(Mathf.Min(verticalSpeed, 0f));
        running = false;
        resumeNextFrame = false;
    }

    Vector3 Chest => transform.position +
                     Vector3.up * (controller.center.y - controller.height * 0.5f + controller.height * 0.6f);

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return; // paused

        if (resumeNextFrame)
        {
            resumeNextFrame = false;
            Release(resumeVerticalSpeed);
            return;
        }

        // A mantle took over (it suspends movement itself): just let go of the wall.
        if (mantle != null && mantle.IsMantling)
        {
            if (running) { running = false; RememberWall(); WallRunEnded?.Invoke(); }
            return;
        }

        if (running) UpdateRun(dt);
        else TryStart();
    }

    // ---- Starting ----

    void TryStart()
    {
        if (fpc.Grounded || fpc.MovementSuspended || input == null) return;
        if (input.move.y < 0.5f) return; // must be holding forward

        Vector3 horizontal = fpc.LastMoveVelocity;
        horizontal.y = 0f;
        if (horizontal.magnitude < minSpeed) return;
        if (!HighEnoughAboveGround()) return;

        for (int s = 1; s >= -1; s -= 2) // check right, then left
        {
            Vector3 direction = transform.right * s;
            if (!FindWall(direction, out RaycastHit hit)) continue;

            Vector3 along = Vector3.Cross(hit.normal, Vector3.up).normalized;
            if (Vector3.Dot(along, horizontal) < 0f) along = -along;
            if (Vector3.Dot(along, horizontal.normalized) < 0.5f) continue; // heading into or away from it

            // Don't immediately re-grab the wall we just left.
            bool sameWall = Vector3.Angle(hit.normal, lastWallNormal) < 20f;
            if (sameWall && Time.time - lastWallTime < sameWallCooldown) continue;

            Begin(hit.normal, along, s, horizontal.magnitude, fpc.LastMoveVelocity.y);
            return;
        }
    }

    bool FindWall(Vector3 direction, out RaycastHit hit)
    {
        float distance = controller.radius + wallCheckDistance;
        if (!Physics.Raycast(Chest, direction, out hit, distance, wallLayers, QueryTriggerInteraction.Ignore))
            return false;
        if (hit.collider == controller || hit.collider.transform.IsChildOf(transform)) return false;
        return Mathf.Abs(hit.normal.y) <= Mathf.Sin(maxWallLean * Mathf.Deg2Rad); // close to vertical
    }

    bool HighEnoughAboveGround()
    {
        Vector3 feet = transform.position + Vector3.up * (controller.center.y - controller.height * 0.5f + 0.05f);
        return !Physics.Raycast(feet, Vector3.down, minHeightAboveGround, fpc.GroundLayers,
                                QueryTriggerInteraction.Ignore);
    }

    void Begin(Vector3 normal, Vector3 along, int wallSide, float entrySpeed, float entryVertical)
    {
        running = true;
        runTime = 0f;
        side = wallSide;
        wallNormal = normal;
        wallForward = along;
        speed = entrySpeed;
        verticalSpeed = Mathf.Max(entryVertical, upwardBoost);
        fpc.MovementSuspended = true; // we move the player ourselves while on the wall
        WallRunStarted?.Invoke();
    }

    // ---- Running ----

    void UpdateRun(float dt)
    {
        runTime += dt;

        if (input != null && input.jump)
        {
            WallJump(dt);
            return;
        }

        // Still a wall beside us?
        bool holdingForward = input != null && input.move.y > 0.3f;
        if (!holdingForward || runTime > maxDuration || !FindWall(-wallNormal, out RaycastHit hit))
        {
            End();
            return;
        }

        // Follow the wall's surface (gentle curves), keeping our running direction.
        wallNormal = hit.normal;
        Vector3 along = Vector3.Cross(wallNormal, Vector3.up).normalized;
        wallForward = Vector3.Dot(along, wallForward) >= 0f ? along : -along;

        verticalSpeed = Mathf.Max(verticalSpeed - wallGravity * dt, -maxSlideDownSpeed);

        Vector3 motion = wallForward * speed + Vector3.up * verticalSpeed - wallNormal * stickForce;
        CollisionFlags flags = controller.Move(motion * dt);

        if ((flags & CollisionFlags.Above) != 0 && verticalSpeed > 0f) verticalSpeed = 0f;
        if ((flags & CollisionFlags.Below) != 0) End(); // touched the ground
    }

    void WallJump(float dt)
    {
        input.jump = false;
        float upSpeed = Mathf.Sqrt(2f * Mathf.Abs(fpc.Gravity) * wallJumpHeight);
        Vector3 launch = wallForward * (speed + wallJumpSpeedBoost) + wallNormal * wallJumpPushAway;

        // Make the first bit of the jump ourselves while movement is still suspended, so the controller records
        // the launch (including the push away from the wall) as our velocity. Momentum then carries it on from
        // next frame, when we hand control back.
        controller.Move((launch + Vector3.up * upSpeed) * dt);

        running = false;
        RememberWall();
        resumeNextFrame = true;
        resumeVerticalSpeed = upSpeed;
        WallJumped?.Invoke();
        WallRunEnded?.Invoke();
    }

    void End()
    {
        running = false;
        RememberWall();
        Release(verticalSpeed);
        WallRunEnded?.Invoke();
    }

    void RememberWall()
    {
        lastWallNormal = wallNormal;
        lastWallTime = Time.time;
    }

    // Hand movement back to FirstPersonController with the given vertical speed.
    void Release(float vertical)
    {
        fpc.SetVerticalVelocity(vertical);
        fpc.MovementSuspended = false;
    }
}
