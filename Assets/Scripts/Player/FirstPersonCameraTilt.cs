using StarterAssets;
using UnityEngine;

/// <summary>
/// Camera roll for the Starter Assets FirstPersonController. Put it on PlayerCapsule.
///
/// Sprinting: the camera leans into strafes (A/D) and into turns.
/// Sliding:   the camera rolls to the right (configurable), and leans a bit more when you steer the slide.
///
/// Rolls PlayerCameraRoot (the Cinemachine follow target) on top of the pitch FirstPersonController sets,
/// so looking around keeps working normally.
/// </summary>
[RequireComponent(typeof(FirstPersonController))]
[DefaultExecutionOrder(50)] // after FirstPersonController's LateUpdate has set the camera pitch
public class FirstPersonCameraTilt : MonoBehaviour
{
    [Header("Strafe Lean")]
    [Tooltip("Lean when strafing while sprinting (degrees).")]
    [SerializeField] float sprintStrafeTilt = 3f;
    [Tooltip("Lean when strafing while walking (degrees). 0 = only lean while sprinting.")]
    [SerializeField] float walkStrafeTilt = 0.75f;

    [Header("Turn Lean (sprint only)")]
    [Tooltip("Degrees of lean per 100 degrees/sec of turning while sprinting.")]
    [SerializeField] float sprintTurnTilt = 1f;
    [Tooltip("Most the turn lean can add (degrees).")]
    [SerializeField] float maxTurnTilt = 3f;

    [Header("Slide")]
    [Tooltip("Roll while sliding (degrees).")]
    [SerializeField] float slideTilt = 6f;
    [Tooltip("Which way the slide rolls the camera. On = right, off = left.")]
    [SerializeField] bool slideTiltsRight = true;
    [Tooltip("Extra lean when steering a slide with A/D (degrees).")]
    [SerializeField] float slideSteerTilt = 3f;
    [Tooltip("Slide speed (m/s) at which the full slide roll applies. Below it the camera levels out " +
             "as the slide slows. 0 = always full roll.")]
    [SerializeField] float slideTiltFullSpeed = 8f;

    [Header("Wall Run")]
    [Tooltip("Roll away from the wall while wall running (degrees).")]
    [SerializeField] float wallRunTilt = 12f;

    [Header("Smoothing")]
    [Tooltip("How quickly the camera eases into and out of a tilt (sec). Lower = snappier.")]
    [SerializeField] float smoothTime = 0.12f;

    public float CurrentRoll => roll;

    FirstPersonController fpc;
    StarterAssetsInputs input;
    FirstPersonCrouchSlide crouchSlide; // optional
    FirstPersonWallRun wallRun;         // optional
    Transform cameraRoot;

    float roll, rollVelocity;
    float smoothedTurnRate;
    float lastYaw;

    // What we applied last frame, so we can tell whether FirstPersonController has overwritten it since.
    Quaternion appliedRotation;
    float appliedRoll;
    bool hasApplied;

    void Awake()
    {
        fpc = GetComponent<FirstPersonController>();
        input = GetComponent<StarterAssetsInputs>();
        crouchSlide = GetComponent<FirstPersonCrouchSlide>();
        wallRun = GetComponent<FirstPersonWallRun>();
        if (fpc.CinemachineCameraTarget != null) cameraRoot = fpc.CinemachineCameraTarget.transform;
        lastYaw = transform.eulerAngles.y;
    }

    void OnDisable()
    {
        // Level the camera again.
        if (cameraRoot != null && hasApplied) cameraRoot.localRotation = BaseRotation();
        roll = rollVelocity = 0f;
        hasApplied = false;
    }

    void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (cameraRoot == null || dt <= 0f) return;

        // How fast the body is turning (degrees/sec), smoothed so mouse jitter doesn't shake the camera.
        float yaw = transform.eulerAngles.y;
        float turnRate = Mathf.DeltaAngle(lastYaw, yaw) / dt;
        lastYaw = yaw;
        smoothedTurnRate = Mathf.Lerp(smoothedTurnRate, turnRate, 1f - Mathf.Exp(-10f * dt));

        roll = Mathf.SmoothDamp(roll, TargetRoll(), ref rollVelocity, smoothTime);

        appliedRotation = BaseRotation() * Quaternion.Euler(0f, 0f, roll);
        cameraRoot.localRotation = appliedRotation;
        appliedRoll = roll;
        hasApplied = true;
    }

    float TargetRoll()
    {
        Vector2 move = input != null ? input.move : Vector2.zero;
        float strafe = Mathf.Clamp(move.x, -1f, 1f);

        // Positive roll leans left, so strafing right (positive x) uses negative roll.

        // Wall running: lean away from the wall (wall on the right = lean left = positive roll).
        if (wallRun != null && wallRun.isActiveAndEnabled && wallRun.IsWallRunning)
            return wallRun.WallSide * wallRunTilt;

        if (crouchSlide != null && crouchSlide.isActiveAndEnabled && crouchSlide.IsSliding)
        {
            // Full roll at speed, levelling out as the slide slows down.
            float speedFactor = slideTiltFullSpeed > 0f ? Mathf.Clamp01(crouchSlide.SlideSpeed / slideTiltFullSpeed) : 1f;
            // Positive roll leans left, negative leans right.
            return ((slideTiltsRight ? -slideTilt : slideTilt) - strafe * slideSteerTilt) * speedFactor;
        }

        bool moving = move.sqrMagnitude > 0.01f;
        bool sprinting = moving && input != null && input.sprint;
        if (!sprinting) return -strafe * walkStrafeTilt;

        float turnLean = Mathf.Clamp(smoothedTurnRate * sprintTurnTilt / 100f, -maxTurnTilt, maxTurnTilt);
        return -strafe * sprintStrafeTilt - turnLean;
    }

    // The camera rotation without our roll: either FirstPersonController's fresh pitch this frame,
    // or last frame's rotation with our old roll taken back out.
    Quaternion BaseRotation()
    {
        Quaternion current = cameraRoot.localRotation;
        bool untouchedSinceLastFrame = hasApplied && current == appliedRotation;
        return untouchedSinceLastFrame ? current * Quaternion.Inverse(Quaternion.Euler(0f, 0f, appliedRoll)) : current;
    }
}
