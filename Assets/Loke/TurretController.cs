// Input System compatibility
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using UnityEngine;

// =============================================================================
//  TurretController.cs  -  Unity 6
//
//  Turret (horizontal): follows the camera via a screen-centre raycast,
//  so it always points where you are looking regardless of hull orientation.
//
//  Barrel (vertical): controlled directly by mouse Y, with adjustable speed,
//  clamped between maxDepression and maxElevation. Both axes ease smoothly
//  toward their targets instead of snapping.
//
//  Setup:
//    1. Attach this script to any GameObject (e.g. the tank root).
//    2. Hull      - tank body the turret sits on. Leave empty to use
//                  the turret's parent automatically.
//    3. Turret    - rotates left/right (child of hull).
//    4. Barrel    - tilts up/down (child of turret).
//    5. Aim Camera - camera the player looks through. Leave empty for Camera.main.
//    6. Hit Play - cursor locks automatically. Press Escape to unlock.
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

    // ── Turret (raycast-driven) ───────────────────────────────────────────────
    [Header("Turret - Raycast Aiming")]
    [Tooltip("How far the aim ray reaches before giving up on a hit (metres).")]
    public float aimRange = 500f;

    [Tooltip("Which layers the aim ray can hit. Exclude the tank's own layer " +
             "so it does not aim at itself.")]
    public LayerMask aimLayerMask = Physics.DefaultRaycastLayers;

    [Range(0.5f, 20f)]
    [Tooltip("How quickly the turret rotates to match the camera direction.\n" +
             "Low (1-3) = slow and heavy.  Mid (5-8) = smooth lag.  High (15+) = near-instant.")]
    public float turretSmoothing = 5f;

    // ── Barrel (mouse-driven) ─────────────────────────────────────────────────
    [Header("Barrel - Mouse Control")]
    [Range(0.5f, 15f)]
    [Tooltip("How fast mouse Y moves the barrel target angle.")]
    public float barrelSpeed = 3f;

    [Range(0.5f, 20f)]
    [Tooltip("How quickly the barrel physically rotates toward its target angle.\n" +
             "Low (1-3) = slow and heavy.  Mid (5-8) = smooth lag.  High (15+) = near-instant.")]
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
    [Tooltip("Lock and hide the cursor when the game starts.")]
    public bool lockCursorOnStart = true;

    [Tooltip("Press this key to toggle the cursor lock at runtime.")]
#if ENABLE_INPUT_SYSTEM
    public Key cursorToggleKey = Key.Escape;
#else
    public KeyCode cursorToggleKey = KeyCode.Escape;
#endif

    // ─────────────────────────────────────────────────────────────────────────
    //  Private state
    // ─────────────────────────────────────────────────────────────────────────

    private float turretYaw    = 0f;  // actual yaw  - lerped toward targetYaw
    private float barrelPitch  = 0f;  // actual pitch - lerped toward targetPitch
    private float targetPitch  = 0f;  // accumulates mouse Y input

    // New Input System mouse.delta gives raw pixels; scale down so barrelSpeed
    // feels equivalent to the normalised values Legacy Input.GetAxis returns.
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

        // Seed angles from existing transforms so nothing snaps on frame 1
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

        // ── Turret yaw: raycast to find where the camera points ───────────────
        Vector3 aimPoint  = GetAimPoint();
        Vector3 toAim     = aimPoint - turret.position;
        Vector3 toAimFlat = Vector3.ProjectOnPlane(toAim, hull.up);

        float targetYaw = turretYaw;
        if (toAimFlat.sqrMagnitude > 0.001f)
        {
            Vector3 localDir = hull.InverseTransformDirection(toAimFlat.normalized);
            targetYaw = Mathf.Atan2(localDir.x, localDir.z) * Mathf.Rad2Deg;
        }

        // ── Barrel pitch: accumulate mouse Y directly ─────────────────────────
        ReadMouseY(out float mouseY);
        targetPitch -= mouseY * barrelSpeed * InputScale;
        targetPitch  = Mathf.Clamp(targetPitch, -maxElevation, maxDepression);

        // ── Ease both toward their targets ────────────────────────────────────
        turretYaw   = Mathf.LerpAngle(turretYaw,  targetYaw,  turretSmoothing * Time.deltaTime);
        barrelPitch = Mathf.LerpAngle(barrelPitch, targetPitch, barrelSmoothing * Time.deltaTime);

        // ── Apply in local space ──────────────────────────────────────────────
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

    public Vector3 GetCurrentAimPoint()  => GetAimPoint();
    public float   GetBarrelElevation()  => barrelPitch;
    public float   GetTurretYaw()        => turretYaw;
}
