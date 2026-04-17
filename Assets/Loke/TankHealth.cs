using UnityEngine;
using UnityEngine.Events;

// =============================================================================
//  TankHealth.cs  -  Unity 6  (Module System Integration)
//
//  Central hull health manager for a tank. Works alongside ModuleManager,
//  which handles individual module / crew damage and aggregated penalties.
//
//  Responsibility split
//  ─────────────────────
//    TankHealth   – tracks hull HP, fires global events, owns basePenDamage
//                   and maxHealth (referenced by ModuleManager for damage scaling).
//    ModuleManager – routes penetrating hits to modules and crew, distributes
//                   spall and overpressure, handles fires and knock-out logic.
//
//  Attach both to the same root tank GameObject.
//
//  CannonProjectile calls:
//    - armor penetration → result passed to ModuleManager.ApplyPenetratingHit()
//      and TankHealth.ApplyInternalDamage() (hull structural damage).
//    - non-armored hits   → TakeDamage() via SendMessage (legacy fallback).
//
//  Knock-out can be triggered by:
//    - Hull HP reaching 0
//    - ModuleManager (crew killed, catastrophic module event)
//    - Both paths converge in ForceKnockOut()
// =============================================================================

public class TankHealth : MonoBehaviour
{
    // ── Hull health ───────────────────────────────────────────────────────────
    [Header("Hull Health")]
    [Tooltip("Total hull hit points. When this reaches 0 the tank is knocked out.")]
    public float maxHealth = 400f;

    [Tooltip("Base damage applied to the hull per penetrating hit before the\n" +
             "post-penetration energy multiplier is applied.\n" +
             "ModuleManager also reads this value for module damage scaling.")]
    public float basePenDamage = 80f;

    // ── HE blast (non-penetrating) ────────────────────────────────────────────
    [Header("HE Blast  (non-penetrating hits)")]
    [Tooltip("Damage dealt to the hull by HE overpressure when the shell does NOT\n" +
             "fully penetrate the armor. Scales with how thin the armor is.")]
    public float heOverpressureDamageBase = 40f;

    [Tooltip("Armor thickness in mm below which HE overpressure reaches full damage.\n" +
             "Damage scales from 0 (at this threshold) to heOverpressureDamageBase (at 0 mm).")]
    public float heEffectiveArmorThresholdMM = 30f;

    // ── APHE ──────────────────────────────────────────────────────────────────
    [Header("APHE Hull Damage Multiplier")]
    [Tooltip("Additional hull damage multiplier when an APHE shell penetrates.\n" +
             "The internal explosion causes structural damage beyond simple kinetic impact.")]
    [Range(1f, 6f)]
    public float apheHullDamageMultiplier = 1.8f;

    // ── Feedback ──────────────────────────────────────────────────────────────
    [Header("Feedback")]
    [Tooltip("VFX spawned at the hit point on a ricochet or blocked hit.")]
    public GameObject ricochetVFXPrefab;

    [Tooltip("VFX spawned at the hit point on a penetrating hit.")]
    public GameObject penetrationSparkVFXPrefab;

    [Tooltip("Played on any penetrating hit.")]
    public AudioClip penetrationSound;

    [Tooltip("Played on ricochet or blocked hit.")]
    public AudioClip ricochetSound;

    // ── Events ────────────────────────────────────────────────────────────────
    [Header("Events")]
    [Tooltip("Fired when any shell penetrates the hull armor.")]
    public UnityEvent onPenetrated;

    [Tooltip("Fired when a shell ricochets or is blocked.")]
    public UnityEvent onRicochetOrBlock;

    [Tooltip("Fired with the module name whenever a module is destroyed.\n" +
             "ModuleManager calls OnModuleDestroyedCallback to relay the event here.")]
    public UnityEvent<string> onModuleDestroyed;

    [Tooltip("Fired when the tank is knocked out by any means.")]
    public UnityEvent onTankKnockedOut;

    // ─────────────────────────────────────────────────────────────────────────
    //  Runtime state
    // ─────────────────────────────────────────────────────────────────────────

    private float        currentHealth;
    private bool         knockedOut = false;
    private ModuleManager moduleManager;
    private AudioSource  audioSrc;

    public float CurrentHealth  => currentHealth;
    public float HealthFraction => currentHealth / Mathf.Max(1f, maxHealth);
    public bool  IsKnockedOut   => knockedOut;

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        currentHealth = maxHealth;
        moduleManager = GetComponent<ModuleManager>();

