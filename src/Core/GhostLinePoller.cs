using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using HarmonyLib;
using Debug = LiftoffGhostLines.ModLog;
using static LiftoffGhostLines.GhostLinesPlugin;
using static LiftoffGhostLines.Prefs;

namespace LiftoffGhostLines
{
    public partial class GhostLinePoller : MonoBehaviour
    {
        private float nextCheck = 0f;
        private Rect settingsRect = new Rect(-1f, -1f, 400f, 360f);
        private bool settingsOpen = false;
        private readonly WindowDrag settingsDrag = new WindowDrag();
        private bool pipFarSliderDragging = false;
        private bool settingsPanelPositionCustomized = false;
        private bool settingsControlPressed = false;
        private bool settingsDragSelecting = false;
        private bool settingsDragSelectMoved = false;
        private int settingsDragSelectColumn = -1;
        private int settingsDragSelectStartRow = -1;
        private int settingsDragSelectStartColumn = -1;
        private int settingsDragSelectLastColumn = -1;
        private bool settingsDragSelectStartValue = false;
        private int settingsDragSelectLastIndex = -1;
        private readonly HashSet<int> settingsDragSelectProcessed = new HashSet<int>();
        private bool autoDownloadWorkshop = false;
        private string mpCurrentTrackName = null;
        private string mpCurrentRaceName = null;
        private Type wsTrackNameType = null;
        private readonly HashSet<string> wsHolderChecked = new HashSet<string>();
        private string wsTrackItemId = null;
        private string wsRaceItemId = null;
        private Type wsRaceNameType = null;
        private float wsNamePollAt = 0f;
        private float wsLastDownloadClickAt = -999f;
        private string wsAutoAppliedTrackName = null;
        private string wsAutoAppliedRaceName = null;
        private bool wsOfficialContent = false;
        private bool wsPendingItemStatusOfficial = false;
        private string wsEnvironmentName = null;
        private bool wsQueryBusy = false;
        private float wsQueryStartedAt = -1f;
        private string wsStatus = "";
        private string wsPendingTargetName = null;
        private Steamworks.CallResult<Steamworks.SteamUGCQueryCompleted_t> wsQueryCallResult = null;
        private readonly List<string> wsPendingNames = new List<string>();
        private int wsCurrentTargetIndex = 0;
        private readonly Dictionary<string, Steamworks.PublishedFileId_t> wsNameCache = new Dictionary<string, Steamworks.PublishedFileId_t>(StringComparer.OrdinalIgnoreCase);
        private ulong wsDownloadingId = 0;
        private string wsDownloadingName = null;
        private readonly System.Collections.Generic.HashSet<ulong> wsDownloadWatchIds = new System.Collections.Generic.HashSet<ulong>();
        private readonly System.Collections.Generic.HashSet<ulong> wsCompletedIds = new System.Collections.Generic.HashSet<ulong>();
        private float wsStatePollAt = 0f;
        private Texture2D whiteTex;
        private Texture2D roundedBtnTex;
        private GameObject replayControlsObj;
        private CanvasGroup controlsCG;
        private bool controlsHidden = false;
        private bool controlsStateCaptured = false;
        private bool controlsCanvasGroupAdded = false;
        private float originalControlsAlpha;
        private bool originalControlsInteractable;
        private bool originalControlsBlocksRaycasts;
        private static readonly System.Reflection.FieldInfo droneHudModuleParentField =
            AccessTools.Field(typeof(DroneHUD), "moduleParent");
        private DroneHUD nativeDroneHud;
        private Canvas nativeHudModuleParent;
        private bool nativeHudModuleParentStateCaptured;
        private bool nativeHudModuleParentOriginalEnabled;
        private float nextNativeHudResolve = 0f;
        private GameObject nativeWarningsRoot;
        private bool nativeWarningsOriginalActive = true;
        private float nextNativeWarningsResolve = 0f;
        private int nativeWarningsResolveMisses = 0;
        private FlightManager flightManagerInstance;
        private float nextFlightManagerScanAt = 0f;
        private readonly object nativeDroneResetLockToken = new object();
        private bool nativeDroneResetLocked = false;
        private System.Type flyableZoneCheckerType;
        private readonly List<MonoBehaviour> nativeZoneCheckers = new List<MonoBehaviour>();
        private float nextNativeZoneCheckersResolve = 0f;
        private float nextGateDiag = 0f;
        private System.Reflection.FieldInfo gateDiagInZone;
        private System.Reflection.FieldInfo gateDiagAllowedHeight;
        private System.Reflection.FieldInfo gateDiagOutOfZoneTimer;
        private GameObject gateDiagFade;
        private float nextGateDiagFadeResolve = 0f;
        private Transform gateDiagLocalDrone;
        private float nextGateDiagLocalDroneResolve = 0f;
        private Vector3 gateDiagLastDronePos;
        private bool gateDiagLastDronePosValid = false;
        private float nextNativeStandingsResolve = 0f;
        private int nativeStandingsSceneHandle = int.MinValue;
        private bool nativeStandingsSceneValid = false;
        // A replay without the native standings HUD has zero matching Texts; the
        // old code then re-ran a full-scene FindObjectsOfType<Text> every 0.5s
        // forever. Retry a couple of times per scene, then stop until it changes.
        private int nativeStandingsEmptyScans = 0;
        private float nextNativeStandingsEmptyScan = 0f;
        private List<Text> nativeStandingsTexts = new List<Text>();
        private Dictionary<Text, FontStyle> nativeStandingsBaseStyles = new Dictionary<Text, FontStyle>();
        private Dictionary<Text, string> nativeStandingsTextLastContent = new Dictionary<Text, string>();
        private Dictionary<Text, GhostLineEntry> nativeStandingsEntryCache = new Dictionary<Text, GhostLineEntry>();
        private int nativeStandingsEntriesEpochSeen = int.MinValue;
        internal bool replayEntriesDirty = true;
        private object lastSyncedReplayRace;
        private System.Reflection.FieldInfo replayRaceFieldInfo;
        private System.Reflection.PropertyInfo replayRacePropInfo;
        private const string NATIVE_SPEED_MEMBER = @"""""'""""""'""""""'""""""""""'""""'""""""""'''''""'''''""'""""''""""''""""";
        private System.Reflection.PropertyInfo nativeSpeedPropInfo;
        private System.Reflection.FieldInfo nativeSpeedFieldInfo;
        private UnityEngine.Transform nativePlayPauseIcon;
        private bool nativeSpeedResolveLogged = false;
        private bool nativeSpeedReadFailedLogged = false;
        private System.Reflection.FieldInfo flightCamImplFieldInfo;
        private System.Reflection.PropertyInfo currentCameraModePropInfo;
        private Slider sliderComponent;
        private float sliderSearchTimer = 0f;
        private float scrubberX = -1f;
        private float scrubberY = -1f;
        private float scrubberTimeSnapshot = 0f;
        private float scrubberMaxSnapshot = 1f;
        private bool scrubberPositionCustomized = false;
        private int overlayScreenWidth = -1;
        private int overlayScreenHeight = -1;
        private bool draggingScrubber = false;
        private float stickCamX = -1f;
        private float stickCamY = -1f;
        private bool stickCamPositionCustomized = false;
        private bool draggingStickCam = false;
        private Vector2 dragStartStickCamPos;
        private float savedStickCamPositionX = 0.5f;
        private float savedStickCamPositionY = 0.8f;
        private bool savedStickCamPositionCustomized = false;
        private float savedPlaybackUiPositionX = 0.5f;
        private float savedPlaybackUiPositionY = 0.9f;
        private bool savedPlaybackUiPositionCustomized = false;
        private float savedSettingsPanelPositionX = 0.7f;
        private float savedSettingsPanelPositionY = 0.4f;
        private bool savedSettingsPanelPositionCustomized = false;
        private bool savedSettingsPanelOpen = false;

