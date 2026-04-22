using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

// =============================================================================
//  ModuleManager.cs  -  Unity 6
//
//  Central coordinator for all TankModule and CrewMember components on a tank.
//
//  Attach to the SAME root GameObject as TankController and TankHealth.
//  TankModule and CrewMember components anywhere in the hierarchy register
//  themselves here automatically at Awake via GetComponentsInChildren.
//
//  What ModuleManager does
//  ───────────────────────
//  1. Aggregates module/crew penalties into tank-wide stats that TankController
//     and CannonController consume each frame.
//
//  2. Routes incoming penetrating hits through a priority-based damage
//     distribution system (spall, APHE internal blast, HE overpressure).
//
//  3. Enforces interaction rules between modules (engine + tracks compound
//     failure; commander + gunner synergy; fire propagation).
//
//  4. Fires Unity Events on key events (module destroyed, crew lost, KO).
//
//  Consuming aggregate stats (examples)
//  ─────────────────────────────────────
//    TankController:   motorForce *= moduleManager.GetMobilityMultiplier();
//    CannonController: reloadTime  *= moduleManager.GetReloadMultiplier();
//                      if (!moduleManager.CanFire()) return;
//                      spread     *= moduleManager.GetAccuracyMultiplier();
// =============================================================================

// ── Round damage rules (designer-facing) ─────────────────────────────────────

[System.Serializable]
public class RoundDamageRules
{
    [Header("Spall Distribution")]
    [Tooltip("Number of additional modules hit by spall after an AP/APHE/APDS penetration.\n" +
             "Higher = more fragmentation damage spread through the interior.")]
    [Range(0, 8)]
    public int apSpallModuleCount = 3;

    [Tooltip("Spall count for APFSDS — long-rod penetrator creates a narrow jet,\n" +
             "damaging fewer modules than a blunt AP round.")]
    [Range(0, 4)]
    public int apfsdsSpallModuleCount = 1;

    [Tooltip("Spall count for HEAT jet penetration.")]
    [Range(0, 6)]
    public int heatSpallModuleCount = 2;

    [Tooltip("Fraction of the direct-hit damage applied to each spall-struck module.")]
    [Range(0f, 1f)]
    public float spallDamageFractionPerModule = 0.30f;

    [Tooltip("Fraction of the direct-hit damage applied to the crew member assigned\n" +
             "to each spall-struck module.")]
    [Range(0f, 1f)]
    public float spallCrewDamageFraction = 0.20f;

    [Header("HE Overpressure  (non-penetrating HE against thin armor)")]
    [Tooltip("Fraction of the HE blast damage applied to EVERY module when overpressure\n" +
             "enters the tank (e.g. open-top vehicle, very thin roof armor).")]
    [Range(0f, 1f)]
    public float heOverpressureModuleFraction = 0.35f;

    [Tooltip("Fraction of the HE blast damage applied to EVERY crew member.")]
    [Range(0f, 1f)]
    public float heOverpressureCrewFraction = 0.55f;

    [Header("APHE Internal Blast")]
    [Tooltip("Fraction of total APHE hit damage applied to each module in the interior.")]
    [Range(0f, 1f)]
    public float apheInternalModuleFraction = 0.45f;

    [Tooltip("Fraction of APHE blast damage applied to each crew member.")]
    [Range(0f, 1f)]
    public float apheInternalCrewFraction = 0.75f;

    [Header("Fire")]
    [Tooltip("Damage per tick applied to the hull when a module is on fire.")]
    [Range(1f, 100f)]
    public float fireTickDamage = 18f;

    [Tooltip("Seconds between fire damage ticks.")]
    [Range(0.5f, 5f)]
    public float fireTickInterval = 2f;

    [Tooltip("Duration (seconds) before a fire is extinguished or causes tank knock-out.")]
    [Range(5f, 60f)]
    public float fireDuration = 25f;

    [Tooltip("Fraction of fire tick damage spread to adjacent non-burning modules.")]
    [Range(0f, 1f)]
    public float fireSpreadFraction = 0.25f;

