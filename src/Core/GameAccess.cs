using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace LiftoffGhostLines
{
    // One place for runtime type discovery against the game's (partly obfuscated)
    // assemblies. Hits are cached for the session. Misses are cached too, with a
    // backoff, so a type a game update renamed doesn't turn every retry into a
    // walk of every loaded assembly's GetTypes(). A miss is retried early when a
    // new assembly has loaded since, because that's the usual reason for one.
    internal static class GameAccess
    {
        private const float MissRetryMin = 5f;
        private const float MissRetryMax = 60f;

        private sealed class Miss
        {
            public int assemblyCount;
            public float retryAt;
            public float backoff;
        }

        private static readonly Dictionary<string, Type> hits = new Dictionary<string, Type>();
        private static readonly Dictionary<string, Miss> misses = new Dictionary<string, Miss>();
        private static readonly Dictionary<Assembly, Type[]> typesByAssembly = new Dictionary<Assembly, Type[]>();
        // Assemblies whose GetTypes() threw, with the loaded-assembly count at the
        // time. A load exception usually means a dependency wasn't loaded yet, so
        // the list is fetched again once more assemblies are loaded.
        private static readonly Dictionary<Assembly, int> partialTypes = new Dictionary<Assembly, int>();
        private static Assembly gameAssembly;

        // Assembly-CSharp. LocalGhostStatesRecording is referenced at compile time,
        // so this never needs a search.
        internal static Assembly GameAssembly
        {
            get
            {
                if (gameAssembly == null)
                    gameAssembly = typeof(LocalGhostStatesRecording).Assembly;
                return gameAssembly;
            }
        }

        // Every type in Assembly-CSharp (partial on a load exception), fetched once.
        internal static Type[] GameTypes()
        {
            return TypesOf(GameAssembly);
        }

        internal static Type[] TypesOf(Assembly asm)
        {
            if (asm == null)
                return new Type[0];
            Type[] types;
            int partialAt = 0;
            bool cached = typesByAssembly.TryGetValue(asm, out types);
            if (cached && !partialTypes.TryGetValue(asm, out partialAt))
                return types;
            int loaded = AppDomain.CurrentDomain.GetAssemblies().Length;
            if (cached && loaded <= partialAt)
                return types;
            partialTypes.Remove(asm);
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types ?? new Type[0];
                partialTypes[asm] = loaded;
            }
            catch
            {
                types = new Type[0];
                partialTypes[asm] = loaded;
            }
            typesByAssembly[asm] = types;
            return types;
        }

        // A game type by simple name (namespace ignored), Assembly-CSharp only.
        internal static Type FindGameType(string simpleName)
        {
            return Lookup("game:" + simpleName, () =>
            {
                Type direct = null;
                try { direct = GameAssembly.GetType(simpleName, false); }
                catch { }
                return direct ?? FindByName(GameTypes(), simpleName);
            });
        }

        // A type by full name in Assembly-CSharp.
        internal static Type FindGameTypeByFullName(string fullName)
        {
            return Lookup("gamefull:" + fullName, () =>
            {
                try { return GameAssembly.GetType(fullName, false); }
                catch { return null; }
            });
        }

        // A type by simple name in any loaded assembly (Assembly-CSharp first).
        internal static Type FindAnyType(string simpleName)
        {
            return Lookup("any:" + simpleName, () =>
            {
                Type t = FindByName(GameTypes(), simpleName);
                if (t != null)
                    return t;
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int a = 0; a < assemblies.Length; a++)
                {
                    if (assemblies[a] == GameAssembly || assemblies[a].IsDynamic)
                        continue;
                    t = FindByName(TypesOf(assemblies[a]), simpleName);
                    if (t != null)
                        return t;
                }
                return null;
            });
        }

        private static Type FindByName(Type[] types, string simpleName)
        {
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] != null && types[i].Name == simpleName)
                    return types[i];
            }
            return null;
        }

        private static Type Lookup(string key, Func<Type> search)
        {
            Type hit;
            if (hits.TryGetValue(key, out hit))
                return hit;
            int assemblyCount = AppDomain.CurrentDomain.GetAssemblies().Length;
            Miss miss;
            if (misses.TryGetValue(key, out miss) &&
                miss.assemblyCount == assemblyCount && Time.unscaledTime < miss.retryAt)
                return null;
            Type found = null;
            try { found = search(); }
            catch { }
            if (found != null)
            {
                hits[key] = found;
                misses.Remove(key);
                return found;
            }
            if (miss == null)
            {
                miss = new Miss { backoff = MissRetryMin };
                misses[key] = miss;
                ModLog.LogWarning("[GhostLines] type lookup missed: " + key + " (retrying with backoff)");
            }
            else
            {
                miss.backoff = Mathf.Min(miss.backoff * 2f, MissRetryMax);
            }
            miss.assemblyCount = assemblyCount;
            miss.retryAt = Time.unscaledTime + miss.backoff;
            return null;
        }
    }
}