        public void SetSavedSettingsPanelOpen(bool open)
        {
            savedSettingsPanelOpen = open;
        }
        private bool isDraggingHandle = false;
        private bool resumePlaybackAfterSeek = false;
        private float playbackSpeed = 1f;
        private bool overridePlayback = false;
        private Vector2 dragStartMouse;
        private Vector2 dragStartBarPos;
        private float fadeAmount = 1f;
        private float settingsFadeAmount = 1f;
        private bool playbackMouseCaptured = false;
        private bool playbackPinned = false;
        private bool settingsPinned = false;
        private bool bindingsOpen = false;
        private bool bindingsOpenedInReplay = false;
        private bool bindingsPinned = false;
        private readonly WindowDrag bindingsDrag = new WindowDrag();
        public bool NativeReplayReady { get { return replayReady; } }
        public bool IsBindingListening { get { return bindingListeningFor != null; } }
        public void MarkBindingCaptureFrame() { bindingCaptureSuppressFrame = Time.frameCount; }
        private string bindingListeningFor = null;
        private bool bindingListeningJustStarted = false;
        private string bindingClickCancelledId = null;
        private int bindingCaptureSuppressFrame = -1;
        private Rect bindingsWindowRect = new Rect(0f, 0f, 0f, 0f);
        private float bindingsFadeAmount = 0f;
        private float bindingsScroll = 0f;
        private bool bindingsScrollDragging = false;
        private class StickGraphState
        {
            public GhostLineEntry pilot;
            public Rect rect = new Rect(-1f, -1f, 460f, STICK_GRAPH_DEFAULT_HEIGHT);
            public bool dragging;
            public bool dragCandidate;
            public Vector2 dragOffset;
            public float fadeAmount;
            public int channel;
            public bool channelDropdown;
            public bool pilotDropdown;
            public bool heatmap;
            public bool pinned;
            // Cached "100%" stat and what it was computed for.
            public GhostLineEntry fullStatPilot;
            public float[] fullStatTimes;
            public int fullStatChannel = -1;
            public float fullStatStart;
            public float fullStatEnd;
            public string fullStatLabel = "";
            public bool resizing;
            public int resizeEdge;
            public int edgeHover;
            public Vector2 resizeStartMouse;
            public Rect resizeStartRect;
            public Texture2D traceTex;
            public Color32[] tracePixels;
            public float traceLastBuildTime;
            public int traceCachePlotW;
            public int traceCachePlotH;
            public GhostLineEntry traceCachePilot;
            public int traceCacheChannel;
            public bool traceCacheHeatmap;
            public Vector4[] traceCacheInputs;
            public float traceCacheLapStart;
            public float traceCacheLapDuration;
            public int traceCacheHeatRange = -1;
        }

