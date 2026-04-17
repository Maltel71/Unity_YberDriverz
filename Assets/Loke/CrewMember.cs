using UnityEngine;
using UnityEngine.Events;

// =============================================================================
//  CrewMember.cs  -  Unity 6
//
//  Represents a single crew member inside the tank.
//
//  Attach to a child GameObject of the tank (one per crew member).
//  ModuleManager auto-discovers all CrewMember components in the hierarchy.
//
//  Crew roles
//  ──────────
//    Driver     - controls mobility; incapacitation immobilises the tank.
//    Commander  - provides spotting / accuracy synergy with the Gunner.
//    Loader     - loads the main gun; incapacitation slows/halts reloading.
//    Gunner     - operates the main gun; incapacitation prevents firing.
//    Radioman   - communications bonus; loss reduces crew coordination.
//
//  Status progression
//  ──────────────────
//    Active → Injured → Incapacitated → KIA
//
//  Role effectiveness
//  ──────────────────
//  GetRoleEffectiveness() returns 1.0 (Active), 0.6 (Injured), or 0 (Incap/KIA).
//  ModuleManager uses this to scale reload speed, accuracy, and mobility.
//
//  Commander proximity bonus
//  ─────────────────────────
//  GetCommanderAccuracyBonus() is > 1 when this member is a Commander and Active.
//  ModuleManager further multiplies this by a synergy factor when the Gunner
//  is also active.
//
//  Movement speed modifier
//  ───────────────────────
//  GetMovementSpeedMultiplier() is intended for on-foot crew movement (future).
//  Injured crew move at injuredMovementMultiplier of normal speed.
// =============================================================================

public enum CrewRole
{
    Driver,     // Vehicle movement
    Commander,  // Spotting + accuracy synergy
    Loader,     // Main gun reload
    Gunner,     // Main gun aiming and firing
    Radioman,   // Communications / crew coordination
}

public enum CrewStatus
{
    Active,         // Performing duties at full effectiveness.
    Injured,        // Performing duties at reduced effectiveness.
    Incapacitated,  // Unable to perform duties; still alive.
    KIA,            // Killed in action.
}

public class CrewMember : MonoBehaviour
{
    // ── Identity ──────────────────────────────────────────────────────────────
    [Header("Identity")]
    public CrewRole role        = CrewRole.Driver;
    public string   displayName = "Crew";

    // ── Health ────────────────────────────────────────────────────────────────
    [Header("Health")]
    [Range(10f, 200f)]
    public float maxHealth = 100f;

    [Tooltip("Health fraction below which this crew member becomes Injured.\n" +
             "e.g. 0.65 = Injured when HP drops below 65 %.")]
    [Range(0.3f, 0.9f)]
    public float injuredThreshold = 0.65f;

    [Tooltip("Health fraction below which this crew member becomes Incapacitated.\n" +
             "Must be less than injuredThreshold.")]
    [Range(0.01f, 0.29f)]
    public float incapacitatedThreshold = 0.10f;

    // ── Assignment ────────────────────────────────────────────────────────────
    [Header("Module Assignment")]
    [Tooltip("The TankModule this crew member primarily operates.\n" +
             "ModuleManager uses this to route spall damage to the crew member\n" +
             "when their module is directly hit.")]
    public TankModule assignedModule;

    // ── Role-specific bonuses ─────────────────────────────────────────────────
    [Header("Role-Specific Bonuses")]
    [Tooltip("Accuracy improvement multiplier applied by the Commander when Active.\n" +
             "> 1 = better accuracy (tighter spread).\n" +
             "ModuleManager multiplies this by its commanderGunnerSynergyBonus when\n" +
             "the Gunner is also active.")]
    [Range(1f, 2f)]
    public float commanderAccuracyBonus = 1.20f;

    [Tooltip("Spotting / view range multiplier when this Commander is Active.")]
    [Range(1f, 2f)]
    public float commanderViewRangeBonus = 1.15f;

