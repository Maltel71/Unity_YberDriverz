// Input System compatibility
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using UnityEngine;
using UnityEngine.VFX;

// =============================================================================
//  CannonController.cs  -  Unity 6  (Penetration System)
//
//  Physical-projectile cannon with multi-ammo-type support and full ballistic
//  penetration data per ammo type.
//  Press Q (configurable) to cycle ammo types.
//
//  Ammo types are defined in the Inspector as an array of CannonAmmoType.
//  Each type now carries complete shell physics data that is forwarded to
//  CannonProjectile on spawn:
//    - Shell type  (AP, APHE, APDS, APFSDS, HEAT, HESH, HE)
//    - Caliber     (mm)
//    - Shell mass  (kg, not used in the current calc but available for future drag)
//    - Base penetration (mm RHA at 0° at muzzle velocity)
//    - Normalization  (degrees)
//    - Ricochet angle (degrees)
//    - APHE blast data
//
//  The projectile prefab must have CannonProjectile attached.
//  CannonController sets all fields right after Instantiate so you can share
//  one prefab across all ammo types.
//
//  Setup:
//    1. Attach this script to the turret or barrel.
//    2. Create an empty GameObject at the barrel tip (the Shoot Point).
//       Attach your muzzle-flash VisualEffect on that object.
//    3. Assign the Shoot Point in the Inspector.
//    4. Add at least one CannonAmmoType entry with a projectile prefab and
//       fill in the ballistic data for that shell.
//    5. Assign the hull Rigidbody for recoil.
// =============================================================================

[System.Serializable]
public class CannonAmmoType
{
    // ── Identity ──────────────────────────────────────────────────────────────
    [Tooltip("Name shown in the HUD and console when cycling to this ammo type.\n" +
             "Examples: 'AP', 'APHE', '76mm APDS', 'HEAT-FS'")]
    public string displayName = "AP";

    // ── Projectile ────────────────────────────────────────────────────────────
    [Tooltip("Projectile prefab. Must have CannonProjectile attached.\n" +
             "One shared prefab can be used across all ammo types.")]
    public CannonProjectile projectilePrefab;

    // ── Shell type & caliber ──────────────────────────────────────────────────
    [Header("Shell Properties")]
    [Tooltip("Physical shell type. Determines the penetration physics model used:\n" +
             "  AP     – solid kinetic, moderate normalization (~5°)\n" +
             "  APHE   – kinetic + internal HE explosion on penetration\n" +
             "  APDS   – high velocity, low normalization (~2°)\n" +
             "  APFSDS – extreme velocity, minimal normalization (~1°), v^1.43 pen curve\n" +
             "  HEAT   – shaped charge, velocity-independent penetration\n" +
             "  HESH   – squash head, mostly velocity-independent\n" +
             "  HE     – blast/fragmentation, minimal direct penetration")]
    public ShellType shellType = ShellType.AP;

    [Tooltip("Shell outer diameter in mm.\n" +
             "Used for overmatch and partial-overmatch normalization bonus.\n" +
             "Examples: Tiger I = 88 mm, T-34-85 = 85 mm, M4 76mm = 76 mm")]
    [Range(20f, 200f)]
    public float caliber = 75f;

    [Tooltip("Shell mass in kg. Currently informational; reserved for future drag modelling.\n" +
             "Typical values: 75mm AP = 6.3 kg, 88mm AP = 10.2 kg, 128mm AP = 28 kg")]
    [Range(0.1f, 100f)]
    public float shellMassKg = 6.3f;

    // ── Ballistics ────────────────────────────────────────────────────────────
    [Header("Ballistics")]
    [Tooltip("Speed at which the shell leaves the barrel in m/s.\n" +
             "Also stored on the projectile so it can compute velocity retention.\n" +
             "Typical values: 75mm L/48 = 750 m/s, 88mm L/71 = 1000 m/s, " +
             "120mm APFSDS = 1650 m/s")]
    [Range(100f, 2000f)]
    public float muzzleVelocity = 800f;