        private List<StickGraphState> stickGraphs = new List<StickGraphState>();
        private StickGraphState stickGraphActivated;
        private readonly List<GhostLineEntry> stickGraphPilotOptions = new List<GhostLineEntry>();
        private const float STICK_GRAPH_MIN_WIDTH = 320f;
        private const float STICK_GRAPH_MIN_HEIGHT = 120f;
        private const float STICK_GRAPH_DEFAULT_HEIGHT = 200f;
        private const float STICK_GRAPH_EDGE_THICK = 6f;
        private const float STICK_GRAPH_CORNER_GRAB = 14f;
        private const int STICK_GRAPH_EDGE_RIGHT = 1;
        private const int STICK_GRAPH_EDGE_BOTTOM = 2;
        private const int STICK_GRAPH_EDGE_LEFT = 4;
        private const int STICK_GRAPH_EDGE_TOP = 8;
        private const float STICK_GRAPH_OPEN_OFFSET = 28f;
        private float lastStickCamClickTime = -1f;
        private Vector2 lastStickCamClickPos;
        private static readonly string[] stickGraphChannelNames = { "Throttle", "Yaw", "Roll", "Pitch" };
        private class LiveGraphState
        {
            public GhostLineEntry pilot;
            public MultiplayerPipTarget mpTarget;
            public Rect rect = new Rect(-1f, -1f, 460f, STICK_GRAPH_DEFAULT_HEIGHT);
            public bool dragging;
            public bool dragCandidate;
            public Vector2 dragOffset;
            public float fadeAmount;
            public int channel;
            public bool channelDropdown;
            public bool pilotDropdown;
            public bool pinned;
            public bool resizing;
            public int resizeEdge;
            public int edgeHover;
            public Vector2 resizeStartMouse;
            public Rect resizeStartRect;
            public int historyIndex = 0;
            public bool historyDropdown;
            public Texture2D traceTex;
            public Color32[] tracePixels;
            public float traceLastBuildTime;
            public int traceCachePlotW;
            public int traceCachePlotH;
            public GhostLineEntry traceCachePilot;
            public MultiplayerPipTarget traceCacheMpTarget;
            public int traceCacheChannel;
            public int traceCacheHistory;
            public int traceCacheStateCount;
            public Vector4[] mpSamples;
            public float[] mpSampleTimes;
            public int mpSampleHead;
            public int mpSampleCount;
            public float lastMpSampleTime;
            public bool inputLogged;
            public bool mpSettleDone;
            public int mpReadSuccess;
            public int mpReadFail;
            public string mpPilotName;
            public Color mpColor;
            public float waitPilotSince;
        }
        private List<LiveGraphState> liveInputGraphs = new List<LiveGraphState>();
        private LiveGraphState liveGraphActivated;
        private bool liveGraphSwitchOpen = false;
        private readonly List<GhostLineEntry> liveGraphPilotOptions = new List<GhostLineEntry>();
        private readonly List<MultiplayerPipTarget> liveGraphMpPilotOptions = new List<MultiplayerPipTarget>();
        private static readonly int[] LIVE_HISTORY_OPTIONS = { 5, 10, 20, 60 };
        private float replayEnterTime = 0f;
        private bool replayReady = false;
        private object activeReplayRace;
        private float nextReplaySync = 0f;
        private float nextReplayControlsSearch = 0f;
        private int replayControlsSearchMisses = 0;
        private int frameStepHeldDirection = 0;
        private float frameStepNextRepeat = 0f;
        private bool frameStepPaused = false;
        private int pipForceRenderFrame = -1;
        private float pipStepTargetTime = -1f;
        private const float FRAME_STEP_SECONDS = 1f / 60f;
        private const float FRAME_STEP_INITIAL_DELAY = 0.25f;
        private const float FRAME_STEP_REPEAT_INTERVAL = 1f / 30f;
        private float leaderboardSearchTimer = 0f;
        private GameObject leaderboardSearchInputObject;
        private InputField leaderboardSearchInput;
        private Dropdown leaderboardContentDropdown;
        private Dropdown leaderboardEnvironmentDropdown;
        private int leaderboardEnvironmentValue = -1;
        private bool leaderboardContentRefreshPending = false;
        private string leaderboardPendingContentSignature = "";
        private int leaderboardPendingContentStableFrames = 0;
        private Component leaderboardController;
        private List<Dropdown.OptionData> leaderboardContentOptions = new List<Dropdown.OptionData>();
        private string leaderboardDisplayedSignature = "";
        private string leaderboardSearchSelectedText = "";
        private Text leaderboardOriginalContentText;
        private bool leaderboardOriginalContentTextActive;
        private bool leaderboardOriginalDropdownEnabled;
        private int leaderboardOriginalDropdownValue;
        private bool leaderboardSearchStarting;
        private bool leaderboardSearchRefocusPending;
        private bool leaderboardSearchFocusLock;
        private System.Collections.IEnumerator leaderboardSearchRefocusCoroutine;
        private System.Reflection.FieldInfo leaderboardDropdownPopupField;
        private System.Reflection.FieldInfo leaderboardDropdownItemsField;
        private System.Reflection.PropertyInfo leaderboardPopupItemRectProperty;
        private System.Reflection.PropertyInfo leaderboardPopupItemTextProperty;
        private bool leaderboardPopupLayoutCached;
        private float leaderboardPopupFirstItemX;
        private float leaderboardPopupLastItemY;
        private float leaderboardPopupRowHeight;
        private float leaderboardPopupBaseContentHeight;
        private bool leaderboardSearchUpdating = false;
        private bool leaderboardNotFoundLogged = false;
        private int leaderboardFindFailures = 0;
        private int leaderboardFindSceneCount = -1;
        private bool leaderboardSearchReady = false;
        private List<Toggle> leaderboardSelectedTogglesQueue = new List<Toggle>();
        private HashSet<Toggle> leaderboardDragProcessedToggles = new HashSet<Toggle>();
        private bool isLeaderboardDragSelecting = false;
        private bool leaderboardDragStartWasSelected = false;
        private int leaderboardDragStartRowIndex = -1;
        private int leaderboardDragLastRowIndex = -1;
        private ScrollRect leaderboardCachedScrollRect = null;
        private bool leaderboardScrollRectWasEnabled = true;
        private Transform leaderboardCachedScrollViewContent = null;
        private float leaderboardScrollViewContentSearchTimer = 0f;
        private bool leaderboardScrollViewFallbackExhausted = false;
        private bool isPlaying = false;
        private bool restartRequested = false;
        private bool restartPlaybackPending = false;
        private bool pendingPlayToggle = false;
        private ReplayControls replayController;
        private System.Reflection.MethodInfo attachToDroneMethod;
        private System.Type attachToDroneParameterType;
        private bool cameraDumped = false;
        private bool noTargetLogged = false;
        private Transform orbitTarget;
        private float orbitDist = 0f;
        private float orbitYaw = 0f;
        private float orbitPitch = 0f;
        private bool isOrbiting = false;
        private bool cameraLookDragActive = false;

