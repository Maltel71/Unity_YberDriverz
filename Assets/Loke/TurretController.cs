// Input System compatibility
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using UnityEngine;

// =============================================================================
//  TurretController.cs  -  Unity 6
//
//  Behaviour depends on camera mode (read from TankCameraController):
//
//    Third Person  - turret yaw: screen-centre raycast (camera is free).
//                    Barrel pitch: mouse Y.
//
//    Gun Sight     - turret yaw: mouse X directly.
//                    Barrel pitch: mouse Y.
//                    Raycast cannot be used here because the sight camera is
//                    parented to the turret/barrel - it always points where the
//                    gun already points, so the raycast never produces rotation.
//
//    LMG Sight     - turret and barrel fully locked. No rotation at all.
//
//  If no TankCameraController is assigned, always uses Third Person behaviour.
// =============================================================================

public class TurretController : MonoBehaviour
{
    // ── References ────────────────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("The tank hull. Leave empty to use the turret's parent automatically.")]
    public Transform hull;

    [Tooltip("The turret body that rotates left/right (child of hull).")]
    public Transform turret;

    [Tooltip("The barrel that tilts up/down (child of turret).")]
    public Transform barrel;

    [Tooltip("The camera the player looks through. Leave empty to use Camera.main.")]
    public Camera aimCamera;

    [Tooltip("Assign TankCameraController to enable sight mode behaviour.")]
    public TankCameraController cameraController;

    // ── Turret ────────────────────────────────────────────────────────────────
    [Header("Turret")]
    [Tooltip("Aim ray range for third-person mode (metres).")]
    public float aimRange = 500f;

    [Tooltip("Which layers the aim ray can hit.")]
    public LayerMask aimLayerMask = Physics.DefaultRaycastLayers;

    [Range(0.5f, 20f)]
    [Tooltip("How quickly the turret rotates toward its target.")]
    public float turretSmoothing = 5f;

    [Range(0.5f, 15f)]
    [Tooltip("How fast mouse X rotates the turret in Gun Sight mode.")]
    public float turretMouseSpeed = 3f;

    // ── Barrel ────────────────────────────────────────────────────────────────
    [Header("Barrel")]
    [Range(0.5f, 15f)]
    [Tooltip("How fast mouse Y moves the barrel target angle.")]
    public float barrelSpeed = 3f;

    [Range(0.5f, 20f)]
    [Tooltip("How quickly the barrel rotates toward its target.")]
    public float barrelSmoothing = 5f;

    [Header("Barrel Elevation Limits")]
    [Range(0f, 45f)]
    public float maxDepression = 10f;

    [Range(0f, 80f)]
    public float maxElevation = 30f;

    // ── Cursor ────────────────────────────────────────────────────────────────
    [Header("Cursor")]
    public bool lockCursorOnStart = true;

#if ENABLE_INPUT_SYSTEM
    public Key cursorToggleKey = Key.Escape;
#else
    public KeyCode cursorToggleKey = KeyCode.Escape;
#endif

    // ─────────────────────────────────────────────────────────────────────────
    //  Private state
    // ─────────────────────────────────────────────────────────────────────────

    private float turretTargetYaw = 0f;  // accumulated in sight, set from raycast in 3P
    private float turretYaw       = 0f;  // smoothed actual yaw
    private float targetPitch     = 0f;  // accumulated mouse Y
    private float barrelPitch     = 0f;  // smoothed actual pitch
    private float turretYawVel    = 0f;
    private float barrelPitchVel  = 0f;

#if ENABLE_INPUT_SYSTEM
    private const float InputScale = 0.05f;
#else
    private const float InputScale = 1f;
#endif

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        if (hull == null && turret != null)
            hull = turret.parent;

        if (aimCamera == null)
            aimCamera = Camera.main;

        if (turret != null)
        {
            turretYaw       = turret.localEulerAngles.y;
            turretTargetYaw = turretYaw;
        }

        if (barrel != null)
        {
            float p = barrel.localEulerAngles.x;
            barrelPitch = p > 180f ? p - 360f : p;
            barrelPitch = Mathf.Clamp(barrelPitch, -maxElevation, maxDepression);
            targetPitch = barrelPitch;
        }

