using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace StarterAssets
{
	[RequireComponent(typeof(CharacterController))]
#if ENABLE_INPUT_SYSTEM
	[RequireComponent(typeof(PlayerInput))]
#endif
	public class FirstPersonController : MonoBehaviour
	{
		[Header("Player")]
		[Tooltip("Move speed of the character in m/s")]
		public float MoveSpeed = 6.0f;
		[Tooltip("Sprint speed of the character in m/s")]
		public float SprintSpeed = 9.0f;
		[Tooltip("Rotation speed of the character")]
		public float RotationSpeed = 1.0f;
		[Tooltip("Acceleration and deceleration")]
		public float SpeedChangeRate = 10.0f;

		[Space(10)]
		[Tooltip("The height the player can jump")]
		public float JumpHeight = 1.2f;
		[Tooltip("The character uses its own gravity value. The engine default is -9.81f")]
		public float Gravity = -16.5f;
		[Tooltip("Gravity is multiplied by this once falling, for snappier, less floaty jumps. Jump height is unaffected " +
		         "(rising gravity is unchanged). 1 = off. Added for this project.")]
		public float FallGravityMultiplier = 2.0f;
		[Tooltip("Around the top of a jump, gravity ramps smoothly up to the fall multiplier over this range of " +
		         "vertical speed (m/s), instead of switching suddenly. Added for this project.")]
		public float ApexGravityBlend = 2.0f;

		[Space(10)]
		[Tooltip("Time required to pass before being able to jump again. Set to 0f to instantly jump again")]
		public float JumpTimeout = 0.1f;
		[Tooltip("Time required to pass before entering the fall state. Useful for walking down stairs")]
		public float FallTimeout = 0.15f;
		[Tooltip("A jump pressed this long before landing still happens on landing (sec). Added for this project.")]
		public float JumpBufferTime = 0.15f;

		[Header("Player Grounded")]
		[Tooltip("If the character is grounded or not. Not part of the CharacterController built in grounded check")]
		public bool Grounded = true;
		[Tooltip("Useful for rough ground")]
		public float GroundedOffset = -0.14f;
		[Tooltip("The radius of the grounded check. Should match the radius of the CharacterController")]
		public float GroundedRadius = 0.5f;
		[Tooltip("What layers the character uses as ground")]
		public LayerMask GroundLayers;

		[Header("Cinemachine")]
		[Tooltip("The follow target set in the Cinemachine Virtual Camera that the camera will follow")]
		public GameObject CinemachineCameraTarget;
		[Tooltip("How far in degrees can you move the camera up")]
		public float TopClamp = 90.0f;
		[Tooltip("How far in degrees can you move the camera down")]
		public float BottomClamp = -90.0f;

		// ---- Hooks for add-on components (e.g. FirstPersonCrouchSlide). Added for this project. ----
		/// <summary>When above 0, replaces MoveSpeed/SprintSpeed (e.g. crouch speed).</summary>
		public float SpeedLimitOverride { get; set; }
		/// <summary>While true, Move() uses OverrideHorizontalVelocity instead of input (e.g. during a slide).</summary>
		public bool HorizontalControlOverridden { get; set; }
		/// <summary>Horizontal velocity used while HorizontalControlOverridden is true.</summary>
		public Vector3 OverrideHorizontalVelocity { get; set; }
		/// <summary>Fires the moment a jump starts.</summary>
		public event System.Action Jumped;
		/// <summary>While true, this controller doesn't move the player (e.g. during a mantle). Looking still works.</summary>
		public bool MovementSuspended { get; set; }
		/// <summary>
		/// Velocity of this controller's own move last frame. Use this instead of CharacterController.velocity,
		/// which extra moves afterwards (e.g. snapping to the ground) overwrite.
		/// </summary>
		public Vector3 LastMoveVelocity { get; private set; }
		/// <summary>Sets the current vertical speed, e.g. to 0 after a mantle so the player doesn't drop suddenly.</summary>
		public void SetVerticalVelocity(float value) { _verticalVelocity = value; }

		// cinemachine
		private float _cinemachineTargetPitch;

		// player
		private float _speed;
		private float _rotationVelocity;
		private float _verticalVelocity;
		private float _terminalVelocity = 53.0f;

		// timeout deltatime
		private float _jumpTimeoutDelta;
		private float _fallTimeoutDelta;
		private float _jumpBufferDelta;

	
#if ENABLE_INPUT_SYSTEM
		private PlayerInput _playerInput;
#endif
		private CharacterController _controller;
		private StarterAssetsInputs _input;
		private GameObject _mainCamera;

		private const float _threshold = 0.01f;

		private bool IsCurrentDeviceMouse
		{
			get
			{
				#if ENABLE_INPUT_SYSTEM
				return _playerInput.currentControlScheme == "KeyboardMouse";
				#else
				return false;
				#endif
			}
		}

		private void Awake()
		{
			// get a reference to our main camera
			if (_mainCamera == null)
			{
				_mainCamera = GameObject.FindGameObjectWithTag("MainCamera");
			}
		}

		private void Start()
		{
			_controller = GetComponent<CharacterController>();
			_input = GetComponent<StarterAssetsInputs>();
#if ENABLE_INPUT_SYSTEM
			_playerInput = GetComponent<PlayerInput>();
#else
			Debug.LogError( "Starter Assets package is missing dependencies. Please use Tools/Starter Assets/Reinstall Dependencies to fix it");
#endif

			// reset our timeouts on start
			_jumpTimeoutDelta = JumpTimeout;
			_fallTimeoutDelta = FallTimeout;
		}

		private void Update()
		{
			// an add-on component (e.g. a mantle) is moving the player this frame
			if (MovementSuspended)
			{
				LastMoveVelocity = _controller.velocity;
				return;
			}

			JumpAndGravity();
			GroundedCheck();
			Move();
		}

		private void LateUpdate()
		{
			CameraRotation();
		}

		private void GroundedCheck()
		{
			// set sphere position, with offset
			Vector3 spherePosition = new Vector3(transform.position.x, transform.position.y - GroundedOffset, transform.position.z);
			Grounded = Physics.CheckSphere(spherePosition, GroundedRadius, GroundLayers, QueryTriggerInteraction.Ignore);
		}

		private void CameraRotation()
		{
			// if there is an input
			if (_input.look.sqrMagnitude >= _threshold)
			{
				//Don't multiply mouse input by Time.deltaTime
				float deltaTimeMultiplier = IsCurrentDeviceMouse ? 1.0f : Time.deltaTime;
				
				_cinemachineTargetPitch += _input.look.y * RotationSpeed * deltaTimeMultiplier;
				_rotationVelocity = _input.look.x * RotationSpeed * deltaTimeMultiplier;

				// clamp our pitch rotation
				_cinemachineTargetPitch = ClampAngle(_cinemachineTargetPitch, BottomClamp, TopClamp);

				// Update Cinemachine camera target pitch
				CinemachineCameraTarget.transform.localRotation = Quaternion.Euler(_cinemachineTargetPitch, 0.0f, 0.0f);

				// rotate the player left and right
				transform.Rotate(Vector3.up * _rotationVelocity);
			}
		}

		private void Move()
		{
			// an add-on component (e.g. a slide) is driving horizontal movement this frame
			if (HorizontalControlOverridden)
			{
				Vector3 horizontal = new Vector3(OverrideHorizontalVelocity.x, 0.0f, OverrideHorizontalVelocity.z);
				_speed = horizontal.magnitude;
				_controller.Move((horizontal + new Vector3(0.0f, _verticalVelocity, 0.0f)) * Time.deltaTime);
				LastMoveVelocity = _controller.velocity;
				return;
			}

			// set target speed based on move speed, sprint speed and if sprint is pressed
			float targetSpeed = SpeedLimitOverride > 0.0f ? SpeedLimitOverride : (_input.sprint ? SprintSpeed : MoveSpeed);

			// a simplistic acceleration and deceleration designed to be easy to remove, replace, or iterate upon

			// note: Vector2's == operator uses approximation so is not floating point error prone, and is cheaper than magnitude
			// if there is no input, set the target speed to 0
			if (_input.move == Vector2.zero) targetSpeed = 0.0f;

			// a reference to the players current horizontal velocity
			float currentHorizontalSpeed = new Vector3(LastMoveVelocity.x, 0.0f, LastMoveVelocity.z).magnitude;

			float speedOffset = 0.1f;
			float inputMagnitude = _input.analogMovement ? _input.move.magnitude : 1f;

			// accelerate or decelerate to target speed
			if (currentHorizontalSpeed < targetSpeed - speedOffset || currentHorizontalSpeed > targetSpeed + speedOffset)
			{
				// creates curved result rather than a linear one giving a more organic speed change
				// note T in Lerp is clamped, so we don't need to clamp our speed
				_speed = Mathf.Lerp(currentHorizontalSpeed, targetSpeed * inputMagnitude, Time.deltaTime * SpeedChangeRate);

				// round speed to 3 decimal places
				_speed = Mathf.Round(_speed * 1000f) / 1000f;
			}
			else
			{
				_speed = targetSpeed;
			}

			// normalise input direction
			Vector3 inputDirection = new Vector3(_input.move.x, 0.0f, _input.move.y).normalized;

			// note: Vector2's != operator uses approximation so is not floating point error prone, and is cheaper than magnitude
			// if there is a move input rotate player when the player is moving
			if (_input.move != Vector2.zero)
			{
				// move
				inputDirection = transform.right * _input.move.x + transform.forward * _input.move.y;
			}

			// move the player
			_controller.Move(inputDirection.normalized * (_speed * Time.deltaTime) + new Vector3(0.0f, _verticalVelocity, 0.0f) * Time.deltaTime);
			LastMoveVelocity = _controller.velocity;
		}

		private void JumpAndGravity()
		{
			if (Grounded)
			{
				// reset the fall timeout timer
				_fallTimeoutDelta = FallTimeout;

				// stop our velocity dropping infinitely when grounded.
				// (Changed for this project: only once the capsule actually touches down. The Grounded sphere
				// reaches ~0.3 m below the feet, and cutting fall speed that early caused a hover-then-jolt on landing.)
				if (_verticalVelocity < 0.0f && _controller.isGrounded)
				{
					_verticalVelocity = -2f;
				}

				// Jump. A press buffered from just before landing jumps right away (added for this project,
				// so bunnyhops don't need frame-perfect timing).
				bool bufferedJump = _jumpBufferDelta > 0.0f;
				if ((_input.jump && _jumpTimeoutDelta <= 0.0f) || bufferedJump)
				{
					// the square root of H * -2 * G = how much velocity needed to reach desired height
					_verticalVelocity = Mathf.Sqrt(JumpHeight * -2f * Gravity);
					_input.jump = false;
					_jumpBufferDelta = 0.0f;
					Jumped?.Invoke();
				}

				// jump timeout
				if (_jumpTimeoutDelta >= 0.0f)
				{
					_jumpTimeoutDelta -= Time.deltaTime;
				}
			}
			else
			{
				// reset the jump timeout timer
				_jumpTimeoutDelta = JumpTimeout;

				// fall timeout
				if (_fallTimeoutDelta >= 0.0f)
				{
					_fallTimeoutDelta -= Time.deltaTime;
				}

				// if we are not grounded, do not jump now, but remember a fresh press for a moment
				// in case we land right after it (jump buffering, added for this project)
				if (_input.jump) _jumpBufferDelta = JumpBufferTime;
				else if (_jumpBufferDelta > 0.0f) _jumpBufferDelta -= Time.deltaTime;
				_input.jump = false;
			}

			// apply gravity over time (multiply by delta time twice to linearly speed up over time).
			// Changed for this project: gravity ramps up from normal to FallGravityMultiplier as the jump nears its
			// peak and while falling, and falling speed is capped at terminal velocity (the original check never
			// capped the fall, since it compared against a positive number).
			float gravityScale = 1.0f;
			if (FallGravityMultiplier > 1.0f)
			{
				float blend = Mathf.Max(ApexGravityBlend, 0.01f);
				// 0 while rising faster than +blend, 1 once falling faster than -blend
				float t = Mathf.InverseLerp(blend, -blend, _verticalVelocity);
				gravityScale = Mathf.Lerp(1.0f, FallGravityMultiplier, t);
			}
			_verticalVelocity = Mathf.Max(_verticalVelocity + Gravity * gravityScale * Time.deltaTime, -_terminalVelocity);
		}

		private static float ClampAngle(float lfAngle, float lfMin, float lfMax)
		{
			if (lfAngle < -360f) lfAngle += 360f;
			if (lfAngle > 360f) lfAngle -= 360f;
			return Mathf.Clamp(lfAngle, lfMin, lfMax);
		}

		private void OnDrawGizmosSelected()
		{
			Color transparentGreen = new Color(0.0f, 1.0f, 0.0f, 0.35f);
			Color transparentRed = new Color(1.0f, 0.0f, 0.0f, 0.35f);

			if (Grounded) Gizmos.color = transparentGreen;
			else Gizmos.color = transparentRed;

			// when selected, draw a gizmo in the position of, and matching radius of, the grounded collider
			Gizmos.DrawSphere(new Vector3(transform.position.x, transform.position.y - GroundedOffset, transform.position.z), GroundedRadius);
		}
	}
}