        private bool cameraLookSampleValid = false;
        private Vector2 lastCameraLookMouse;
        private int cameraLookScreenWidth = -1;
        private int cameraLookScreenHeight = -1;
        private int cameraRawMouseAxesState = 0;
        private float cameraRawMouseScaleX = 1f;
        private float cameraRawMouseScaleY = 1f;
        private struct DroneLabelHitbox
        {
            public GhostLineEntry entry;
            public Rect rect;
            public float depth;
        }
        private List<DroneLabelHitbox> droneLabelHitboxes = new List<DroneLabelHitbox>();
        private static readonly Vector3[] scratchLabelCorners = new Vector3[8];
        private GhostLineEntry droneLabelClickCandidate;
        private GhostLineEntry hoveredDroneLabelEntry;
        private Vector2 droneLabelClickStart;
        private Rect currentPlaybackOverlayRect;
        private Rect currentStickCamOverlayRect;
        private bool currentPlaybackOverlayRectValid;
        private bool currentStickCamOverlayRectValid;
        private bool pendingCameraViewRestore;
        private bool pendingFreeCamViewRestore;
        private bool pendingOrbitViewRestore;
        private Vector3 pendingFreeCamPosition;
        private float pendingFreeCamYaw;
        private float pendingFreeCamPitch;
        private float pendingOrbitDist;
        private float pendingOrbitYaw;
        private float pendingOrbitPitch;
        private bool pendingOrbiting;
        private bool customCameraActive = false;
        private bool startedFromFpv = false;
        private float pendingThirdPersonScroll = 0f;
        private bool freeCamActive = false;
        private float freeCamEntryHoldStart = -1f;
        private const float FREE_CAM_ENTRY_HOLD_SECONDS = 0.2f;
        private bool playheadDragCameraActive = false;
        private FlightCameraController flightCamCtrl;
        private object flightCamImpl = null;
        private System.Reflection.MethodInfo cameraModeSetMethod = null;
        private System.Type cameraModeEnumType = null;
        private int lastNativeCameraMode = 0;
        private int pendingCameraModeRestore = 0;
        private int replayEntryCameraMode = 0;
        private const int CAMERA_MODE_FIRST_PERSON = 1;
        private const int CAMERA_MODE_THIRD_PERSON_CLOSE = 2;
        private const int CAMERA_MODE_THIRD_PERSON_FAR = 3;
        private bool fpvOnReplayPending = false;
        private float fpvForceTimeout = 0f;
        private bool fpvForceLogged = false;
        private bool referenceAutoInitialized = false;
        private bool xrayEnabled = false;
        public bool IsXrayEnabled { get { return xrayEnabled; } }
        private bool darkMode = false;
        private bool stickCamEnabled = true;
    private readonly List<StickCamInstance> stickCamInstances = new List<StickCamInstance>();
    private StickCamInstance currentStickCamInstance;
    private StickCamInstance stickCamResizeTarget;
    private GhostLineEntry lastStickCamSpectateEntry;
    private float lastPipDockClickTime = -1f;
    private Vector2 lastPipDockClickPos;
        private bool smoothDroneTrails = false;
        private float stickCamSize = 1f;
        private float playbackUiSize = 1f;
        private int stickCamEdgeHover = 0;
        private Rect currentStickCamCloseRect = Rect.zero;
        private bool currentStickCamCloseRectValid = false;
        private PiPWindow dockedStickPip = null;
        private int dockedStickSlot = -1;
        private bool stickCamResizing = false;
        private int stickCamResizeEdge = 0;
        private float stickCamResizeStartScale = 1f;
        private Vector2 stickCamResizeStartMouse = Vector2.zero;
        // Screen position of the corner that stays fixed while resizing.
        private Vector2 stickCamResizeAnchor = Vector2.zero;
        private int playbackEdgeHover = 0;
        private bool playbackResizing = false;
        private int playbackResizeEdge = 0;
        private float playbackResizeStartScale = 1f;
        private Vector2 playbackResizeStartMouse = Vector2.zero;
        private List<XrayStructure> xrayStructures = new List<XrayStructure>();
        // Structures from the last query, keyed by collider instance ID; swapped with
        // xrayNextStructureById on each refresh so kept structures aren't rebuilt.
        private Dictionary<int, XrayStructure> xrayStructureById = new Dictionary<int, XrayStructure>();
        private Dictionary<int, XrayStructure> xrayNextStructureById = new Dictionary<int, XrayStructure>();
        // Collider instance ID -> rejected by the static filters (hierarchy/name/size).
        // Those checks walk parents and compare strings, so they run once per collider.
        private readonly Dictionary<int, bool> xrayRejectedById = new Dictionary<int, bool>();
        private Collider[] xrayColliderBuffer = new Collider[2048];
        private Vector3 xrayStructureCenter;
        private bool xrayStructureCenterValid = false;
        private List<Renderer> xrayDroneRenderers = new List<Renderer>();
        private Transform xrayDroneRoot;
        private Mesh xrayMapMesh;
        private readonly Vector3[] xrayMapVertices = new Vector3[XRAY_MAX_LINES * 2];
        private readonly Color32[] xrayMapColors = new Color32[XRAY_MAX_LINES * 2];
        private int[] xrayMapIndices;
        private bool xrayMapCacheValid = false;
        private int xrayStructureRevision = 0;
        private int xrayMapCachedStructureRevision = -1;
        private Vector3 xrayMapCachedDronePosition;
        private Vector3[] xrayDroneBoundsCorners = new Vector3[8];
        private Material xrayWireMaterial;
        private const int XRAY_MAX_LINES = 12000;
        private const float XRAY_FADE_BUFFER = 5f;