        SetCursorLocked(lockCursorOnStart);
    }

    private void Update()
    {
        HandleCursorToggle();

        if (turret == null || hull == null) return;

        TankCameraMode mode = cameraController != null
            ? cameraController.CurrentMode
            : TankCameraMode.ThirdPerson;

        // LMG Sight: turret and barrel lock completely
        if (mode == TankCameraMode.LMGSight)
            return;

        // ── Turret target yaw ─────────────────────────────────────────────────
        if (mode == TankCameraMode.GunSight)
        {
            // Camera is parented to the barrel so a raycast would never produce
            // rotation. Drive the turret directly with mouse X instead.
            ReadMouseX(out float mouseX);
            turretTargetYaw += mouseX * turretMouseSpeed * InputScale;
        }
        else
        {
            // Third Person: camera is free, raycast works correctly
            Vector3 aimPoint  = GetAimPoint();
            Vector3 toAimFlat = Vector3.ProjectOnPlane(aimPoint - turret.position, hull.up);

            if (toAimFlat.sqrMagnitude > 0.001f)
            {
                Vector3 localDir = hull.InverseTransformDirection(toAimFlat.normalized);
                turretTargetYaw  = Mathf.Atan2(localDir.x, localDir.z) * Mathf.Rad2Deg;
            }
        }

        // ── Barrel pitch: mouse Y in all non-locked modes ─────────────────────
        ReadMouseY(out float mouseY);

        bool pushingPastTop    = mouseY > 0f && targetPitch <= -maxElevation;
        bool pushingPastBottom = mouseY < 0f && targetPitch >=  maxDepression;
        if (!pushingPastTop && !pushingPastBottom)
            targetPitch -= mouseY * barrelSpeed * InputScale;

        targetPitch = Mathf.Clamp(targetPitch, -maxElevation, maxDepression);

        // ── Smooth and apply ──────────────────────────────────────────────────
        turretYaw   = Mathf.SmoothDampAngle(turretYaw,   turretTargetYaw, ref turretYawVel,
                                             Mathf.Max(0.01f, 1f / turretSmoothing));
        barrelPitch = Mathf.SmoothDampAngle(barrelPitch,  targetPitch,     ref barrelPitchVel,
                                             Mathf.Max(0.01f, 1f / barrelSmoothing));

        if (barrelPitch <= -maxElevation || barrelPitch >= maxDepression)
            barrelPitchVel = 0f;
        barrelPitch = Mathf.Clamp(barrelPitch, -maxElevation, maxDepression);

        turret.localRotation = Quaternion.Euler(0f, turretYaw, 0f);

        if (barrel != null)
            barrel.localRotation = Quaternion.Euler(barrelPitch, 0f, 0f);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private Vector3 GetAimPoint()
    {
        if (aimCamera == null)
            return turret.position + turret.forward * aimRange;

        Ray ray = aimCamera.ScreenPointToRay(
            new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));

        if (Physics.Raycast(ray, out RaycastHit hit, aimRange, aimLayerMask,
                            QueryTriggerInteraction.Ignore))
            return hit.point;

        return ray.origin + ray.direction * aimRange;
    }

    private void ReadMouseX(out float mouseX)
    {
        mouseX = 0f;
        if (Cursor.lockState != CursorLockMode.Locked) return;
#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        if (mouse != null)
            mouseX = mouse.delta.ReadValue().x;
#else
        mouseX = Input.GetAxis("Mouse X");
#endif
    }

    private void ReadMouseY(out float mouseY)
    {
        mouseY = 0f;
        if (Cursor.lockState != CursorLockMode.Locked) return;
#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        if (mouse != null)
            mouseY = mouse.delta.ReadValue().y;
#else
        mouseY = Input.GetAxis("Mouse Y");
#endif
    }

    private void HandleCursorToggle()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null && kb[cursorToggleKey].wasPressedThisFrame)
            SetCursorLocked(Cursor.lockState != CursorLockMode.Locked);
#else
        if (Input.GetKeyDown(cursorToggleKey))
            SetCursorLocked(Cursor.lockState != CursorLockMode.Locked);
#endif
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────────

    public void SetCursorLocked(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible   = !locked;
    }

    public Vector3 GetCurrentAimPoint() => GetAimPoint();
    public float   GetBarrelElevation() => barrelPitch;
    public float   GetTurretYaw()       => turretYaw;
}
