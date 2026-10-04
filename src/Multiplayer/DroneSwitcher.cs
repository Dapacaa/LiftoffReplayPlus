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
        private static System.Type FindGameType(string simpleName)
        {
            return GameAccess.FindGameType(simpleName);
        }

        private static List<System.Reflection.MethodInfo> IlCallTargets(System.Reflection.MethodInfo method)
        {
            var result = new List<System.Reflection.MethodInfo>();
            if (method == null || method.GetMethodBody() == null)
                return result;
            byte[] il;
            try { il = method.GetMethodBody().GetILAsByteArray(); }
            catch { return result; }
            if (il == null || il.Length == 0)
                return result;
            System.Reflection.Module module = method.Module;
            var ops = new Dictionary<int, System.Reflection.Emit.OpCode>();
            try
            {
                foreach (var f in typeof(System.Reflection.Emit.OpCodes).GetFields(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    var oc = (System.Reflection.Emit.OpCode)f.GetValue(null);
                    ops[(int)oc.Value] = oc;
                }
            }
            catch { return result; }
            int i = 0;
            while (i < il.Length)
            {
                int code = il[i];
                int size = 1;
                if (code == 0xFE && i + 1 < il.Length)
                {
                    code = 0xFE00 | il[i + 1];
                    size = 2;
                }
                System.Reflection.Emit.OpCode op;
                if (!ops.TryGetValue(code, out op))
                {
                    i++;
                    continue;
                }
                int dataLen = 0;
                switch (op.OperandType)
                {
                    case System.Reflection.Emit.OperandType.InlineNone: dataLen = 0; break;
                    case System.Reflection.Emit.OperandType.ShortInlineI:
                    case System.Reflection.Emit.OperandType.ShortInlineVar:
                    case System.Reflection.Emit.OperandType.ShortInlineBrTarget:
                    case System.Reflection.Emit.OperandType.ShortInlineR: dataLen = 1; break;
                    case System.Reflection.Emit.OperandType.InlineVar: dataLen = 2; break;
                    case System.Reflection.Emit.OperandType.InlineI:
                    case System.Reflection.Emit.OperandType.InlineBrTarget:
                    case System.Reflection.Emit.OperandType.InlineField:
                    case System.Reflection.Emit.OperandType.InlineType:
                    case System.Reflection.Emit.OperandType.InlineString:
                    case System.Reflection.Emit.OperandType.InlineMethod:
                    case System.Reflection.Emit.OperandType.InlineTok: dataLen = 4; break;
                    case System.Reflection.Emit.OperandType.InlineI8:
                    case System.Reflection.Emit.OperandType.InlineR: dataLen = 8; break;
                    case System.Reflection.Emit.OperandType.InlineSwitch:
                        if (i + size + 4 <= il.Length)
                        {
                            int switchCount = BitConverter.ToInt32(il, i + size);
                            dataLen = 4 + 4 * switchCount;
                        }
                        break;
                    default: dataLen = 0; break;
                }
                if ((op.OperandType == System.Reflection.Emit.OperandType.InlineMethod ||
                     op.OperandType == System.Reflection.Emit.OperandType.InlineField ||
                     op.OperandType == System.Reflection.Emit.OperandType.InlineTok) &&
                    i + size + 4 <= il.Length)
                {
                    int token = BitConverter.ToInt32(il, i + size);
                    try
                    {
                        if (module.ResolveMember(token) is System.Reflection.MethodInfo mi)
                            result.Add(mi);
                    }
                    catch { }
                }
                i += size + dataLen;
            }
            return result;
        }

        private void EnsureDroneSwitchReflection()
        {
            if (droneSwitchReflectionDone)
                return;
            try
            {
                System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Static;
                inGameMenuDroneSelectionType = FindGameType("InGameMenuDroneSelection");
                droneSelectionMenuType = FindGameType("DroneSelectionMenu");
                userDataType = FindGameType("UserData");
                currentContentContainerType = FindGameType("CurrentContentContainer");
                if (inGameMenuDroneSelectionType != null)
                {
                    inGameMenuOnDroneSelectedMethod = inGameMenuDroneSelectionType.GetMethod("OnDroneSelected", flags);
                    if (inGameMenuOnDroneSelectedMethod != null)
                    {
                        var chain = IlCallTargets(inGameMenuOnDroneSelectedMethod);
                        for (int ci = 0; ci + 1 < chain.Count; ci++)
                        {
                            var getter = chain[ci];
                            var next = chain[ci + 1];
                            if (getter == null || next == null || !getter.IsStatic || next.IsStatic)
                                continue;
                            if (next.DeclaringType != getter.ReturnType)
                                continue;
                            System.Reflection.ParameterInfo[] nextParams = next.GetParameters();
                            if (currentContentContainerType != null && getter.ReturnType == currentContentContainerType)
                            {
                                if (next.Name == "SetDroneConfiguration" && nextParams.Length == 1)
                                {
                                    cccUseGetter = getter;
                                    cccSetDroneConfiguration = next;
                                }
                                else if (next.Name == "get_InMultiplayer")
                                    cccInMultiplayerGetter = next;
                                else if (next.Name == "get_DroneConfig")
                                    cccDroneConfigGetter = next;
                            }
                            else if (getter.ReturnType == typeof(FlightManager))
                            {
                                if (next.Name == "Reset" && nextParams.Length == 0)
                                {
                                    flightManagerUseGetter = getter;
                                    flightManagerResetMethod = next;
                                }
                            }
                            else if (getter.DeclaringType.Name == "LugusSingletonCrossScene`1" && nextParams.Length == 0)
                            {
                                if (userDataType != null && next.ReturnType == userDataType)
                                {
                                    xUseGetter = getter;
                                    xM1Method = next;
                                }
                                else if (next.ReturnType == typeof(void))
                                {
                                    yUseGetter = getter;
                                    yM2Method = next;
                                }
                            }
                        }
                        if (userDataType != null)
                        {
                            foreach (var m in chain)
                            {
                                if (m == null || m.IsStatic || m.Name != "set_CurrentDroneConfiguration")
                                    continue;
                                if (m.DeclaringType == userDataType && m.GetParameters().Length == 1)
                                {
                                    userDataSetter = m;
                                    break;
                                }
                            }
                        }
                        for (int ci = 0; ci + 1 < chain.Count; ci++)
                        {
                            var getter = chain[ci];
                            var next = chain[ci + 1];
                            if (getter == null || next == null || !getter.IsStatic || next.IsStatic)
                                continue;
                            if (next.DeclaringType != getter.ReturnType)
                                continue;
                            if (getter.GetParameters().Length != 0 || next.GetParameters().Length != 1)
                                continue;
                            if (getter.DeclaringType.Name.Contains("LugusSingleton"))
                                continue;
                            if (currentContentContainerType != null && next.DeclaringType == currentContentContainerType)
                                continue;
                            mpObjectGetter = getter;
                            mpObjectSetter = next;
                            break;
                        }
                    }
                }
                if (droneSelectionMenuType != null)
                {
                    System.Reflection.MethodInfo createLineup = droneSelectionMenuType.GetMethod("CreateDroneLineup", flags);
                    if (createLineup != null)
                    {
                        var lineup = IlCallTargets(createLineup);
                        for (int li = 0; li + 1 < lineup.Count; li++)
                        {
                            var getter = lineup[li];
                            var next = lineup[li + 1];
                            if (getter == null || next == null || !getter.IsStatic || next.IsStatic)
                                continue;
                            if (next.DeclaringType != getter.ReturnType)
                                continue;
                            System.Reflection.ParameterInfo[] nextParams = next.GetParameters();
                            if (nextParams.Length != 1 || getter.GetParameters().Length != 0)
                                continue;
                            if (droneLineupProviderGetter == null)
                            {
                                droneLineupProviderGetter = getter;
                                droneLineupProviderMethod = next;
                            }
                        }
                    }
                }
                droneSwitchReflectionReady =
                    droneLineupProviderGetter != null &&
                    droneLineupProviderMethod != null &&
                    userDataSetter != null &&
                    xUseGetter != null &&
                    xM1Method != null &&
                    cccUseGetter != null &&
                    cccSetDroneConfiguration != null &&
                    cccInMultiplayerGetter != null;
                if (droneSwitchReflectionReady)
                {
                    // Latch only on success so a transient miss (type not loaded yet)
                    // retries on the next scan instead of disabling the picker forever.
                    droneSwitchReflectionDone = true;
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH reflection ready (lineup " +
                        droneLineupProviderGetter.Name + "/" + droneLineupProviderMethod.Name +
                        " chain X=" + xM1Method.DeclaringType.Name + "." + xM1Method.Name +
                        " Y=" + (yM2Method != null ? yM2Method.DeclaringType.Name + "." + yM2Method.Name : "none") +
                        " mp=" + (mpObjectGetter != null && mpObjectSetter != null ? "yes" : "no") + ")");
                }
                else
                {
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH reflection INIT INCOMPLETE menu=" +
                        (inGameMenuDroneSelectionType != null && inGameMenuOnDroneSelectedMethod != null) +
                        " lineup=" + (droneLineupProviderGetter != null && droneLineupProviderMethod != null) +
                        " userData=" + (userDataSetter != null && xUseGetter != null && xM1Method != null) +
                        " ccc=" + (cccUseGetter != null && cccSetDroneConfiguration != null) +
                        " inMp=" + (cccInMultiplayerGetter != null) +
                        " mpE=" + (mpObjectGetter != null && mpObjectSetter != null));
                }
            }
            catch (System.Exception ex)
            {
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH reflection EX " + ex);
            }
        }

        private void RefreshActiveDrone()
        {
            try
            {
                object current = null;
                if (cccUseGetter != null && cccDroneConfigGetter != null)
                {
                    object ccc = cccUseGetter.Invoke(null, null);
                    if (ccc != null)
                        current = cccDroneConfigGetter.Invoke(ccc, null);
                }
                if (object.ReferenceEquals(current, activeDroneConfig))
                    return;
                activeDroneConfig = current;
                activeDroneName = current == null ? "-" : (ReadDroneName(current) ?? "Unknown");
            }
            catch (System.Exception ex)
            {
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH active EX " + ex);
            }
        }

        private string ReadDroneName(object config)
        {
            try
            {
                if (droneNameProperty == null)
                    droneNameProperty = config.GetType().GetProperty("Name",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                if (droneNameProperty != null)
                    return droneNameProperty.GetValue(config, null) as string;
            }
            catch { }
            return null;
        }

        private string ReadDroneLocalId(object config)
        {
            try
            {
                if (droneLocalIdProperty == null)
                    droneLocalIdProperty = config.GetType().GetProperty("LocalID",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                if (droneLocalIdProperty != null)
                {
                    object value = droneLocalIdProperty.GetValue(config, null);
                    if (value != null)
                    {
                        System.Type valueType = value.GetType();
                        System.Reflection.FieldInfo strField = valueType.GetField("str",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Instance);
                        if (strField != null && strField.FieldType == typeof(string))
                        {
                            string str = strField.GetValue(value) as string;
                            if (!string.IsNullOrEmpty(str))
                                return str;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private void RefreshDroneSwitchEntriesIfNeeded()
        {
            if (!dronesNeedScan)
                return;
            if (Time.unscaledTime < droneSwitchScanRetryAt)
                return;
            dronesNeedScan = false;
            try
            {
                RefreshDroneSwitchEntries();
                if (droneSwitchEntries.Count == 0 && !droneSwitchReflectionReady)
                {
                    droneSwitchScanRetryAt = Time.unscaledTime + 3f;
                    dronesNeedScan = true;
                }
            }
            catch (System.Exception ex)
            {
                droneSwitchScanRetryAt = Time.unscaledTime + 3f;
                dronesNeedScan = true;
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH scan EX " + ex);
            }
        }

        private void RefreshDroneSwitchEntries()
        {
            EnsureDroneSwitchReflection();
            if (!droneSwitchReflectionReady)
                return;
            try
            {
                object provider = droneLineupProviderGetter.Invoke(null, null);
                if (provider == null)
                    return;
                System.Reflection.ParameterInfo[] parameters = droneLineupProviderMethod.GetParameters();
                object[] args;
                if (parameters.Length == 0)
                    args = new object[0];
                else
                {
                    System.Type paramType = parameters[0].ParameterType;
                    object filterArg;
                    if (paramType.IsEnum)
                        filterArg = System.Enum.ToObject(paramType, 7);
                    else if (paramType == typeof(int))
                        filterArg = 7;
                    else if (paramType.IsValueType)
                        filterArg = System.Activator.CreateInstance(paramType);
                    else
                        filterArg = null;
                    args = new object[] { filterArg };
                }
                object raw = droneLineupProviderMethod.Invoke(provider, args);
                if (!(raw is System.Collections.IEnumerable enumerable))
                    return;
                var next = new List<DroneSwitchEntry>();
                foreach (object config in enumerable)
                {
                    if (config == null)
                        continue;
                    string name = ReadDroneName(config);
                    string localId = ReadDroneLocalId(config);
                    if (string.IsNullOrEmpty(localId))
                        localId = name;
                    if (string.IsNullOrEmpty(localId))
                        localId = "entry" + (next.Count + 1);
                    if (string.IsNullOrEmpty(name))
                        name = "Drone " + (next.Count + 1);
                    next.Add(new DroneSwitchEntry(config, name, localId));
                }
                droneSwitchEntries = next;
                if (next.Count != lastLoggedDroneCount)
                {
                    lastLoggedDroneCount = next.Count;
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH list n=" + next.Count);
                }
            }
            catch (System.Exception ex)
            {
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH refresh EX " + ex);
            }
        }

        private object FindInGameMenuDroneSelection()
        {
            if (inGameMenuDroneSelectionType == null)
                return null;
            try
            {
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(inGameMenuDroneSelectionType);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] is UnityEngine.Behaviour behaviour && behaviour.isActiveAndEnabled)
                        return all[i];
                }
            }
            catch { }
            return null;
        }

        private void SwitchToDrone(DroneSwitchEntry entry)
        {
            if (entry == null)
                return;
            // Re-read the lineup and match by LocalID: the list holds configs weakly
            // and is otherwise only rescanned on session start, so after a switch
            // the game may have replaced (or GC freed) the objects it held, and the
            // next switch passed a stale config or none -- only the first one worked.
            string localId = entry.localId;
            RefreshDroneSwitchEntries();
            object config = null;
            for (int i = 0; i < droneSwitchEntries.Count; i++)
            {
                DroneSwitchEntry fresh = droneSwitchEntries[i];
                if (fresh != null && fresh.localId == localId)
                {
                    config = fresh.GetConfig();
                    break;
                }
            }
            if (config == null)
                config = entry.GetConfig();
            if (config == null)
            {
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH no config for " + entry.name);
                return;
            }
            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH request " + entry.name + " id=" + localId +
                " fresh=" + !object.ReferenceEquals(config, entry.GetConfig()) +
                " entries=" + droneSwitchEntries.Count + " " + DescribeDroneSwitchState());
            try
            {
                EnsureDroneSwitchReflection();
                object liveMenu = FindInGameMenuDroneSelection();
                if (liveMenu != null && inGameMenuOnDroneSelectedMethod != null)
                {
                    BeginOwnDroneReset();
                    try { inGameMenuOnDroneSelectedMethod.Invoke(liveMenu, new object[] { config }); }
                    finally { EndOwnDroneReset(); }
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH via in-game menu: " + entry.name + " after: " + DescribeDroneSwitchState());
                    return;
                }
                if (!droneSwitchReflectionReady)
                {
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH reflection not ready");
                    return;
                }
                object ccc = cccUseGetter.Invoke(null, null);
                if (ccc == null)
                {
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH no CurrentContentContainer");
                    return;
                }
                if (cccDroneConfigGetter != null)
                {
                    object current = cccDroneConfigGetter.Invoke(ccc, null);
                    if (current != null && object.ReferenceEquals(current, config))
                    {
                        if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH already active: " + entry.name);
                        return;
                    }
                }
                bool inMultiplayer = cccInMultiplayerGetter == null ||
                    (bool)cccInMultiplayerGetter.Invoke(ccc, null);
                if (!inMultiplayer)
                {
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH not in multiplayer, ignored: " + entry.name);
                    return;
                }
                if (xUseGetter != null && xM1Method != null && userDataSetter != null)
                {
                    object x = xUseGetter.Invoke(null, null);
                    if (x != null)
                    {
                        object userData = xM1Method.Invoke(x, null);
                        if (userData != null)
                            userDataSetter.Invoke(userData, new object[] { config });
                    }
                }
                if (yUseGetter != null && yM2Method != null)
                {
                    object y = yUseGetter.Invoke(null, null);
                    if (y != null)
                        yM2Method.Invoke(y, null);
                }
                if (mpObjectGetter != null && mpObjectSetter != null)
                {
                    object target = mpObjectGetter.Invoke(null, null);
                    if (target != null)
                        mpObjectSetter.Invoke(target, new object[] { config });
                }
                if (cccSetDroneConfiguration != null)
                    cccSetDroneConfiguration.Invoke(ccc, new object[] { config });
                if (flightManagerUseGetter != null && flightManagerResetMethod != null)
                {
                    object fm = flightManagerUseGetter.Invoke(null, null);
                    if (fm != null)
                    {
                        BeginOwnDroneReset();
                        object handle;
                        try { handle = flightManagerResetMethod.Invoke(fm, null); }
                        finally { EndOwnDroneReset(); }
                        // A null handle means Reset refused (drone-reset lock held).
                        ModLog.LogFile("[GhostLines] DRONESWITCH reset handle=" + (handle == null ? "null" : handle.GetType().Name));
                    }
                    else
                        ModLog.LogFile("[GhostLines] DRONESWITCH no FlightManager instance");
                }
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH applied: " + entry.name +
                    (flightManagerResetMethod != null ? " (respawn)" : " (no reset method)") + " after: " + DescribeDroneSwitchState());
            }
            catch (System.Exception ex)
            {
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH EX " + ex);
            }
        }

        // Diagnostics for the switch path: active config, our locks, and whether
        // the game still thinks a previous reset is running.
        private string DescribeDroneSwitchState()
        {
            string active = "?";
            string resetting = "?";
            try
            {
                if (cccUseGetter != null && cccDroneConfigGetter != null)
                {
                    object ccc = cccUseGetter.Invoke(null, null);
                    object current = ccc != null ? cccDroneConfigGetter.Invoke(ccc, null) : null;
                    active = current == null ? "null" : (ReadDroneName(current) ?? "unnamed");
                }
                FlightManager fm = flightManagerUseGetter != null ? flightManagerUseGetter.Invoke(null, null) as FlightManager : null;
                if (fm != null)
                    resetting = fm.IsResettingDrone.ToString();
            }
            catch (System.Exception ex)
            {
                active = "EX " + ex.GetType().Name;
            }
            return "active=" + active + " resetting=" + resetting +
                " gate=" + GhostLinesPlugin.blockDroneReset + " nativeLock=" + nativeDroneResetLocked +
                " freecam=" + mpFreeCamActive + " menu=" + (FindInGameMenuDroneSelection() != null);
        }

        private bool ownDroneResetRelockNative;

        // While in MP freecam the drone is held by two locks: the Harmony reset gate
        // and FlightManager.LockDroneReset. Both would swallow the respawn that
        // applies a switched drone, so open them just for this call. Reset starts
        // its coroutine synchronously, so relocking right after is safe.
        private void BeginOwnDroneReset()
        {
            GhostLinesPlugin.allowOwnDroneReset = true;
            ownDroneResetRelockNative = false;
            if (!nativeDroneResetLocked || flightManagerInstance == null)
                return;
            try
            {
                flightManagerInstance.UnlockDroneReset(nativeDroneResetLockToken);
                ownDroneResetRelockNative = true;
            }
            catch (System.Exception ex)
            {
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH unlock EX " + ex);
            }
        }

        private void EndOwnDroneReset()
        {
            GhostLinesPlugin.allowOwnDroneReset = false;
            if (!ownDroneResetRelockNative)
                return;
            ownDroneResetRelockNative = false;
            try
            {
                if (flightManagerInstance != null)
                    flightManagerInstance.LockDroneReset(nativeDroneResetLockToken);
                else
                    nativeDroneResetLocked = false;
            }
            catch (System.Exception ex)
            {
                // Next EnforceNativeReplayOverrides re-takes the lock.
                nativeDroneResetLocked = false;
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH relock EX " + ex);
            }
        }

        // Called from OnGUI. The search field keeps IMGUI keyboard focus after the
        // dropdown stops being drawn, which left dronesSearchFocused stuck true and
        // silently disabled every drone hotkey.
        private void CloseDroneDropdown()
        {
            droneSwitchDropdownOpen = false;
            droneDropdownPending = false;
            if (GUI.GetNameOfFocusedControl() == "GhostLinesDroneSearch")
                GUIUtility.keyboardControl = 0;
            dronesSearchFocused = false;
        }

        private void TryDroneSwitchHotkeys()
        {
            if (droneSwitchEntries.Count == 0)
                return;
            for (int i = 0; i < droneSwitchEntries.Count; i++)
            {
                DroneSwitchEntry entry = droneSwitchEntries[i];
                if (entry == null || string.IsNullOrEmpty(entry.localId))
                    continue;
                KeyCode key = GhostLinesPlugin.GetDroneBinding(entry.localId);
                if (key != KeyCode.None && Input.GetKeyDown(key))
                {
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DRONESWITCH hotkey " + FriendlyKeyName(key) + " -> " + entry.name);
                    SwitchToDrone(entry);
                    return;
                }
            }
        }

        private void DrawDroneSwitchSection(Rect menu, ref float y, float alpha, Event e, bool pipBlock)
        {
            EnsureSettingsStyles();
            EnsureSettingsTextures();
            RefreshActiveDrone();
            string barName = string.IsNullOrEmpty(activeDroneName) ? "None" : activeDroneName;
            if (barName.Length > 30)
                barName = barName.Substring(0, 30);
            Rect barRect = new Rect(menu.x + 10f, y + 2f, menu.width - 20f, 22f);
            if (DrawSettingsOption(barRect, "Drone: " + barName, droneSwitchDropdownOpen, alpha,
                TextAnchor.MiddleLeft, -1, !pipBlock, false, settingsDropdownBarStyle))
                droneSwitchDropdownOpen = !droneSwitchDropdownOpen;
            GUI.color = ThemeLabel(alpha);
            GUI.Label(new Rect(barRect.xMax - 18f, barRect.y, 14f, barRect.height),
                droneSwitchDropdownOpen ? "^" : "v", settingsSmallStyle);
            y += 26f;
            // The dropdown itself floats over the menu (drawn later, on top) so it
            // never reflows the settings UI or grows its height.
            droneDropdownAnchor = barRect;
            droneDropdownAlpha = alpha;
            droneDropdownPipBlock = pipBlock;
            droneDropdownPending = droneSwitchDropdownOpen;
        }

        // Floating, searchable drone picker anchored under the "Drone:" bar. Drawn
        // after the whole menu body so it overlays everything without resizing it.
        private void DrawDroneDropdownOverlay(Event e)
        {
            if (droneDropdownAnchor.width < 1f)
                return;
            EnsureSettingsStyles();
            float alpha = droneDropdownAlpha;
            bool pipBlock = droneDropdownPipBlock;
            const float rowH = 24f;
            int maxVisible = 7;
            RefreshDroneSwitchEntriesIfNeeded();
            List<DroneSwitchEntry> visibleEntries = droneSwitchEntries;
            if (!string.IsNullOrEmpty(dronesSearchTerm))
            {
                visibleEntries = new List<DroneSwitchEntry>();
                for (int si = 0; si < droneSwitchEntries.Count; si++)
                {
                    DroneSwitchEntry cand = droneSwitchEntries[si];
                    if (cand != null && cand.name.IndexOf(dronesSearchTerm, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        visibleEntries.Add(cand);
                }
            }
            int totalRows = visibleEntries.Count;
            int visRows = Mathf.Clamp(totalRows, 1, maxVisible);
            const float searchH = 20f;
            float listH = visRows * rowH;
            const float pad = 6f;
            // Flush under the bar (sharing its bottom border line) so the two read
            // as one control.
            Rect panel = new Rect(droneDropdownAnchor.x, droneDropdownAnchor.yMax - 1f,
                droneDropdownAnchor.width, pad + searchH + 4f + listH + pad);
            if (panel.yMax > Screen.height - 4f)
                panel.y = Mathf.Max(4f, Screen.height - 4f - panel.height);
            // Dismiss when clicking outside the bar/panel.
            if (e.type == EventType.MouseDown && e.button == 0 &&
                !panel.Contains(e.mousePosition) && !droneDropdownAnchor.Contains(e.mousePosition))
            {
                CloseDroneDropdown();
                return;
            }
            // Shadow + background.
            GUI.color = new Color(0f, 0f, 0f, 0.28f * alpha);
            GUI.DrawTexture(new Rect(panel.x + 2f, panel.y + 2f, panel.width, panel.height), whiteTex);
            GUI.color = ThemePanelBg(alpha);
            GUI.DrawTexture(panel, whiteTex);
            GUI.color = ThemePanelBorder(alpha);
            GUI.DrawTexture(new Rect(panel.x, panel.y, panel.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(panel.x, panel.yMax - 1f, panel.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(panel.x, panel.y, 1f, panel.height), whiteTex);
            GUI.DrawTexture(new Rect(panel.xMax - 1f, panel.y, 1f, panel.height), whiteTex);
            // Search field — draw a placeholder when empty so it reads as searchable.
            // Same flat box as the other options; blue border while typing.
            Rect searchRect = new Rect(panel.x + pad, panel.y + pad, panel.width - pad * 2f, searchH);
            bool searchFocusedNow = GUI.GetNameOfFocusedControl() == "GhostLinesDroneSearch";
            GUI.color = ThemeOptionBg(alpha);
            GUI.DrawTexture(searchRect, whiteTex);
            GUI.color = searchFocusedNow ? new Color(0.25f, 0.62f, 0.95f, alpha) : ThemeOptionBorder(alpha);
            GUI.DrawTexture(new Rect(searchRect.x, searchRect.y, searchRect.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(searchRect.x, searchRect.yMax - 1f, searchRect.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(searchRect.x, searchRect.y, 1f, searchRect.height), whiteTex);
            GUI.DrawTexture(new Rect(searchRect.xMax - 1f, searchRect.y, 1f, searchRect.height), whiteTex);
            GUI.SetNextControlName("GhostLinesDroneSearch");
            GUI.color = ThemeLabel(alpha);
            string nextSearchTerm = GUI.TextField(searchRect, dronesSearchTerm ?? "", settingsTextFieldStyle);
            if (nextSearchTerm != dronesSearchTerm)
            {
                dronesSearchTerm = nextSearchTerm;
                dronesScroll = 0f;
                dronesNeedScan = true;
            }
            dronesSearchFocused = GUI.GetNameOfFocusedControl() == "GhostLinesDroneSearch";
            if (dronesSearchFocused && bindingListeningFor != null)
                bindingListeningFor = null;
            if (string.IsNullOrEmpty(dronesSearchTerm) && !dronesSearchFocused)
            {
                GUI.color = ThemeDimLabel(0.7f * alpha);
                GUI.Label(new Rect(searchRect.x + 6f, searchRect.y, searchRect.width - 8f, searchRect.height),
                    "Search drones…", settingsDropdownBarStyle);
            }
            float listTop = searchRect.yMax + 4f;
            Rect listRect = new Rect(panel.x, listTop, panel.width, listH);
            if (totalRows > maxVisible && e.type == EventType.ScrollWheel && listRect.Contains(e.mousePosition))
            {
                dronesScroll -= Input.mouseScrollDelta.y;
                e.Use();
            }
            dronesScroll = Mathf.Clamp(dronesScroll, 0f, Mathf.Max(0f, totalRows - maxVisible));
            if (totalRows == 0)
            {
                GUI.color = ThemeDimLabel(alpha);
                GUI.Label(new Rect(panel.x + pad + 8f, listTop, panel.width - pad * 2f - 8f, rowH),
                    droneSwitchEntries.Count == 0
                        ? (droneSwitchReflectionReady ? "No drones found" : "Drone list unavailable (see log)")
                        : "No matches", settingsSmallStyle);
                return;
            }
            int firstRow = Mathf.FloorToInt(dronesScroll);
            int lastRow = Mathf.Min(totalRows, firstRow + maxVisible);
            float keyW = 86f;
            float selW = 54f;
            float gap = 6f;
            float nameW = panel.width - pad * 2f - 8f - keyW - gap - selW;
            for (int i = firstRow; i < lastRow; i++)
            {
                DroneSwitchEntry entry = visibleEntries[i];
                if (entry == null)
                    continue;
                float ry = listTop + (i - firstRow) * rowH;
                Rect row = new Rect(panel.x + pad, ry, panel.width - pad * 2f, rowH - 2f);
                bool isActiveRow = activeDroneConfig != null && entry.GetConfig() != null &&
                    object.ReferenceEquals(entry.GetConfig(), activeDroneConfig);
                GUI.color = isActiveRow
                    ? new Color(0.22f, 0.55f, 0.3f, 0.38f * alpha)
                    : new Color(1f, 1f, 1f, ((i & 1) == 1 ? 0.06f : 0.02f) * alpha);
                GUI.DrawTexture(row, whiteTex);
                string displayName = entry.name;
                if (displayName.Length > 24)
                    displayName = displayName.Substring(0, 24);
                GUI.color = ThemeLabel(alpha);
                GUI.Label(new Rect(row.x + 8f, ry, nameW - 8f, rowH - 2f), displayName, settingsSmallStyle);
                string listeningId = GhostLinesPlugin.DroneBindingPrefix + entry.localId;
                bool listening = bindingListeningFor == listeningId;
                Rect selRect = new Rect(row.xMax - selW, ry + 1f, selW, rowH - 4f);
                Rect keyRect = new Rect(selRect.x - gap - keyW, ry + 1f, keyW, rowH - 4f);
                string keyText = listening ? "Press key..." : GhostLinesPlugin.DroneBindingKeyName(entry.localId);
                if (DrawSettingsOption(keyRect, keyText, listening, alpha, TextAnchor.MiddleCenter, -1, !pipBlock))
                    ToggleBindingListen(listeningId);
                if (DrawSettingsOption(selRect, "Select", false, alpha, TextAnchor.MiddleCenter, -1, !pipBlock))
                {
                    bindingListeningFor = null;
                    SwitchToDrone(entry);
                    CloseDroneDropdown();
                }
            }
        }
    }
}
