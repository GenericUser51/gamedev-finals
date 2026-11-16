using StarterAssets;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Sprint zoom for the Starter Assets FirstPersonController: the view eases from Normal Fov to Sprint Fov
/// while sprinting, and a bit further the faster you go above sprint speed (slides, slide-jumps, downhill runs).
/// Put it on PlayerCapsule.
///
/// The view is controlled by the Cinemachine camera (not the Camera component), so this changes that
/// camera's lens. Works with both CinemachineCamera and the older CinemachineVirtualCamera.
/// </summary>
[RequireComponent(typeof(FirstPersonController))]
[DefaultExecutionOrder(60)]
public class FirstPersonSprintFov : MonoBehaviour
{
    [Tooltip("Cinemachine camera to change. Leave empty to use the one the Main Camera is showing.")]
    [SerializeField] CinemachineVirtualCameraBase followCamera;

    [Header("Field of View")]
    [Tooltip("Normal view when not sprinting (degrees). 0 = keep the Cinemachine camera's own setting.")]
    [SerializeField] float normalFov = 54f;
    [Tooltip("View while sprinting (degrees). Lower than Normal Fov = zoom in, higher = zoom out.")]
    [SerializeField] float sprintFov = 40f;

    [Header("Extra Speed")]
    [Tooltip("Extra zoom per m/s above sprint speed (degrees), e.g. during slides and slide-jumps. " +
             "Goes the same way as the sprint zoom.")]
    [SerializeField] float fovPerExtraSpeed = 0.6f;
    [Tooltip("Most the extra-speed zoom can add (degrees).")]
    [SerializeField] float maxExtraSpeedFov = 8f;

    [Header("Limits")]
    [Tooltip("The view never zooms narrower than this (degrees).")]
    [SerializeField] float minFov = 20f;
    [Tooltip("The view never zooms wider than this (degrees).")]
    [SerializeField] float maxFov = 75f;

    [Header("Smoothing")]
    [Tooltip("How gradually the view eases into the zoom when you start sprinting (sec). Higher = smoother.")]
    [SerializeField] float zoomSmoothTime = 0.35f;
    [Tooltip("How quickly the view returns to normal when you stop sprinting (sec). 0 = snap back instantly.")]
    [SerializeField] float returnSmoothTime = 0.08f;
    [Tooltip("How steadily the speed reading follows your real speed. Lower = steadier (ignores bumps and " +
             "small speed wobbles), higher = more responsive.")]
    [SerializeField] float speedSmoothing = 6f;

    public float CurrentFovOffset => offset;

    FirstPersonController fpc;
    StarterAssetsInputs input;
    FirstPersonCrouchSlide crouchSlide; // optional
    PlayerInput playerInput;
    InputAction sprintAction;

    float baseFov;
    bool hasBaseFov;
    float offset, offsetVelocity;
    float smoothedSpeed;
    bool wasSprintHeld;

    void Awake()
    {
        fpc = GetComponent<FirstPersonController>();
        input = GetComponent<StarterAssetsInputs>();
        crouchSlide = GetComponent<FirstPersonCrouchSlide>();
        playerInput = GetComponent<PlayerInput>();
    }

    void OnDisable()
    {
        // Put the view back to normal.
        if (hasBaseFov && followCamera != null) SetFov(baseFov);
        offset = offsetVelocity = smoothedSpeed = 0f;
    }

    void LateUpdate()
    {
        if (!EnsureCamera()) return;

        float dt = Time.deltaTime;
        if (dt <= 0f) return; // paused

        // Steady speed reading, so bumps and tiny speed wobbles don't make the zoom flicker.
        Vector3 velocity = fpc.LastMoveVelocity;
        velocity.y = 0f;
        smoothedSpeed = Mathf.Lerp(smoothedSpeed, velocity.magnitude, 1f - Mathf.Exp(-speedSmoothing * dt));

        bool moving = input != null && input.move.sqrMagnitude > 0.01f;
        bool sprintHeld = moving && SprintHeld();
        bool sliding = crouchSlide != null && crouchSlide.isActiveAndEnabled && crouchSlide.IsSliding;

        // Sprint zoom follows the Sprint key itself: it starts easing in the moment you press it (while moving)
        // and starts easing back the moment you let go.
        // Only while sprinting or sliding: plain walking never zooms, even when momentum makes it fast.
        float sprintChange = sprintFov - baseFov;
        float direction = sprintChange >= 0f ? 1f : -1f;
        float target = sprintHeld ? sprintChange : 0f;
        if (sprintHeld || sliding)
        {
            float extraSpeed = smoothedSpeed - fpc.SprintSpeed;
            if (extraSpeed > 0f) target += direction * Mathf.Min(extraSpeed * fovPerExtraSpeed, maxExtraSpeedFov);
        }

        // The moment you stop sprinting, drop any leftover zoom-in motion so the return starts immediately.
        if (wasSprintHeld && !sprintHeld) offsetVelocity = 0f;
        wasSprintHeld = sprintHeld;

        // Heading back toward the normal view = returning: use the quick return time.
        bool returning = Mathf.Abs(target) < Mathf.Abs(offset);
        float smoothTime = returning ? returnSmoothTime : zoomSmoothTime;
        if (smoothTime <= 0f)
        {
            offset = target;
            offsetVelocity = 0f;
        }
        else
        {
            offset = Mathf.SmoothDamp(offset, target, ref offsetVelocity, smoothTime);
        }
        SetFov(Mathf.Clamp(baseFov + offset, minFov, maxFov));
    }

    // Whether the Sprint key/button is physically held right now. Reads the action directly rather than
    // StarterAssetsInputs.sprint, which only updates on press/release and can start out stale.
    bool SprintHeld()
    {
        if (sprintAction == null && playerInput != null && playerInput.actions != null)
            sprintAction = playerInput.actions.FindAction("Sprint", false);
        if (sprintAction != null) return sprintAction.IsPressed();
        return input != null && input.sprint;
    }

    bool EnsureCamera()
    {
        if (followCamera == null)
        {
            Camera main = Camera.main;
            CinemachineBrain brain = main != null ? main.GetComponent<CinemachineBrain>() : null;
            if (brain != null) followCamera = brain.ActiveVirtualCamera as CinemachineVirtualCameraBase;
            if (followCamera == null) followCamera = FindAnyObjectByType<CinemachineVirtualCameraBase>();
            if (followCamera == null) return false; // try again next frame
            hasBaseFov = false;
        }

        if (!hasBaseFov)
        {
            baseFov = normalFov > 0f ? normalFov : GetFov();
            hasBaseFov = true;
        }
        return true;
    }

#pragma warning disable 0618 // CinemachineVirtualCamera is deprecated, but the Starter Assets prefab still uses it
    float GetFov()
    {
        if (followCamera is CinemachineCamera cam) return cam.Lens.FieldOfView;
        if (followCamera is CinemachineVirtualCamera legacy) return legacy.m_Lens.FieldOfView;
        return 60f;
    }

    void SetFov(float fov)
    {
        fov = Mathf.Clamp(fov, 1f, 179f);
        if (followCamera is CinemachineCamera cam) cam.Lens.FieldOfView = fov;
        else if (followCamera is CinemachineVirtualCamera legacy) legacy.m_Lens.FieldOfView = fov;
    }
#pragma warning restore 0618
}
