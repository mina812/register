// ============================================================
//  MenuManager.cs
//  HOARES — Milestone 8 (Full UI Rewrite)
//
//  Controls three FlatUnityCanvas menus:
//  • Main menu
//  • Spine menu        
//  NAVIGATION RULES:
//  ─────────────────
//  • Menu navigation NEVER resets scene objects.
//  • CT quads and spine model keep their state when
//    switching menus via Back button.
//  • Each FlatUnityCanvas remembers its position
//    (doctor can move them via GrabKnob handle).
//
//  SPINE MENU BUTTONS:
//  ───────────────────
//  Show, Hide, Transparent, Grab, Lock, Reset are
//  wired DIRECTLY to SpineController in Inspector.
//  MenuManager does NOT handle spine visibility.
//
//  CONNECTIVITY:
//  ─────────────
//  Single toggle replaces Connect/Disconnect buttons.
//  Green when connected, default when disconnected.
//
//  LIVE SYNC:
//  ──────────
//  First CT button press sends LIVE_ALL to Slicer.
//  From then on, scrolling in Slicer auto-pushes
//  new slices without pressing buttons again.
//
//  INSPECTOR SETUP:
//  ────────────────
//  • Drag each FlatUnityCanvas root into the menu fields
//  • Drag IGTManager into igtConnector, imageHandler, stringHandler
//  • Drag CT quads (CTAxial, CTCoronal, CTSagittal) into quad fields
//  • Drag SpineModel into spineModel field
//  • Drag the connectivity Toggle into connectivityToggle
//  • CT view buttons → ToggleAxial/Coronal/Sagittal/ShowAll
//  • FlipH/FlipV buttons → FlipH/FlipV
//  • Back buttons → ShowMainMenu
//  • Spine button → ShowSpineMenu
//  • CT Slices button → ShowCTSlicesMenu
//  • Register button → OnRegisterPressed
// ============================================================

using UnityEngine;
using UnityEngine.UI;
using TMPro;
using HOARES.IGT;

public class MenuManager : MonoBehaviour
{
    // ── Menu canvases (FlatUnityCanvas roots) ─────────────────
    [Header("Menu Canvases")]
    [Tooltip("Main_canvas Variant")]
    public GameObject mainMenu;

    [Tooltip("SpineCanvas Variant")]
    public GameObject spineMenu;

    [Tooltip("CT canvas Variant")]
    public GameObject ctSlicesMenu;

    // ── Scene objects ─────────────────────────────────────────
    [Header("Scene Objects")]
    public GameObject spineModel;
    public GameObject quadAxial;
    public GameObject quadCoronal;
    public GameObject quadSagittal;

    // ── IGT references ────────────────────────────────────────
    [Header("IGT Connection")]
    [Tooltip("Drag the IGTManager GameObject here")]
    public IGTLinkConnector igtConnector;

    [Header("CT Image Handler")]
    [Tooltip("Drag the IGTManager GameObject here as well")]
    public IGTImageHandler imageHandler;

    [Header("IGT String Handler")]
    public IGTStringHandler stringHandler;

    // ── Connectivity toggle ───────────────────────────────────
    [Header("Connectivity Toggle (Main Menu)")]
    [Tooltip("The Meta SDK toggle that replaces Connect/Disconnect buttons")]
    public Toggle connectivityToggle;

    // ── CT view toggle buttons ────────────────────────────────
    [Header("CT View Toggle Buttons")]
    public Toggle axialToggleBtn;
    public Toggle coronalToggleBtn;
    public Toggle sagittalToggleBtn;
    public Toggle showAllBtn;

    // ── CT flip buttons ───────────────────────────────────────
    [Header("CT Flip Buttons")]
    [Tooltip("Wired to FlipH() — operates on the selected view")]
    public Toggle flipHBtn;

    [Tooltip("Wired to FlipV() — operates on the selected view")]
    public Toggle flipVBtn;

    [Tooltip("Optional label — shows which view the flip buttons target")]
    public TextMeshProUGUI selectedViewLabel;

    // ── Colours ───────────────────────────────────────────────
    // CT view toggle buttons
    private static readonly Color COL_CT_ACTIVE   = new Color(0.35f, 0.75f, 1.00f, 1f); // blue  = visible
    private static readonly Color COL_CT_INACTIVE = new Color(1.00f, 1.00f, 1.00f, 1f); // white = hidden
    private static readonly Color COL_CT_SELECTED = new Color(1.00f, 0.92f, 0.30f, 1f); // gold  = selected for flip

