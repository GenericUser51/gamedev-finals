using StarterAssets;
using UnityEngine;

/// <summary>
/// Camera bob for the Starter Assets FirstPersonController. Put it on PlayerCapsule.
///
/// Moving:   a dip on each footstep plus a gentle side-to-side sway, quicker and stronger the faster you go.
///           Fades out smoothly when you stop, jump, slide, wall run or mantle.
/// Mantling: the camera dips as you catch the ledge, lifts with the pull-up, then settles.
///
/// Offsets PlayerCameraRoot (the Cinemachine follow target) on top of where other scripts put it
/// (e.g. FirstPersonCrouchSlide lowering it while crouched), so they don't fight.
/// </summary>
[RequireComponent(typeof(FirstPersonController))]
[DefaultExecutionOrder(55)] // after the movement scripts have updated this frame
public class FirstPersonCameraBob : MonoBehaviour
{
    [Header("Footsteps")]
    [Tooltip("How far the camera dips on each step at sprint speed (m).")]
    [SerializeField] float stepDip = 0.07f;
    [Tooltip("How far the camera sways side to side at sprint speed (m).")]
    [SerializeField] float sideSway = 0.035f;
    [Tooltip("Steps per second when walking slowly.")]
    [SerializeField] float slowStepRate = 1.8f;
    [Tooltip("Steps per second at sprint speed.")]
    [SerializeField] float sprintStepRate = 2.8f;
    [Tooltip("Below this speed (m/s) there is no bob.")]
    [SerializeField] float minSpeed = 0.5f;
    [Tooltip("How quickly the bob fades in and out when you start/stop moving (sec).")]
    [SerializeField] float fadeTime = 0.15f;

    [Header("Mantle")]
    [Tooltip("How far the camera dips when you catch a ledge (m).")]
    [SerializeField] float mantleGrabDip = 0.15f;
    [Tooltip("How far the camera lifts during the pull-up (m).")]
    [SerializeField] float mantlePullLift = 0.06f;
    [Tooltip("How quickly the camera follows the mantle dip and lift (sec). Lower = snappier.")]
    [SerializeField] float mantleSmoothTime = 0.07f;

    FirstPersonController fpc;
    FirstPersonCrouchSlide crouchSlide; // optional
    FirstPersonWallRun wallRun;         // optional
    FirstPersonMantle mantle;           // optional
    Transform cameraRoot;

    float stepPhase;
    float bobWeight, bobWeightVelocity;
    float mantleOffset, mantleVelocity;

    // What we applied last frame, so we can tell whether another script has moved the root since.
    Vector3 appliedOffset;
    Vector3 appliedPosition;
    bool hasApplied;

    void Awake()
    {
        fpc = GetComponent<FirstPersonController>();
        crouchSlide = GetComponent<FirstPersonCrouchSlide>();
        wallRun = GetComponent<FirstPersonWallRun>();
        mantle = GetComponent<FirstPersonMantle>();
        if (fpc.CinemachineCameraTarget != null) cameraRoot = fpc.CinemachineCameraTarget.transform;
    }

    void OnDisable()
    {
        if (cameraRoot != null && hasApplied) cameraRoot.localPosition = BasePosition();
        hasApplied = false;
        bobWeight = bobWeightVelocity = mantleOffset = mantleVelocity = 0f;
    }

    void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (cameraRoot == null || dt <= 0f) return;

        bool mantling = mantle != null && mantle.isActiveAndEnabled && mantle.IsMantling;
        bool sliding = crouchSlide != null && crouchSlide.isActiveAndEnabled && crouchSlide.IsSliding;
        bool wallRunning = wallRun != null && wallRun.isActiveAndEnabled && wallRun.IsWallRunning;

        // ---- Footstep bob ----
        Vector3 velocity = fpc.LastMoveVelocity;
        velocity.y = 0f;
        float speed = velocity.magnitude;
        bool walking = fpc.Grounded && !mantling && !sliding && !wallRunning && speed > minSpeed;

        float speedFactor = fpc.SprintSpeed > 0f ? Mathf.Clamp(speed / fpc.SprintSpeed, 0f, 1.2f) : 0f;
        bobWeight = Mathf.SmoothDamp(bobWeight, walking ? speedFactor : 0f, ref bobWeightVelocity, fadeTime);

        if (walking)
        {
            float stepsPerSecond = Mathf.Lerp(slowStepRate, sprintStepRate, Mathf.Clamp01(speedFactor));
            stepPhase += Mathf.PI * stepsPerSecond * dt; // half a sine wave per step
            if (stepPhase > Mathf.PI * 2f) stepPhase -= Mathf.PI * 2f;
        }

        // A dip on each footfall (|sin| bottoms out once per step) and a sway that alternates sides each step.
        float dip = -Mathf.Abs(Mathf.Sin(stepPhase)) * stepDip * bobWeight;
        float sway = Mathf.Sin(stepPhase) * sideSway * bobWeight;

        // ---- Mantle dip and lift ----
        float mantleTarget = 0f;
        if (mantling) mantleTarget = mantle.IsGrabbing ? -mantleGrabDip : mantlePullLift;
        mantleOffset = Mathf.SmoothDamp(mantleOffset, mantleTarget, ref mantleVelocity, mantleSmoothTime);

        Vector3 offset = new Vector3(sway, dip + mantleOffset, 0f);
        appliedPosition = BasePosition() + offset;
        cameraRoot.localPosition = appliedPosition;
        appliedOffset = offset;
        hasApplied = true;
    }

    // The camera root's position without our bob. Checked per axis: another script may rewrite just one
    // (crouching only changes the height), so any axis it set this frame is taken as-is, and any axis still
    // at what we applied last frame has our old offset taken back out.
    Vector3 BasePosition()
    {
        Vector3 current = cameraRoot.localPosition;
        if (!hasApplied) return current;
        return new Vector3(
            Untouched(current.x, appliedPosition.x) ? current.x - appliedOffset.x : current.x,
            Untouched(current.y, appliedPosition.y) ? current.y - appliedOffset.y : current.y,
            Untouched(current.z, appliedPosition.z) ? current.z - appliedOffset.z : current.z);
    }

    static bool Untouched(float current, float applied) => Mathf.Abs(current - applied) < 1e-5f;
}
