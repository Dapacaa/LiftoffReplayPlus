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
    // Event-driven gate capture. Liftoff's race participant interface declares a
    // per-gate event (participant, RaceCheckpointPassage, float) and owns a lap
    // timer backed by an ObscuredFloat. The participant types are name-obfuscated
    // and change between builds, so every member is located by shape rather than
    // by name. Written independently from the game's structure; no third-party
    // code is reproduced.
    internal static class GateSplitsBinding
    {
        public static bool Resolved { get; private set; }
        public static bool Subscribed { get { return gateHandler != null && IsAlive(activePlayer); } }
        public static System.Reflection.PropertyInfo HudPlayerProperty { get; private set; }

        private static object activePlayer;
        // The race HUD that last assigned the player; when it is destroyed the
        // race is over and the subscription is stale.
        private static object activeHud;

        // Unity-null aware: a destroyed participant/HUD compares equal to null
        // only through UnityEngine.Object's operator.
        private static bool IsAlive(object o)
        {
            if (o == null)
                return false;
            UnityEngine.Object uo = o as UnityEngine.Object;
            return (object)uo == null || uo != null;
        }

        public static void SetHud(object hud)
        {
            activeHud = hud;
        }

        // Called on scene change: drop the subscription if the player or the HUD
        // that owned it is gone, so the trigger fallback takes over again.
        public static void DetachIfStale()
        {
            if (gateHandler == null && activePlayer == null)
                return;
            if (!IsAlive(activePlayer) || (activeHud != null && !IsAlive(activeHud)))
                Detach();
        }
        private static Delegate gateHandler;
        private static System.Type participantType;
        private static System.Reflection.EventInfo gateEvent;
        private static System.Reflection.PropertyInfo lapTimerProperty;
        private static System.Reflection.PropertyInfo lapTimerValueProperty;
        private static System.Reflection.MethodInfo timerToFloat;
        private static System.Reflection.PropertyInfo raceAccessorProperty;
        private static System.Reflection.PropertyInfo raceMemberProperty;
        private static System.Reflection.PropertyInfo raceNameProperty;
        private static System.Reflection.PropertyInfo raceIdProperty;
        private static System.Reflection.FieldInfo raceIdStrField;
        private static System.Type raceType;

        public static bool Resolve()
        {
            if (Resolved)
                return true;
            try
            {
                System.Type passageType = typeof(RaceCheckpointPassage);
                System.Type[] types = GameAccess.GameTypes();

                if (!FindGateEvent(types, passageType, out participantType, out gateEvent))
                    return false;
                if (!FindLapTimer())
                    return false;
                if (!FindHudProperty(types))
                    return false;
                FindRaceIdentity();
                BuildLapTimeAccessor();
                Resolved = true;
                if (ModLog.DebugEnabled) GhostLinesPlugin.LogGateSplitsMessage("[GhostLines] gate-splits event binding resolved" +
                    (raceType != null ? " (race identity available)" : " (no race identity)"));
                return true;
            }
            catch (System.Exception ex)
            {
                ModLog.LogWarning("[GhostLines] gate-splits binding failed: " + ex.Message);
                return false;
            }
        }

        // The participant interface is the only interface whose event handler is
        // Action<itself, RaceCheckpointPassage, float>.
        private static bool FindGateEvent(System.Type[] types, System.Type passageType,
            out System.Type participant, out System.Reflection.EventInfo found)
        {
            participant = null;
            found = null;
            for (int i = 0; i < types.Length; i++)
            {
                System.Type t = types[i];
                if (t == null || !t.IsInterface)
                    continue;
                System.Reflection.EventInfo[] events;
                try
                {
                    events = t.GetEvents(System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                }
                catch { continue; }
                for (int e = 0; e < events.Length; e++)
                {
                    System.Type handler;
                    try { handler = events[e].EventHandlerType; }
                    catch { continue; }
                    if (handler == null || !handler.IsGenericType ||
                        handler.GetGenericTypeDefinition() != typeof(System.Action<,,>))
                        continue;
                    System.Type[] args = handler.GetGenericArguments();
                    if (args[0] == t && args[1] == passageType && args[2] == typeof(float))
                    {
                        if (found != null)
                            return false; // ambiguous — unsupported build
                        found = events[e];
                        participant = t;
                    }
                }
            }
            return found != null;
        }

        // The timer is the participant property whose type exposes an ObscuredFloat
        // (or, failing that, a plain float) that converts to float.
        private static bool FindLapTimer()
        {
            System.Reflection.PropertyInfo[] props = participantType.GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly);
            for (int i = 0; i < props.Length; i++)
            {
                System.Reflection.PropertyInfo p = props[i];
                if (!p.CanRead || p.GetIndexParameters().Length != 0)
                    continue;
                System.Type holder = p.PropertyType;
                if (holder == null || holder.IsPrimitive || holder == typeof(string))
                    continue;
                System.Reflection.PropertyInfo valueProp = null;
                System.Reflection.PropertyInfo floatFallback = null;
                try
                {
                    System.Reflection.PropertyInfo[] inner = holder.GetProperties(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    for (int k = 0; k < inner.Length; k++)
                    {
                        if (!inner[k].CanRead || inner[k].GetIndexParameters().Length != 0)
                            continue;
                        System.Type it;
                        try { it = inner[k].PropertyType; } catch { continue; }
                        if (it == null)
                            continue;
                        if (it.Name == "ObscuredFloat")
                        {
                            valueProp = inner[k];
                            break;
                        }
                        if (it == typeof(float) && floatFallback == null)
                            floatFallback = inner[k];
                    }
                }
                catch { continue; }
                if (valueProp == null)
                    valueProp = floatFallback;
                if (valueProp == null)
                    continue;
                System.Reflection.MethodInfo toFloat = null;
                if (valueProp.PropertyType != typeof(float))
                {
                    try
                    {
                        System.Reflection.MethodInfo[] methods = valueProp.PropertyType.GetMethods(
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        for (int mi = 0; mi < methods.Length; mi++)
                        {
                            if ((methods[mi].Name == "op_Implicit" || methods[mi].Name == "op_Explicit") &&
                                methods[mi].ReturnType == typeof(float))
                            {
                                System.Reflection.ParameterInfo[] ps = methods[mi].GetParameters();
                                if (ps.Length == 1 && ps[0].ParameterType == valueProp.PropertyType)
                                {
                                    toFloat = methods[mi];
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                    if (toFloat == null)
                        continue;
                }
                lapTimerProperty = p;
                lapTimerValueProperty = valueProp;
                timerToFloat = toFloat;
                return true;
            }
            return false;
        }

        // The local HUD owns a settable property of the participant type. Prefer a
        // candidate whose declaring type also holds a lap-timer UI field.
        private static bool FindHudProperty(System.Type[] types)
        {
            System.Reflection.PropertyInfo first = null;
            System.Reflection.PropertyInfo preferred = null;
            for (int i = 0; i < types.Length; i++)
            {
                System.Type t = types[i];
                if (t == null || t.IsInterface || t.IsAbstract)
                    continue;
                System.Reflection.PropertyInfo[] props;
                try
                {
                    props = t.GetProperties(System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.DeclaredOnly);
                }
                catch { continue; }
                for (int p = 0; p < props.Length; p++)
                {
                    System.Type pt;
                    try { pt = props[p].PropertyType; } catch { continue; }
                    if (pt != participantType)
                        continue;
                    if (props[p].GetSetMethod(true) == null || props[p].GetGetMethod(true) == null)
                        continue;
                    if (first == null)
                        first = props[p];
                    if (preferred == null)
                    {
                        try
                        {
                            System.Reflection.FieldInfo[] fields = t.GetFields(
                                System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            for (int f = 0; f < fields.Length; f++)
                            {
                                if (fields[f].FieldType != null &&
                                    fields[f].FieldType.Name.IndexOf("LapTimer",
                                        System.StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    preferred = props[p];
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            HudPlayerProperty = preferred ?? first;
            return HudPlayerProperty != null;
        }

        // Race identity is optional; reference splits just reset if it is missing.
        private static void FindRaceIdentity()
        {
            try
            {
                System.Reflection.PropertyInfo[] props = participantType.GetProperties(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.DeclaredOnly);
                for (int i = 0; i < props.Length; i++)
                {
                    if (props[i] == lapTimerProperty || !props[i].CanRead)
                        continue;
                    System.Type holder = props[i].PropertyType;
                    if (holder == null || holder.IsPrimitive || holder == typeof(string))
                        continue;
                    System.Reflection.PropertyInfo[] inner;
                    try
                    {
                        inner = holder.GetProperties(System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Instance);
                    }
                    catch { continue; }
                    for (int k = 0; k < inner.Length; k++)
                    {
                        System.Type it;
                        try { it = inner[k].PropertyType; } catch { continue; }
                        if (it != null && it.Name == "Race" && inner[k].CanRead)
                        {
                            raceAccessorProperty = props[i];
                            raceMemberProperty = inner[k];
                            raceType = it;
                            break;
                        }
                    }
                    if (raceType != null)
                        break;
                }
                if (raceType == null)
                    return;
                raceNameProperty = raceType.GetProperty("Name",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                raceIdProperty = raceType.GetProperty("LocalID",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (raceIdProperty != null)
                    raceIdStrField = raceIdProperty.PropertyType.GetField("str",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            }
            catch { }
        }

        public static void Attach(object player)
        {
            if (player == null)
            {
                Detach();
                return;
            }
            if (ReferenceEquals(player, activePlayer) && gateHandler != null)
                return;
            Detach();
            try
            {
                System.Reflection.MethodInfo dispatch = typeof(GateSplitsBinding).GetMethod(
                    "Dispatch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var thunk = new System.Reflection.Emit.DynamicMethod(
                    "GhGateSplitsThunk", typeof(void),
                    new System.Type[] { participantType, typeof(RaceCheckpointPassage), typeof(float) },
                    typeof(GateSplitsBinding), true);
                System.Reflection.Emit.ILGenerator il = thunk.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_2);
                il.Emit(System.Reflection.Emit.OpCodes.Call, dispatch);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                gateHandler = thunk.CreateDelegate(gateEvent.EventHandlerType);
                gateEvent.AddEventHandler(player, gateHandler);
                activePlayer = player;
                if (ModLog.DebugEnabled) GhostLinesPlugin.LogGateSplitsMessage("[GhostLines] gate-splits subscribed to the game gate event");
            }
            catch (System.Exception ex)
            {
                gateHandler = null;
                activePlayer = null;
                ModLog.LogWarning("[GhostLines] gate-splits subscribe failed: " + ex.Message);
            }
        }

        public static void Detach()
        {
            if (activePlayer != null && gateHandler != null)
            {
                try { gateEvent.RemoveEventHandler(activePlayer, gateHandler); }
                catch { }
            }
            activePlayer = null;
            activeHud = null;
            gateHandler = null;
        }

        private static bool dispatchErrorLogged = false;

        private static void Dispatch(object player, object passage, float gameDelta)
        {
            try
            {
                GhostLinePoller p = GhostLinesPlugin.poller;
                if (p == null || passage == null)
                    return;
                string id = ((RaceCheckpointPassage)passage).passageId;
                float lapTime = GetLapTime(player);
                p.OnRaceGateEvent(id, lapTime);
            }
            catch (System.Exception ex)
            {
                if (!dispatchErrorLogged)
                {
                    dispatchErrorLogged = true;
                    ModLog.LogWarning("[GhostLines] gate-splits dispatch EX " + ex);
                }
            }
        }

        public static float CurrentLapTime()
        {
            return IsAlive(activePlayer) ? GetLapTime(activePlayer) : -1f;
        }

        // Per-frame reflection (two PropertyInfo.GetValue calls plus a boxing
        // op_Implicit.Invoke) was the one recurring cost in the gate-splits tick.
        // Compile the same chain to an IL delegate once; fall back to reflection
        // if the delegate build fails for an unexpected shape.
        private static System.Func<object, float> lapTimeFast;

        private static void BuildLapTimeAccessor()
        {
            lapTimeFast = null;
            try
            {
                if (lapTimerProperty == null || lapTimerValueProperty == null)
                    return;
                System.Type holder = lapTimerProperty.PropertyType;
                System.Type valueType = lapTimerValueProperty.PropertyType;
                System.Reflection.MethodInfo getTimer = lapTimerProperty.GetGetMethod(true);
                System.Reflection.MethodInfo getTimerValue = lapTimerValueProperty.GetGetMethod(true);
                if (getTimer == null || getTimerValue == null)
                    return;
                var thunk = new System.Reflection.Emit.DynamicMethod(
                    "GhLapTimeRead", typeof(float), new System.Type[] { typeof(object) },
                    typeof(GateSplitsBinding), true);
                System.Reflection.Emit.ILGenerator il = thunk.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(holder.IsValueType ? System.Reflection.Emit.OpCodes.Call
                    : System.Reflection.Emit.OpCodes.Callvirt, getTimer);
                il.Emit(holder.IsValueType ? System.Reflection.Emit.OpCodes.Call
                    : System.Reflection.Emit.OpCodes.Callvirt, getTimerValue);
                if (valueType != typeof(float))
                    il.Emit(System.Reflection.Emit.OpCodes.Call, timerToFloat);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                lapTimeFast = (System.Func<object, float>)thunk.CreateDelegate(typeof(System.Func<object, float>));
            }
            catch
            {
                lapTimeFast = null;
            }
        }

        private static float GetLapTime(object player)
        {
            if (lapTimeFast != null)
            {
                try { return lapTimeFast(player); }
                catch { return -1f; }
            }
            try
            {
                object timer = lapTimerProperty.GetValue(player, null);
                if (timer == null)
                    return -1f;
                object raw = lapTimerValueProperty.GetValue(timer, null);
                if (raw == null)
                    return -1f;
                if (raw is float)
                    return (float)raw;
                if (timerToFloat == null)
                    return -1f;
                return (float)timerToFloat.Invoke(null, new object[] { raw });
            }
            catch { return -1f; }
        }

        private static object CurrentRace()
        {
            if (!IsAlive(activePlayer) || raceAccessorProperty == null || raceMemberProperty == null)
                return null;
            try
            {
                object accessor = raceAccessorProperty.GetValue(activePlayer, null);
                return accessor == null ? null : raceMemberProperty.GetValue(accessor, null);
            }
            catch { return null; }
        }

        public static string CurrentRaceName()
        {
            object race = CurrentRace();
            if (race == null)
                return null;
            try
            {
                if (raceNameProperty != null)
                    return raceNameProperty.GetValue(race, null) as string;
            }
            catch { }
            return null;
        }

        public static string CurrentRaceKey()
        {
            object race = CurrentRace();
            if (race == null)
                return null;
            try
            {
                if (raceIdProperty != null && raceIdStrField != null)
                {
                    object id = raceIdProperty.GetValue(race, null);
                    string str = id == null ? null : raceIdStrField.GetValue(id) as string;
                    if (!string.IsNullOrEmpty(str))
                        return str;
                }
            }
            catch { }
            try
            {
                string name = CurrentRaceName();
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            catch { }
            return race.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