    // ── Penetration ───────────────────────────────────────────────────────────
    [Header("Penetration")]
    [Tooltip("Nominal penetration in mm of RHA at 0° impact angle at muzzle velocity.\n" +
             "Penetration decreases with distance (v² for kinetic, unchanged for HEAT).\n" +
             "Typical values:\n" +
             "  75mm AP (500m)  = ~100 mm\n" +
             "  88mm AP         = ~165 mm\n" +
             "  100mm AP        = ~185 mm\n" +
             "  HEAT-FS (any range) = 400–600 mm")]
    [Range(1f, 1000f)]
    public float basePenetrationMM = 100f;

    [Tooltip("Degrees of normalization on impact. Defaults are filled automatically\n" +
             "when this is set to 0 (uses PenetrationCalculator.DefaultNormalization).\n" +
             "AP ~5°, APHE ~3°, APDS ~2°, APFSDS ~1°, HEAT/HE/HESH 0°")]
    [Range(0f, 15f)]
    public float normalizationDegOverride = 0f;

    [Tooltip("Impact angle from the armor normal at which the shell ricochets.\n" +
             "Set to 0 to use the per-shell-type default from PenetrationCalculator.\n" +
             "AP ~70°, APDS ~75°, APFSDS ~80°, HEAT ~85°")]
    [Range(0f, 90f)]
    public float ricochetAngleOverride = 0f;

    // ── APHE internal explosion ───────────────────────────────────────────────
    [Header("APHE Detonation  (APHE shell type only)")]
    [Tooltip("Damage dealt by the internal explosion after penetration.\n" +
             "Applied in addition to hull damage via TankHealth.apheExplosionMultiplier.")]
    public float apheBlastDamage = 150f;

    [Tooltip("Radius (metres) of the APHE internal explosion inside the tank.\n" +
             "0 = no internal explosion (treat as regular AP).")]
    [Range(0f, 10f)]
    public float apheBlastRadius = 3f;

    // ── Legacy damage (fallback for non-armored objects) ──────────────────────
    [Header("Legacy Damage  (non-armored targets)")]
    [Tooltip("Flat damage sent to non-armored objects via SendMessage('TakeDamage').")]
    public float damage = 100f;

    [Tooltip("Area damage radius for non-armored objects. 0 = point hit only.")]
    public float impactRadius = 0f;

    [Tooltip("Explosion impulse force on Rigidbodies in the impact radius.")]
    public float explosionForce = 0f;

    [Range(0f, 3f)]
    public float explosionUpwardModifier = 1f;

    // ── VFX ───────────────────────────────────────────────────────────────────
    [Header("VFX")]
    [Tooltip("VFX spawned at the hit point on impact (penetration spark or ricochet flash).")]
    public VisualEffect impactVFXPrefab;

    [Tooltip("How long the impact VFX lives (seconds).")]
    public float impactVFXLifetime = 2f;

    [Tooltip("Enable a separate explosion VFX when impactRadius > 0.")]
    public bool useExplosionVFX = false;

    [Tooltip("Explosion VFX spawned at the hit point. Only used when useExplosionVFX is true.")]
    public VisualEffect explosionVFXPrefab;

    [Tooltip("How long the explosion VFX lives (seconds).")]
    public float explosionVFXLifetime = 3f;

    // ── Lifetime ──────────────────────────────────────────────────────────────
    [Header("Projectile Lifetime")]
    [Tooltip("Seconds before the projectile self-destructs if it hits nothing.")]
    public float projectileLifetime = 10f;

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Returns the effective normalization angle, using the type default if override is 0.</summary>
    public float GetNormalization() =>
        normalizationDegOverride > 0f
            ? normalizationDegOverride
            : PenetrationCalculator.DefaultNormalization(shellType);

    /// <summary>Returns the effective ricochet angle, using the type default if override is 0.</summary>
    public float GetRicochetAngle() =>
        ricochetAngleOverride > 0f
            ? ricochetAngleOverride
            : PenetrationCalculator.DefaultRicochetAngle(shellType);
}

// ── Fire button enum ──────────────────────────────────────────────────────────

public enum CannonFireButton { LeftMouse, RightMouse, MiddleMouse }

// ── CannonController ──────────────────────────────────────────────────────────

