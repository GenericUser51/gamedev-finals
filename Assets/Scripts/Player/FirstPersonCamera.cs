using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// First-person mouse/stick look. Put this on the Main Camera (NOT as a child of the player).
/// The camera sits at the player's eye height and owns its own yaw/pitch, so it is unaffected
/// by PlayerController turning the body toward its movement direction.
///
/// Input comes from the project-wide Input System action Player/Look.
/// </summary>
[DefaultExecutionOrder(-10)] // rotate before PlayerController reads the camera direction
public class FirstPersonCamera : MonoBehaviour
{
    [Tooltip("The player to follow. Leave empty to find the PlayerController in the scene.")]
    [SerializeField] Transform target;
    [Tooltip("Camera height above the player's feet (units).")]
    [SerializeField] float eyeHeight = 1.7f;
    [Tooltip("Degrees turned per pixel of mouse movement.")]
    [SerializeField] float mouseSensitivity = 0.12f;
    [Tooltip("Degrees per second at full gamepad stick tilt.")]
    [SerializeField] float stickSensitivity = 180f;
    [Tooltip("Gamepad stick input weaker than this is ignored, so worn sticks don't turn the view on their own.")]
    [SerializeField, Range(0f, 0.5f)] float stickDeadzone = 0.15f;
    [Tooltip("Debug: log which device is turning the camera to the Console. Turn off when done.")]
    [SerializeField] bool logLookInput = false;
    [Tooltip("How far you can look up and down (degrees).")]
    [SerializeField, Range(10f, 89f)] float maxPitch = 85f;
    [Tooltip("Horizontal field of view in degrees, like a game's FOV slider (90 is typical). " +
             "Overrides the Camera's own Field of View, which Unity measures vertically. " +
             "Set to 0 to use the Camera's setting instead.")]
    [SerializeField, Range(0f, 120f)] float horizontalFov = 90f;

    Camera cam;
    InputAction lookAction;
    PlayerController player;
    CharacterController targetCapsule; // when it shrinks (crouch/slide), the eye lowers with it
    float standingHeight;
    float yaw, pitch;

    void Awake()
    {
        cam = GetComponent<Camera>();

        if (target == null)
        {
            player = FindAnyObjectByType<PlayerController>();
            if (player != null) target = player.transform;
        }
        else
        {
            player = target.GetComponent<PlayerController>();
        }

        if (target == null)
        {
            Debug.LogError("FirstPersonCamera: no target set and no PlayerController found in the scene.", this);
            enabled = false;
            return;
        }

        targetCapsule = target.GetComponent<CharacterController>();
        if (targetCapsule != null) standingHeight = targetCapsule.height;

        InputActionAsset actions = InputSystem.actions;
        if (actions != null) lookAction = actions.FindAction("Player/Look");
        if (lookAction == null)
            Debug.LogWarning("FirstPersonCamera: Player/Look action not found, looking is disabled.", this);

        FaceTargetForward();
    }

    void OnEnable()
    {
        if (player != null)
        {
            player.Respawned += FaceTargetForward;
            player.PlatformRotated += OnPlatformRotated;
        }
    }

    void OnDisable()
    {
        if (player != null)
        {
            player.Respawned -= FaceTargetForward;
            player.PlatformRotated -= OnPlatformRotated;
        }
    }

    // Turn the view with spinning platforms so the player doesn't slide out from under the camera.
    void OnPlatformRotated(float yawDelta) => yaw += yawDelta;

    void Update()
    {
        // Only look while the cursor is captured, so Esc frees the mouse without spinning the view.
        if (lookAction != null && Cursor.lockState == CursorLockMode.Locked)
        {
            Vector2 look = lookAction.ReadValue<Vector2>();
            InputControl source = lookAction.activeControl;
            bool isPointer = source != null && source.device is Pointer;

            // Ignore tiny stick drift so the view doesn't creep on its own.
            if (!isPointer && look.magnitude < stickDeadzone) look = Vector2.zero;

            if (logLookInput && look != Vector2.zero && source != null)
                Debug.Log($"FirstPersonCamera: look {look} from {source.path} ({source.device.displayName})", this);

            // Mouse delta is already "per frame"; a stick is a rate and needs deltaTime.
            float scale = isPointer ? mouseSensitivity : stickSensitivity * Time.deltaTime;
            yaw += look.x * scale;
            pitch = Mathf.Clamp(pitch - look.y * scale, -maxPitch, maxPitch);
        }

        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        ApplyFov();
    }

    // Convert horizontal FOV to Unity's vertical FOV for the current aspect ratio,
    // so the view stays the same width when the window is resized.
    void ApplyFov()
    {
        if (cam == null || horizontalFov <= 0f || cam.aspect <= 0f) return;
        float halfHorizontal = horizontalFov * 0.5f * Mathf.Deg2Rad;
        cam.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(halfHorizontal) / cam.aspect) * Mathf.Rad2Deg;
    }

    void LateUpdate()
    {
        // Follow after the player has moved this frame.
        if (target == null) return; // player destroyed or never found
        float crouchOffset = targetCapsule != null ? standingHeight - targetCapsule.height : 0f; // lowers smoothly
        transform.position = target.position + Vector3.up * (eyeHeight - crouchOffset);
    }

    void FaceTargetForward()
    {
        if (target == null) return;
        yaw = target.eulerAngles.y;
        pitch = 0f;
        transform.SetPositionAndRotation(target.position + Vector3.up * eyeHeight, Quaternion.Euler(0f, yaw, 0f));
    }
}