        audioSrc = GetComponent<AudioSource>();
        if (audioSrc == null)
            audioSrc = gameObject.AddComponent<AudioSource>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API  (called by CannonProjectile)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Primary entry point called by CannonProjectile after PenetrationCalculator
    /// returns a result. Handles VFX/audio feedback and delegates damage to
    /// ModuleManager (if present) or applies it directly to hull health.
    /// </summary>
    public void ApplyPenetrationResult(PenetrationResult result, ShellType shellType,
                                        Vector3 hitPoint)
    {
        if (knockedOut) return;

        // ── Non-penetrating outcomes ──────────────────────────────────────────
        if (result.ricocheted || result.heatDefeatedBySpaced || !result.penetrated)
        {
            SpawnVFX(ricochetVFXPrefab, hitPoint, Vector3.up);
            PlayAudio(ricochetSound);
            onRicochetOrBlock?.Invoke();

            if (result.ricocheted)
                Debug.Log($"[TankHealth] {name} — RICOCHET at {result.impactAngleDeg:F1}°");
            else if (result.heatDefeatedBySpaced)
                Debug.Log($"[TankHealth] {name} — HEAT defeated by spaced armor");
            else
                Debug.Log($"[TankHealth] {name} — BLOCKED " +
                          $"({result.shellPenetrationMM:F0}mm vs {result.effectiveArmorMM:F0}mm)");
            return;
        }

        // ── Penetrating hit ───────────────────────────────────────────────────
        SpawnVFX(penetrationSparkVFXPrefab, hitPoint, Vector3.up);
        PlayAudio(penetrationSound);
        onPenetrated?.Invoke();

        Debug.Log($"[TankHealth] {name} — PENETRATED " +
                  $"({result.shellPenetrationMM:F0}mm vs {result.effectiveArmorMM:F0}mm, " +
                  $"{result.excessPenetrationMM:F0}mm excess)");

        // ── Delegate to ModuleManager for module/crew damage ──────────────────
        if (moduleManager != null)
        {
            moduleManager.ApplyPenetratingHit(result, shellType, null, hitPoint);
        }
        else
        {
            // Standalone mode: apply hull damage directly if no ModuleManager
            float damage = basePenDamage * result.postPenDamageMultiplier;
            if (shellType == ShellType.APHE) damage *= apheHullDamageMultiplier;
            ApplyHullDamage(damage);
        }
    }

    /// <summary>
    /// Apply structural hull damage from a penetrating hit.
    /// Called by ModuleManager.ApplyPenetratingHit() to track hull integrity
    /// separately from module damage.
    /// </summary>
    public void ApplyInternalDamage(float damage, ShellType shellType)
    {
        if (knockedOut || damage <= 0f) return;

        float finalDamage = shellType == ShellType.APHE
            ? damage * apheHullDamageMultiplier
            : damage;

        ApplyHullDamage(finalDamage);
    }

    /// <summary>
    /// Apply HE blast damage when a non-penetrating HE shell detonates on thin armor.
    /// Damage scales with how thin the armor is relative to heEffectiveArmorThresholdMM.
    /// </summary>
    public void TakeHEBlastDamage(float blastDamage, float armorThicknessMM)
    {
        if (knockedOut) return;

        float fraction = 1f - Mathf.Clamp01(
            armorThicknessMM / Mathf.Max(1f, heEffectiveArmorThresholdMM));

        float damage = heOverpressureDamageBase * fraction;
        if (damage <= 0f) return;

        ApplyHullDamage(damage);

        // Forward overpressure to ModuleManager
        moduleManager?.ApplyHEOverpressure(blastDamage * fraction);
    }

    /// <summary>
    /// Legacy SendMessage compatibility. Used by non-armored props, explosions,
    /// environmental hazards, etc.
    /// </summary>
    public void TakeDamage(float damage)
    {
        if (knockedOut) return;
        ApplyHullDamage(damage);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Callbacks from ModuleManager
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Called by ModuleManager when a module is destroyed.</summary>
    public void OnModuleDestroyedCallback(string moduleName)
    {
        onModuleDestroyed?.Invoke(moduleName);
        Debug.Log($"[TankHealth] {name} — module '{moduleName}' destroyed.");
    }

    /// <summary>Force knock-out from ModuleManager (crew death, catastrophic event).</summary>
    public void ForceKnockOut(string reason)
    {
        if (knockedOut) return;
        knockedOut    = true;
        currentHealth = 0f;
        Debug.Log($"[TankHealth] {name} — KNOCKED OUT ({reason})");
        onTankKnockedOut?.Invoke();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void ApplyHullDamage(float damage)
    {
        if (knockedOut || damage <= 0f) return;

        currentHealth = Mathf.Max(0f, currentHealth - damage);
        Debug.Log($"[TankHealth] {name} — hull {damage:F0} dmg. " +
                  $"HP: {currentHealth:F0}/{maxHealth:F0}");

        if (currentHealth <= 0f)
        {
            knockedOut = true;
            Debug.Log($"[TankHealth] {name} — HULL DESTROYED");
            onTankKnockedOut?.Invoke();
            moduleManager?.ForceKnockOut("hull HP depleted");
        }
    }

    private void SpawnVFX(GameObject prefab, Vector3 position, Vector3 normal)
    {
        if (prefab == null) return;
        Destroy(Instantiate(prefab, position, Quaternion.LookRotation(normal)), 5f);
    }

    private void PlayAudio(AudioClip clip)
    {
        if (audioSrc == null || clip == null) return;
        audioSrc.PlayOneShot(clip);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Editor gizmos
    // ─────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;

        float frac   = HealthFraction;
        Gizmos.color = Color.Lerp(Color.red, Color.green, frac);
        Gizmos.DrawWireSphere(transform.position + Vector3.up * 2f, 0.3f);

        UnityEditor.Handles.Label(
            transform.position + Vector3.up * 2.6f,
            $"Hull HP: {currentHealth:F0}/{maxHealth:F0}{(knockedOut ? "  [KO]" : "")}");
    }
#endif
}