public class CannonController : MonoBehaviour
{
    // ── Ammo Types ────────────────────────────────────────────────────────────
    [Header("Ammo Types")]
    [Tooltip("Define one entry per ammo type. Cycle with the Cycle Ammo Key (default Q).")]
    public CannonAmmoType[] ammoTypes;

    // ── Shoot Point ───────────────────────────────────────────────────────────
    [Header("Shoot Point")]
    [Tooltip("Empty GameObject at the barrel tip. " +
             "The muzzle-flash VFX should live on or under this object.")]
    public Transform shootPoint;

    [Tooltip("Muzzle flash Visual Effect. Leave empty to auto-find on the Shoot Point.")]
    public VisualEffect muzzleVFX;

    // ── Physics ───────────────────────────────────────────────────────────────
    [Header("Physics")]
    [Tooltip("Layers the projectile can collide with. Exclude the tank's own layer.")]
    public LayerMask hitLayers = Physics.DefaultRaycastLayers;

    // ── Recoil ────────────────────────────────────────────────────────────────
    [Header("Recoil")]
    [Tooltip("Rigidbody that receives the recoil impulse (usually the tank hull). " +
             "Leave empty to auto-find on this GameObject or its parents.")]
    public Rigidbody recoilBody;

    [Tooltip("Recoil impulse (N*s) applied backwards along the barrel when firing.")]
    public float recoilForce = 12000f;

    // ── Audio ─────────────────────────────────────────────────────────────────
    [Header("Audio")]
    [Tooltip("AudioSource used for the fire sound. Leave empty to auto-find on this GameObject.")]
    public AudioSource audioSource;

    [Tooltip("Sound played when the cannon fires.")]
    public AudioClip fireSound;

    [Range(0f, 1f)]
    public float fireSoundVolume = 1f;

    // ── Reload ────────────────────────────────────────────────────────────────
    [Header("Reload")]
    [Tooltip("Time in seconds between shots.")]
    public float reloadTime = 3f;

    // ── Input ─────────────────────────────────────────────────────────────────
    [Header("Input")]
    [Tooltip("Mouse button that fires the cannon.")]
    public CannonFireButton fireButton = CannonFireButton.LeftMouse;

#if ENABLE_INPUT_SYSTEM
    [Tooltip("Key that cycles through ammo types.")]
    public Key cycleAmmoKey = Key.Q;
#else
    [Tooltip("Key that cycles through ammo types.")]
    public KeyCode cycleAmmoKey = KeyCode.Q;
#endif

    // ─────────────────────────────────────────────────────────────────────────
    //  Private
    // ─────────────────────────────────────────────────────────────────────────

