// Input System compatibility
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using UnityEngine;
using Unity.Cinemachine;

// =============================================================================
//  TankCameraController.cs  -  Unity 6 / Cinemachine 3.x
//
//  Cycles between three Cinemachine Virtual Cameras:
//    1. Third Person   - normal driving view
//    2. Gun Sight      - cannon scope, supports scroll-wheel zoom
//    3. LMG Sight      - secondary weapon scope
//
//  Activation pattern mirrors the reference script:
//  only the active virtual camera's GameObject is enabled;
//  the CinemachineBrain on your scene Camera picks it up automatically.
//
//  Setup:
//    1. Attach to any GameObject (e.g. the tank root).
//    2. Assign the three virtual camera GameObjects in the Inspector.
//    3. Set the cycle key (default: C) and optional zoom settings.
//    4. Optionally assign AimReticle so it can be hidden inside sights.
// =============================================================================

public enum TankCameraMode { ThirdPerson, GunSight, LMGSight }

public class TankCameraController : MonoBehaviour
{
    // ── Cameras ───────────────────────────────────────────────────────────────
    [Header("Cinemachine Virtual Cameras")]
    [Tooltip("Third-person follow camera used during normal driving.")]
    public GameObject thirdPersonCamera;

    [Tooltip("Cannon gun sight camera. Zoom is applied to this camera's FOV.")]
    public GameObject gunSightCamera;

    [Tooltip("LMG sight camera.")]
    public GameObject lmgSightCamera;

    // ── Mode ──────────────────────────────────────────────────────────────────
    [Header("Starting Mode")]
    public TankCameraMode startingMode = TankCameraMode.ThirdPerson;

    // ── Input ─────────────────────────────────────────────────────────────────
    [Header("Input")]
#if ENABLE_INPUT_SYSTEM
    [Tooltip("Key that cycles through camera modes.")]
    public Key cycleCameraKey = Key.C;
#else
    [Tooltip("Key that cycles through camera modes.")]
    public KeyCode cycleCameraKey = KeyCode.C;
#endif

    // ── Zoom (Gun Sight only) ─────────────────────────────────────────────────
    [Header("Gun Sight Zoom")]
    [Tooltip("Field of view when fully zoomed out (widest view).")]
    [Range(5f, 60f)]
    public float zoomOutFOV = 20f;

    [Tooltip("Field of view when fully zoomed in (tightest view).")]
    [Range(1f, 20f)]
    public float zoomInFOV = 5f;

    [Tooltip("How many FOV degrees each scroll wheel tick changes.")]
    [Range(0.5f, 10f)]
    public float zoomStep = 3f;

    [Range(1f, 30f)]
    [Tooltip("How quickly the FOV eases toward the target zoom. Higher = snappier.")]
    public float zoomSmoothing = 10f;

    // ── Optional references ───────────────────────────────────────────────────
    [Header("Optional")]
    [Tooltip("Assign the AimReticle to automatically hide it when inside a sight.")]
    public AimReticle aimReticle;

    // ─────────────────────────────────────────────────────────────────────────
    //  Private state
    // ─────────────────────────────────────────────────────────────────────────

    private TankCameraMode currentMode;
    private CinemachineCamera gunSightCM;
    private float targetFOV;
    private float currentFOV;

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────────

    public TankCameraMode CurrentMode => currentMode;

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        if (gunSightCamera != null)
            gunSightCM = gunSightCamera.GetComponent<CinemachineCamera>();

        targetFOV  = zoomOutFOV;
        currentFOV = zoomOutFOV;

        SetMode(startingMode);
    }

    private void Update()
    {
        HandleCycleInput();
        HandleZoom();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Input
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleCycleInput()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb[cycleCameraKey].wasPressedThisFrame)
            CycleMode();
#else
        if (Input.GetKeyDown(cycleCameraKey))
            CycleMode();
#endif
    }

    private void HandleZoom()
    {
        if (currentMode != TankCameraMode.GunSight) return;
        if (gunSightCM == null) return;

        // Read scroll wheel
        float scroll = 0f;
#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        if (mouse != null)
            scroll = mouse.scroll.ReadValue().y;
#else
        scroll = Input.GetAxis("Mouse ScrollWheel") * 10f; // legacy returns small values
#endif

        if (Mathf.Abs(scroll) > 0.01f)
            targetFOV = Mathf.Clamp(targetFOV - scroll * zoomStep, zoomInFOV, zoomOutFOV);

        // Smooth zoom
        currentFOV = Mathf.Lerp(currentFOV, targetFOV, Time.deltaTime * zoomSmoothing);

        LensSettings lens = gunSightCM.Lens;
        lens.FieldOfView = currentFOV;
        gunSightCM.Lens  = lens;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Mode switching
    // ─────────────────────────────────────────────────────────────────────────

    private void CycleMode()
    {
        TankCameraMode next = (TankCameraMode)(((int)currentMode + 1) % 3);
        SetMode(next);
    }

    public void SetMode(TankCameraMode mode)
    {
        currentMode = mode;

        if (thirdPersonCamera != null)
            thirdPersonCamera.SetActive(mode == TankCameraMode.ThirdPerson);

        if (gunSightCamera != null)
            gunSightCamera.SetActive(mode == TankCameraMode.GunSight);

        if (lmgSightCamera != null)
            lmgSightCamera.SetActive(mode == TankCameraMode.LMGSight);

        // Reset gun sight zoom when entering the sight
        if (mode == TankCameraMode.GunSight)
        {
            targetFOV  = zoomOutFOV;
            currentFOV = zoomOutFOV;
        }

        // Reticle is only meaningful in third-person
        if (aimReticle != null)
            aimReticle.SetVisible(mode == TankCameraMode.ThirdPerson);
    }
}
