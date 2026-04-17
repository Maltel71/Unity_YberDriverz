using UnityEngine;

// =============================================================================
//  ArmorPlate.cs  -  Unity 6
//
//  Attach this component to any Collider that should represent an armored
//  surface on a tank. PenetrationCalculator reads these values to determine
//  whether an incoming shell penetrates.
//
//  Setup:
//    1. Create child GameObjects on your tank for each armor zone
//       (e.g. "FrontUpperPlate", "FrontLowerPlate", "Side", "TurretFace").
//    2. Give each child a BoxCollider or MeshCollider sized to that zone.
//    3. Attach this script to each child and configure the values below.
//    4. Make sure these armor-zone colliders are on a layer that shells can hit.
//       (Put the hull's primary BoxCollider on a DIFFERENT layer so shells
//        only interact with the detailed armor-zone colliders, not both.)
//    5. Assign the TankHealth reference, or let it auto-find via GetComponentInParent.
//
//  Spaced armor:
//    Set isSpacedArmor = true and assign a spacedBackingPlate to model
//    composite / spaced configurations. HEAT shaped charges prematurely
//    detonate against the outer spaced plate and lose most of their
//    penetration before reaching the backing plate.
// =============================================================================

public enum ArmorMaterial
{
    RHA,         // Rolled Homogeneous Armor   - baseline              (multiplier 1.00)
    CHA,         // Cast Homogeneous Armor     - slightly weaker       (multiplier 0.90)
    Cast,        // Generic cast steel         - weaker than RHA       (multiplier 0.85)
    Composite,   // Composite / layered armor  - stronger than RHA     (multiplier varies)
    Spaced,      // Spaced outer plate         - thin but defeats HEAT (multiplier 0.80)
}

[RequireComponent(typeof(Collider))]
public class ArmorPlate : MonoBehaviour
{
    // ── Zone Identity ─────────────────────────────────────────────────────────
    [Header("Zone Identity")]
    [Tooltip("Human-readable name shown in penetration logs and debug gizmos.\n" +
             "Examples: 'Front Upper Plate', 'Glacis', 'Turret Face', 'Side Hull'.")]
    public string zoneName = "Unnamed Zone";

    // ── Armor Properties ──────────────────────────────────────────────────────
    [Header("Armor Properties")]
    [Tooltip("Physical thickness of this plate in millimetres.")]
    [Range(1f, 1000f)]
    public float thicknessMM = 80f;

    [Tooltip("Material type. Selects a default RHA-equivalent multiplier.\n" +
             "Override the multiplier with rhaMultiplierOverride if needed.")]
    public ArmorMaterial material = ArmorMaterial.RHA;

    [Tooltip("RHA-equivalent multiplier for this plate's material.\n\n" +
             "Defaults by material:\n" +
             "  RHA       = 1.00\n" +
             "  CHA       = 0.90\n" +
             "  Cast      = 0.85\n" +
             "  Composite = 1.50 (set higher for modern composites, e.g. 2.5-4.0)\n" +
             "  Spaced    = 0.80\n\n" +
             "Set to 0 to use the material default automatically.")]
    [Range(0f, 5f)]
    public float rhaMultiplierOverride = 0f;

    // ── Special Properties ────────────────────────────────────────────────────
    [Header("Spaced / Composite Configuration")]
    [Tooltip("Mark this as a spaced armor plate. HEAT shaped charges prematurely\n" +
             "detonate against spaced armor, drastically reducing penetration.\n" +
             "Kinetic rounds (AP, APDS, APFSDS) are unaffected.")]
    public bool isSpacedArmor = false;

    [Tooltip("The inner (backing) armor plate behind this spaced gap.\n" +
             "If a kinetic round defeats this plate, CannonProjectile will\n" +
             "re-run the penetration check against the backing plate with\n" +
             "the shell's remaining energy.")]
    public ArmorPlate spacedBackingPlate;

    // ── References ────────────────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("The TankHealth component that owns this plate.\n" +
             "Leave empty to auto-find on the parent hierarchy at runtime.")]
    public TankHealth tankHealth;

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (tankHealth == null)
            tankHealth = GetComponentInParent<TankHealth>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the effective RHA-equivalent thickness of this plate in mm,
    /// factoring in the material multiplier.
    /// </summary>
    public float GetEffectiveRHAMM()
    {
        float multiplier = rhaMultiplierOverride > 0f
            ? rhaMultiplierOverride
            : GetDefaultMultiplier(material);

        return thicknessMM * multiplier;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static float GetDefaultMultiplier(ArmorMaterial mat)
    {
        switch (mat)
        {
            case ArmorMaterial.RHA:       return 1.00f;
            case ArmorMaterial.CHA:       return 0.90f;
            case ArmorMaterial.Cast:      return 0.85f;
            case ArmorMaterial.Composite: return 1.50f;  // conservative default
            case ArmorMaterial.Spaced:    return 0.80f;
            default:                      return 1.00f;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Editor gizmos
    // ─────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Collider col = GetComponent<Collider>();
        if (col == null) return;

        // Color ramps from green (thin) to red (heavy): 0 mm = green, 300 mm = red
        float rha = GetEffectiveRHAMM();
        float t   = Mathf.Clamp01(rha / 300f);
        Gizmos.color = Color.Lerp(Color.green, Color.red, t);
        Gizmos.DrawWireCube(col.bounds.center, col.bounds.size * 1.02f);

        if (isSpacedArmor)
        {
            // Draw a second wider wire to indicate spaced armor
            Gizmos.color = new Color(0f, 0.6f, 1f, 0.6f);
            Gizmos.DrawWireCube(col.bounds.center, col.bounds.size * 1.08f);
        }

        UnityEditor.Handles.Label(
            transform.position + Vector3.up * 0.35f,
            string.Format("{0}\n{1:F0} mm ({2}){3}\n\u2248 {4:F0} mm RHA",
                          zoneName,
                          thicknessMM,
                          material,
                          isSpacedArmor ? " [SPACED]" : "",
                          rha));
    }
#endif
}
