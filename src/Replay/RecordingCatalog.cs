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
        // The replay-state object a recording belongs to: the one holding both a
        // GhostPlayerProfile field and a LocalGhostStatesRecording field that
        // points at the recording. Name and colour are both read from it.
        private sealed class RecordedPlayerOwner
        {
            public object obj;
            public System.Reflection.FieldInfo profileField;
            public System.Reflection.FieldInfo colorField;
        }

        // Owners found in the current replay race, by recording. Only hits are
        // cached; a hit stays valid for the race, a miss may still fill in.
        private readonly Dictionary<object, RecordedPlayerOwner> recordedOwners = new Dictionary<object, RecordedPlayerOwner>();
        private object recordedOwnersRace;
        private readonly Dictionary<Type, System.Reflection.FieldInfo[]> instanceFieldsByType = new Dictionary<Type, System.Reflection.FieldInfo[]>();

        public string ResolvePilotNameFromReplayState(object recording)
        {
            RecordedPlayerOwner owner = FindRecordedPlayerOwner(recording);
            if (owner == null)
                return null;
            try
            {
                var profile = owner.profileField.GetValue(owner.obj) as GhostPlayerProfile;
                string nickname = profile != null ? profile.Nickname : null;
                return string.IsNullOrWhiteSpace(nickname) ? null : nickname;
            }
            catch { return null; }
        }

        public Color? ResolvePilotColorFromReplayState(object recording)
        {
            RecordedPlayerOwner owner = FindRecordedPlayerOwner(recording);
            if (owner == null)
                return null;
            if (owner.colorField != null)
            {
                try
                {
                    Color c = (Color)owner.colorField.GetValue(owner.obj);
                    if (c.a > 0.01f && (c.r > 0.01f || c.g > 0.01f || c.b > 0.01f))
                        return c;
                }
                catch { }
            }
            try
            {
                var profile = owner.profileField.GetValue(owner.obj) as GhostPlayerProfile;
                if (profile != null)
                    return profile.Color;
            }
            catch { }
            return null;
        }

        private RecordedPlayerOwner FindRecordedPlayerOwner(object recording)
        {
            if (recording == null || replayController == null)
                return null;
            object replayRace = GetReplayRace();
            if (replayRace == null)
                return null;
            if (!object.ReferenceEquals(replayRace, recordedOwnersRace))
            {
                recordedOwnersRace = replayRace;
                recordedOwners.Clear();
            }
            RecordedPlayerOwner owner;
            if (recordedOwners.TryGetValue(recording, out owner))
                return owner;
            owner = WalkForRecordedPlayerOwner(replayRace, recording, 0, new HashSet<object>());
            if (owner != null)
                recordedOwners[recording] = owner;
            return owner;
        }

        private System.Reflection.FieldInfo[] InstanceFields(Type type)
        {
            System.Reflection.FieldInfo[] fields;
            if (!instanceFieldsByType.TryGetValue(type, out fields))
            {
                fields = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                instanceFieldsByType[type] = fields;
            }
            return fields;
        }

        private RecordedPlayerOwner WalkForRecordedPlayerOwner(object obj, object recording, int depth, HashSet<object> visited)
        {
            if (obj == null || depth > 4)
                return null;
            if (object.ReferenceEquals(obj, recording))
                return null;
            if (obj is string || obj is System.Type || obj is System.Reflection.MemberInfo || obj is System.Collections.IDictionary)
                return null;
            if (obj is UnityEngine.Object && depth > 0)
                return null;
            if (!visited.Add(obj))
                return null;

            System.Reflection.FieldInfo[] fields = InstanceFields(obj.GetType());
            System.Reflection.FieldInfo profileField = null;
            System.Reflection.FieldInfo recordingField = null;
            System.Reflection.FieldInfo colorField = null;
            for (int i = 0; i < fields.Length; i++)
            {
                Type ft = fields[i].FieldType;
                if (ft == typeof(GhostPlayerProfile))
                    profileField = fields[i];
                else if (ft == typeof(LocalGhostStatesRecording))
                    recordingField = fields[i];
                else if (ft == typeof(Color))
                    colorField = fields[i];
            }

            if (profileField != null && recordingField != null)
            {
                object linked = null;
                try { linked = recordingField.GetValue(obj); } catch { }
                if (object.ReferenceEquals(linked, recording))
                    return new RecordedPlayerOwner { obj = obj, profileField = profileField, colorField = colorField };
            }

            for (int i = 0; i < fields.Length; i++)
            {
                Type ft = fields[i].FieldType;
                if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string) || ft == typeof(System.Type))
                    continue;
                object child = null;
                try { child = fields[i].GetValue(obj); } catch { }
                RecordedPlayerOwner nested = WalkForRecordedPlayerOwner(child, recording, depth + 1, visited);
                if (nested != null)
                    return nested;
            }

            if (obj is System.Collections.IEnumerable enumerable)
            {
                int walked = 0;
                foreach (var item in enumerable)
                {
                    if (walked++ >= 256)
                        break;
                    RecordedPlayerOwner nested = WalkForRecordedPlayerOwner(item, recording, depth + 1, visited);
                    if (nested != null)
                        return nested;
                }
            }

            return null;
        }
    }
}