        public void LoadSavedPreferences()
        {
            bool savedSmoothing;
            bool savedDelta;
            bool savedNameTags;
            bool savedThroughObjects;
            bool savedXray;
            GhostLinesPlugin.LoadGlobalDisplayPreferences(out savedSmoothing, out savedDelta, out savedNameTags, out savedThroughObjects, out savedXray);
            GhostLinesPlugin.smoothingEnabled = savedSmoothing;
            GhostLinesPlugin.showDeltaDisplay = savedDelta;
            GhostLinesPlugin.showNameTags = savedNameTags;
            GhostLinesPlugin.linesThroughObjects = savedThroughObjects;
            stickCamEnabled = stickCamEnabledConfig.Value;
            stickCamSize = ClampSavedScale(stickCamSizeConfig.Value, STICK_CAM_MIN_SIZE, STICK_CAM_MAX_SIZE, 1f);
            playbackUiSize = ClampSavedScale(playbackUiSizeConfig.Value, 0.6f, 1.6f, 1f);
            darkMode = darkModeConfig.Value;
            smoothDroneTrails = smoothDroneTrailsConfig != null && smoothDroneTrailsConfig.Value;
            autoDownloadWorkshop = autoDownloadWorkshopConfig != null && autoDownloadWorkshopConfig.Value;
            xrayEnabled = savedXray;
            savedSettingsPanelOpen = settingsPanelOpenConfig.Value;
            savedStickCamPositionCustomized = stickCamPositionCustomizedConfig.Value;
            savedStickCamPositionX = Mathf.Clamp01(ClampSavedScale(stickCamPositionXConfig.Value, 0f, 1f, 0.5f));
            savedStickCamPositionY = Mathf.Clamp01(ClampSavedScale(stickCamPositionYConfig.Value, 0f, 1f, 0.8f));
            savedPlaybackUiPositionCustomized = playbackUiPositionCustomizedConfig.Value;
            savedPlaybackUiPositionX = Mathf.Clamp01(ClampSavedScale(playbackUiPositionXConfig.Value, 0f, 1f, 0.5f));
            savedPlaybackUiPositionY = Mathf.Clamp01(ClampSavedScale(playbackUiPositionYConfig.Value, 0f, 1f, 0.9f));
            savedSettingsPanelPositionCustomized = settingsPanelPositionCustomizedConfig.Value;
            savedSettingsPanelPositionX = Mathf.Clamp01(ClampSavedScale(settingsPanelPositionXConfig.Value, 0f, 1f, 0.7f));
            savedSettingsPanelPositionY = Mathf.Clamp01(ClampSavedScale(settingsPanelPositionYConfig.Value, 0f, 1f, 0.4f));
            settingsPanelPositionCustomized = savedSettingsPanelPositionCustomized;
            stickCamPositionCustomized = savedStickCamPositionCustomized;
            scrubberPositionCustomized = savedPlaybackUiPositionCustomized;
            pipEnabled = pipEnabledConfig.Value;
            pipRefreshAuto = pipRefreshAutoConfig.Value;
            int savedPipHz = pipRefreshHzConfig.Value;
            pipRefreshHz = savedPipHz == 15 || savedPipHz == 60 ? savedPipHz : 30;
            float savedPipScale = pipResolutionScaleConfig.Value;
            pipResolutionScale = savedPipScale == 0.25f || savedPipScale == 0.75f || savedPipScale == 1f ? savedPipScale : 0.5f;
            float savedPipFar = pipFarClipConfig.Value;
            pipFarClip = savedPipFar == 25f || savedPipFar == 50f || savedPipFar == 100f || savedPipFar == 150f || savedPipFar == 200f || savedPipFar == 250f || savedPipFar == 300f || savedPipFar == 400f || savedPipFar == 500f ? savedPipFar : 100f;
            pipShowNamesDelta = pipShowNamesDeltaConfig.Value;
            pipExternalMonitor = pipExternalMonitorConfig.Value;
        }

        private static float ClampSavedScale(float value, float min, float max, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp(value, min, max);
        }

        private void SaveDisplayPreferences()
        {
            GhostLinesPlugin.SaveDisplayPreferences(stickCamEnabled, stickCamSize, playbackUiSize, darkMode);
        }

        private void SetSmoothDroneTrails(bool enabled)
        {
            if (smoothDroneTrails == enabled)
                return;
            smoothDroneTrails = enabled;
            GhostLinesPlugin.SaveSmoothDroneTrails(enabled);
            ClearSmoothDroneTrails();
            MarkPiPWindowsDirty();
        }

        private void SaveCurrentOverlayPositions()
        {
            float stickX = savedStickCamPositionX;
            float stickY = savedStickCamPositionY;
            float playbackX = savedPlaybackUiPositionX;
            float playbackY = savedPlaybackUiPositionY;
            float settingsX = savedSettingsPanelPositionX;
            float settingsY = savedSettingsPanelPositionY;

            if (stickCamPositionCustomized && Screen.width > 0 && Screen.height > 0 && stickCamX >= 0f && stickCamY >= 0f)
            {
                stickX = stickCamX / Screen.width;
                stickY = stickCamY / Screen.height;
                savedStickCamPositionCustomized = true;
            }
            if (scrubberPositionCustomized && Screen.width > 0 && Screen.height > 0 && scrubberX >= 0f && scrubberY >= 0f)
            {
                playbackX = scrubberX / Screen.width;
                playbackY = scrubberY / Screen.height;
                savedPlaybackUiPositionCustomized = true;
            }
            if (settingsPanelPositionCustomized && settingsRect.x >= 0f && settingsRect.y >= 0f && Screen.width > 0 && Screen.height > 0)
            {
                settingsX = settingsRect.x / Screen.width;
                settingsY = settingsRect.y / Screen.height;
                savedSettingsPanelPositionCustomized = true;
            }

            savedStickCamPositionX = Mathf.Clamp01(stickX);
            savedStickCamPositionY = Mathf.Clamp01(stickY);
            savedPlaybackUiPositionX = Mathf.Clamp01(playbackX);
            savedPlaybackUiPositionY = Mathf.Clamp01(playbackY);
            savedSettingsPanelPositionX = Mathf.Clamp01(settingsX);
            savedSettingsPanelPositionY = Mathf.Clamp01(settingsY);
            GhostLinesPlugin.SaveOverlayPositions(
                savedStickCamPositionCustomized,
                savedStickCamPositionX,
                savedStickCamPositionY,
                savedPlaybackUiPositionCustomized,
                savedPlaybackUiPositionX,
                savedPlaybackUiPositionY,
                savedSettingsPanelPositionCustomized,
                savedSettingsPanelPositionX,
                savedSettingsPanelPositionY);
        }

