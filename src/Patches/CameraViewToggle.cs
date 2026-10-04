using System;
using HarmonyLib;
using UnityEngine;

namespace LiftoffGhostLines
{
    // The game's camera key (V) cycles first person -> third person close -> third
    // person far -> first person. With the mod's orbit camera that far step is
    // redundant, and in freecam/orbit the key already means "back to the drone
    // camera". This patch turns the game's cycle into a toggle (first person <->
    // third person close) and skips it entirely on frames where the mod used the
    // key itself, so one press never does both.
    internal static class CameraViewToggle
    {
        private static bool patched;
        private static AccessTools.FieldRef<FlightCameraControllerDefault, FlightCameraControllerDefault.CameraMode> currentModeRef;

        // Frame on which the mod consumed the camera key (set from the poller).
        internal static int modUsedKeyFrame = -1;
        // Asked from the prefix: would the mod consume the key this frame? Covers
        // the game's input running before the poller's Update in the same frame.
        internal static Func<bool> modWantsKey;

        internal static void Apply(Harmony harmony)
        {
            if (patched || harmony == null)
                return;
            patched = true;
            try
            {
                System.Reflection.MethodInfo cycle = FindCycleMethod();
                if (cycle == null)
                {
                    ModLog.LogWarning("[GhostLines] camera view toggle: game's camera cycle method not found; V keeps the game's behaviour");
                    return;
                }
                currentModeRef = AccessTools.FieldRefAccess<FlightCameraControllerDefault, FlightCameraControllerDefault.CameraMode>("currentCameraMode");
                harmony.Patch(cycle,
                    prefix: new HarmonyMethod(typeof(CameraViewToggle), nameof(CyclePrefix)),
                    postfix: new HarmonyMethod(typeof(CameraViewToggle), nameof(CyclePostfix)));
                GhostLinesPlugin.pluginLog.LogInfo("[GhostLines] camera view toggle patched");
                PatchStartInFirstPerson(harmony);
            }
            catch (Exception ex)
            {
                ModLog.LogWarning("[GhostLines] camera view toggle patch failed: " + ex.Message);
            }
        }

        // Every flight scene starts in first person. The game's camera setup applies
        // the session's camera mode, which comes from the saved preferred view (so
        // a third-person view picked in a replay carried into the next lobby).
        // Postfix the setup (public void taking the FlightCameraController) and
        // switch to first person through the game's own set-mode method (public
        // void (CameraMode, bool instant)).
        private static System.Reflection.MethodInfo setModeMethod;

        private static void PatchStartInFirstPerson(Harmony harmony)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
            System.Reflection.MethodInfo setup = null;
            foreach (var m in typeof(FlightCameraControllerDefault).GetMethods(flags))
            {
                var ps = m.GetParameters();
                if (m.ReturnType != typeof(void))
                    continue;
                if (ps.Length == 1 && ps[0].ParameterType == typeof(FlightCameraController))
                    setup = m;
                else if (ps.Length == 2 && ps[0].ParameterType == typeof(FlightCameraControllerDefault.CameraMode) &&
                    ps[1].ParameterType == typeof(bool))
                    setModeMethod = m;
            }
            if (setup == null || setModeMethod == null)
            {
                ModLog.LogWarning("[GhostLines] start-in-first-person: camera setup not found; scenes keep the saved view");
                return;
            }
            harmony.Patch(setup, postfix: new HarmonyMethod(typeof(CameraViewToggle), nameof(SetupPostfix)));
        }

        private static void SetupPostfix(FlightCameraControllerDefault __instance)
        {
            try
            {
                if (__instance.CurrentCameraMode != FlightCameraControllerDefault.CameraMode.FIRSTPERSON)
                    setModeMethod.Invoke(__instance, new object[] { FlightCameraControllerDefault.CameraMode.FIRSTPERSON, true });
            }
            catch (Exception ex)
            {
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] start-in-first-person EX " + ex);
            }
        }

        // The cycle method's name is obfuscated. It's the public parameterless void
        // method that stores the new mode into the game settings'
        // preferredCameraViewpoint field, so find it by that store in its IL.
        private static System.Reflection.MethodInfo FindCycleMethod()
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (var m in typeof(FlightCameraControllerDefault).GetMethods(flags))
            {
                if (m.ReturnType != typeof(void) || m.GetParameters().Length != 0)
                    continue;
                if (StoresField(m, "preferredCameraViewpoint"))
                    return m;
            }
            return null;
        }

        private static bool StoresField(System.Reflection.MethodInfo m, string fieldName)
        {
            byte[] il;
            try { il = m.GetMethodBody() != null ? m.GetMethodBody().GetILAsByteArray() : null; }
            catch { return false; }
            if (il == null)
                return false;
            const byte Stfld = 0x7D;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != Stfld)
                    continue;
                int token = BitConverter.ToInt32(il, i + 1);
                try
                {
                    var f = m.Module.ResolveField(token);
                    if (f != null && f.Name == fieldName)
                        return true;
                }
                catch { }
            }
            return false;
        }

        private static bool CyclePrefix(FlightCameraControllerDefault __instance, out bool __state)
        {
            __state = false;
            if (modUsedKeyFrame == Time.frameCount)
                return false;
            bool wants = false;
            GhostLinesPlugin.ourInputScope++;
            try { wants = modWantsKey != null && modWantsKey(); }
            catch { }
            finally { GhostLinesPlugin.ourInputScope--; }
            if (wants)
                return false;
            // Toggle: from third person close go straight back to first person by
            // letting the original step from "far" (far -> first person).
            if (currentModeRef != null && __instance.CurrentCameraMode == FlightCameraControllerDefault.CameraMode.THIRDPERSON_CLOSE)
            {
                currentModeRef(__instance) = FlightCameraControllerDefault.CameraMode.THIRDPERSON_FAR;
                __state = true;
            }
            return true;
        }

        // The game only switches when the session allows camera changes; if it
        // didn't (mode still the "far" we set), put the real mode back.
        private static void CyclePostfix(FlightCameraControllerDefault __instance, bool __state)
        {
            if (__state && currentModeRef != null &&
                __instance.CurrentCameraMode == FlightCameraControllerDefault.CameraMode.THIRDPERSON_FAR)
                currentModeRef(__instance) = FlightCameraControllerDefault.CameraMode.THIRDPERSON_CLOSE;
        }
    }
}