    private float nextFireTime     = 0f;
    private int   currentAmmoIndex = 0;

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        if (muzzleVFX == null && shootPoint != null)
            muzzleVFX = shootPoint.GetComponentInChildren<VisualEffect>();

        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        if (recoilBody == null)
            recoilBody = GetComponentInParent<Rigidbody>();
    }

    private void Update()
    {
        HandleAmmoSwitch();

        if (FireKeyDown() && Time.time >= nextFireTime)
            Fire();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Ammo cycling
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleAmmoSwitch()
    {
        if (ammoTypes == null || ammoTypes.Length <= 1) return;

#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null && kb[cycleAmmoKey].wasPressedThisFrame)
            CycleAmmo();
#else
        if (Input.GetKeyDown(cycleAmmoKey))
            CycleAmmo();
#endif
    }

    private void CycleAmmo()
    {
        if (ammoTypes == null || ammoTypes.Length == 0) return;
        currentAmmoIndex = (currentAmmoIndex + 1) % ammoTypes.Length;
        CannonAmmoType ammo = ammoTypes[currentAmmoIndex];
        Debug.Log($"[CannonController] Ammo → {ammo.displayName} " +
                  $"({ammo.shellType}, {ammo.basePenetrationMM:F0}mm pen, " +
                  $"{ammo.muzzleVelocity:F0} m/s)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Firing
    // ─────────────────────────────────────────────────────────────────────────

    public void Fire()
    {
        if (shootPoint == null) return;
        if (ammoTypes == null || ammoTypes.Length == 0) return;

        CannonAmmoType ammo = ammoTypes[currentAmmoIndex];
        if (ammo.projectilePrefab == null) return;

        nextFireTime = Time.time + reloadTime;

        // ── Muzzle flash ──────────────────────────────────────────────────────
        if (muzzleVFX != null)
        {
            muzzleVFX.Reinit();
            muzzleVFX.Play();
        }

        // ── Fire sound ────────────────────────────────────────────────────────
        if (audioSource != null && fireSound != null)
            audioSource.PlayOneShot(fireSound, fireSoundVolume);

        // ── Recoil ────────────────────────────────────────────────────────────
        if (recoilBody != null)
            recoilBody.AddForceAtPosition(
                -shootPoint.forward * recoilForce,
                shootPoint.position,
                ForceMode.Impulse);

        // ── Spawn projectile ──────────────────────────────────────────────────
        CannonProjectile proj = Instantiate(ammo.projectilePrefab,
                                            shootPoint.position,
                                            shootPoint.rotation);

        // ── Legacy / explosion data ───────────────────────────────────────────
        proj.damage                  = ammo.damage;
        proj.impactRadius            = ammo.impactRadius;
        proj.explosionForce          = ammo.explosionForce;
        proj.explosionUpwardModifier = ammo.explosionUpwardModifier;
        proj.hitLayers               = hitLayers;
        proj.useExplosionVFX         = ammo.useExplosionVFX;
        proj.impactVFXPrefab         = ammo.impactVFXPrefab;
        proj.impactVFXLifetime       = ammo.impactVFXLifetime;
        proj.explosionVFXPrefab      = ammo.explosionVFXPrefab;
        proj.explosionVFXLifetime    = ammo.explosionVFXLifetime;
        proj.lifetime                = ammo.projectileLifetime;

        // ── Penetration / ballistic data ──────────────────────────────────────
        proj.shellType         = ammo.shellType;
        proj.caliber           = ammo.caliber;
        proj.muzzleVelocity    = ammo.muzzleVelocity;
        proj.basePenetrationMM = ammo.basePenetrationMM;
        proj.normalizationDeg  = ammo.GetNormalization();
        proj.ricochetAngleDeg  = ammo.GetRicochetAngle();

        // ── APHE data ─────────────────────────────────────────────────────────
        proj.apheBlastDamage  = ammo.apheBlastDamage;
        proj.apheBlastRadius  = ammo.apheBlastRadius;

        // ── Launch ────────────────────────────────────────────────────────────
        Rigidbody prb = proj.GetComponent<Rigidbody>();
        if (prb != null)
            prb.linearVelocity = shootPoint.forward * ammo.muzzleVelocity;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Input helper
    // ─────────────────────────────────────────────────────────────────────────

    private bool FireKeyDown()
    {
#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        if (mouse == null) return false;
        switch (fireButton)
        {
            case CannonFireButton.LeftMouse:   return mouse.leftButton.wasPressedThisFrame;
            case CannonFireButton.RightMouse:  return mouse.rightButton.wasPressedThisFrame;
            case CannonFireButton.MiddleMouse: return mouse.middleButton.wasPressedThisFrame;
            default: return false;
        }
#else
        return Input.GetMouseButtonDown((int)fireButton);
#endif
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Gizmos
    // ─────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (shootPoint == null) return;

        CannonAmmoType ammo = (ammoTypes != null && ammoTypes.Length > 0)
            ? ammoTypes[currentAmmoIndex] : null;

        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(shootPoint.position, shootPoint.forward * 5f);

        if (ammo != null && ammo.impactRadius > 0f)
        {
            Gizmos.color = new Color(1f, 0.4f, 0f, 0.25f);
            Gizmos.DrawSphere(shootPoint.position + shootPoint.forward * 5f, ammo.impactRadius);
            Gizmos.color = new Color(1f, 0.4f, 0f, 0.8f);
            Gizmos.DrawWireSphere(shootPoint.position + shootPoint.forward * 5f, ammo.impactRadius);
        }

        if (ammo != null)
        {
            UnityEditor.Handles.Label(
                shootPoint.position + shootPoint.forward * 5.5f,
                string.Format("{0}  |  {1:F0}mm pen  |  {2:F0} m/s",
                              ammo.displayName, ammo.basePenetrationMM, ammo.muzzleVelocity));
        }
    }
#endif
}
