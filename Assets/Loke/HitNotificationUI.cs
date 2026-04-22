using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// =============================================================================
//  HitNotificationUI.cs  -  Unity 6
//
//  Stacking red hit-feedback notifications in the top-right corner of the HUD.
//  Triggered by ModuleManager when an ENEMY module is destroyed or crew is KIA.
//
//  Setup — two steps only
//  ──────────────────────
//  1. In your scene create a Canvas however you like (Screen Space – Overlay,
//     CanvasScaler, etc.). This script does NOT touch that canvas at all —
//     it only adds a layout container inside it as a child.
//     IMPORTANT: remove the GraphicRaycaster from that Canvas (notifications
//     are never clicked, and a raycaster on a full-screen overlay blocks all
//     mouse input to the game).
//
//  2. Attach this script to any GameObject (a dedicated "UI" empty works well).
//     Drag your Canvas into the "Target Canvas" Inspector slot.
//
//  3. On the PLAYER tank's ModuleManager tick "Is Player Tank = true".
//     Leave it false on all enemy tanks (the default).
//
//  Colour rules
//  ────────────
//  Vivid red    →  Ammo Rack, Gunner KIA, Driver KIA, Commander KIA  (critical)
//  Medium red   →  Engine, Tracks, Fuel Tank, Loader KIA             (high)
//  Darker red   →  Radio, Hull, Generic                              (standard)
// =============================================================================

public class HitNotificationUI : MonoBehaviour
{
    // ── Singleton ─────────────────────────────────────────────────────────────
    public static HitNotificationUI Instance { get; private set; }

    // ── Inspector ─────────────────────────────────────────────────────────────
    [Header("Canvas")]
    [Tooltip("Drag your existing scene Canvas here. Must have NO GraphicRaycaster.")]
    public Canvas targetCanvas;

    [Header("Timing")]
    [Range(1f, 10f)]
    public float displayDuration = 3.5f;

    [Range(0.05f, 1f)]
    public float fadeInDuration = 0.12f;

    [Range(0.2f, 2f)]
    public float fadeOutDuration = 0.5f;

    [Header("Layout")]
    [Range(1, 12)]
    public int maxNotifications = 6;

    [Tooltip("Width × height of each card in UI pixels at reference resolution.")]
    public Vector2 cardSize = new Vector2(290f, 44f);

    [Range(0f, 20f)]
    public float cardSpacing = 4f;

    [Tooltip("Pixel offset inward from the top-right corner of the screen.")]
    public Vector2 panelOffset = new Vector2(-14f, -14f);

    // ── Runtime ───────────────────────────────────────────────────────────────
    private RectTransform             container;
    private readonly List<GameObject> activeCards = new List<GameObject>();

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        if (targetCanvas == null)
        {
            Debug.LogError("[HitNotificationUI] Target Canvas is not assigned! " +
                           "Drag a Canvas into the Inspector slot.", this);
            enabled = false;
            return;
        }

        CreateContainer();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API  —  called by ModuleManager
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Show a module-destroyed notification for an enemy tank.</summary>
    public void ShowModuleDestroyed(string moduleName, ModuleType moduleType)
    {
        SpawnCard("MODULE", $"{moduleName.ToUpper()}  —  DESTROYED",
                  ModuleColor(moduleType));
    }