    [Header("Spall Priority Order")]
    [Tooltip("Modules at the TOP of this list are hit first by spall.\n" +
             "Drag entries to reorder priority.")]
    public List<ModuleType> spallPriorityOrder = new List<ModuleType>
    {
        ModuleType.AmmoRack,
        ModuleType.FuelTank,
        ModuleType.Gunner,
        ModuleType.Loader,
        ModuleType.Engine,
        ModuleType.Driver,
        ModuleType.Commander,
        ModuleType.Radio,
        ModuleType.Tracks,
        ModuleType.Hull,
        ModuleType.Generic,
    };
}

// ── ModuleManager ─────────────────────────────────────────────────────────────

public class ModuleManager : MonoBehaviour
{
    // ── Designer settings ─────────────────────────────────────────────────────
    [Header("Round Damage Rules")]
    public RoundDamageRules damageRules = new RoundDamageRules();

    [Header("Cross-Module Interaction Rules")]
    [Tooltip("Additional mobility penalty multiplier when BOTH the Engine AND Tracks are\n" +
             "in a non-Operational state simultaneously. Simulates compounding failure.")]
    [Range(0f, 1f)]
    public float engineAndTracksCombinedPenalty = 0.50f;

    [Tooltip("Accuracy bonus multiplier applied when the Commander AND Gunner are both\n" +
             "Active. Divides the accuracy spread multiplier (> 1 = better accuracy).")]
    [Range(1f, 2f)]
    public float commanderGunnerSynergyBonus = 1.15f;

    [Header("Knock-Out Rules")]
    [Tooltip("Tank is knocked out when active (non-incapacitated) crew falls below this.")]
    [Range(1, 6)]
    public int minimumActiveCrew = 1;

    [Header("Hit Notification UI")]
    [Tooltip("Tick ON for the player's own tank.\n" +
             "When true, this tank's destructions will NOT appear in the hit notification HUD.\n" +
             "Leave false (default) on all enemy tanks.")]
    public bool isPlayerTank = false;

    // ── Unity Events ──────────────────────────────────────────────────────────
    [Header("Events")]
    [Tooltip("Fired when the tank is knocked out from any cause.")]
    public UnityEvent onTankKnockedOut;

    [Tooltip("Fired with the destroyed TankModule whenever a module is destroyed.")]
    public UnityEvent<TankModule> onModuleDestroyedEvent;

    [Tooltip("Fired with the crew member whenever their status changes.")]
    public UnityEvent<CrewMember> onCrewStatusChangedEvent;

    [Tooltip("Fired with the module that started a fire.")]
    public UnityEvent<TankModule> onModuleFireEvent;

    // ─────────────────────────────────────────────────────────────────────────
    //  Runtime state
    // ─────────────────────────────────────────────────────────────────────────

    private List<TankModule>  modules     = new List<TankModule>();
    private List<CrewMember>  crew        = new List<CrewMember>();
    private TankHealth        tankHealth;
    private bool              isKnockedOut = false;

    // ── Public read access ────────────────────────────────────────────────────
    public IReadOnlyList<TankModule> Modules      => modules;
    public IReadOnlyList<CrewMember> Crew         => crew;
    public bool                      IsKnockedOut => isKnockedOut;

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        modules.AddRange(GetComponentsInChildren<TankModule>());
        crew.AddRange(GetComponentsInChildren<CrewMember>());
        tankHealth = GetComponent<TankHealth>();