    // Flip toggle buttons
    private static readonly Color COL_FLIP_ON  = new Color(1.00f, 0.55f, 0.10f, 1f); // orange = flip active
    private static readonly Color COL_FLIP_OFF = new Color(1.00f, 1.00f, 1.00f, 1f); // white  = no flip

    // Connectivity toggle
    private static readonly Color COL_CONNECTED    = new Color(0.15f, 0.80f, 0.25f, 1f); // green
    private static readonly Color COL_DISCONNECTED = new Color(1.00f, 1.00f, 1.00f, 1f); // white (default)

    // ── Device name constants ─────────────────────────────────
    private const string DEV_AXIAL    = "HOARES_CT_Axial";
    private const string DEV_CORONAL  = "HOARES_CT_Cor";
    private const string DEV_SAGITTAL = "HOARES_CT_Sag";

    // ── Internal state ────────────────────────────────────────
    private enum ConnState { Disconnected, Connecting, Connected }
    private ConnState _lastState       = ConnState.Disconnected;
    private float     _connectingTimer = 0f;
    private const float CONNECT_TIMEOUT = 5f;

    // Currently selected view — flip buttons target this device.
    private string _selectedDevice = string.Empty;

    // Live sync — sent once per connection session
    private bool _liveSyncStarted = false;

    // ─────────────────────────────────────────────────────────
    //  Unity lifecycle
    // ─────────────────────────────────────────────────────────
    void Start()
    {
        if (igtConnector != null)
            igtConnector.autoConnect = false;

        if (imageHandler == null && igtConnector != null)
            imageHandler = igtConnector.GetComponent<IGTImageHandler>();

        // Hide all scene objects at startup
        SetSceneObjectsActive(false, false, false, false);

        // Show only the main menu
        ShowMainMenu();

        // Refresh button visuals
        RefreshCTButtonVisuals();
        RefreshFlipButtons();
        RefreshConnectivityToggle();

        // Wire connectivity toggle listener
        if (connectivityToggle != null)
            connectivityToggle.onValueChanged.AddListener(OnConnectivityToggleChanged);
    }

