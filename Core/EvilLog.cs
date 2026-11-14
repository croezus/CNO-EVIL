using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Categories of the debug log. Each has a switch in section 02 of the config, except Mark (always on with DebugLogging).
    /// </summary>
    public enum LogCat
    {
        Radar,
        Track,
        Missile,
        AI,
        EW,
        Net,
        Placement,
        Data,
        Mark
    }

    /// <summary>
    /// Debug log of the mod: one file for each game session in BepInEx\CNO-EVIL-Logs, written only with DebugLogging.
    /// Each line has the scene number, the time in the scene and the frame, so that events can be put in order.
    /// Plain BepInEx lines of the mod (information, warnings, errors) stay in LogOutput.log and are copied to the file.
    /// A caller on a frequent path tests On(cat) first: the arguments of Write are boxed before Write can return.
    /// </summary>
    public static class EvilLog
    {
        private const string FolderName = "CNO-EVIL-Logs";
        private const long MaxFileChars = 64L * 1024L * 1024L;
        private const int MaxPendingLines = 200;

        private sealed class EveryState
        {
            public float Next;
            public int Skipped;
        }

        // Copies the BepInEx lines of the mod to the session file
        private sealed class Mirror : ILogListener
        {
            public void LogEvent(object sender, LogEventArgs eventArgs)
            {
                if (eventArgs == null || eventArgs.Source != CnoEvilPlugin.Log) return;
                MirrorLine(eventArgs.Level, eventArgs.Data);
            }

            public void Dispose()
            {
            }
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly int[] s_counts = new int[Enum.GetValues(typeof(LogCat)).Length];
        private static readonly Dictionary<string, int> s_once = new Dictionary<string, int>();
        private static readonly Dictionary<string, EveryState> s_every = new Dictionary<string, EveryState>();
        private static readonly List<string> s_pending = new List<string>();

        private static Mirror s_mirror;
        private static StreamWriter s_writer;
        private static bool s_openFailed;
        private static bool s_dirty;
        private static bool s_full;
        private static long s_chars;
        private static int s_scene;
        private static string s_sceneName = "-";
        // The clock restarts before EndScene runs: the duration comes from the last Flush
        private static float s_sceneTime;

        /// <summary>
        /// Plugin Awake: starts the copy of the BepInEx lines and the capture of exceptions that no patch caught.
        /// </summary>
        public static void Initialize()
        {
            if (s_mirror != null) return;
            s_mirror = new Mirror();
            BepInEx.Logging.Logger.Listeners.Add(s_mirror);
            Application.logMessageReceived += OnUnityLog;
        }

        /// <summary>
        /// True when lines of this category are written. No allocation.
        /// </summary>
        public static bool On(LogCat cat)
        {
            if (CnoEvilConfig.DebugLogging == null || !CnoEvilConfig.DebugLogging.Value) return false;
            switch (cat)
            {
                case LogCat.Radar: return CnoEvilConfig.DebugLogRadar.Value;
                case LogCat.Track: return CnoEvilConfig.DebugLogTrack.Value;
                case LogCat.Missile: return CnoEvilConfig.DebugLogMissile.Value;
                case LogCat.AI: return CnoEvilConfig.DebugLogAI.Value;
                case LogCat.EW: return CnoEvilConfig.DebugLogEW.Value;
                case LogCat.Net: return CnoEvilConfig.DebugLogNet.Value;
                case LogCat.Placement: return CnoEvilConfig.DebugLogPlacement.Value;
                case LogCat.Data: return CnoEvilConfig.DebugLogData.Value;
                default: return true;
            }
        }

        /// <summary>
        /// Scene number, time in the scene (s) and frame.
        /// </summary>
        public static string Stamp()
        {
            return string.Format(Inv, "M{0} t={1:F1} f={2}", s_scene, Time.timeSinceLevelLoad, Time.frameCount);
        }

        public static void Write(LogCat cat, string format, params object[] args)
        {
            if (!On(cat)) return;
            WriteFormatted(cat, format, args, null);
        }

        /// <summary>
        /// One line for each key and scene. The end of the scene shows how many times the key came.
        /// </summary>
        public static void Once(LogCat cat, string key, string format, params object[] args)
        {
            if (!On(cat)) return;
            int count;
            s_once.TryGetValue(key, out count);
            s_once[key] = count + 1;
            if (count == 0) WriteFormatted(cat, format, args, null);
        }

        /// <summary>
        /// At most one line for each key in this number of seconds. The next line tells how many were skipped.
        /// </summary>
        public static void Every(LogCat cat, string key, float seconds, string format, params object[] args)
        {
            if (!On(cat)) return;
            float now = Time.unscaledTime;
            EveryState state;
            if (!s_every.TryGetValue(key, out state))
            {
                state = new EveryState();
                s_every[key] = state;
            }
            else if (now < state.Next)
            {
                state.Skipped++;
                return;
            }
            string suffix = state.Skipped > 0 ? string.Format(Inv, " (+{0} skipped)", state.Skipped) : null;
            state.Next = now + seconds;
            state.Skipped = 0;
            WriteFormatted(cat, format, args, suffix);
        }

        /// <summary>
        /// Name of a unit for the log: the unit name, else the jsonKey (a unit that is not ready has no name yet), and its id.
        /// </summary>
        public static string Name(Unit unit)
        {
            if (unit == null) return "none";
            return Label(unit) + "#" + unit.GetInstanceID().ToString(Inv);
        }

        /// <summary>
        /// Name of a unit without the id.
        /// </summary>
        public static string Label(Unit unit)
        {
            if (unit == null) return "none";
            if (!string.IsNullOrEmpty(unit.unitName)) return unit.unitName;
            if (unit.definition != null && !string.IsNullOrEmpty(unit.definition.jsonKey)) return unit.definition.jsonKey;
            return unit.name;
        }

        /// <summary>
        /// Start of a scene. The session file opens here, after all plugins have loaded.
        /// </summary>
        public static void BeginScene(string sceneName)
        {
            s_scene++;
            s_sceneName = sceneName;
            if (CnoEvilConfig.DebugLogging == null || !CnoEvilConfig.DebugLogging.Value) return;
            WriteFormatted(LogCat.Mark, "=== scene {0} {1}, role {2}, mod active {3} ===",
                new object[] { s_scene, sceneName, CnoEvilNet.Role, CnoEvilConfig.IsModActive }, null);
        }

        /// <summary>
        /// End of a scene (CnoEvilPlugin.ResetAll): the counts of the scene, then all scene data is cleared.
        /// </summary>
        public static void EndScene()
        {
            PatchErrorLog.ReportAndClear();

            if (s_writer != null)
            {
                StringBuilder line = new StringBuilder();
                line.AppendFormat(Inv, "=== end of scene {0} {1} after about {2:F0} s:", s_scene, s_sceneName, s_sceneTime);
                bool any = false;
                for (int i = 0; i < s_counts.Length; i++)
                {
                    if (s_counts[i] == 0) continue;
                    line.AppendFormat(Inv, " {0} {1}", (LogCat)i, s_counts[i]);
                    any = true;
                }
                line.Append(any ? " ===" : " no events ===");
                WriteFormatted(LogCat.Mark, "{0}", new object[] { line.ToString() }, null);

                foreach (KeyValuePair<string, int> entry in s_once)
                {
                    if (entry.Value > 1) WriteFormatted(LogCat.Mark, "repeats: {0} x{1}", new object[] { entry.Key, entry.Value }, null);
                }
                Flush();
            }

            s_once.Clear();
            s_every.Clear();
            Array.Clear(s_counts, 0, s_counts.Length);
            s_sceneTime = 0.0f;
        }

        /// <summary>
        /// Writes the buffer to disk. Called each 2 s, so a crash loses at most that much.
        /// </summary>
        public static void Flush()
        {
            s_sceneTime = Mathf.Max(s_sceneTime, Time.timeSinceLevelLoad);
            if (s_writer == null || !s_dirty) return;
            try
            {
                s_writer.Flush();
                s_dirty = false;
            }
            catch (Exception ex)
            {
                CloseAfterFailure(ex);
            }
        }

        /// <summary>
        /// Plugin unload.
        /// </summary>
        public static void Close()
        {
            Application.logMessageReceived -= OnUnityLog;
            if (s_mirror != null)
            {
                BepInEx.Logging.Logger.Listeners.Remove(s_mirror);
                s_mirror = null;
            }
            if (s_writer == null) return;
            try
            {
                s_writer.Dispose();
            }
            catch (Exception)
            {
                // The game is closing and the log is the only place to report this
            }
            s_writer = null;
        }

        private static void WriteFormatted(LogCat cat, string format, object[] args, string suffix)
        {
            try
            {
                string text = (args != null && args.Length > 0) ? string.Format(Inv, format, args) : format;
                s_counts[(int)cat]++;
                WriteLine(string.Format(Inv, "[{0} {1}] {2}{3}", Stamp(), cat.ToString().ToUpperInvariant(), text, suffix));
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("EvilLog.Write", ex);
            }
        }

        private static void WriteLine(string line)
        {
            if (s_writer == null && !Open()) return;
            if (s_full) return;
            s_writer.WriteLine(line);
            s_dirty = true;
            s_chars += line.Length + 2;
            if (s_chars >= MaxFileChars)
            {
                s_writer.WriteLine("The session log is full: no more lines are written.");
                s_full = true;
                Flush();
            }
        }

        private static void MirrorLine(LogLevel level, object data)
        {
            if (data == null) return;
            bool urgent = (level & (LogLevel.Fatal | LogLevel.Error)) != 0;
            try
            {
                string line = string.Format(Inv, "[{0} {1}] {2}", Stamp(), level.ToString().ToUpperInvariant(), data);
                if (s_writer != null)
                {
                    WriteLine(line);
                    if (urgent) Flush();
                }
                else if (s_pending.Count < MaxPendingLines)
                {
                    // Lines before the file opens: the start of the plugin
                    s_pending.Add(line);
                }
            }
            catch (Exception)
            {
                // A failed copy must not break the BepInEx log: the line is already in LogOutput.log
            }
        }

        private static bool Open()
        {
            if (s_openFailed) return false;
            try
            {
                string folder = Path.Combine(Paths.BepInExRootPath, FolderName);
                Directory.CreateDirectory(folder);
                PruneOldFiles(folder);
                string path = Path.Combine(folder, "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv) + ".log");
                s_writer = new StreamWriter(path, false, new UTF8Encoding(false));
                s_chars = 0;
                s_full = false;
                WriteHeader();
                for (int i = 0; i < s_pending.Count; i++) s_writer.WriteLine(s_pending[i]);
                s_pending.Clear();
                s_writer.Flush();
                return true;
            }
            catch (Exception ex)
            {
                s_openFailed = true;
                s_writer = null;
                if (CnoEvilPlugin.Log != null) CnoEvilPlugin.Log.LogWarning("Debug log file not opened: " + ex.Message);
                return false;
            }
        }

        private static void PruneOldFiles(string folder)
        {
            string[] files = Directory.GetFiles(folder, "session-*.log");
            Array.Sort(files, StringComparer.Ordinal);
            int keep = Mathf.Max(1, CnoEvilConfig.DebugLogKeepSessions.Value);
            // The new file is the last one of the kept files
            for (int i = 0; i < files.Length - keep + 1; i++)
            {
                File.Delete(files[i]);
            }
        }

        private static void WriteHeader()
        {
            s_writer.WriteLine(string.Format(Inv, "CNO-EVIL {0}, protocol {1}, game {2}, Unity {3}, started {4:yyyy-MM-dd HH:mm:ss}",
                CnoEvilPlugin.ModVersion, CnoEvilNet.Protocol, Application.version, Application.unityVersion, DateTime.Now));

            s_writer.WriteLine("Plugins:");
            foreach (KeyValuePair<string, PluginInfo> plugin in BepInEx.Bootstrap.Chainloader.PluginInfos)
            {
                if (plugin.Value == null || plugin.Value.Metadata == null) continue;
                s_writer.WriteLine(string.Format(Inv, "  {0} {1} ({2})", plugin.Value.Metadata.Name, plugin.Value.Metadata.Version, plugin.Key));
            }

            s_writer.WriteLine("Settings that are not the default:");
            foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> entry in CnoEvilConfig.File)
            {
                if (entry.Value == null || Equals(entry.Value.BoxedValue, entry.Value.DefaultValue)) continue;
                s_writer.WriteLine(string.Format(Inv, "  [{0}] {1} = {2} (default {3})",
                    entry.Key.Section, entry.Key.Key, entry.Value.BoxedValue, entry.Value.DefaultValue));
            }
        }

        // An exception that no patch or try caught reaches only the Unity log. It goes to the mod log when its stack has mod code.
        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception || stackTrace == null) return;
            if (stackTrace.IndexOf("CnoEvil", StringComparison.Ordinal) < 0) return;
            string first = condition ?? "exception";
            if (first.Length > 80) first = first.Substring(0, 80);
            PatchErrorLog.Once("Unhandled " + first, condition + "\n" + stackTrace);
        }

        private static void CloseAfterFailure(Exception ex)
        {
            s_openFailed = true;
            s_writer = null;
            if (CnoEvilPlugin.Log != null) CnoEvilPlugin.Log.LogWarning("Debug log file closed: " + ex.Message);
        }
    }
}
