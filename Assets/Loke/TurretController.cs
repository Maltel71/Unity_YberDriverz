// Input System compatibility
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using UnityEngine;

// =============================================================================
//  TurretController.cs  -  Unity 6
//
//  Turret (horizontal): follows the camera via a screen-centre raycast.
//  Barrel (vertical): controlled by mouse Y, clamped between limits.
//
//  In LMG Sight mode (read from TankCameraController): turret and barrel lock
//  completely so the LMG can be aimed independently without the turret moving.
//  In all other modes the raycast drives the turret as normal.
//
//  Setup:
//    1. Attach to any GameObject (e.g. the tank root).
//    2. Hull, Turret, Barrel - assign as before.
//    3. Aim Camera           - leave empty for Camera.main.
//    4. Camera Controller    - assign TankCameraController so the LMG sight
//                              lock works. Leave empty to never lock.
// =============================================================================

public class TurretController : MonoBehaviour
{
    // ── References ────────────────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("The tank hull. Used as the local-space parent for turret yaw. " +
             "Leave empty to use the turret's parent Transform automatically.")]
    public Transform hull;

    [Tooltip("The turret body that rotates left/right (child of hull).")]
    public Transform turret;

    [Tooltip("The barrel that tilts up/down (child of turret).")]
    public Transform barrel;

    [Tooltip("The camera the player looks through. Leave empty to use Camera.main.")]
    public Camera aimCamera;

    [Tooltip("Assign TankCameraController so the turret locks in LMG Sight mode.")]
    public TankCameraController cameraController;

    // ── Turret (raycast-driven) ───────────────────────────────────────────────
    [Header("Turret - Raycast Aiming")]
    [Tooltip("How far the aim ray reaches (metres).")]
    public float aimRange = 500f;

    [Tooltip("Which layers the aim ray can hit. Exclude the tank's own layer.")]
    public LayerMask aimLayerMask = Physics.DefaultRaycastLayers;

    [Range(0.5f, 20f)]
    [Tooltip("How quickly the turret rotates to match the camera direction.")]
    public float turretSmoothing = 5f;

    // ── Barrel (mouse-driven) ─────────────────────────────────────────────────
    [Header("Barrel - Mouse Control")]
    [Range(0.5f, 15f)]
    [Tooltip("How fast mouse Y moves the barrel target angle.")]
    public float barrelSpeed = 3f;

    [Range(0.5f, 20f)]
    [Tooltip("How quickly the barrel physically rotates toward its target angle.")]
    public float barrelSmoothing = 5f;

    // ── Barrel Elevation Limits ───────────────────────────────────────────────
    [Header("Barrel Elevation Limits")]
    [Range(0f, 45f)]
    [Tooltip("How far the barrel can depress downward (degrees below horizontal).")]
    public float maxDepression = 10f;

    [Range(0f, 80f)]
    [Tooltip("How far the barrel can elevate upward (degrees above horizontal).")]
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

    private float turretYaw      = 0f;
    private float barrelPitch    = 0f;
    private float targetPitch    = 0f;
    private float turretYawVel   = 0f;
    private float barrelPitchVel = 0f;

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
            turretYaw = turret.localEulerAngles.y;

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

        // Turret and barrel lock completely when the LMG sight is active
        if (cameraController != null && cameraController.CurrentMode == TankCameraMode.LMGSight)
            return;

        // ── Turret yaw: screen-centre raycast ─────────────────────────────────
        Vector3 aimPoint  = GetAimPoint();
        Vector3 toAimFlat = Vector3.ProjectOnPlane(aimPoint - turret.position, hull.up);

        float targetYaw = turretYaw;
        if (toAimFlat.sqrMagnitude > 0.001f)
        {
            Vector3 localDir = hull.InverseTransformDirection(toAimFlat.normalized);
            targetYaw = Mathf.Atan2(localDir.x, localDir.z) * Mathf.Rad2Deg;
        }

        // ── Barrel pitch: mouse Y ─────────────────────────────────────────────
        ReadMouseY(out float mouseY);

        bool pushingPastTop    = mouseY > 0f && targetPitch <= -maxElevation;
        bool pushingPastBottom = mouseY < 0f && targetPitch >=  maxDepression;
        if (!pushingPastTop && !pushingPastBottom)
            targetPitch -= mouseY * barrelSpeed * InputScale;

        targetPitch = Mathf.Clamp(targetPitch, -maxElevation, maxDepression);

        // ── Smooth both toward their targets ──────────────────────────────────
        turretYaw   = Mathf.SmoothDampAngle(turretYaw,   targetYaw,   ref turretYawVel,
                                             Mathf.Max(0.01f, 1f / turretSmoothing));
        barrelPitch = Mathf.SmoothDampAngle(barrelPitch,  targetPitch, ref barrelPitchVel,
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
