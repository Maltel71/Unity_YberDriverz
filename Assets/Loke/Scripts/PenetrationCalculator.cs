using UnityEngine;

// =============================================================================
//  PenetrationCalculator.cs  -  Unity 6
//
//  Static utility class for realistic armor penetration calculations.
//
//  Physics model summary
//  ─────────────────────
//  1. Impact angle   - angle between incoming shell and the armor surface normal
//                      (0° = perpendicular / worst for armor, 90° = grazing).
//
//  2. Normalization  - AP/APHE shell noses rotate slightly toward the normal on
//                      impact, effectively reducing the angle. APDS/APFSDS have
//                      minimal normalization; HEAT/HESH have none.
//
//  3. Ricochet       - beyond a critical angle the shell skips off the surface.
//                      Disabled by overmatch.
//
//  4. Overmatch      - when caliber > 3x armor thickness the shell physically
//                      punches through regardless of angle (no ricochet, no
//                      normalization needed). Angle is forced to 0°.
//
//  5. LOS thickness  - effectiveArmor = nominalRHA / cos(normalizedAngle).
//                      Higher angle = more armor the shell must traverse.
//
//  6. Velocity decay - kinetic rounds lose penetration proportional to v²
//                      (energy loss). APFSDS uses v^1.43 (empirical long-rod).
//                      HEAT / HESH penetration is velocity-independent.
//
//  7. HEAT vs spaced - shaped charges prematurely detonate against spaced
//                      armor, neutralising them before reaching the main plate.
//
//  References: de Marre formula (simplified), NATO STANAG 4569, War Thunder
//  dev blogs on ballistics, and Panzer War ballistic simulation papers.
// =============================================================================

// ── Shell type enumeration ────────────────────────────────────────────────────

/// <summary>Shell types supported by the penetration calculator.</summary>
public enum ShellType
{
    AP,       // Armor Piercing              - solid kinetic, moderate normalization (~5°)
    APHE,     // AP High Explosive           - kinetic + internal blast on penetration
    APDS,     // AP Discarding Sabot         - high velocity, low normalization (~2°)
    APFSDS,   // Fin-Stabilized DS           - extreme velocity, minimal normalization (~1°)
    HEAT,     // High Explosive Anti-Tank    - shaped charge, velocity-independent pen
    HESH,     // High Explosive Squash Head  - spall liner, pen mostly velocity-independent
    HE,       // High Explosive              - blast damage, negligible armor penetration
}

// ── Penetration result ────────────────────────────────────────────────────────

/// <summary>
/// Full result returned by PenetrationCalculator.Calculate().
/// Contains all intermediate values useful for debugging, UI, and damage calculation.
/// </summary>
public struct PenetrationResult
{
    /// <summary>True if the shell fully penetrated the armor plate.</summary>
    public bool  penetrated;

    /// <summary>True if the shell bounced off (angle exceeded ricochet threshold).</summary>
    public bool  ricocheted;

    /// <summary>True if the caliber was large enough to ignore ricochet rules.</summary>
    public bool  overmatched;

    /// <summary>True if this is a HEAT shell that was defeated by spaced armor.</summary>
    public bool  heatDefeatedBySpaced;

    /// <summary>Angle from the armor normal at point of impact, in degrees (0 = perpendicular).</summary>
    public float impactAngleDeg;

    /// <summary>Impact angle after normalization, in degrees.</summary>
    public float normalizedAngleDeg;

    /// <summary>Line-of-sight armor thickness after applying the normalized angle, in mm RHA.</summary>
    public float effectiveArmorMM;

    /// <summary>Actual shell penetration capability at impact velocity, in mm.</summary>
    public float shellPenetrationMM;

    /// <summary>
    /// Ratio of shell penetration to effective armor (> 1.0 = penetrated).
    /// Useful for "how close was it?" feedback.
    /// </summary>
    public float penRatio;

    /// <summary>How many mm of penetration the shell had left after defeating the armor.</summary>
    public float excessPenetrationMM;

    /// <summary>
    /// Post-penetration damage multiplier (0.5 – 1.5).
    /// A shell that barely penetrates carries less energy inside than one with lots of excess pen.
    /// </summary>
    public float postPenDamageMultiplier;
}

// ── Calculator ────────────────────────────────────────────────────────────────

