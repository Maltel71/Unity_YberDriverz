using UnityEngine;
using UnityEngine.Events;

// =============================================================================
//  TankModule.cs  -  Unity 6
//
//  Component representing a single discrete vehicle module: Engine, Driver,
//  Gunner, Loader, Commander, Ammo Rack, Fuel Tank, Tracks, etc.
//
//  Attach to a child GameObject of the tank. Give it a Collider so shells
//  can hit it directly. Optionally add an ArmorPlate if the module has its
//  own internal armor screen (e.g. an armoured engine firewall).
//
//  ModuleManager auto-discovers all TankModule components in the hierarchy.
//
//  State machine
//  ─────────────
//    Operational  (healthFraction > damagedThreshold)
//    Damaged      (healthFraction between damagedThreshold and disabledThreshold)
//    Disabled     (healthFraction between disabledThreshold and 0)
//    Destroyed    (healthFraction == 0)
//
//  Penalties
//  ─────────
//  Each module type carries penalty values that ModuleManager aggregates:
//    Engine / Driver / Tracks → GetMobilityMultiplier()
//    Loader                   → GetReloadMultiplier()
//    Gunner                   → GetAccuracyMultiplier()
//    Commander                → GetCommanderBonus()
//
//  Defaults by module type are applied in Awake if useTypeDefaults is true.
// =============================================================================

public enum ModuleType
{
    Driver,       // Controls mobility. Penalty: reduced speed.
    Commander,    // Provides spotting and accuracy synergy bonus.
    Loader,       // Controls reload rate. Penalty: slower reload.
    Gunner,       // Controls firing. Penalty: reduced accuracy or inability to fire.
    Engine,       // Controls speed. Penalty: reduced top speed and acceleration.
    AmmoRack,     // Catastrophic explosion risk when hit.
    FuelTank,     // Fire risk when disabled.
    Tracks,       // Immobilises when destroyed.
    Radio,        // Communications (multiplayer). Penalty: no voice / minimap.
    Hull,         // General structural plate with no specific functional penalty.
    Generic,      // Custom module — configure penalties manually.
}

public enum ModuleState
{
    Operational,  // Full function, no penalties.
    Damaged,      // Reduced function — light penalties active.
    Disabled,     // Severe penalties — barely functional.
    Destroyed,    // No function. May trigger catastrophic events.
}

[RequireComponent(typeof(Collider))]
public class TankModule : MonoBehaviour
{
    // ── Identity ──────────────────────────────────────────────────────────────
    [Header("Identity")]
    [Tooltip("The functional role of this module in the tank.")]
    public ModuleType moduleType  = ModuleType.Generic;

    [Tooltip("Human-readable name shown in logs and UI.")]
    public string     displayName = "Module";

    [Tooltip("If true, Awake() pre-fills penalty and multiplier fields with\n" +
             "sensible defaults for the selected module type.\n" +
             "Disable to configure all values manually.")]
    public bool useTypeDefaults = true;

    // ── Health ────────────────────────────────────────────────────────────────
    [Header("Health")]
    [Range(10f, 500f)]
    public float maxHealth = 100f;

    [Tooltip("Health fraction at which the module transitions to Damaged.\n" +
             "e.g. 0.75 → Damaged when HP drops below 75 %.")]
    [Range(0.4f, 0.95f)]
    public float damagedThreshold  = 0.75f;

    [Tooltip("Health fraction at which the module transitions to Disabled.\n" +
             "Must be lower than damagedThreshold.")]
    [Range(0.05f, 0.45f)]
    public float disabledThreshold = 0.25f;

    // ── Mobility penalties (Engine, Driver, Tracks) ───────────────────────────
    [Header("Mobility Penalty  (Engine / Driver / Tracks)")]
    [Tooltip("Mobility multiplier when Damaged. 1 = no penalty, 0 = fully immobile.")]
    [Range(0f, 1f)]
    public float mobilityDamaged  = 0.70f;

    [Tooltip("Mobility multiplier when Disabled.")]
    [Range(0f, 1f)]
    public float mobilityDisabled = 0.30f;