        private static float SnapOverlayToHorizontalCenter(float x)
        {
            float center = Screen.width * 0.5f;
            return Mathf.Abs(x - center) <= 24f ? center : x;
        }
        // Clip radius plus the rebuild slack, so structures the drone can reach
        // before the next re-query are already in the set.
        private const float XRAY_QUERY_RADIUS = XRAY_RENDER_RADIUS + XRAY_FADE_BUFFER + XRAY_REBUILD_DISTANCE;
        private const float XRAY_RENDER_RADIUS = 15f;
        private const float XRAY_REBUILD_DISTANCE = 1.5f;
        private Vector3 freeCamPosition;
        private float freeCamYaw = 0f;
        private float freeCamPitch = 0f;
        private const float FREE_CAM_SPEED = 6f;
        private const float FREE_CAM_SHIFT_MAX_SPEED = 48f;
        private const float FREE_CAM_SHIFT_ACCELERATION = 18f;
        private const float FREE_CAM_SHIFT_DECELERATION = 72f;
        private const float FREE_CAM_MOUSE_SENSITIVITY = 0.3f;
        private float freeCamCurrentSpeed = FREE_CAM_SPEED;
        private bool mpFreeCamActive = false;
        private Vector3 mpFreeCamPosition;
        private float mpFreeCamYaw = 0f;
        private float mpFreeCamPitch = 0f;
        private float mpFreeCamCurrentSpeed = FREE_CAM_SPEED;
        private bool mpZoomActive = false;
        private int mpZoomPilotIndex = 0;
        private int multiplayerPipScanVersion = 0;
        private List<MultiplayerPipTarget> mpZoomSorted = new List<MultiplayerPipTarget>();
        private int mpZoomSortedVersion = -1;
        private bool mpZoomFovCaptured = false;
        private float mpZoomNormalFov = 60f;
        private float mpZoomFovBias = 0f;
        private float mpZoomCurrentFov = 60f;
        private float mpZoomSmoothFov = 60f;
        private float mpZoomTransitionUntil = 0f;
        private float mpZoomTransitionStartYaw = 0f;
        private float mpZoomTransitionStartPitch = 0f;
        private Vector3 mpZoomTrackPos;
        private bool mpZoomTrackValid = false;
        private MultiplayerPipTarget mpZoomTrackTarget = null;
        private const float MP_ZOOM_TRACK_TAU = 0.08f;
        private bool mpShowDroneNames = false;
        private GUIStyle mpNameLabelStyle = null;
        private int mpRadioStartRow = -1;
        private int mpRadioStartColumn = -1;
        private int mpRadioLastColumn = -1;
        private bool mpRadioDragSelecting = false;
        private bool mpRadioDragMoved = false;
        private bool mpRadioStartValue = false;
        private int mpRadioLastIndex = -1;
        private readonly HashSet<int> mpRadioProcessed = new HashSet<int>();
        private Dictionary<Text, ChatTextState> mpChatStates = new Dictionary<Text, ChatTextState>();
        private float mpChatScanAt = 0f;
        private int mpChatLastChildCount = -1;
        private float mpChatRescanAt = 0f;
        private RectTransform mpChatMessageParent = null;
        private InputField mpChatInputField = null;
        private ScrollRect mpChatScrollRect = null;
        private bool mpChatScrollOrigEnabled = true;
        private bool mpChatColorPickerOpen = false;
        private Font mpChatColorButtonFont = null;
        private bool mpChatWheelOpen = false;
        private int mpChatWheelDrag = 0;
        private float mpChatWheelH = 0f;
        private float mpChatWheelS = 1f;
        private float mpChatWheelV = 1f;
        private string mpWheelHexText = null;
        private const string MpWheelHexCtrl = "GlWheelHex";
        private Texture2D mpChatWheelTex = null;
        private Texture2D mpChatRampTex = null;
        private Texture2D mpChatWheelMarkerTex = null;
        private Texture2D mpChatRoundBtnTex = null;
        private int mpChatApplyFrame = -100;
        private System.Collections.IEnumerator chatSelectionCoroutine = null;
        private ChatInputState mpChatInputStyle = new ChatInputState();
        private int mpStickyVisLo = -1;
        private int mpStickyVisHi = -1;
        private string mpStickyPlainText = null;
        private Text mpSelAnchorT = null;
        private int mpSelAnchorI = -1;
        private Text mpSelFocusT = null;
        private int mpSelFocusI = -1;
        private bool mpSelDragging = false;
        private bool mpSelScrollLocked = false;
        private int mpSelScrollFrame = -1;
        private bool mpSelShowMenu = false;
        private Vector2 mpSelMenuPos = Vector2.zero;
        private Rect mpFormatUiRect = Rect.zero;
        public static bool mpChatSelActive = false;
        private static readonly Color[] MP_PILOT_PALETTE = new Color[]
        {
            new Color(0.25f, 0.62f, 0.95f),
            new Color(0.95f, 0.35f, 0.35f),
            new Color(0.35f, 0.85f, 0.45f),
            new Color(0.95f, 0.8f, 0.3f),
            new Color(0.75f, 0.45f, 0.95f),
            new Color(0.95f, 0.55f, 0.25f),
            new Color(0.4f, 0.9f, 0.9f),
            new Color(0.9f, 0.6f, 0.8f)
        };
        private bool mpInSession = false;
        private class DroneSwitchEntry
        {
            public string localId;
            public string name;
            public System.WeakReference configRef;
            public DroneSwitchEntry(object config, string entryName, string entryLocalId)
            {
                configRef = new System.WeakReference(config);
                name = entryName;
                localId = entryLocalId;
            }
            public object GetConfig()
            {
                return configRef != null ? configRef.Target : null;
            }
        }
        private List<DroneSwitchEntry> droneSwitchEntries = new List<DroneSwitchEntry>();
        private bool wasMpInSession = false;
    private bool wasMpInRoom = false;
        private bool dronesNeedScan = true;
        private float droneSwitchScanRetryAt = 0f;
        private int lastLoggedDroneCount = -1;
        private float dronesScroll = 0f;
        private string dronesSearchTerm = "";
        private bool dronesSearchFocused = false;
    private bool droneSwitchDropdownOpen = false;
        // Deferred floating dropdown (drawn on top of the menu body, so opening it
        // never reflows/expands the settings UI).
        private Rect droneDropdownAnchor = new Rect(0f, 0f, 0f, 0f);
        private float droneDropdownAlpha = 1f;
        private bool droneDropdownPipBlock = false;
        private bool droneDropdownPending = false;
        private object activeDroneConfig = null;
        private string activeDroneName = "";
        private bool droneSwitchReflectionDone = false;
        private bool droneSwitchReflectionReady = false;
        private System.Type inGameMenuDroneSelectionType;
        private System.Reflection.MethodInfo inGameMenuOnDroneSelectedMethod;
        private System.Type droneSelectionMenuType;
        private System.Type userDataType;
        private System.Type currentContentContainerType;
        private System.Reflection.MethodInfo droneLineupProviderGetter;
        private System.Reflection.MethodInfo droneLineupProviderMethod;
        private System.Reflection.MethodInfo xUseGetter;
        private System.Reflection.MethodInfo xM1Method;
        private System.Reflection.MethodInfo userDataSetter;
        private System.Reflection.MethodInfo yUseGetter;
        private System.Reflection.MethodInfo yM2Method;
        private System.Reflection.MethodInfo mpObjectGetter;
        private System.Reflection.MethodInfo mpObjectSetter;
        private System.Reflection.MethodInfo cccUseGetter;
        private System.Reflection.MethodInfo cccSetDroneConfiguration;
        private System.Reflection.MethodInfo cccInMultiplayerGetter;
        private System.Reflection.MethodInfo cccDroneConfigGetter;
        private System.Reflection.MethodInfo flightManagerUseGetter;
        private System.Reflection.MethodInfo flightManagerResetMethod;
        private System.Reflection.PropertyInfo droneNameProperty;
        private System.Reflection.PropertyInfo droneLocalIdProperty;
        private const float HOLD_ZOOM_FOV_SCALE = 0.35f;
        private const float HOLD_ZOOM_MIN_FOV = 10f;
        private const float HOLD_ZOOM_EXTRA_MIN_FOV = 2f;
        private const float HOLD_ZOOM_SMOOTH_TAU = 0.22f;
        private const float HOLD_ZOOM_SCROLL_STEP = 6f;
        private const float HOLD_ZOOM_SCROLL_RATIO = 1.39f;
        private bool holdZoomFovCaptured = false;
        private float holdZoomNormalFov = 60f;
        private float holdZoomFovBias = 0f;
        private float holdZoomCurrentFov = 60f;
        private float holdZoomSmoothFov = 60f;
        private bool holdZoomActive = false;
        private float stableReplayTime = 0f;
        private float prevReplayTime = 0f;
        private float replayTimeDelta = 0f;
        private float predictionLead = 0f;
        private float displayReplayTime = 0f;
        private float pinnedPiPReplayTime = -1f;
        private float zoomTransitionUntil = 0f;
        private float zoomTransitionStartYaw = 0f;
        private float zoomTransitionStartPitch = 0f;
        private const float ZOOM_TRANSITION_DURATION = 0.25f;
        private const float DRONE_MARKER_HEIGHT = 2.8f;
        private const float DRONE_TETHER_START_OFFSET = 0.15f;
        private GhostLineEntry spectatedEntry;
        public GhostLineEntry referenceEntry;
        public static GhostLinePoller Instance;
        private Dictionary<GhostLineEntry, Transform> ghostDroneMap = new Dictionary<GhostLineEntry, Transform>();
        private Dictionary<GhostLineEntry, Vector3> ghostSmoothedPos = new Dictionary<GhostLineEntry, Vector3>();
        private Dictionary<GhostLineEntry, Quaternion> ghostSmoothedRot = new Dictionary<GhostLineEntry, Quaternion>();
        private float ghostPosBlend = 0f;
        private Dictionary<GhostLineEntry, Transform> ghostCamMountMap = new Dictionary<GhostLineEntry, Transform>();
        private bool pipEnabled = false;
        private bool pipRefreshAuto = true;
        private int pipRefreshHz = 30;
        private float pipResolutionScale = 0.5f;
        private List<PiPWindow> pipWindows = new List<PiPWindow>();
        private PiPWindow pipActivated = null;
        private class MultiplayerPipTarget
        {
            public Transform root;
            public Photon.Pun.PhotonView photonView;
            public Transform mount;
            public string playerName;
            public int viewId;
            public int ownerActor;
            public bool enabled;
            public object syncComponent;
            public System.Reflection.FieldInfo playbackStateField;
            public System.Reflection.FieldInfo containerField;
            public System.Reflection.FieldInfo inputField;
            public float lastSyncAttempt;
            public float lastSceneAttempt;
            public object lastVerifiedComponent;
            public bool readExLogged;
            public float lastFailLog;
            public float firstSeenTime;
            public float lastSeenAt;
            public Transform syncRoot;
            public Photon.Pun.PhotonView syncView;
            public float resolvedAt;
            public Transform lastMountRoot;
            public bool tagVisible;
            public Color pilotColor;
            public bool pilotColorResolved;
            public Transform pilotColorRoot;
            public int pilotColorSource;
            public float lastColorAttemptAt;
        }