    [Tooltip("Reload speed bonus multiplier when this Loader is Active.\n" +
             "> 1 = faster reload (divides the reload time multiplier).")]
    [Range(1f, 1.5f)]
    public float loaderSpeedBonus = 1.0f;

    // ── Movement penalties (on-foot / future first-person) ────────────────────
    [Header("Movement Speed (on-foot)")]
    [Tooltip("Speed multiplier when this crew member is Injured.")]
    [Range(0.1f, 1f)]
    public float injuredMovementMultiplier = 0.50f;

    [Tooltip("Speed multiplier when Incapacitated (crawling / dragging).")]
    [Range(0f, 0.2f)]
    public float incapacitatedMovementMultiplier = 0.10f;

    // ── Damage taken from shell types (incoming hull damage fraction) ─────────
    [Header("Incoming Damage Fractions")]
    [Tooltip("Fraction of a penetrating hit's damage applied directly to this crew member.\n" +
             "Spall from different shell types varies — APHE and HE are most lethal to crew.")]
    [Range(0f, 2f)] public float apCrewDamageFraction     = 0.50f;
    [Range(0f, 2f)] public float apheCrewDamageFraction   = 1.00f;
    [Range(0f, 2f)] public float apfsdsCrewDamageFraction = 0.40f;
    [Range(0f, 2f)] public float heatCrewDamageFraction   = 0.60f;
    [Range(0f, 2f)] public float heCrewDamageFraction     = 0.80f;
    [Range(0f, 2f)] public float heshCrewDamageFraction   = 0.70f;

    // ── Events ────────────────────────────────────────────────────────────────
    [Header("Events")]
    public UnityEvent             onInjured;
    public UnityEvent             onIncapacitated;
    public UnityEvent             onKIA;
    public UnityEvent<CrewStatus> onStatusChanged;

    // ─────────────────────────────────────────────────────────────────────────
    //  Runtime state
    // ─────────────────────────────────────────────────────────────────────────

    private float      currentHealth;
    private CrewStatus status = CrewStatus.Active;
    private ModuleManager manager;

    public float      CurrentHealth   => currentHealth;
    public float      HealthFraction  => currentHealth / Mathf.Max(1f, maxHealth);
    public CrewStatus Status          => status;
    public bool       IsActive        => status == CrewStatus.Active;
    public bool       IsInjured       => status == CrewStatus.Injured;
    public bool       IsIncapacitated => status is CrewStatus.Incapacitated or CrewStatus.KIA;

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        currentHealth = maxHealth;
        manager       = GetComponentInParent<ModuleManager>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Apply damage to this crew member (flat, from spall or blast).</summary>
    public void TakeDamage(float damage)
    {
        if (status == CrewStatus.KIA || damage <= 0f) return;

        currentHealth = Mathf.Max(0f, currentHealth - damage);
        Debug.Log($"[CrewMember] {displayName} ({role}) — {damage:F0} dmg. " +
                  $"HP: {currentHealth:F0}/{maxHealth:F0}");
        UpdateStatus();
    }

    /// <summary>
    /// Apply damage from a specific shell type, scaled by the role's incoming damage fraction.
    /// </summary>
    public void TakeDamageFromShell(float baseDamage, ShellType shellType)
    {
        TakeDamage(baseDamage * GetShellDamageFraction(shellType));
    }

    // ── Role effectiveness ────────────────────────────────────────────────────

    /// <summary>
    /// Returns how effectively this crew member is performing their assigned role.
    /// 1.0 = fully effective. 0.6 = injured. 0.0 = incapacitated/KIA.
    /// </summary>
    public float GetRoleEffectiveness() => status switch
    {
        CrewStatus.Active        => 1.0f,
        CrewStatus.Injured       => 0.6f,
        CrewStatus.Incapacitated => 0.0f,
        CrewStatus.KIA           => 0.0f,
        _                        => 1.0f,
    };