    // ── Reload penalties (Loader) ─────────────────────────────────────────────
    [Header("Reload Penalty  (Loader)")]
    [Tooltip("Reload time multiplier when Damaged. > 1 = slower. e.g. 1.5 = 50 % slower.")]
    [Range(1f, 8f)]
    public float reloadDamaged  = 1.5f;

    [Tooltip("Reload time multiplier when Disabled.")]
    [Range(1f, 15f)]
    public float reloadDisabled = 3.0f;

    // ── Accuracy penalties (Gunner) ───────────────────────────────────────────
    [Header("Accuracy Penalty  (Gunner)")]
    [Tooltip("Accuracy spread multiplier when Damaged. > 1 = worse accuracy.")]
    [Range(1f, 8f)]
    public float accuracyDamaged  = 1.5f;

    [Tooltip("Accuracy spread multiplier when Disabled.")]
    [Range(1f, 15f)]
    public float accuracyDisabled = 4.0f;

    // ── Commander bonus (Commander) ───────────────────────────────────────────
    [Header("Commander Bonus  (Commander)")]
    [Tooltip("Spotting / accuracy bonus multiplier when Operational. > 1 = benefit.\n" +
             "Synergy with an active Gunner amplifies this further in ModuleManager.")]
    [Range(1f, 2f)]
    public float commanderBonus = 1.20f;

    // ── Incoming shell damage multipliers ─────────────────────────────────────
    [Header("Incoming Damage Multipliers by Shell Type")]
    [Tooltip("How much more (or less) damage each shell type does to THIS specific module.\n" +
             "1.0 = normal damage.  2.0 = double damage.  0.5 = half damage.")]
    [Range(0f, 5f)] public float apDamageMultiplier     = 1.0f;
    [Range(0f, 5f)] public float apheDamageMultiplier   = 1.5f;
    [Range(0f, 5f)] public float apfsdsDamageMultiplier = 1.0f;
    [Range(0f, 5f)] public float heatDamageMultiplier   = 1.2f;
    [Range(0f, 5f)] public float heDamageMultiplier     = 0.8f;
    [Range(0f, 5f)] public float heshDamageMultiplier   = 1.1f;

    // ── Secondary effects ─────────────────────────────────────────────────────
    [Header("Secondary Effects")]
    [Tooltip("If true, destroying this module immediately knocks out the entire tank.\n" +
             "Use for: AmmoRack detonation, combined Driver + Engine loss, etc.")]
    public bool catastrophicOnDestruction = false;

    [Tooltip("Probability (0–1) that any single hit on this module — regardless of damage\n" +
             "— triggers a catastrophic event. Ammo Rack: 0.05–0.15.")]
    [Range(0f, 1f)]
    public float catastrophicHitChance = 0f;

    [Tooltip("If true, this module starts a fire when it reaches Disabled state.\n" +
             "Wire onDisabled to fire-suppression logic or leave it to ModuleManager.")]
    public bool fireRiskOnDisabled = false;

    // ── Ricochet ──────────────────────────────────────────────────────────────
    [Header("Ricochet")]
    [Tooltip("Delta added to the shell's ricochet angle threshold when hitting this module\n" +
             "without an ArmorPlate present (exposed component).\n" +
             "+ve = harder to ricochet. -ve = easier.  0 = default shell rules.")]
    [Range(-20f, 20f)]
    public float ricochetAngleModifier = 0f;

    // ── Events ────────────────────────────────────────────────────────────────
    [Header("Events")]
    public UnityEvent onBecameDamaged;
    public UnityEvent onBecameDisabled;
    public UnityEvent onDestroyed;
    public UnityEvent onRepaired;

    // ─────────────────────────────────────────────────────────────────────────
    //  Runtime state
    // ─────────────────────────────────────────────────────────────────────────

    private float       currentHealth;
    private ModuleState state = ModuleState.Operational;
    private ModuleManager manager;

    public float       CurrentHealth  => currentHealth;
    public float       HealthFraction => currentHealth / Mathf.Max(1f, maxHealth);
    public ModuleState State          => state;
    public bool        IsOperational  => state == ModuleState.Operational;
    public bool        IsDamaged      => state == ModuleState.Damaged;
    public bool        IsDisabled     => state == ModuleState.Disabled;
    public bool        IsDestroyed    => state == ModuleState.Destroyed;

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        currentHealth = maxHealth;
        manager       = GetComponentInParent<ModuleManager>();