/// <summary>
/// Static utility class. Call Calculate() once per collision to get a PenetrationResult.
/// </summary>
public static class PenetrationCalculator
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Main entry point
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Determine whether a shell penetrates a given armor plate.
    /// </summary>
    /// <param name="shellVelocityMS">Shell speed at impact in m/s (use rb.linearVelocity.magnitude).</param>
    /// <param name="muzzleVelocityMS">Original launch speed in m/s.</param>
    /// <param name="caliber">Shell outer diameter in mm.</param>
    /// <param name="basePenetrationMM">Nominal penetration at 0° at muzzle velocity, in mm RHA.</param>
    /// <param name="shellType">Shell category (AP, HEAT, APFSDS, etc.).</param>
    /// <param name="normalizationDeg">Angle the shell normalises by on impact, in degrees.</param>
    /// <param name="ricochetAngleDeg">Angle from the normal at which ricochet occurs, in degrees.</param>
    /// <param name="shellDirection">Normalised velocity direction of the shell at impact.</param>
    /// <param name="armorNormal">Outward-facing surface normal of the armor plate (normalised).</param>
    /// <param name="armorPlate">The ArmorPlate component attached to the hit collider.</param>
    /// <returns>A PenetrationResult with all intermediate values.</returns>
    public static PenetrationResult Calculate(
        float      shellVelocityMS,
        float      muzzleVelocityMS,
        float      caliber,
        float      basePenetrationMM,
        ShellType  shellType,
        float      normalizationDeg,
        float      ricochetAngleDeg,
        Vector3    shellDirection,
        Vector3    armorNormal,
        ArmorPlate armorPlate)
    {
        PenetrationResult result = default;

        float armorRHA = armorPlate.GetEffectiveRHAMM();

        // ── Step 1: Impact angle ──────────────────────────────────────────────
        // The shell travels INTO the plate, so we flip shellDirection to face OUTWARD
        // and dot it against the outward armor normal.
        // Result: 0° = shell comes in perpendicular, 90° = shell arrives parallel (grazing).
        float cosImpact   = Mathf.Clamp(
            Vector3.Dot(-shellDirection.normalized, armorNormal.normalized), -1f, 1f);
        float impactAngle = Mathf.Acos(cosImpact) * Mathf.Rad2Deg;
        result.impactAngleDeg = impactAngle;

        // ── Step 2: HEAT defeated by spaced armor ─────────────────────────────
        // Shaped charges create a plasma jet that has a specific standoff requirement.
        // Spaced armor disrupts the jet before it forms properly.
        if (shellType == ShellType.HEAT && armorPlate.isSpacedArmor)
        {
            result.heatDefeatedBySpaced = true;
            result.penetrated           = false;
            result.postPenDamageMultiplier = 0f;
            return result;
        }

        // ── Step 3: Overmatch check ───────────────────────────────────────────
        // If the shell caliber is more than 3× the plate's nominal thickness,
        // the shell simply pushes through without deflecting. Ricochet is impossible.
        // The effective angle is forced to 0° (perpendicular equivalent).
        bool overmatch     = caliber > armorRHA * 3f;
        result.overmatched = overmatch;

        // ── Step 4: Ricochet check ────────────────────────────────────────────
        // Performed BEFORE normalization because the real-world effect is that
        // a steeply angled shot deflects before the shell tip can rotate.
        // HEAT and HE shells have very high ricochet thresholds (they explode on contact).
        if (!overmatch && shellType != ShellType.HEAT && shellType != ShellType.HE)
        {
            if (impactAngle >= ricochetAngleDeg)
            {
                result.ricocheted             = true;
                result.penetrated             = false;
                result.postPenDamageMultiplier = 0f;
                return result;
            }
        }

        // ── Step 5: Normalization ─────────────────────────────────────────────
        // The shell nose geometry causes the round to rotate slightly toward the
        // perpendicular on impact, reducing the effective angle.
        // Overmatch bypasses this entirely (angle is already 0°).
        float normalizedAngle;
        if (overmatch)
        {
            normalizedAngle = 0f;
        }
        else
        {
            normalizedAngle = Mathf.Max(0f, impactAngle - normalizationDeg);

            // Partial overmatch bonus: when caliber is approaching (but below) the 3× threshold,
            // large-bore AP shells get additional normalization due to their steep ogive.
            if (shellType == ShellType.AP || shellType == ShellType.APHE)
            {
                // Ramps from 0 at caliber/armor = 0.5 to full at caliber/armor = 3
                float partialRatio = Mathf.Clamp01((caliber / Mathf.Max(1f, armorRHA) - 0.5f) / 2.5f);
                normalizedAngle    = Mathf.Lerp(normalizedAngle, 0f, partialRatio * 0.4f);
            }
        }
        result.normalizedAngleDeg = normalizedAngle;

        // ── Step 6: Line-of-sight thickness ──────────────────────────────────
        // The shell must traverse more material when hitting at an angle.
        // effectiveArmor = nominalRHA / cos(normalizedAngle)
        // Guard against division by near-zero (grazing shots that weren't ricocheted).
        float cosNorm          = Mathf.Max(0.001f, Mathf.Cos(normalizedAngle * Mathf.Deg2Rad));
        float effectiveArmor   = armorRHA / cosNorm;
        result.effectiveArmorMM = effectiveArmor;

        // ── Step 7: Shell penetration at impact velocity ──────────────────────
        float shellPen          = ComputeShellPenetration(
            shellVelocityMS, muzzleVelocityMS, basePenetrationMM, shellType);
        result.shellPenetrationMM = shellPen;

        // ── Step 8: Penetration decision ──────────────────────────────────────
        float penRatio    = shellPen / Mathf.Max(0.001f, effectiveArmor);
        result.penRatio   = penRatio;
        result.penetrated = penRatio >= 1f;

        // ── Step 9: Post-penetration energy ───────────────────────────────────
        if (result.penetrated)
        {
            result.excessPenetrationMM = shellPen - effectiveArmor;

            // A shell that barely squeezes through (near 1.0 ratio) carries little
            // energy inside and does moderate damage.
            // A shell with large excess penetration does much more internal damage
            // (more fragmentation, more spall, APHE fuse has time to function).
            float excessRatio = Mathf.Clamp01(result.excessPenetrationMM / Mathf.Max(1f, shellPen));
            result.postPenDamageMultiplier = Mathf.Lerp(0.5f, 1.5f, excessRatio);
        }
        else
        {
            result.excessPenetrationMM     = 0f;
            result.postPenDamageMultiplier = 0f;
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Shell penetration at current velocity
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns how many mm of RHA this shell can penetrate at its current velocity.
    /// </summary>
    private static float ComputeShellPenetration(
        float     currentVelocityMS,
        float     muzzleVelocityMS,
        float     basePenetrationMM,
        ShellType shellType)
    {
        // Guard: muzzle velocity must be positive
        float mv = Mathf.Max(1f, muzzleVelocityMS);

        switch (shellType)
        {
            // ── HEAT: velocity-independent (chemical energy, not kinetic) ─────
            // A very slow HEAT shell (destabilised, tumbling) loses effectiveness.
            // Below 50 m/s the fuse may not arm or the standoff collapses.
            case ShellType.HEAT:
            {
                float stability = Mathf.Clamp01(currentVelocityMS / 50f);
                return basePenetrationMM * stability;
            }

            // ── HESH: mostly velocity-independent ────────────────────────────
            // The plastic explosive needs to spread before detonating;
            // velocity matters very little as long as the fuse arms.
            case ShellType.HESH:
                return basePenetrationMM;

            // ── HE: negligible kinetic penetration ────────────────────────────
            // HE relies entirely on blast overpressure and spall. Treat any
            // nominal "penetration" as 10% of base to account for very thin armor.
            case ShellType.HE:
                return basePenetrationMM * 0.10f;

            // ── APFSDS: long-rod penetrator (v^1.43 exponent) ─────────────────
            // Long-rod penetrators follow a different energy-to-penetration curve
            // than blunt AP projectiles. Empirical exponent ≈ 1.43.
            case ShellType.APFSDS:
            {
                float vRatio = Mathf.Max(0f, currentVelocityMS / mv);
                return basePenetrationMM * Mathf.Pow(vRatio, 1.43f);
            }

            // ── AP / APHE / APDS: kinetic energy (v²) ────────────────────────
            // Penetration scales with kinetic energy, which is proportional to v².
            default:
            {
                float vRatio = Mathf.Max(0f, currentVelocityMS / mv);
                return basePenetrationMM * (vRatio * vRatio);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Default values per shell type
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a sensible default normalization angle for a given shell type.
    /// Use this as the normalizationDeg parameter if you do not want to set
    /// a per-ammo-type override.
    /// </summary>
    public static float DefaultNormalization(ShellType type)
    {
        switch (type)
        {
            case ShellType.AP:      return 5f;
            case ShellType.APHE:    return 3f;
            case ShellType.APDS:    return 2f;
            case ShellType.APFSDS:  return 1f;
            case ShellType.HEAT:    return 0f;
            case ShellType.HESH:    return 0f;
            case ShellType.HE:      return 0f;
            default:                return 3f;
        }
    }

    /// <summary>
    /// Returns a sensible default ricochet angle for a given shell type.
    /// The angle is measured from the armor normal (0 = perpendicular).
    /// </summary>
    public static float DefaultRicochetAngle(ShellType type)
    {
        switch (type)
        {
            case ShellType.AP:      return 70f;
            case ShellType.APHE:    return 67f;
            case ShellType.APDS:    return 75f;
            case ShellType.APFSDS:  return 80f;
            case ShellType.HEAT:    return 85f;  // HEAT detonates; rarely truly ricochets
            case ShellType.HESH:    return 85f;
            case ShellType.HE:      return 90f;  // HE always detonates; no ricochet
            default:                return 70f;
        }
    }
}