    /// <summary>Show a crew-KIA notification for an enemy tank.</summary>
    public void ShowCrewKIA(string crewName, CrewRole role)
    {
        string name = string.IsNullOrWhiteSpace(crewName)
                      ? role.ToString().ToUpper()
                      : crewName.ToUpper();
        SpawnCard("CREW", $"{name}  —  {role.ToString().ToUpper()} KIA", VividRed);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Container — anchored top-right inside the user's canvas
    // ─────────────────────────────────────────────────────────────────────────

    private void CreateContainer()
    {
        GameObject go = new GameObject("HitNotifications_Container");
        go.transform.SetParent(targetCanvas.transform, false);

        container = go.AddComponent<RectTransform>();
        container.anchorMin        = Vector2.one;   // top-right anchor
        container.anchorMax        = Vector2.one;
        container.pivot            = Vector2.one;   // pivot at top-right
        container.anchoredPosition = panelOffset;
        container.sizeDelta        = new Vector2(cardSize.x, 0f);

        VerticalLayoutGroup vlg    = go.AddComponent<VerticalLayoutGroup>();
        vlg.childAlignment         = TextAnchor.UpperRight;
        vlg.spacing                = cardSpacing;
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = false;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;

        ContentSizeFitter csf  = go.AddComponent<ContentSizeFitter>();
        csf.verticalFit        = ContentSizeFitter.FitMode.PreferredSize;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Card spawning
    // ─────────────────────────────────────────────────────────────────────────

    private void SpawnCard(string category, string line, Color accent)
    {
        if (container == null) return;

        // Remove any cards that have already been destroyed by their coroutine
        activeCards.RemoveAll(c => c == null);

        // Evict the oldest card if we are at the cap
        while (activeCards.Count >= maxNotifications)
        {
            GameObject oldest = activeCards[activeCards.Count - 1];
            activeCards.RemoveAt(activeCards.Count - 1);
            if (oldest != null) Destroy(oldest);
        }

        GameObject card = BuildCard(category, line, accent);
        card.transform.SetParent(container, false);
        card.transform.SetAsFirstSibling();   // newest card always at the top
        activeCards.Insert(0, card);

        StartCoroutine(CardLifetime(card));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Card lifetime coroutine
    // ─────────────────────────────────────────────────────────────────────────

    private IEnumerator CardLifetime(GameObject card)
    {
        if (card == null) yield break;

        CanvasGroup cg = card.GetComponent<CanvasGroup>();
        if (cg == null) yield break;

        // Fade in
        cg.alpha = 0f;
        for (float t = 0f; t < fadeInDuration; t += Time.unscaledDeltaTime)
        {
            if (card == null) yield break;
            cg.alpha = Mathf.Clamp01(t / fadeInDuration);
            yield return null;
        }
        cg.alpha = 1f;

        // Hold
        yield return new WaitForSecondsRealtime(displayDuration);

        // Fade out
        for (float t = 0f; t < fadeOutDuration; t += Time.unscaledDeltaTime)
        {
            if (card == null) yield break;
            cg.alpha = Mathf.Clamp01(1f - t / fadeOutDuration);
            yield return null;
        }

        activeCards.Remove(card);
        if (card != null) Destroy(card);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Card builder — dark background + red accent bar + two text lines
    // ─────────────────────────────────────────────────────────────────────────

    private GameObject BuildCard(string category, string mainLine, Color accent)
    {
        // ── Root ─────────────────────────────────────────────────────────────
        GameObject root = new GameObject("Card");
        root.AddComponent<RectTransform>().sizeDelta = cardSize;
        root.AddComponent<CanvasGroup>();

        // ── Dark background ──────────────────────────────────────────────────
        GameObject bg = new GameObject("BG");
        bg.transform.SetParent(root.transform, false);
        StretchFill(bg.AddComponent<RectTransform>());
        bg.AddComponent<Image>().color = new Color(0.05f, 0.04f, 0.04f, 0.88f);

        // ── Left accent bar (3 px wide) ───────────────────────────────────────
        GameObject bar = new GameObject("Bar");
        bar.transform.SetParent(root.transform, false);
        RectTransform barRT = bar.AddComponent<RectTransform>();
        barRT.anchorMin        = new Vector2(0f, 0f);
        barRT.anchorMax        = new Vector2(0f, 1f);
        barRT.pivot            = new Vector2(0f, 0.5f);
        barRT.anchoredPosition = Vector2.zero;
        barRT.sizeDelta        = new Vector2(3f, 0f);
        bar.AddComponent<Image>().color = accent;

        // ── Category label  (small muted text at top-left) ────────────────────
        GameObject catGO = new GameObject("Category");
        catGO.transform.SetParent(root.transform, false);
        RectTransform catRT = catGO.AddComponent<RectTransform>();
        catRT.anchorMin = new Vector2(0f, 0.56f);
        catRT.anchorMax = new Vector2(1f, 1f);
        catRT.offsetMin = new Vector2(10f, 0f);
        catRT.offsetMax = new Vector2(-6f, -2f);
        TextMeshProUGUI catTMP = catGO.AddComponent<TextMeshProUGUI>();
        catTMP.text      = category;
        catTMP.fontSize  = 8f;
        catTMP.color     = new Color(0.6f, 0.3f, 0.3f, 1f);
        catTMP.fontStyle = FontStyles.Bold;
        catTMP.alignment = TextAlignmentOptions.BottomLeft;

        // ── Main line  (bold, accent colour) ─────────────────────────────────
        GameObject lineGO = new GameObject("MainLine");
        lineGO.transform.SetParent(root.transform, false);
        RectTransform lineRT = lineGO.AddComponent<RectTransform>();
        lineRT.anchorMin = new Vector2(0f, 0f);
        lineRT.anchorMax = new Vector2(1f, 0.60f);
        lineRT.offsetMin = new Vector2(10f, 3f);
        lineRT.offsetMax = new Vector2(-6f, 0f);
        TextMeshProUGUI lineTMP = lineGO.AddComponent<TextMeshProUGUI>();
        lineTMP.text         = mainLine;
        lineTMP.fontSize     = 12.5f;
        lineTMP.color        = accent;
        lineTMP.fontStyle    = FontStyles.Bold;
        lineTMP.alignment    = TextAlignmentOptions.Left;
        lineTMP.richText     = false;
        lineTMP.overflowMode = TextOverflowModes.Ellipsis;

        return root;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static void StretchFill(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Colour palette  —  all red, three brightness tiers
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly Color VividRed    = new Color(1.00f, 0.18f, 0.18f);  // critical
    private static readonly Color MediumRed   = new Color(0.90f, 0.22f, 0.22f);  // high
    private static readonly Color DarkRed     = new Color(0.72f, 0.22f, 0.22f);  // standard

    private static Color ModuleColor(ModuleType t) => t switch
    {
        ModuleType.AmmoRack  => VividRed,    // tank will explode
        ModuleType.Gunner    => VividRed,    // can no longer fire
        ModuleType.Driver    => VividRed,    // can no longer move
        ModuleType.Commander => VividRed,    // spotting + accuracy gone
        ModuleType.FuelTank  => MediumRed,   // fire risk
        ModuleType.Engine    => MediumRed,   // mobility kill
        ModuleType.Tracks    => MediumRed,   // mobility kill
        ModuleType.Loader    => MediumRed,   // reload halted
        ModuleType.Radio     => DarkRed,
        ModuleType.Hull      => DarkRed,
        _                    => DarkRed,
    };
}