        if (useTypeDefaults)
            ApplyTypeDefaults();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Apply damage from a specific shell type (applies multiplier).</summary>
    public void TakeDamage(float rawDamage, ShellType shellType)
    {
        if (state == ModuleState.Destroyed) return;

        float damage = rawDamage * GetShellMultiplier(shellType);
        currentHealth = Mathf.Max(0f, currentHealth - damage);

        Debug.Log($"[TankModule] {displayName} — {damage:F0} dmg ({shellType}). " +
                  $"HP: {currentHealth:F0}/{maxHealth:F0}");

        // Catastrophic hit roll (e.g. ammo rack random detonation chance)
        if (catastrophicHitChance > 0f && Random.value < catastrophicHitChance)
        {
            Debug.Log($"[TankModule] {displayName} — catastrophic hit triggered!");
            manager?.OnCatastrophicModuleEvent(this);
            return; // tank will be knocked out; no need to update state further
        }

        UpdateState();
    }

    /// <summary>Apply flat damage with no shell-type multiplier.</summary>
    public void TakeDamage(float rawDamage) => TakeDamage(rawDamage, ShellType.AP);

    /// <summary>Repair this module by the given amount. Restores state transitions.</summary>
    public void Repair(float amount)
    {
        if (state == ModuleState.Destroyed) return;
        currentHealth = Mathf.Min(maxHealth, currentHealth + amount);
        UpdateState();
        onRepaired?.Invoke();
        Debug.Log($"[TankModule] {displayName} repaired by {amount:F0}. HP: {currentHealth:F0}");
    }

    // ── Penalty accessors (consumed by ModuleManager aggregate methods) ───────

    /// <summary>Mobility multiplier contribution from this module (0–1).</summary>
    public float GetMobilityMultiplier() => state switch
    {
        ModuleState.Damaged   => mobilityDamaged,
        ModuleState.Disabled  => mobilityDisabled,
        ModuleState.Destroyed => 0f,
        _                     => 1f,
    };

    /// <summary>Reload time multiplier from this module (≥1; 99 = cannot reload).</summary>
    public float GetReloadMultiplier() => state switch
    {
        ModuleState.Damaged   => reloadDamaged,
        ModuleState.Disabled  => reloadDisabled,
        ModuleState.Destroyed => 99f,
        _                     => 1f,
    };

    /// <summary>Accuracy spread multiplier from this module (≥1; 99 = cannot aim).</summary>
    public float GetAccuracyMultiplier() => state switch
    {
        ModuleState.Damaged   => accuracyDamaged,
        ModuleState.Disabled  => accuracyDisabled,
        ModuleState.Destroyed => 99f,
        _                     => 1f,
    };

    /// <summary>Commander bonus (>1 = accuracy / spotting benefit).</summary>
    public float GetCommanderBonus() => state switch
    {
        ModuleState.Operational => commanderBonus,
        ModuleState.Damaged     => Mathf.Lerp(1f, commanderBonus, 0.5f),
        _                       => 1f,
    };

