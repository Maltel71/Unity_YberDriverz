using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

// =============================================================================
//  TankHealth.cs  -  Unity 6
//
//  Central health and damage manager for a tank.
//
//  Attach this to the same root GameObject as TankController.
//  ArmorPlate components anywhere in the hierarchy will automatically
//  find this via GetComponentInParent<TankHealth>().
//
//  Damage model
//  ─────────────
//  - Each penetrating hit deals basePenDamage modified by postPenDamageMultiplier
//    (which reflects how much energy the shell had left after defeating the armor).
//  - APHE: the internal explosion multiplies damage further.
//  - HE: blast damage scaled by blast radius vs armor thickness (no penetration needed).
//  - Crew: each penetrating hit has a chance to incapacitate a crew member.
//    When all crew are incapacitated the tank is knocked out.
//  - Modules: each penetrating hit rolls per-module to see if that component
//    is damaged (engine, ammo rack, tracks, gunner, driver, etc.).
//    A destroyed ammo rack should trigger a catastrophic kill in your event handler.
//
//  Events
//  ──────
//  Wire up UnityEvents in the Inspector to drive explosions, UI, audio, etc.
//    onPenetrated        - fired on any penetrating hit (before damage is applied)
//    onModuleDestroyed   - fired with the module name string when a module dies
//    onCrewIncapacitated - fired with remaining crew count
//    onTankDestroyed     - fired when the tank is knocked out
// =============================================================================

// ── Module definition ─────────────────────────────────────────────────────────

[System.Serializable]
public class TankModule
{
    [Tooltip("Human-readable name (e.g. 'Engine', 'Ammo Rack', 'Driver', 'Gunner', 'Tracks').")]
    public string moduleName = "Module";

    [Tooltip("Maximum health for this module.")]
    [Range(1f, 500f)]
    public float maxHealth = 100f;

    [Tooltip("Probability (0–1) that this module is damaged on any penetrating hit.\n" +
             "Higher = more critical position in the hull.\n" +
             "e.g. Engine = 0.15, Ammo Rack = 0.10, Driver = 0.20")]
    [Range(0f, 1f)]
    public float hitProbability = 0.15f;

    [Tooltip("Hull damage multiplier when this module takes a direct hit.\n" +
             "An ammo rack cook-off might be 3–5×; a fuel tank fire might be 2×.")]
    [Range(0.1f, 10f)]
    public float hullDamageOnHit = 1.0f;

    [Tooltip("If true, destroying this module immediately knocks out the tank\n" +
             "(e.g. ammo rack detonation, driver killed in certain configurations).")]
    public bool catastrophicOnDestruction = false;

    // Runtime — do not set in Inspector
    [System.NonSerialized] public float currentHealth;
    [System.NonSerialized] public bool  isDestroyed;
    [System.NonSerialized] public bool  isDisabled;
}

// ── TankHealth ────────────────────────────────────────────────────────────────

public class TankHealth : MonoBehaviour
{
    // ── Hull ──────────────────────────────────────────────────────────────────
    [Header("Hull Health")]
    [Tooltip("Total hit points of the tank hull.")]
    public float maxHealth = 400f;

    [Tooltip("Base damage applied per penetrating kinetic hit (before post-pen multiplier).")]
    public float basePenDamage = 80f;

    // ── Crew ──────────────────────────────────────────────────────────────────
    [Header("Crew")]
    [Tooltip("Total crew count. Tank is knocked out when all crew are incapacitated.")]
    public int crewCount = 4;

    [Tooltip("Probability (0–1) that a penetrating hit incapacitates one crew member.")]
    [Range(0f, 1f)]
    public float crewHitProbability = 0.15f;

    // ── Modules ───────────────────────────────────────────────────────────────
    [Header("Modules")]
    [Tooltip("Add one entry per critical internal module.")]
    public List<TankModule> modules = new List<TankModule>();

    // ── APHE ──────────────────────────────────────────────────────────────────
    [Header("APHE Post-Penetration Explosion")]
    [Tooltip("Damage multiplier applied on top of basePenDamage when an APHE shell penetrates.\n" +
             "APHE detonates inside the tank, creating massive fragmentation.")]
    [Range(1f, 10f)]
    public float apheExplosionMultiplier = 2.5f;

    // ── HE blast ──────────────────────────────────────────────────────────────
    [Header("HE Blast Damage (non-penetrating)")]
    [Tooltip("Damage dealt by HE overpressure against very thin armor (open-top vehicles, etc.).\n" +
             "Only applied when the HE shell itself hits this tank (via TakeHEDamage).")]
    public float heBlastDamageBase = 50f;

