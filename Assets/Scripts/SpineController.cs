using UnityEngine;

public class SpineController : MonoBehaviour
{
    [Header("Spine Renderer")]
    public Renderer spineRenderer;

    private bool isTransparent = false;
    private bool isLocked = false;
    private bool isHidden = false;
    private bool isGrabbing = false;

    // Store original material settings
    private Color originalColor;
    private float originalAlpha = 1f;

    void Start()
    {
        if (spineRenderer == null)
            spineRenderer = GetComponent<Renderer>();

        originalColor = spineRenderer.material.color;

        // Make sure material supports transparency
        SetMaterialTransparentMode(false);
    }

    // ─────────────────────────────────────────
    // TRANSPARENT TOGGLE
    // ─────────────────────────────────────────
    public void ToggleTransparent()
    {
        if (isHidden) return; // don't change transparency if hidden

        isTransparent = !isTransparent;

        if (isTransparent)
        {
            SetMaterialTransparentMode(true);
            Color c = spineRenderer.material.color;
            c.a = 0.25f; // adjust this value for how transparent you want
            spineRenderer.material.color = c;
        }
        else
        {
            SetMaterialTransparentMode(false);
            Color c = spineRenderer.material.color;
            c.a = 1f;
            spineRenderer.material.color = c;
        }
    }

    // ─────────────────────────────────────────
    // LOCK TOGGLE
    // ─────────────────────────────────────────
    public void ToggleLock()
    {
        isLocked = !isLocked;

        // If locked, disable grab so the model cannot be moved
        if (isLocked && isGrabbing)
        {
            StopGrab();
        }

        Debug.Log(isLocked ? "Spine LOCKED" : "Spine UNLOCKED");
    }

    public bool IsLocked()
    {
        return isLocked;
    }

    // ─────────────────────────────────────────
    // HIDE TOGGLE
    // ─────────────────────────────────────────
    public void ToggleHide()
    {
        isHidden = !isHidden;
        spineRenderer.enabled = !isHidden;

        // If hiding while transparent, keep state but hide
        // When shown again it will restore correctly
    }

    // ─────────────────────────────────────────
    // GRAB
    // ─────────────────────────────────────────
    public void ToggleGrab()
    {
        if (isLocked)
        {
            Debug.Log("Cannot grab — spine is locked");
            return;
        }

        isGrabbing = !isGrabbing;

        if (isGrabbing)
            StartGrab();
        else
            StopGrab();
    }

    private void StartGrab()
    {
        // Enable the OVRGrabbable component so the hand can pick it up
        OVRGrabbable grabbable = GetComponent<OVRGrabbable>();
        if (grabbable != null)
            grabbable.enabled = true;

        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = false;
            rb.useGravity = false;
        }

        Debug.Log("Grab ENABLED");
    }

    private void StopGrab()
    {
        isGrabbing = false;

        OVRGrabbable grabbable = GetComponent<OVRGrabbable>();
        if (grabbable != null)
            grabbable.enabled = false;

        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb != null)
            rb.isKinematic = true;

        Debug.Log("Grab DISABLED");
    }

    // ─────────────────────────────────────────
    // MATERIAL HELPER
    // ─────────────────────────────────────────
    private void SetMaterialTransparentMode(bool transparent)
    {
        Material mat = spineRenderer.material;

        if (transparent)
        {
            mat.SetFloat("_Mode", 3); // Transparent mode in Standard shader
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
        }
        else
        {
            mat.SetFloat("_Mode", 0); // Opaque mode
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            mat.SetInt("_ZWrite", 1);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = -1;
        }
    }
    public void ShowSpine()
    {
        isHidden = false;
        if (spineRenderer != null)
         spineRenderer.enabled = true;
    }

    public void HideSpine()
        {
        isHidden = true;
        if (spineRenderer != null)
            spineRenderer.enabled = false;
    }
}