        private class PipDockEntry
        {
            public StickCamInstance stick;
            public LiveGraphState graph;
            public StickGraphState stickGraph;
            public int slot;
        }

        private class LobbyWatchRoom
        {
            public string Name;
            public int PlayerCount;
            public int MaxPlayers;
            public bool IsOpen;
            public bool IsVisible;
            public bool Listed;
            public float LastUpdate;
            public ExitGames.Client.Photon.Hashtable CustomProps;
            public string HostName;
            public string TrackName;
            public string EnvName;
            public int GameMode = -1;
            public int GameModeSub = -1;
            public bool PropsLogged;
        }

        private class MultiplayerPipWindow
        {            public MultiplayerPipTarget target;
            public Rect rect;
            public List<PipDockEntry> docked = new List<PipDockEntry>();
            public GameObject camObject;
            public Camera cam;
            public RenderTexture rt;
            public int rtW = -1;
            public int rtH = -1;
            public float nextRenderAt;
            public bool dragging = false;
            public bool dragCandidate = false;
            public float dragAccumX = 0f;
            public float dragAccumY = 0f;
            public Rect dragStartRect;
            public bool resizing = false;
            public int resizeEdge = 0;
            public int edgeHover = 0;
            public bool dropdownOpen = false;
            public bool pinned = true;
            public float resizeAccumX = 0f;
            public float resizeAccumY = 0f;
            public Rect resizeStartRect;
            public float lastSeenAt = 0f;
            public float orphanedSince = -1f;
            public float chromeAlpha = 1f;
            public NativePipExternal nativePip;
        }

        private List<MultiplayerPipTarget> multiplayerPipTargets = new List<MultiplayerPipTarget>();
        private List<MultiplayerPipWindow> multiplayerPipWindows = new List<MultiplayerPipWindow>();
        private bool pipExternalMonitor = false;
        private List<NativePipExternal> nativePips = new List<NativePipExternal>();
        private static readonly List<NativePipExternal> nativePipRegistry = new List<NativePipExternal>();
        private static readonly object nativePipRegistryLock = new object();
        private Texture2D nativePipReadTex = null;