    [Tooltip("Armor thickness in mm below which HE blast fully affects the crew.\n" +
             "Damage scales linearly to zero as armor increases toward this value.")]
    public float heEffectiveArmorThresholdMM = 30f;

    // ── Audio / VFX Feedback ──────────────────────────────────────────────────
    [Header("Feedback")]
    [Tooltip("VFX spawned at the hit point on ricochet or bounce.")]
    public GameObject ricochetVFXPrefab;

    [Tooltip("VFX spawned at the hit point on a penetrating hit.")]
    public GameObject penetrationSparkVFXPrefab;

    [Tooltip("Played when the tank is destroyed (assign AudioSource to this GO or a child).")]
    public AudioClip penetrationSound;

    [Tooltip("Played on a ricochet / blocked hit.")]
    public AudioClip ricochetSound;

    // ── Unity Events ──────────────────────────────────────────────────────────
    [Header("Events")]
    [Tooltip("Fired when any shell penetrates. Wire up internal spark VFX, UI flash, etc.")]
    public UnityEvent onPenetrated;

    [Tooltip("Fired with the module name string whenever a module is destroyed.")]
    public UnityEvent<string> onModuleDestroyed;

    [Tooltip("Fired with remaining crew count when a crew member is incapacitated.")]
    public UnityEvent<int> onCrewIncapacitated;

    [Tooltip("Fired when the tank is knocked out (health ≤ 0 or all crew incapacitated).")]
    public UnityEvent onTankDestroyed;

    // ─────────────────────────────────────────────────────────────────────────
    //  Runtime state
    // ─────────────────────────────────────────────────────────────────────────

    private float       currentHealth;
    private int         activeCrew;
    private bool        isKnockedOut = false;
    private AudioSource audioSrc;

    // ── Public read-only accessors ────────────────────────────────────────────
    public float CurrentHealth   => currentHealth;
    public float HealthFraction  => currentHealth / Mathf.Max(1f, maxHealth);
    public bool  IsKnockedOut    => isKnockedOut;
    public int   ActiveCrew      => activeCrew;

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        currentHealth = maxHealth;
        activeCrew    = crewCount;

        foreach (TankModule mod in modules)
        {
            mod.currentHealth = mod.maxHealth;
            mod.isDestroyed   = false;
            mod.isDisabled    = false;
        }

        audioSrc = GetComponent<AudioSource>();
        if (audioSrc == null)
            audioSrc = gameObject.AddComponent<AudioSource>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API — called by CannonProjectile
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Main entry point. Called by CannonProjectile after the penetration
    /// calculation completes. Handles all outcomes: penetrations, ricochets,
    /// HEAT defeats, APHE explosions, crew/module damage, and tank destruction.
    /// </summary>
    /// <param name="result">The PenetrationResult from PenetrationCalculator.Calculate().</param>
    /// <param name="shellType">Shell type for APHE and HE special handling.</param>
    /// <param name="hitPoint">World-space point of impact, used for VFX placement.</param>
    public void ApplyPenetrationResult(PenetrationResult result, ShellType shellType,
                                        Vector3 hitPoint)
    {
        if (isKnockedOut) return;

        // ── Non-penetrating outcomes ──────────────────────────────────────────
        if (result.ricocheted || result.heatDefeatedBySpaced || !result.penetrated)
        {
            SpawnVFX(ricochetVFXPrefab, hitPoint, Vector3.up);
            PlayAudio(ricochetSound);

            if (result.ricocheted)
                Debug.Log($"[TankHealth] {name} — RICOCHET at {result.impactAngleDeg:F1}°");
            else if (result.heatDefeatedBySpaced)
                Debug.Log($"[TankHealth] {name} — HEAT defeated by spaced armor");
            else
                Debug.Log($"[TankHealth] {name} — BLOCKED  " +
                          $"({result.shellPenetrationMM:F0}mm pen vs " +
                          $"{result.effectiveArmorMM:F0}mm eff. armor)");
            return;
        }

        // ── Penetrating hit ───────────────────────────────────────────────────
        onPenetrated?.Invoke();
        SpawnVFX(penetrationSparkVFXPrefab, hitPoint, Vector3.up);
        PlayAudio(penetrationSound);

        Debug.Log($"[TankHealth] {name} — PENETRATED  " +
                  $"({result.shellPenetrationMM:F0}mm pen vs " +
                  $"{result.effectiveArmorMM:F0}mm eff. armor, " +
                  $"{result.excessPenetrationMM:F0}mm excess)");

        // Calculate final hull damage
        float damage = basePenDamage * result.postPenDamageMultiplier;

        if (shellType == ShellType.APHE)
            damage *= apheExplosionMultiplier;

        ApplyHullDamage(damage, "penetrating hit");

        // ── Crew check ────────────────────────────────────────────────────────
        if (Random.value < crewHitProbability && activeCrew > 0)
        {
            activeCrew--;
            Debug.Log($"[TankHealth] {name} — Crew incapacitated. " +
                      $"Remaining: {activeCrew}/{crewCount}");

            onCrewIncapacitated?.Invoke(activeCrew);

            if (activeCrew <= 0)
            {
                KnockOut("all crew incapacitated");
                return;
            }
        }

        // ── Module checks ─────────────────────────────────────────────────────
        foreach (TankModule mod in modules)
        {
            if (mod.isDestroyed) continue;

            if (Random.value < mod.hitProbability)
                DamageModule(mod, damage);
        }
    }