    void Update()
    {
        if (igtConnector == null) return;

        ConnState current;
        if (igtConnector.IsConnected)
        {
            current = ConnState.Connected;
            _connectingTimer = 0f;
        }
        else if (_connectingTimer > 0f)
        {
            _connectingTimer -= Time.deltaTime;
            current = (_connectingTimer > 0f) ? ConnState.Connecting : ConnState.Disconnected;
        }
        else
        {
            current = ConnState.Disconnected;
        }

        if (current != _lastState)
        {
            _lastState = current;
            RefreshConnectivityToggle();

            // If connection dropped unexpectedly, sync toggle
            if (current == ConnState.Disconnected
                && connectivityToggle != null
                && connectivityToggle.isOn)
            {
                connectivityToggle.onValueChanged.RemoveListener(OnConnectivityToggleChanged);
                connectivityToggle.isOn = false;
                connectivityToggle.onValueChanged.AddListener(OnConnectivityToggleChanged);
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  Connectivity (single toggle)
    // ─────────────────────────────────────────────────────────
    public void OnConnectivityToggleChanged(bool isOn)
    {
        if (igtConnector == null)
        {
            Debug.LogError("[HOARES] MenuManager: igtConnector is not assigned!");
            return;
        }

        if (isOn)
        {
            if (igtConnector.IsConnected) return;
            _connectingTimer = CONNECT_TIMEOUT;
            igtConnector.Connect();

            if (igtConnector.IsConnected)
            {
                _connectingTimer = 0f;
                Debug.Log("[HOARES] GUI: Connection successful.");
            }
            else
            {
                Debug.LogWarning("[HOARES] GUI: Connect() returned not-connected. Waiting...");
            }
        }
        else
        {
            _connectingTimer = 0f;
            _liveSyncStarted = false;
            igtConnector.Disconnect();
            Debug.Log("[HOARES] GUI: Disconnected.");
        }

        RefreshConnectivityToggle();
    }

    private void RefreshConnectivityToggle()
    {
        if (connectivityToggle == null) return;

        bool connected = igtConnector != null && igtConnector.IsConnected;
        ColorBlock cb    = connectivityToggle.colors;
        cb.normalColor   = connected ? COL_CONNECTED : COL_DISCONNECTED;
        cb.selectedColor = connected ? COL_CONNECTED : COL_DISCONNECTED;
        connectivityToggle.colors = cb;
    }

    // ─────────────────────────────────────────────────────────
    //  Menu navigation
    //  NOTE: None of these touch scene objects — navigation
    //  never resets CT quads or spine visibility.
    // ─────────────────────────────────────────────────────────
    public void ShowMainMenu()
    {
        mainMenu.SetActive(true);
        spineMenu.SetActive(false);
        ctSlicesMenu.SetActive(false);
    }

    public void ShowSpineMenu()
    {
        mainMenu.SetActive(false);
        spineMenu.SetActive(true);
        ctSlicesMenu.SetActive(false);

        // Ensure spine GameObject is active — SpineController controls renderer.enabled
        if (spineModel != null && !spineModel.activeSelf)
            spineModel.SetActive(true);
    }

    public void ShowCTSlicesMenu()
    {
        mainMenu.SetActive(false);
        spineMenu.SetActive(false);
        ctSlicesMenu.SetActive(true);

        // Refresh button visuals to reflect current quad states
        RefreshCTButtonVisuals();
        RefreshFlipButtons();
    }

    // ─────────────────────────────────────────────────────────
    //  Register button (placeholder for registration workflow)
    // ─────────────────────────────────────────────────────────
    public void OnRegisterPressed()
    {
        Debug.Log("[HOARES] Register pressed — registration workflow not yet implemented.");
        // TODO: Open registration panel or start fiducial collection
    }

    // ─────────────────────────────────────────────────────────
    //  CT view toggles — also SELECT the view for flip buttons
    // ─────────────────────────────────────────────────────────

    /// <summary>Wired to the "Axial" button OnClick().</summary>
    public void ToggleAxial()
    {
        if (quadAxial == null) return;
        bool nowActive = !quadAxial.activeSelf;
        quadAxial.SetActive(nowActive);

        SelectView(DEV_AXIAL);
        stringHandler?.SendCommand(IGTStringHandler.CMD_STREAM_AXIAL);
        TryStartLiveSync();
    }

    /// <summary>Wired to the "Coronal" button OnClick().</summary>
    public void ToggleCoronal()
    {
        if (quadCoronal == null) return;
        bool nowActive = !quadCoronal.activeSelf;
        quadCoronal.SetActive(nowActive);

        SelectView(DEV_CORONAL);
        stringHandler?.SendCommand(IGTStringHandler.CMD_STREAM_CORONAL);
        TryStartLiveSync();

        Debug.Log($"[HOARES] CTCoronal → {(nowActive ? "visible" : "hidden")}, selected for flip");
    }

    /// <summary>Wired to the "Sagittal" button OnClick().</summary>
    public void ToggleSagittal()
    {
        if (quadSagittal == null) return;
        bool nowActive = !quadSagittal.activeSelf;
        quadSagittal.SetActive(nowActive);

        SelectView(DEV_SAGITTAL);
        stringHandler?.SendCommand(IGTStringHandler.CMD_STREAM_SAGITTAL);
        TryStartLiveSync();

        Debug.Log($"[HOARES] CTSagittal → {(nowActive ? "visible" : "hidden")}, selected for flip");
    }

    /// <summary>Wired to the "ALL" button OnClick().</summary>
    public void ShowAll()
    {
        if (quadAxial == null || quadCoronal == null || quadSagittal == null) return;

        bool allVisible = quadAxial.activeSelf
                       && quadCoronal.activeSelf
                       && quadSagittal.activeSelf;
        bool newState = !allVisible;

        quadAxial.SetActive(newState);
        quadCoronal.SetActive(newState);
        quadSagittal.SetActive(newState);

        RefreshCTButtonVisuals();
        stringHandler?.SendCommand(IGTStringHandler.CMD_STREAM_ALL);
        TryStartLiveSync();
        Debug.Log($"[HOARES] All CT quads → {(newState ? "visible" : "hidden")}");
    }

    // ─────────────────────────────────────────────────────────
    //  Live sync — send LIVE_ALL once per connection session
    // ─────────────────────────────────────────────────────────
    private void TryStartLiveSync()
    {
        if (_liveSyncStarted) return;
        stringHandler?.SendCommand("LIVE_ALL");
        _liveSyncStarted = true;
        Debug.Log("[HOARES] Live sync started for all views.");
    }

    // ─────────────────────────────────────────────────────────
    //  Flip button callbacks
    // ─────────────────────────────────────────────────────────

    /// <summary>Wired to the "FlipH" button.</summary>
    public void FlipH()
    {
        if (imageHandler == null || string.IsNullOrEmpty(_selectedDevice)) return;

        var setting = imageHandler.sliceSettings.Find(s => s.deviceName == _selectedDevice);
        if (setting == null) return;

        setting.flipHorizontal = !setting.flipHorizontal;
        SetButtonFlipTint(flipHBtn, setting.flipHorizontal);

        // Re-stream to apply the new flip
        ResendCurrentView();
        Debug.Log($"[HOARES] FlipH on '{_selectedDevice}' → {setting.flipHorizontal}");
    }

    /// <summary>Wired to the "FlipV" button.</summary>
    public void FlipV()
    {
        if (imageHandler == null || string.IsNullOrEmpty(_selectedDevice)) return;

        var setting = imageHandler.sliceSettings.Find(s => s.deviceName == _selectedDevice);
        if (setting == null) return;

        setting.flipVertical = !setting.flipVertical;
        SetButtonFlipTint(flipVBtn, setting.flipVertical);

        // Re-stream to apply the new flip
        ResendCurrentView();
        Debug.Log($"[HOARES] FlipV on '{_selectedDevice}' → {setting.flipVertical}");
    }

    /// <summary>Re-sends the current view so flip changes take effect immediately.</summary>
    private void ResendCurrentView()
    {
        if (stringHandler == null || string.IsNullOrEmpty(_selectedDevice)) return;

        string cmd = _selectedDevice switch
        {
            DEV_AXIAL    => IGTStringHandler.CMD_STREAM_AXIAL,
            DEV_CORONAL  => IGTStringHandler.CMD_STREAM_CORONAL,
            DEV_SAGITTAL => IGTStringHandler.CMD_STREAM_SAGITTAL,
            _            => null
        };

        if (cmd != null)
            stringHandler.SendCommand(cmd);
    }

    // ─────────────────────────────────────────────────────────
    //  Private helpers
    // ─────────────────────────────────────────────────────────

    private void SelectView(string deviceName)
    {
        _selectedDevice = deviceName;
        RefreshCTButtonVisuals();
        RefreshFlipButtons();

        if (selectedViewLabel != null)
        {
            string friendly = deviceName switch
            {
                DEV_AXIAL    => "Axial",
                DEV_CORONAL  => "Coronal",
                DEV_SAGITTAL => "Sagittal",
                _            => deviceName
            };
            selectedViewLabel.text = $"Flip target: {friendly}";
        }
    }

    private void RefreshFlipButtons()
    {
        bool hasSelection = !string.IsNullOrEmpty(_selectedDevice) && imageHandler != null;

        if (flipHBtn != null) flipHBtn.interactable = hasSelection;
        if (flipVBtn != null) flipVBtn.interactable = hasSelection;

        if (!hasSelection)
        {
            SetButtonFlipTint(flipHBtn, false);
            SetButtonFlipTint(flipVBtn, false);

            if (selectedViewLabel != null)
                selectedViewLabel.text = "Tap a view to select";
            return;
        }

        var setting = imageHandler.sliceSettings.Find(s => s.deviceName == _selectedDevice);
        if (setting != null)
        {
            SetButtonFlipTint(flipHBtn, setting.flipHorizontal);
            SetButtonFlipTint(flipVBtn, setting.flipVertical);
        }
    }

    private void RefreshCTButtonVisuals()
    {
        PaintCTToggleBtn(axialToggleBtn,    DEV_AXIAL,    quadAxial    != null && quadAxial.activeSelf);
        PaintCTToggleBtn(coronalToggleBtn,  DEV_CORONAL,  quadCoronal  != null && quadCoronal.activeSelf);
        PaintCTToggleBtn(sagittalToggleBtn, DEV_SAGITTAL, quadSagittal != null && quadSagittal.activeSelf);
    }

    private void PaintCTToggleBtn(Toggle btn, string deviceName, bool isVisible)
    {
        if (btn == null) return;
        Color c = (deviceName == _selectedDevice) ? COL_CT_SELECTED
                : isVisible                       ? COL_CT_ACTIVE
                                                  : COL_CT_INACTIVE;
        ColorBlock cb    = btn.colors;
        cb.normalColor   = c;
        cb.selectedColor = c;
        btn.colors       = cb;
    }

    private void SetButtonFlipTint(Toggle btn, bool isActive)
    {
        if (btn == null) return;
        ColorBlock cb    = btn.colors;
        cb.normalColor   = isActive ? COL_FLIP_ON : COL_FLIP_OFF;
        cb.selectedColor = isActive ? COL_FLIP_ON : COL_FLIP_OFF;
        btn.colors       = cb;
    }

    private void SetSceneObjectsActive(bool spine, bool axial, bool coronal, bool sagittal)
    {
        if (spineModel)    spineModel.SetActive(spine);
        if (quadAxial)     quadAxial.SetActive(axial);
        if (quadCoronal)   quadCoronal.SetActive(coronal);
        if (quadSagittal)  quadSagittal.SetActive(sagittal);
    }
}