    // ─────────────────────────────────────────────────────────────────────────
    //  State machine
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateState()
    {
        ModuleState newState;

        if (currentHealth <= 0f)
            newState = ModuleState.Destroyed;
        else if (HealthFraction <= disabledThreshold)
            newState = ModuleState.Disabled;
        else if (HealthFraction <= damagedThreshold)
            newState = ModuleState.Damaged;
        else
            newState = ModuleState.Operational;

        if (newState == state) return;

        ModuleState prev = state;
        state = newState;

        switch (newState)
        {
            case ModuleState.Damaged:
                Debug.Log($"[TankModule] {displayName} → DAMAGED");
                onBecameDamaged?.Invoke();
                break;

            case ModuleState.Disabled:
                if (prev == ModuleState.Operational) onBecameDamaged?.Invoke();
                Debug.Log($"[TankModule] {displayName} → DISABLED");
                onBecameDisabled?.Invoke();
                if (fireRiskOnDisabled) manager?.OnModuleFire(this);
                break;

            case ModuleState.Destroyed:
                Debug.Log($"[TankModule] {displayName} → DESTROYED");
                onDestroyed?.Invoke();
                manager?.OnModuleDestroyed(this);
                if (catastrophicOnDestruction) manager?.OnCatastrophicModuleEvent(this);
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Type defaults
    // ─────────────────────────────────────────────────────────────────────────

    private void ApplyTypeDefaults()
    {
        switch (moduleType)
        {
            case ModuleType.Engine:
                mobilityDamaged  = 0.60f; mobilityDisabled = 0.20f;
                apheDamageMultiplier = 1.8f;  // fuel ignites
                heDamageMultiplier   = 0.9f;
                fireRiskOnDisabled   = true;
                break;

            case ModuleType.Driver:
                mobilityDamaged  = 0.70f; mobilityDisabled = 0.30f;
                heDamageMultiplier = 1.3f;  // overpressure affects crew
                apheDamageMultiplier = 2.0f;
                break;

            case ModuleType.Tracks:
                mobilityDamaged  = 0.50f; mobilityDisabled = 0.10f;
                catastrophicOnDestruction = false;
                apDamageMultiplier    = 1.2f;
                heDamageMultiplier    = 0.7f;
                apfsdsDamageMultiplier = 1.0f;
                break;

            case ModuleType.Loader:
                reloadDamaged  = 1.8f; reloadDisabled = 3.5f;
                heDamageMultiplier   = 1.4f;
                apheDamageMultiplier = 2.2f;
                break;

            case ModuleType.Gunner:
                accuracyDamaged  = 2.0f; accuracyDisabled = 6.0f;
                heDamageMultiplier   = 1.4f;
                apheDamageMultiplier = 2.2f;
                break;

            case ModuleType.Commander:
                commanderBonus   = 1.25f;
                heDamageMultiplier   = 1.4f;
                apheDamageMultiplier = 2.2f;
                break;

            case ModuleType.AmmoRack:
                catastrophicOnDestruction = true;
                catastrophicHitChance     = 0.08f;   // 8 % chance per hit
                apheDamageMultiplier      = 3.0f;    // APHE detonates stored rounds
                heatDamageMultiplier      = 2.0f;
                apDamageMultiplier        = 1.0f;
                heDamageMultiplier        = 0.5f;    // HE rarely detonates ammo
                break;

            case ModuleType.FuelTank:
                catastrophicHitChance = 0.05f;
                fireRiskOnDisabled    = true;
                apheDamageMultiplier  = 2.5f;
                heatDamageMultiplier  = 1.8f;
                apDamageMultiplier    = 1.0f;
                heDamageMultiplier    = 0.8f;
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Shell multiplier lookup
    // ─────────────────────────────────────────────────────────────────────────

    private float GetShellMultiplier(ShellType type) => type switch
    {
        ShellType.AP     => apDamageMultiplier,
        ShellType.APHE   => apheDamageMultiplier,
        ShellType.APDS   => apDamageMultiplier,       // treat same as AP
        ShellType.APFSDS => apfsdsDamageMultiplier,
        ShellType.HEAT   => heatDamageMultiplier,
        ShellType.HESH   => heshDamageMultiplier,
        ShellType.HE     => heDamageMultiplier,
        _                => 1f,
    };

    // ─────────────────────────────────────────────────────────────────────────
    //  Editor gizmos
    // ─────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        float frac   = Application.isPlaying ? HealthFraction : 1f;
        Gizmos.color = Color.Lerp(Color.red, Color.green, frac);

        Collider col = GetComponent<Collider>();
        if (col != null) Gizmos.DrawWireCube(col.bounds.center, col.bounds.size * 1.04f);
        else             Gizmos.DrawWireSphere(transform.position, 0.3f);

        UnityEditor.Handles.Label(
            transform.position + Vector3.up * 0.45f,
            Application.isPlaying
                ? $"{displayName}  [{moduleType}]\n{currentHealth:F0}/{maxHealth:F0} HP  [{state}]"
                : $"{displayName}  [{moduleType}]");
    }
#endif
}