        private int multiplayerPipLastLoggedScanCount = -1;
        private float mouseAxisScaleX = 1f;
        private float mouseAxisScaleY = -1f;
        private Vector2 lastFrameMousePos = Vector2.zero;
        private bool lastFrameMousePosValid = false;
        private bool photonScanRan = false;
        private Dictionary<Transform, List<GameObject>> multiplayerPipTagObjects = new Dictionary<Transform, List<GameObject>>();
        private float multiplayerPipTagPruneAt = 0f;
        private List<GameObject> multiplayerPipRenderHiddenTags = null;
        private List<GhostLineEntry> pipRenderHiddenEntries = null;
        private float pipFov = 60f;
        private float pipNearClip = 0.05f;
        private float pipFarClip = 100f;
        private bool pipShowNamesDelta = true;
        private bool pipCullingMaskLogged = false;
        private const float PIP_RT_MAX_WIDTH = 1280f;
        private const float PIP_RT_MAX_HEIGHT = 720f;
        private const float PIP_MIN_WIDTH = 160f;
        private const float PIP_MIN_HEIGHT = 110f;
        private const float PIP_TITLE_HEIGHT = 24f;
        private const float PIP_RESIZE_GRAB = 14f;
        private const float PIP_OPEN_OFFSET = 28f;
        private const float PIP_EDGE_THICK = 6f;
        private const int PIP_RT_RECREATE_TOLERANCE = 3;
        private const int PIP_EDGE_RIGHT = 1;
        private const int PIP_EDGE_BOTTOM = 2;
        private const int PIP_EDGE_LEFT = 4;
        private const int PIP_EDGE_TOP = 8;
        private const float PIP_CAMERA_FORWARD_OFFSET = 0.25f;
        private const float PIP_CAMERA_UP_OFFSET = 0.04f;
        private const float MULTIPLAYER_PIP_TARGET_GRACE = 5f;
        private class GhostTrailState
        {
            public TrailRenderer trail;
            public bool enabled;
            public bool emitting;
        }

        private class SmoothDroneTrailState
        {
            public GameObject objectRoot;
            public LineRenderer line;
            public List<Vector3> points = new List<Vector3>();
            public List<float> times = new List<float>();
            public float lastTime = -1f;
            public Vector3 lastPosition;
            public int renderedCount;
        }

        private class XrayStructure
        {
            public Bounds bounds;
            // Box corners, wired up by xrayBoxEdgePairs.
            public Vector3[] corners;
            public float sortKey;
        }
        private static readonly int[] xrayBoxEdgePairs =
        {
            0, 1, 1, 2, 2, 3, 3, 0,
            4, 5, 5, 6, 6, 7, 7, 4,
            0, 4, 1, 5, 2, 6, 3, 7
        };
        private Dictionary<GhostLineEntry, List<GhostTrailState>> ghostTrailStates = new Dictionary<GhostLineEntry, List<GhostTrailState>>();
        // One-way suppression marker: ApplyGhostDroneTransforms re-suppresses every
        // mapped ghost every frame; without this the native Clear()/disable calls
        // run N×trails times per frame for nothing.
        private readonly HashSet<GhostLineEntry> ghostTrailsSuppressedSet = new HashSet<GhostLineEntry>();
        private Dictionary<GhostLineEntry, SmoothDroneTrailState> smoothDroneTrailStates = new Dictionary<GhostLineEntry, SmoothDroneTrailState>();
        private const float SMOOTH_TRAIL_SECONDS = 1.25f;
        private const float SMOOTH_TRAIL_SAMPLE_DISTANCE = 0.12f;
        private const int SMOOTH_TRAIL_MAX_POINTS = 96;
        private const float SMOOTH_TRAIL_WIDTH = 0.055f;
        private Material smoothDroneTrailMaterial;
        private Dictionary<Transform, List<GhostTrailState>> unmatchedGhostTrailStates = new Dictionary<Transform, List<GhostTrailState>>();
        private float ghostDroneSearchTimer = 0f;
        private int ghostMatchAttempts = 0;
        private const int GHOST_MATCH_MAX_ATTEMPTS = 15;
        private const float GHOST_MATCH_RETRY_INTERVAL = 2f;
        private bool lateIsThirdPerson = true;
        private Vector3 ghostDronePos;
        private bool ghostDronePosValid = false;
        private Texture2D iconPlay;
        private Texture2D iconPause;
        private Texture2D iconRewind;
        private Texture2D iconFF;
        private Texture2D iconLoop;
        private Texture2D iconTrim;
        private Texture2D iconSettings;
        private Texture2D iconPin;
        private Texture2D iconBindings;
        private bool iconsInitialized;
        private bool loopReplayEnabled = false;
        private bool trimEnabled = false;
        private float trimStart = 0f;
        private float trimEnd = 0f;
        private bool draggingTrimStart = false;
        private bool draggingTrimEnd = false;
        private bool trimResumePlayback = false;
        private Texture2D stickRingTex;
        private Texture2D stickCloseTex;
    private GUIStyle settingsLabelStyle;
    private GUIStyle settingsSmallStyle;
    private GUIStyle settingsNameStyle;
    private GUIStyle settingsPilotHeaderStyle;
        private GUIStyle settingsTextFieldStyle;
        private GUIStyle settingsSmallCenterStyle;
        private GUIStyle settingsAxisLabelStyle;
        private GUIStyle settingsHeaderStyle;
        private GUIStyle settingsSectionStyle;
        private GUIStyle settingsSmallRightStyle;
        private GUIStyle settingsSectionRightStyle;
        private GUIStyle settingsSectionLeftStyle;
        private GUIStyle settingsDropdownBarStyle;
        private GUIStyle settingsPlusStyle;
        private GUIStyle settingsCloseStyle;
        private GUIStyle settingsCloseBigStyle;
        private GUIStyle settingsTooltipStyle;
        private bool settingsTooltipDarkMode;
        private bool settingsStylesDarkMode;
        private GUIStyle playbackTimeLeftStyle;
        private GUIStyle playbackTimeRightStyle;
        private GUIStyle playbackSpeedStyle;
        private Texture2D targetRingTex;
        private Texture2D targetDotTex;
        private Texture2D targetFillTex;
    }
}