    /// <summary>
    /// Apply direct HE blast damage. Call this when an HE shell lands near or on
    /// thin-armored sections of the tank. Damage scales with how thin the armor is
    /// relative to heEffectiveArmorThresholdMM.
    /// </summary>
    /// <param name="baseDamage">Raw blast damage at ground zero.</param>
    /// <param name="armorAtHitPoint">Nominal armor thickness at the hit zone in mm.</param>
    public void TakeHEDamage(float baseDamage, float armorAtHitPoint)
    {
        if (isKnockedOut) return;

        float penetrationFraction = 1f - Mathf.Clamp01(
            armorAtHitPoint / Mathf.Max(1f, heEffectiveArmorThresholdMM));

        float damage = baseDamage * penetrationFraction;

        if (damage > 0f)
            ApplyHullDamage(damage, "HE blast");
    }

    /// <summary>
    /// Legacy damage entry point. Compatible with SendMessage("TakeDamage", amount)
    /// from older scripts (e.g. environmental hazards, non-armored objects).
    /// </summary>
    public void TakeDamage(float damage)
    {
        if (isKnockedOut) return;
        ApplyHullDamage(damage, "legacy TakeDamage");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Internal helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void ApplyHullDamage(float damage, string source)
    {
        if (isKnockedOut || damage <= 0f) return;

        currentHealth = Mathf.Max(0f, currentHealth - damage);

        Debug.Log($"[TankHealth] {name} — {damage:F0} damage ({source}). " +
                  $"HP: {currentHealth:F0}/{maxHealth:F0}");

        if (currentHealth <= 0f)
            KnockOut("hull destroyed");
    }

    private void DamageModule(TankModule mod, float incomingDamage)
    {
        float moduleDamage = incomingDamage * mod.hullDamageOnHit;
        mod.currentHealth -= moduleDamage;

        Debug.Log($"[TankHealth] {name} — Module '{mod.moduleName}' hit " +
                  $"for {moduleDamage:F0}. HP: {mod.currentHealth:F0}/{mod.maxHealth:F0}");

        if (mod.currentHealth <= 0f && !mod.isDestroyed)
        {
            mod.isDestroyed = true;
            mod.isDisabled  = true;

            Debug.Log($"[TankHealth] {name} — Module '{mod.moduleName}' DESTROYED!");
            onModuleDestroyed?.Invoke(mod.moduleName);

            if (mod.catastrophicOnDestruction)
                KnockOut($"'{mod.moduleName}' catastrophic destruction");
        }
    }

    private void KnockOut(string reason)
    {
        if (isKnockedOut) return;
        isKnockedOut  = true;
        currentHealth = 0f;

        Debug.Log($"[TankHealth] {name} KNOCKED OUT ({reason})!");
        onTankDestroyed?.Invoke();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Audio / VFX utilities
    // ─────────────────────────────────────────────────────────────────────────

    private void SpawnVFX(GameObject prefab, Vector3 position, Vector3 normal)
    {
        if (prefab == null) return;
        GameObject fx = Instantiate(prefab, position, Quaternion.LookRotation(normal));
        Destroy(fx, 5f);
    }

    private void PlayAudio(AudioClip clip)
    {
        if (audioSrc == null || clip == null) return;
        audioSrc.PlayOneShot(clip);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public module queries
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Returns true if the named module has been destroyed.</summary>
    public bool IsModuleDestroyed(string name)
    {
        foreach (var mod in modules)
            if (mod.moduleName == name) return mod.isDestroyed;
        return false;
    }

    /// <summary>Returns the health fraction (0–1) of the named module.</summary>
    public float GetModuleHealthFraction(string name)
    {
        foreach (var mod in modules)
            if (mod.moduleName == name)
                return mod.currentHealth / Mathf.Max(1f, mod.maxHealth);
        return 1f;
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
            string.Format("HP: {0:F0}/{1:F0}  |  Crew: {2}/{3}{4}",
                          currentHealth, maxHealth,
                          activeCrew, crewCount,
                          isKnockedOut ? "  [KNOCKED OUT]" : ""));
    }
#endif
}
