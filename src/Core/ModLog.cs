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

namespace LiftoffGhostLines
{
    internal static class ModLog
    {
        private static ManualLogSource source;
        private static ConfigEntry<bool> debugLogging;

        public static bool DebugEnabled
        {
            get { return debugLogging != null && debugLogging.Value; }
        }

        public static void Initialize(ManualLogSource logger, ConfigEntry<bool> enableDebugLogging)
        {
            source = logger;
            debugLogging = enableDebugLogging;
        }

        public static void Reset()
        {
            source = null;
            debugLogging = null;
        }

        public static void Log(object message)
        {
            if (source != null && debugLogging != null && debugLogging.Value)
                source.LogDebug(message);
        }

        public static void LogWarning(object message)
        {
            if (source != null)
                source.LogWarning(message);
            else
                UnityEngine.Debug.LogWarning(message);
        }

        public static void LogError(object message)
        {
            if (source != null)
                source.LogError(message);
            else
                UnityEngine.Debug.LogError(message);
        }

        private static readonly object logFileLock = new object();
        // One writer held open for the session (opened on first use) instead of
        // reopening the file per line; flushed at most once a second and on quit.
        private static System.IO.StreamWriter logFileWriter;
        private static bool logFileFailed;
        private static long logFileLastFlushTicks;
        private static readonly long logFileFlushIntervalTicks = System.Diagnostics.Stopwatch.Frequency;

        public static void LogFile(object message)
        {
            if (debugLogging == null || !debugLogging.Value || logFileFailed)
                return;
            try
            {
                lock (logFileLock)
                {
                    if (logFileWriter == null)
                    {
                        string path = System.IO.Path.Combine(
                            System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                            "ghostlines_delta.log");
                        logFileWriter = new System.IO.StreamWriter(path, true, new System.Text.UTF8Encoding(false), 16 * 1024);
                        logFileWriter.AutoFlush = false;
                    }
                    logFileWriter.Write('[');
                    logFileWriter.Write(System.DateTime.Now.ToString("HH:mm:ss.fff"));
                    logFileWriter.Write("] ");
                    logFileWriter.WriteLine(message);
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (now - logFileLastFlushTicks >= logFileFlushIntervalTicks)
                    {
                        logFileLastFlushTicks = now;
                        logFileWriter.Flush();
                    }
                }
            }
            catch
            {
                logFileFailed = true;
                CloseLogFile();
            }
        }

        public static void FlushLogFile()
        {
            lock (logFileLock)
            {
                try { if (logFileWriter != null) logFileWriter.Flush(); }
                catch { }
            }
        }

        public static void CloseLogFile()
        {
            lock (logFileLock)
            {
                try { if (logFileWriter != null) logFileWriter.Dispose(); }
                catch { }
                logFileWriter = null;
            }
        }
    }
}
