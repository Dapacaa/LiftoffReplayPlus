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
    public partial class GhostLinePoller
    {
        private void ProbeLog(string message)
        {
            pluginLog.LogInfo("[GhostLines][LOBBY] " + message);
        }

        private void MaybePatchLoadFast()
        {
            if (PatchRegistry.LoadFastAttempted)
                return;
            PatchRegistry.LoadFastAttempted = true;
            try
            {
                System.Reflection.Assembly gameAsm = null;
                try { gameAsm = System.Reflection.Assembly.Load("Assembly-CSharp"); } catch { }
                if (gameAsm == null && gameJoinMethod != null)
                {
                    try { gameAsm = gameJoinMethod.DeclaringType.Assembly; } catch { }
                }
                if (gameAsm == null)
                {
                    ProbeLog("load-fast: game assembly not found");
                    return;
                }
                System.Type[] types = GameAccess.TypesOf(gameAsm);
                System.Reflection.MethodInfo target = null;
                int matches = 0;
                foreach (var t in types)
                {
                    if (t == null)
                        continue;
                    System.Reflection.FieldInfo hf = null;
                    try { hf = t.GetField("hostInactivityMinutes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic); }
                    catch { continue; }
                    if (hf == null)
                        continue;
                    System.Type[] nested = null;
                    try { nested = t.GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic); }
                    catch { continue; }
                    foreach (var nt in nested)
                    {
                        System.Reflection.MethodInfo mv = null;
                        try { mv = nt.GetMethod("MoveNext", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic); }
                        catch { continue; }
                        if (mv == null)
                            continue;
                        if (MoveNextHasLoadWait(mv))
                        {
                            matches++;
                            target = mv;
                        }
                    }
                }
                if (matches != 1 || target == null)
                {
                    ProbeLog("load-fast: match count=" + matches + ", skipping");
                    return;
                }
                GhostLinesPlugin.harmony.Patch(target, transpiler: new HarmonyMethod(typeof(GhostLinePoller), nameof(LoadWaitSkipTranspiler)));
                ProbeLog("load-fast: patched waiting-room load wait");
            }
            catch (Exception ex) { ProbeLog("load-fast error: " + ex.Message); }
        }

        private bool MoveNextHasLoadWait(System.Reflection.MethodInfo mv)
        {
            try
            {
                var body = mv.GetMethodBody();
                if (body == null)
                    return false;
                byte[] il = body.GetILAsByteArray();
                if (il == null || il.Length == 0)
                    return false;
                var module = mv.Module;
                if (ilOpOneByte == null || ilOpTwoByte == null)
                    BuildIlOpTables();
                bool hasWait1 = false;
                bool hasLoader = false;
                bool haveR4 = false;
                float lastR4 = 0f;
                int i = 0;
                while (i < il.Length)
                {
                    byte b = il[i++];
                    System.Reflection.Emit.OpCode op;
                    if (b == 0xFE)
                    {
                        if (i >= il.Length)
                            break;
                        op = ilOpTwoByte[il[i++]];
                    }
                    else
                        op = ilOpOneByte[b];
                    int opSize = 0;
                    switch (op.OperandType)
                    {
                        case System.Reflection.Emit.OperandType.InlineNone:
                            opSize = 0;
                            break;
                        case System.Reflection.Emit.OperandType.ShortInlineBrTarget:
                        case System.Reflection.Emit.OperandType.ShortInlineI:
                        case System.Reflection.Emit.OperandType.ShortInlineVar:
                            opSize = 1;
                            break;
                        case System.Reflection.Emit.OperandType.InlineVar:
                            opSize = 2;
                            break;
                        case System.Reflection.Emit.OperandType.InlineBrTarget:
                        case System.Reflection.Emit.OperandType.InlineField:
                        case System.Reflection.Emit.OperandType.InlineI:
                        case System.Reflection.Emit.OperandType.InlineMethod:
                        case System.Reflection.Emit.OperandType.InlineSig:
                        case System.Reflection.Emit.OperandType.InlineString:
                        case System.Reflection.Emit.OperandType.InlineTok:
                        case System.Reflection.Emit.OperandType.InlineType:
                        case System.Reflection.Emit.OperandType.ShortInlineR:
                            opSize = 4;
                            break;
                        case System.Reflection.Emit.OperandType.InlineI8:
                        case System.Reflection.Emit.OperandType.InlineR:
                            opSize = 8;
                            break;
                        case System.Reflection.Emit.OperandType.InlineSwitch:
                            if (i + 4 > il.Length) { i = il.Length; break; }
                            opSize = 4 + 4 * System.BitConverter.ToInt32(il, i);
                            break;
                        default:
                            opSize = 0;
                            break;
                    }
                    if (op.Equals(System.Reflection.Emit.OpCodes.Ldc_R4) && opSize == 4 && i + 4 <= il.Length)
                    {
                        lastR4 = System.BitConverter.ToSingle(il, i);
                        haveR4 = true;
                    }
                    else if (op.OperandType == System.Reflection.Emit.OperandType.InlineMethod && i + 4 <= il.Length)
                    {
                        int token = System.BitConverter.ToInt32(il, i);
                        try
                        {
                            var mref = module.ResolveMethod(token);
                            if (mref is System.Reflection.ConstructorInfo && mref.DeclaringType == typeof(UnityEngine.WaitForSecondsRealtime))
                            {
                                if (haveR4 && System.Math.Abs(lastR4 - 1f) < 0.0001f)
                                    hasWait1 = true;
                            }
                            else if (mref.DeclaringType != null && mref.DeclaringType.FullName != null &&
                                mref.DeclaringType.FullName.IndexOf("SceneLoader", System.StringComparison.Ordinal) >= 0)
                                hasLoader = true;
                        }
                        catch { }
                        haveR4 = false;
                    }
                    else
                        haveR4 = false;
                    i += opSize;
                }
                return hasWait1 && hasLoader;
            }
            catch { return false; }
        }

        private static System.Reflection.Emit.OpCode[] ilOpOneByte = null;
        private static System.Reflection.Emit.OpCode[] ilOpTwoByte = null;

        private static void BuildIlOpTables()
        {
            var one = new System.Reflection.Emit.OpCode[256];
            var two = new System.Reflection.Emit.OpCode[256];
            foreach (var f in typeof(System.Reflection.Emit.OpCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (f.FieldType != typeof(System.Reflection.Emit.OpCode))
                    continue;
                var op = (System.Reflection.Emit.OpCode)f.GetValue(null);
                ushort v = unchecked((ushort)op.Value);
                if (v < 0x100)
                    one[v] = op;
                else if ((v & 0xFF00) == 0xFE00)
                    two[v & 0xFF] = op;
            }
            ilOpOneByte = one;
            ilOpTwoByte = two;
        }

        private static float ModLoadWaitDuration()
        {
            var p = poller;
            if (p != null && p.lobbySwitchActive)
                return 0f;
            return 1f;
        }

        private static System.Collections.Generic.IEnumerable<CodeInstruction> LoadWaitSkipTranspiler(System.Collections.Generic.IEnumerable<CodeInstruction> insns)
        {
            var list = new System.Collections.Generic.List<CodeInstruction>(insns);
            int replaced = 0;
            for (int i = 0; i + 1 < list.Count; i++)
            {
                if (list[i].opcode == System.Reflection.Emit.OpCodes.Ldc_R4 && list[i].operand is float f && System.Math.Abs(f - 1f) < 0.0001f &&
                    list[i + 1].opcode == System.Reflection.Emit.OpCodes.Newobj && list[i + 1].operand is System.Reflection.ConstructorInfo ci &&
                    ci.DeclaringType == typeof(UnityEngine.WaitForSecondsRealtime))
                {
                    list[i] = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, ((System.Func<float>)ModLoadWaitDuration).Method);
                    replaced++;
                }
            }
            var p = poller;
            if (p != null)
                p.ProbeLog("load-fast transpiler: replaced " + replaced + " wait(s)");
            return list;
        }

        private void HookPhotonProbe()
        {
            if (PatchRegistry.PhotonProbeHooked)
                return;
            PatchRegistry.PhotonProbeHooked = true;
            try
            {
                System.Type target = null;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "Assembly-CSharp")
                        continue;
                    System.Type[] types = GameAccess.TypesOf(asm);
                    foreach (var t in types)
                    {
                        if (t == null)
                            continue;
                        try
                        {
                            foreach (var iface in t.GetInterfaces())
                            {
                                if (iface == typeof(Photon.Realtime.ILobbyCallbacks))
                                {
                                    target = t;
                                    break;
                                }
                            }
                        }
                        catch { }
                        if (target != null)
                            break;
                    }
                    if (target != null)
                        break;
                }
                if (target == null)
                {
                    ProbeLog("room-list hook failed: no ILobbyCallbacks implementor found");
                    return;
                }
                System.Reflection.MethodInfo targetMethod = null;
                var map = target.GetInterfaceMap(typeof(Photon.Realtime.ILobbyCallbacks));
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    if (map.InterfaceMethods[i].Name == "OnRoomListUpdate")
                    {
                        targetMethod = map.TargetMethods[i];
                        break;
                    }
                }
                if (targetMethod == null)
                {
                    ProbeLog("room-list hook failed: OnRoomListUpdate slot not found on " + target.FullName);
                    return;
                }
                var postfix = new HarmonyMethod(AccessTools.Method(typeof(GhostLinePoller), nameof(ProbeRoomListPostfix)));
                GhostLinesPlugin.harmony.Patch(targetMethod, postfix: postfix);
                ProbeLog("hooked OnRoomListUpdate on " + target.FullName);

                // Capture the game's own join entry (one static string method) so mod joins
                // reuse the exact game browser path.
                try
                {
                    var joinCapturePrefix = new HarmonyMethod(AccessTools.Method(typeof(GhostLinePoller), nameof(JoinRoomCapturePrefix)));
                    foreach (var m in typeof(Photon.Pun.PhotonNetwork).GetMethods())
                    {
                        if (m.Name != "JoinRoom" || !m.IsStatic)
                            continue;
                        var ps = m.GetParameters();
                        if (ps.Length >= 1 && ps[0].ParameterType == typeof(string))
                            GhostLinesPlugin.harmony.Patch(m, prefix: joinCapturePrefix);
                    }
                }
                catch (Exception joinHookEx)
                {
                    ProbeLog("join capture hook error: " + joinHookEx.Message);
                }
            }
            catch (Exception ex)
            {
                ProbeLog("room-list hook error: " + ex.Message);
            }
        }

        private static void ProbeRoomListPostfix(System.Collections.Generic.List<Photon.Realtime.RoomInfo> __0)
        {
            poller?.ApplyLobbyRoomDelta(__0);
        }

        private static void JoinRoomCapturePrefix(string roomName)
        {
            var p = poller;
            if (p == null || p.gameJoinMethod != null)
                return;
            try
            {
                System.Reflection.Assembly ourAssembly = typeof(GhostLinePoller).Assembly;
                var st = new System.Diagnostics.StackTrace(false);
                for (int i = 0; i < st.FrameCount; i++)
                {
                    var m = st.GetFrame(i).GetMethod();
                    if (m == null)
                        continue;
                    var dt = m.DeclaringType;
                    if (dt == null)
                        continue;
                    if (dt.Assembly == ourAssembly || m.Name.StartsWith("DMD<"))
                        continue;
                    string an = dt.Assembly.GetName().Name;
                    if (an.StartsWith("Photon") || an.StartsWith("Harmony") ||
                        an == "ImpossibleOdds.Photon.WebRpc" || an == "ImpossibleOdds.Examples.Photon.WebRpc" ||
                        dt.FullName.StartsWith("System.") || dt.FullName.StartsWith("UnityEngine.") ||
                        dt.FullName.StartsWith("HarmonyLib"))
                        continue;
                    p.gameJoinMethod = m;
                    p.gameJoinTypeName = dt.FullName;
                    p.ProbeLog("captured game join entry: " + dt.FullName + "::" + m.Name +
                        " static=" + m.IsStatic + " mono=" + typeof(UnityEngine.Object).IsAssignableFrom(dt));
                    break;
                }
            }
            catch { }
        }

        private void ApplyLobbyRoomDelta(System.Collections.Generic.List<Photon.Realtime.RoomInfo> roomList)
        {
            if (roomList == null)
                return;
            float now = Time.unscaledTime;
            for (int i = 0; i < roomList.Count; i++)
            {
                Photon.Realtime.RoomInfo r = roomList[i];
                if (r == null || string.IsNullOrEmpty(r.Name))
                    continue;
                if (r.PlayerCount == 0 && r.MaxPlayers == 0)
                {
                    LobbyWatchRoom gone;
                    if (lobbyRoomStates.TryGetValue(r.Name, out gone) && gone != null)
                    {
                        gone.Listed = false;
                        gone.LastUpdate = now;
                    }
                    continue;
                }
                LobbyWatchRoom room;
                if (!lobbyRoomStates.TryGetValue(r.Name, out room) || room == null)
                {
                    room = new LobbyWatchRoom { Name = r.Name };
                    lobbyRoomStates[r.Name] = room;
                }
                bool wasOpenSlot = room.Listed && room.IsOpen && room.PlayerCount < room.MaxPlayers;
                room.PlayerCount = r.PlayerCount;
                room.MaxPlayers = r.MaxPlayers;
                room.IsOpen = r.IsOpen;
                room.IsVisible = r.IsVisible;
                room.Listed = true;
                room.LastUpdate = now;
                room.CustomProps = r.CustomProperties;
                ExtractRoomDisplayProps(room);
                if (!room.PropsLogged && lobbyPropsLogCount < 12)
                {
                    room.PropsLogged = true;
                    lobbyPropsLogCount++;
                    var sb = new System.Text.StringBuilder();
                    sb.Append("room '").Append(r.Name).Append("' props:");
                    if (room.CustomProps != null)
                    {
                        foreach (System.Collections.DictionaryEntry de in room.CustomProps)
                            sb.Append(' ').Append(de.Key).Append('=').Append(de.Value);
                    }
                    else
                        sb.Append(" none");
                    ProbeLog(sb.ToString());
                }
                if (!wasOpenSlot && room.IsOpen && room.PlayerCount < room.MaxPlayers)
                    OnSelectedSlotOpened(room);
            }
            List<string> prune = null;
            foreach (var kv in lobbyRoomStates)
            {
                LobbyWatchRoom w = kv.Value;
                if (w == null || selectedLobbies.Contains(kv.Key))
                    continue;
                if (!w.Listed && now - w.LastUpdate > 30f)
                {
                    if (prune == null)
                        prune = new List<string>();
                    prune.Add(kv.Key);
                }
            }
            if (prune != null)
                foreach (string key in prune)
                    lobbyRoomStates.Remove(key);
        }

        private void ExtractRoomDisplayProps(LobbyWatchRoom room)
        {
            room.HostName = null;
            room.TrackName = null;
            room.EnvName = null;
            var props = room.CustomProps;
            if (props == null)
                return;
            foreach (System.Collections.DictionaryEntry de in props)
            {
                string key = de.Key as string;
                if (string.IsNullOrEmpty(key))
                    continue;
                string val = de.Value as string;
                if (string.IsNullOrEmpty(val))
                    continue;
                if (key == "H")
                    room.HostName = val;
                else if (key == "CN")
                    room.TrackName = val;
                else if (key == "E")
                    room.EnvName = val;
            }
            if (props.ContainsKey("GM"))
            {
                try { room.GameMode = System.Convert.ToInt32(props["GM"]); } catch { }
            }
            if (props.ContainsKey("GMS"))
            {
                try { room.GameModeSub = System.Convert.ToInt32(props["GMS"]); } catch { }
            }
        }

        // GM room-property codes from Liftoff.Multiplayer.GameMode (not the
        // leaderboard enum, whose 3 is Freestyle): 1=ClassicRace, 2=FreeFlight,
        // 3=InfiniteRace, 4=DropoutRace.
        private static string LobbyGameModeName(int gm)
        {
            switch (gm)
            {
                case 1: return "Race";
                case 2: return "Freestyle";
                case 3: return "Infinite Race";
                case 4: return "Dropout Race";
                default: return gm >= 0 ? "Mode " + gm : null;
            }
        }

        // Room GM (Liftoff.Multiplayer.GameMode) to the leaderboard board it
        // matches (Liftoff.Leaderboards.GameMode); 0 when there is no clear match.
        private static int LeaderboardModeForRoomMode(int gm)
        {
            switch (gm)
            {
                case 1: return 1; // ClassicRace -> RaceTimes
                case 2: return 3; // FreeFlight -> Freestyle
                default: return 0;
            }
        }

        // ---- One-click leaderboard replays ----
        // SetData postfix captures, per populated row: the wired recording consumer
        // (target+method; only the MultiplayerLeaderboards scene wires it) and the
        // entry data object. Both survive scene loads (plain objects / possibly
        // persistent manager), enabling a direct "load this replay" invoke from the
        // MP flight scene.
        private sealed class LbConsumer
        {
            public object Target;
            public System.Reflection.MethodInfo Method;
        }
        private sealed class LbDisplayRow
        {
            public object Entry;
            public object Row;
            public string Key;
            public int Rank;
            public long Score;
            public string Date;
            public bool HasRec;
        }
        private bool lbReplayWindowOpen = false;
        private bool lbLoadSceneRequested = false;
        private float lbForceTrackUntil = 0f;
        private float lbForceTrackNextAt = 0f;
        private string lbForcedTrack = null;
        private bool lbKeywordTried = false;
        private float lbKeywordCheckAt = 0f;
        private string lbKeywordBefore = null;
        private string lbForceEnv = null;
        private bool lbEnvTried = false;
        private bool lbEnvScanned = false;
        private Component lbForceController = null;
        // Jump snapshot: captured at click time in flight (all state valid).
        // Scene transitions can wipe the live fields, so forcing reads these.
        private string lbJumpTrack = null;
        private string lbJumpRace = null;
        private string lbJumpEnv = null;
        private string lbJumpRaceId = null;
        private string lbJumpTrackId = null;
        private int lbJumpMode = -1;
        private bool lbMissLogged = false;
        private float lbAttribCheckAt = 0f;

        // Direct-load: multi-select of captured rows + reflection handles into the
        // leaderboard controller's own "download selected recordings -> load replay"
        // coroutine (the exact call the native View Replay button makes).
        private readonly HashSet<string> lbSelectedKeys = new HashSet<string>();
        private System.Reflection.FieldInfo lbDirectPanelField = null;
        private System.Reflection.MethodInfo lbDirectKeywordGetter = null;
        private System.Reflection.MethodInfo lbDirectLoadCoroutine = null;
        private System.Type lbDirectPanelType = null;

        // The MultiplayerLeaderboards controller is normally destroyed when its
        // scene unloads. To serve the in-game ESC popup (which has no load path of
        // its own) we keep the controller alive: DontDestroyOnLoad its root, hide
        // the canvases once we leave the scene, and invoke its own
        // "download selected recordings -> load replay" coroutine from the popup.
        private MonoBehaviour lbPersistController = null;
        private Component lbPersistPanel = null;
        private GameObject lbPersistRoot = null;
        private bool lbPersistHidden = false;
        private bool lbPersistLogged = false;

        // In-game popup multi-select (up to 5) + drag over the game's own rows.
        // Keyed by the Toggle component itself (reference), not its instance id:
        // Unity recycles instance ids after a row is destroyed, which used to make
        // re-created rows look "already wired" and left their listeners dead.
        private readonly HashSet<UnityEngine.UI.Toggle> lbPopupWired =
            new HashSet<UnityEngine.UI.Toggle>();
        private readonly List<Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry> lbPopupSelected =
            new List<Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry>();
        private bool lbPopupBusy = false;
        private float lbPopupRadioNextAt = 0f;
        private bool lbPopupPresent = false;
        private bool lbPopupRadiosLogged = false;
        // Set from the popup's Awake patch; avoids per-tick scene-wide scans.
        private Component lbPopupAwakeInstance = null;
        private float lbPopupRescanAt = 0f;
        // Achievement date injected into each popup row, coloured by recency.
        private sealed class LbPopupDateLabel
        {
            public UnityEngine.UI.Text Text;
            public UnityEngine.UI.Text Rank;
            public UnityEngine.UI.Text Name;
            public UnityEngine.UI.Image FriendIcon;
            public int PreferredFont = 16;
            public int MinFont = 11;
            public string HiddenReason;
            public bool Hovered;
            // The game's own score colour: freshest age uses it verbatim, older ages
            // fade from it toward grey.
            public Color BaseColor = Color.black;
        }
        private readonly Dictionary<Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry, LbPopupDateLabel>
            lbPopupDateLabels = new Dictionary<Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry, LbPopupDateLabel>();
        private static System.Reflection.FieldInfo lbPopupScoreTextField = null;
        private static System.Reflection.FieldInfo lbPopupRankTextField = null;
        private static System.Reflection.FieldInfo lbPopupNameTextField = null;
        private static System.Reflection.FieldInfo lbPopupFriendField = null;
        private bool lbPopupRowHierarchyDumped = false;
        private bool lbPopupNoTimestampLogged = false;
        private bool lbPopupHiddenLogged = false;
        // Hover tooltip (drawn in IMGUI so it can't be clipped by the scroll viewport).
        private string lbPopupDateHoverText = null;
        private Vector2 lbPopupDateHoverPos;
        private GUIStyle lbPopupDateTooltipStyle = null;
        private static System.Reflection.FieldInfo lbPopupToggleField = null;
        private static System.Reflection.FieldInfo lbPopupGroupField = null;
        private static readonly System.Type LbPopupType =
            typeof(Liftoff.Multiplayer.Leaderboards.PopupInGameCommunityLeaderboard);
        private static readonly System.Type LbKeywordType =
            typeof(Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardKeyword);
        private const int LbPopupMaxSelect = 5;

        // Drag selection over popup rows.
        private bool lbPopupDragSelecting = false;
        private bool lbPopupDragStartWasSelected = false;
        private int lbPopupDragStartIndex = -1;
        private int lbPopupDragLastIndex = -1;
        private readonly HashSet<int> lbPopupDragProcessed = new HashSet<int>();
        private ScrollRect lbPopupCachedScrollRect = null;
        private bool lbPopupScrollRectWasEnabled = true;

        // On-demand provisioning of the carried-over controller, so the popup works
        // even before the player has ever opened the Leaderboards screen. We load
        // the leaderboards scene additively, let the controller Start postfix move
        // its canvas to DontDestroyOnLoad, then unload the additive copy.
        private bool lbProvisioning = false;
        private List<Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry> lbPendingPopupEntries = null;
        private object lbPendingPopupKeyword = null;

        // Live multiplayer Level captured while flying. `Level` is a plain
        // System.Object (not a Unity object), so a reference survives the replay
        // scene load. On return we reload it through the game's own SceneLoader,
        // staying in the Photon room (no leave/rejoin/menu flash).
        private object lbLiveLevel = null;
        private object lbLiveDrone = null;
        private float lbLiveLevelCaptureAt = 0f;
        private bool lbReturnDirectTried = false;
        // Direct-return watchdog state: remember the scene at the moment the direct
        // SceneLoader load was fired, and how many times we've re-fired it. A scene
        // change means the load actually started (give it time); no change means the
        // request was swallowed, so retry before falling back to leave+rejoin.
        private string lbDirectSceneAt = null;
        private float lbDirectLastInvokeAt = 0f;
        // The replay content scene and the live content scene share the same name
        // (the scene is reloaded in place), so a scene-name check cannot tell a
        // completed load from a swallowed one. The only reliable "a load is running"
        // marker is the game's own LoadingScreen scene; remember whether we saw it.
        private bool lbDirectSawLoading = false;

        // Current popup rows (refreshed on the throttled scan, read every frame by
        // the drag handler) and the injected native-style Load button.
        private List<Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry> lbPopupRowsCache =
            new List<Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry>();
        private GameObject lbPopupLoadButton = null;
        private Component lbPopupLoadButtonPopup = null;
    }
}