        Debug.Log($"[ModuleManager] {name}: registered {modules.Count} modules, " +
                  $"{crew.Count} crew.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Aggregate stat accessors  (called every frame by TankController /
    //  CannonController — results are cheap reads, no allocation)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Overall mobility multiplier (0 – 1).
    /// Aggregates Engine, Driver, and Tracks module penalties plus a compound
    /// interaction penalty when both Engine and Tracks are non-Operational.
    /// </summary>
    public float GetMobilityMultiplier()
    {
        if (isKnockedOut) return 0f;

        float mult = 1f;

        foreach (TankModule m in modules)
        {
            if (m.moduleType is ModuleType.Engine or
                                ModuleType.Driver  or
                                ModuleType.Tracks)
                mult *= m.GetMobilityMultiplier();
        }

        // Compound failure: engine AND tracks both degraded
        TankModule engine = GetModule(ModuleType.Engine);
        TankModule tracks = GetModule(ModuleType.Tracks);
        if (engine != null && tracks != null &&
            engine.State != ModuleState.Operational &&
            tracks.State != ModuleState.Operational)
        {
            mult *= engineAndTracksCombinedPenalty;
        }

        // Driver crew effectiveness
        CrewMember driver = GetCrew(CrewRole.Driver);
        if (driver != null && !driver.IsActive)
            mult *= Mathf.Lerp(0.3f, 1f, driver.GetRoleEffectiveness());

        return Mathf.Clamp01(mult);
    }

    /// <summary>
    /// Reload time multiplier (≥ 1; 99 = cannot reload).
    /// Aggregates Loader module state and Loader crew effectiveness.
    /// </summary>
    public float GetReloadMultiplier()
    {
        if (isKnockedOut) return 99f;

        float moduleMult = 1f;
        float crewMult   = 1f;

        TankModule loaderModule = GetModule(ModuleType.Loader);
        if (loaderModule != null)
            moduleMult = loaderModule.GetReloadMultiplier();

        CrewMember loaderCrew = GetCrew(CrewRole.Loader);
        if (loaderCrew != null)
        {
            float eff = loaderCrew.GetRoleEffectiveness();
            crewMult  = eff > 0.01f ? Mathf.Lerp(5f, 1f, eff) : 99f;
            // Loader speed bonus (well-trained loader: < 1.0 multiplier)
            crewMult /= Mathf.Max(0.1f, loaderCrew.GetLoaderSpeedBonus());
        }

        // Worst of module or crew dictates reload time
        return Mathf.Max(moduleMult, crewMult);
    }

    /// <summary>
    /// Accuracy spread multiplier (≥ 1 = wider spread; 99 = cannot aim).
    /// Aggregates Gunner state, Gunner crew, and Commander synergy bonus.
    /// </summary>
    public float GetAccuracyMultiplier()
    {
        if (isKnockedOut) return 99f;

        float penaltyMult = 1f;

        // Gunner module
        TankModule gunnerMod = GetModule(ModuleType.Gunner);
        if (gunnerMod != null)
            penaltyMult = Mathf.Max(penaltyMult, gunnerMod.GetAccuracyMultiplier());

        // Gunner crew effectiveness
        CrewMember gunnerCrew = GetCrew(CrewRole.Gunner);
        if (gunnerCrew != null)
        {
            float eff = gunnerCrew.GetRoleEffectiveness();
            float crewPenalty = eff > 0.01f ? Mathf.Lerp(4f, 1f, eff) : 99f;
            penaltyMult = Mathf.Max(penaltyMult, crewPenalty);
        }

        // Commander accuracy bonus (divides the penalty → better accuracy)
        float cmdBonus = 1f;
        CrewMember cmdCrew = GetCrew(CrewRole.Commander);
        if (cmdCrew != null && !cmdCrew.IsIncapacitated)
        {
            // Commander module state also contributes
            TankModule cmdMod = GetModule(ModuleType.Commander);
            float modBonus  = cmdMod != null ? cmdMod.GetCommanderBonus() : 1f;
            float crewBonus = cmdCrew.GetCommanderAccuracyBonus();
            cmdBonus = modBonus * crewBonus;

            // Synergy: Commander + active Gunner
            CrewMember gc = GetCrew(CrewRole.Gunner);
            if (gc != null && gc.IsActive)
                cmdBonus *= commanderGunnerSynergyBonus;
        }

        return penaltyMult / Mathf.Max(0.01f, cmdBonus);
    }

    /// <summary>
    /// Commander view-range multiplier (> 1 = further spotting range).
    /// </summary>
    public float GetCommanderViewRangeMultiplier()
    {
        CrewMember cmd = GetCrew(CrewRole.Commander);
        if (cmd == null || cmd.IsIncapacitated) return 1f;
        return cmd.GetCommanderViewRangeBonus();
    }

    /// <summary>Returns true if the main gun can be fired right now.</summary>
    public bool CanFire()
    {
        if (isKnockedOut) return false;

        TankModule gunnerMod = GetModule(ModuleType.Gunner);
        if (gunnerMod != null && gunnerMod.IsDestroyed) return false;

        CrewMember gunnerCrew = GetCrew(CrewRole.Gunner);
        if (gunnerCrew != null && gunnerCrew.IsIncapacitated) return false;

        return true;
    }

    /// <summary>Returns true if the tank can move at all.</summary>
    public bool CanMove()
    {
        if (isKnockedOut) return false;

        TankModule engine = GetModule(ModuleType.Engine);
        TankModule tracks = GetModule(ModuleType.Tracks);
        TankModule driver = GetModule(ModuleType.Driver);

        if (engine != null && engine.IsDestroyed) return false;
        if (tracks != null && tracks.IsDestroyed) return false;
        if (driver != null && driver.IsDestroyed) return false;

        CrewMember driverCrew = GetCrew(CrewRole.Driver);
        if (driverCrew != null && driverCrew.IsIncapacitated) return false;

        return true;
    }

    /// <summary>Returns the number of non-incapacitated crew.</summary>
    public int GetActiveCrew()
    {
        int n = 0;
        foreach (var c in crew) if (!c.IsIncapacitated) n++;
        return n;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Damage routing  (called by CannonProjectile)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Apply a full penetrating hit from a shell, starting at the given directly-hit module.
    /// Handles spall distribution, APHE internal blast, and crew damage.
    /// Pass null for hitModule when the shell penetrated general hull armor
    /// (ModuleManager will pick the first priority module).
    /// </summary>
    public void ApplyPenetratingHit(PenetrationResult result, ShellType shellType,
                                     TankModule hitModule, Vector3 hitPoint)
    {
        if (isKnockedOut || !result.penetrated) return;

        float baseDamage = tankHealth != null
            ? tankHealth.basePenDamage * result.postPenDamageMultiplier
            : 80f * result.postPenDamageMultiplier;

        // ── Step 1: Direct module hit ─────────────────────────────────────────
        TankModule primaryHit = hitModule ?? GetPriorityModule();
        if (primaryHit != null)
            primaryHit.TakeDamage(baseDamage, shellType);

        // ── Step 2: Crew at the hit module ────────────────────────────────────
        CrewMember primaryCrew = GetCrewAtModule(primaryHit);
        if (primaryCrew != null)
            primaryCrew.TakeDamageFromShell(baseDamage, shellType);

        // ── Step 3: Spall distribution to other modules ───────────────────────
        int   spallCount = GetSpallCount(shellType);
        float spallDmg   = baseDamage * damageRules.spallDamageFractionPerModule;
        DistributeSpall(spallCount, spallDmg, shellType, primaryHit);

        // ── Step 4: APHE internal detonation ──────────────────────────────────
        if (shellType == ShellType.APHE)
            ApplyAPHEInternalBlast(baseDamage);

        // ── Step 5: Forward hull damage to TankHealth ─────────────────────────
        tankHealth?.ApplyInternalDamage(baseDamage, shellType);

        // ── Step 6: Knock-out check ───────────────────────────────────────────
        CheckKnockOut();
    }

    /// <summary>
    /// Apply HE overpressure to all modules and crew when a HE shell's blast
    /// breaches into the tank interior (thin roof, open-top vehicle, etc.).
    /// </summary>
    public void ApplyHEOverpressure(float blastDamage)
    {
        if (isKnockedOut) return;

        float modDmg  = blastDamage * damageRules.heOverpressureModuleFraction;
        float crewDmg = blastDamage * damageRules.heOverpressureCrewFraction;

        foreach (TankModule m in modules)
            if (!m.IsDestroyed) m.TakeDamage(modDmg, ShellType.HE);

        foreach (CrewMember c in crew)
            c.TakeDamage(crewDmg);

        tankHealth?.ApplyInternalDamage(blastDamage * 0.5f, ShellType.HE);
        CheckKnockOut();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Callbacks from TankModule and CrewMember
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Called by TankModule when it transitions to Destroyed.</summary>
    public void OnModuleDestroyed(TankModule mod)
    {
        Debug.Log($"[ModuleManager] {name} — Module '{mod.displayName}' ({mod.moduleType}) DESTROYED.");
        onModuleDestroyedEvent?.Invoke(mod);
        tankHealth?.OnModuleDestroyedCallback(mod.displayName);

        // Show HUD notification only for enemy tanks (isPlayerTank = false)
        if (!isPlayerTank)
            HitNotificationUI.Instance?.ShowModuleDestroyed(mod.displayName, mod.moduleType);

        if (mod.catastrophicOnDestruction)
            KnockOut($"catastrophic module: {mod.displayName}");
        else
            CheckKnockOut();
    }

    /// <summary>Called by TankModule on catastrophic hit or catastrophic destruction.</summary>
    public void OnCatastrophicModuleEvent(TankModule mod)
    {
        Debug.Log($"[ModuleManager] {name} — CATASTROPHIC event: {mod.displayName}!");
        KnockOut($"catastrophic event from {mod.displayName}");
    }

    /// <summary>Called by TankModule when it catches fire.</summary>
    public void OnModuleFire(TankModule mod)
    {
        Debug.Log($"[ModuleManager] {name} — Fire started in '{mod.displayName}'!");
        onModuleFireEvent?.Invoke(mod);
        StartCoroutine(FireDamageRoutine(mod));
    }

    /// <summary>Called by CrewMember when their status changes.</summary>
    public void OnCrewStatusChanged(CrewMember member)
    {
        onCrewStatusChangedEvent?.Invoke(member);

        // Show HUD notification only for enemy crew going KIA on enemy tanks
        if (!isPlayerTank && member.Status == CrewStatus.KIA)
            HitNotificationUI.Instance?.ShowCrewKIA(member.displayName, member.role);

        CheckKnockOut();
    }

    /// <summary>Force knock-out from external source (e.g. TankHealth hull destroyed).</summary>
    public void ForceKnockOut(string reason) => KnockOut(reason);

    // ─────────────────────────────────────────────────────────────────────────
    //  Module / crew query helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Returns the first TankModule of the given type, or null.</summary>
    public TankModule GetModule(ModuleType type)
    {
        foreach (var m in modules) if (m.moduleType == type) return m;
        return null;
    }

    /// <summary>Returns all TankModules of the given type.</summary>
    public List<TankModule> GetModules(ModuleType type)
    {
        var list = new List<TankModule>();
        foreach (var m in modules) if (m.moduleType == type) list.Add(m);
        return list;
    }

    /// <summary>Returns the first CrewMember with the given role, or null.</summary>
    public CrewMember GetCrew(CrewRole role)
    {
        foreach (var c in crew) if (c.role == role) return c;
        return null;
    }

    /// <summary>Returns all CrewMember with the given role.</summary>
    public List<CrewMember> GetCrewByRole(CrewRole role)
    {
        var list = new List<CrewMember>();
        foreach (var c in crew) if (c.role == role) list.Add(c);
        return list;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void DistributeSpall(int count, float damage, ShellType shell, TankModule exclude)
    {
        // Build priority-ordered candidate list
        var candidates = new List<TankModule>();
        foreach (ModuleType priority in damageRules.spallPriorityOrder)
        {
            foreach (TankModule m in modules)
                if (m != exclude && m.moduleType == priority && !m.IsDestroyed)
                    candidates.Add(m);
        }
        // Append any remaining modules not in the priority list
        foreach (TankModule m in modules)
            if (!candidates.Contains(m) && m != exclude && !m.IsDestroyed)
                candidates.Add(m);

        for (int i = 0; i < count && i < candidates.Count; i++)
        {
            candidates[i].TakeDamage(damage, shell);

            // Crew at this module takes spall too
            CrewMember c = GetCrewAtModule(candidates[i]);
            if (c != null)
                c.TakeDamageFromShell(
                    damage * damageRules.spallCrewDamageFraction, shell);
        }
    }

    private void ApplyAPHEInternalBlast(float baseDamage)
    {
        float modDmg  = baseDamage * damageRules.apheInternalModuleFraction;
        float crewDmg = baseDamage * damageRules.apheInternalCrewFraction;

        foreach (TankModule m in modules)
            if (!m.IsDestroyed) m.TakeDamage(modDmg, ShellType.APHE);

        foreach (CrewMember c in crew)
            c.TakeDamageFromShell(crewDmg, ShellType.APHE);
    }

    private IEnumerator FireDamageRoutine(TankModule originModule)
    {
        float elapsed = 0f;

        while (elapsed < damageRules.fireDuration && !isKnockedOut)
        {
            yield return new WaitForSeconds(damageRules.fireTickInterval);
            elapsed += damageRules.fireTickInterval;

            tankHealth?.TakeDamage(damageRules.fireTickDamage);

            // Fire spreads to other modules
            foreach (TankModule m in modules)
            {
                if (m == originModule || m.IsDestroyed) continue;
                m.TakeDamage(damageRules.fireTickDamage * damageRules.fireSpreadFraction,
                             ShellType.HE);
            }

            // Crew take progressive heat damage
            foreach (CrewMember c in crew)
                c.TakeDamage(damageRules.fireTickDamage * 0.15f);

            CheckKnockOut();
        }
    }

    private void CheckKnockOut()
    {
        if (isKnockedOut) return;

        // Hull health exhausted
        if (tankHealth != null && tankHealth.IsKnockedOut)
        {
            KnockOut("hull health depleted");
            return;
        }

        // Active crew below threshold
        if (GetActiveCrew() < minimumActiveCrew)
        {
            KnockOut($"active crew below minimum ({minimumActiveCrew})");
            return;
        }
    }

    private void KnockOut(string reason)
    {
        if (isKnockedOut) return;
        isKnockedOut = true;
        Debug.Log($"[ModuleManager] {name} KNOCKED OUT — {reason}");
        onTankKnockedOut?.Invoke();
        tankHealth?.ForceKnockOut(reason);
    }

    private TankModule GetPriorityModule()
    {
        // Returns the first non-destroyed module in priority order
        foreach (ModuleType type in damageRules.spallPriorityOrder)
        {
            TankModule m = GetModule(type);
            if (m != null && !m.IsDestroyed) return m;
        }
        return modules.Count > 0 ? modules[0] : null;
    }

    private CrewMember GetCrewAtModule(TankModule mod)
    {
        if (mod == null) return null;
        foreach (var c in crew)
            if (c.assignedModule == mod) return c;
        return null;
    }

    private int GetSpallCount(ShellType shell) => shell switch
    {
        ShellType.APFSDS => damageRules.apfsdsSpallModuleCount,
        ShellType.HEAT   => damageRules.heatSpallModuleCount,
        _                => damageRules.apSpallModuleCount,
    };

    // ─────────────────────────────────────────────────────────────────────────
    //  Editor gizmos
    // ─────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;

        string info =
            $"[ModuleManager] {name}\n" +
            $"Mobility:  {GetMobilityMultiplier() * 100f:F0} %\n" +
            $"Reload:    {GetReloadMultiplier():F1}×\n" +
            $"Accuracy:  {GetAccuracyMultiplier():F1}× spread\n" +
            $"CanFire:   {CanFire()}\n" +
            $"CanMove:   {CanMove()}\n" +
            $"Crew:      {GetActiveCrew()}/{crew.Count} active\n" +
            $"KO:        {isKnockedOut}";

        UnityEditor.Handles.Label(
            transform.position + Vector3.up * 3.5f,
            info);
    }
#endif
}