    // ── Movement (on-foot) ────────────────────────────────────────────────────

    /// <summary>
    /// Movement speed multiplier for this crew member when on foot.
    /// Intended for future first-person / crew-management gameplay.
    /// </summary>
    public float GetMovementSpeedMultiplier() => status switch
    {
        CrewStatus.Active        => 1.0f,
        CrewStatus.Injured       => injuredMovementMultiplier,
        CrewStatus.Incapacitated => incapacitatedMovementMultiplier,
        CrewStatus.KIA           => 0.0f,
        _                        => 1.0f,
    };

    // ── Role-specific bonuses ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the accuracy bonus provided by this Commander (>1 = better).
    /// Returns 1 if not Commander or if incapacitated.
    /// </summary>
    public float GetCommanderAccuracyBonus()
    {
        if (role != CrewRole.Commander || IsIncapacitated) return 1f;
        return Mathf.Lerp(1f, commanderAccuracyBonus, GetRoleEffectiveness());
    }

    /// <summary>
    /// Returns the view range bonus provided by this Commander (>1 = longer range).
    /// </summary>
    public float GetCommanderViewRangeBonus()
    {
        if (role != CrewRole.Commander || IsIncapacitated) return 1f;
        return Mathf.Lerp(1f, commanderViewRangeBonus, GetRoleEffectiveness());
    }

    /// <summary>
    /// Returns the loader speed bonus for this Loader crew member (>1 = faster reload).
    /// </summary>
    public float GetLoaderSpeedBonus()
    {
        if (role != CrewRole.Loader || IsIncapacitated) return 1f;
        return Mathf.Lerp(1f, loaderSpeedBonus, GetRoleEffectiveness());
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateStatus()
    {
        CrewStatus newStatus;

        if (currentHealth <= 0f)
            newStatus = CrewStatus.KIA;
        else if (HealthFraction <= incapacitatedThreshold)
            newStatus = CrewStatus.Incapacitated;
        else if (HealthFraction <= injuredThreshold)
            newStatus = CrewStatus.Injured;
        else
            newStatus = CrewStatus.Active;

        if (newStatus == status) return;

        status = newStatus;
        onStatusChanged?.Invoke(status);

        switch (status)
        {
            case CrewStatus.Injured:
                Debug.Log($"[CrewMember] {displayName} ({role}) → INJURED");
                onInjured?.Invoke();
                break;
            case CrewStatus.Incapacitated:
                Debug.Log($"[CrewMember] {displayName} ({role}) → INCAPACITATED");
                onIncapacitated?.Invoke();
                break;
            case CrewStatus.KIA:
                Debug.Log($"[CrewMember] {displayName} ({role}) → KIA");
                onKIA?.Invoke();
                break;
        }

        manager?.OnCrewStatusChanged(this);
    }

    private float GetShellDamageFraction(ShellType type) => type switch
    {
        ShellType.AP     => apCrewDamageFraction,
        ShellType.APHE   => apheCrewDamageFraction,
        ShellType.APDS   => apCrewDamageFraction,
        ShellType.APFSDS => apfsdsCrewDamageFraction,
        ShellType.HEAT   => heatCrewDamageFraction,
        ShellType.HESH   => heshCrewDamageFraction,
        ShellType.HE     => heCrewDamageFraction,
        _                => 0.5f,
    };

    // ─────────────────────────────────────────────────────────────────────────
    //  Editor gizmos
    // ─────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        float frac   = Application.isPlaying ? HealthFraction : 1f;
        Gizmos.color = Color.Lerp(Color.red, Color.cyan, frac);
        Gizmos.DrawWireSphere(transform.position, 0.18f);

        string label = Application.isPlaying
            ? $"{displayName}  [{role}]\n{currentHealth:F0}/{maxHealth:F0}  [{status}]"
            : $"{displayName}  [{role}]";

        UnityEditor.Handles.Label(transform.position + Vector3.up * 0.3f, label);
    }
#endif
}
