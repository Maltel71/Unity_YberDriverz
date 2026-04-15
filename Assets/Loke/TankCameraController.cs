// Input System compatibility
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using UnityEngine;
using Unity.Cinemachine;

// =============================================================================
//  TankCameraController.cs  -  Unity 6 / Cinemachine 3.x
//
//  Switches between three Cinemachine Virtual Cameras and handles zoom.
//
//  Camera switching:
//    V      - cycle forward through all three modes
//    Shift  - toggle between Third Person and Gun Sight
//    L      - toggle between Third Person and LMG Sight
//    Switching always resets zoom to the camera's original FOV.
//
//  Zoom (works in all three modes):
//    Z            - toggle between default FOV and Zoomed FOV
//    Left Click   - same toggle as Z
//    Scroll Wheel - continuous zoom in/out between Min FOV and default FOV
// =============================================================================

public enum TankCameraMode { ThirdPerson, GunSight, LMGSight }

public class TankCameraController : MonoBehaviour
{
    // ── Cameras ───────────────────────────────────────────────────────────────
    [Header("Cinemachine Virtual Cameras")]
    public GameObject thirdPersonCamera;
    public GameObject gunSightCamera;
    public GameObject lmgSightCamera;

    // ── Camera Switching Input ────────────────────────────────────────────────
    [Header("Camera Switch Input")]
#if ENABLE_INPUT_SYSTEM
    public Key cycleKey    = Key.V;
    public Key gunSightKey = Key.LeftShift;
    public Key lmgSightKey = Key.L;
#else
    public KeyCode cycleKey    = KeyCode.V;
    public KeyCode gunSightKey = KeyCode.LeftShift;
    public KeyCode lmgSightKey = KeyCode.L;
#endif

    // ── Zoom ──────────────────────────────────────────────────────────────────
    [Header("Zoom")]
    [Tooltip("FOV when zoomed in via Z or Left Click toggle.")]
    [Range(1f, 60f)]
    public float zoomedFOV = 15f;

    [Tooltip("How many FOV degrees each scroll wheel tick changes.")]
    [Range(0.5f, 15f)]
    public float scrollZoomStep = 3f;

    [Tooltip("Minimum FOV reachable by scrolling (tightest zoom).")]
    [Range(1f, 30f)]
    public float minFOV = 5f;

    // ─────────────────────────────────────────────────────────────────────────
    //  Private state
    // ─────────────────────────────────────────────────────────────────────────

    private TankCameraMode   currentMode    = TankCameraMode.ThirdPerson;
    private CinemachineCamera[] vcams       = new CinemachineCamera[3];
    private float[]           defaultFOV   = new float[3];
    private float             currentFOV;
    private bool              toggleZoomed = false;

    public TankCameraMode CurrentMode => currentMode;

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        // Cache Cinemachine cameras and store their original FOVs as defaults
        GameObject[] gos = { thirdPersonCamera, gunSightCamera, lmgSightCamera };
        for (int i = 0; i < 3; i++)
        {
            if (gos[i] != null)
                vcams[i] = gos[i].GetComponent<CinemachineCamera>();

            defaultFOV[i] = vcams[i] != null ? vcams[i].Lens.FieldOfView : 60f;
        }

        SetMode(TankCameraMode.ThirdPerson);
    }

    private void Update()
    {
        HandleSwitchInput();
        HandleZoomInput();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Camera switching
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleSwitchInput()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb[cycleKey].wasPressedThisFrame)
            SetMode((TankCameraMode)(((int)currentMode + 1) % 3));

        if (kb[gunSightKey].wasPressedThisFrame)
            SetMode(currentMode == TankCameraMode.GunSight
                ? TankCameraMode.ThirdPerson : TankCameraMode.GunSight);

        if (kb[lmgSightKey].wasPressedThisFrame)
            SetMode(currentMode == TankCameraMode.LMGSight
                ? TankCameraMode.ThirdPerson : TankCameraMode.LMGSight);
#else
        if (Input.GetKeyDown(cycleKey))
            SetMode((TankCameraMode)(((int)currentMode + 1) % 3));

        if (Input.GetKeyDown(gunSightKey))
            SetMode(currentMode == TankCameraMode.GunSight
                ? TankCameraMode.ThirdPerson : TankCameraMode.GunSight);

        if (Input.GetKeyDown(lmgSightKey))
            SetMode(currentMode == TankCameraMode.LMGSight
                ? TankCameraMode.ThirdPerson : TankCameraMode.LMGSight);
#endif
    }

    public void SetMode(TankCameraMode mode)
    {
        currentMode = mode;

        if (thirdPersonCamera != null) thirdPersonCamera.SetActive(mode == TankCameraMode.ThirdPerson);
        if (gunSightCamera    != null) gunSightCamera.SetActive(mode == TankCameraMode.GunSight);
        if (lmgSightCamera    != null) lmgSightCamera.SetActive(mode == TankCameraMode.LMGSight);

        // Reset zoom to this camera's original FOV whenever the view changes
        toggleZoomed = false;
        currentFOV   = defaultFOV[(int)mode];
        ApplyFOV();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Zoom
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleZoomInput()
    {
        bool togglePressed = false;
        float scroll       = 0f;

#if ENABLE_INPUT_SYSTEM
        var kb    = Keyboard.current;
        var mouse = Mouse.current;

        if (kb    != null && kb.zKey.wasPressedThisFrame)            togglePressed = true;
        if (mouse != null && mouse.rightButton.wasPressedThisFrame) togglePressed = true;
        if (mouse != null) scroll = mouse.scroll.ReadValue().y;
#else
        if (Input.GetKeyDown(KeyCode.Z))             togglePressed = true;
        if (Input.GetMouseButtonDown(0))             togglePressed = true;
        scroll = Input.GetAxis("Mouse ScrollWheel") * 120f;
#endif

        // Z / Left Click: toggle between default FOV and zoomed FOV
        if (togglePressed)
        {
            toggleZoomed = !toggleZoomed;
            currentFOV   = toggleZoomed ? zoomedFOV : defaultFOV[(int)currentMode];
            ApplyFOV();
        }

        // Scroll: continuous zoom, clamped between minFOV and this camera's default
        if (Mathf.Abs(scroll) > 0.01f)
        {
            currentFOV = Mathf.Clamp(currentFOV - scroll * scrollZoomStep,
                                      minFOV, defaultFOV[(int)currentMode]);
            // Keep toggle state in sync: if scrolled all the way out, un-zoom
            toggleZoomed = currentFOV < defaultFOV[(int)currentMode] - 0.5f;
            ApplyFOV();
        }
    }

    private void ApplyFOV()
    {
        CinemachineCamera active = vcams[(int)currentMode];
        if (active == null) return;

        LensSettings lens = active.Lens;
        lens.FieldOfView  = currentFOV;
        active.Lens       = lens;
    }
}
