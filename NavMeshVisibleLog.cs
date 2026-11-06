using System;
using System.Collections.Generic;

namespace BetterBannerlordNavmeshing
{
    // Editor-console mirror of the handful of run lines a waiting user needs.
    //
    // The FULL verbose trace stays exactly where it was: NavMeshLog still writes
    // every line to the per-session file, and nothing here adds to, drops or
    // reorders it. This class only decides which of those lines are also worth
    // showing on screen, and how often.
    //
    // Channel: the editor's Console panel is NATIVE (a Qt widget in
    // TaleWorlds.Native.dll: EditWindow finds the child named "consoleTextBrowser",
    // QConsoleManager owns the tabs and takes its text through the Qt signal
    // add_log_text_signal(rglVarString*, QColor, rglVarString*)). No managed type
    // in TaleWorlds.* touches it, so a mod cannot address the panel directly; it
    // can only reach the native sinks the panel is drawn from.
    //
    // The engine's own "No navigation mesh prefab was found with the name ..."
    // reaches it, and so does MBDebug.PostWarningLine: both end up in the native
    // GUI message sink rgl::post_*_line -> rglMessageManager::vfunc[3](text,
    // colour), and (text, colour) is exactly the shape the console's Qt signal
    // takes. That is the channel Print() uses first.
    //
    // TaleWorlds.Library.Debug.Print is a DIFFERENT channel (Debug.Print ->
    // DebugManager -> MBDebug.Print -> engine write_line -> the *filtered* rgl
    // logger: a level gate plus a `debugFilter & user|system_debug_filter` gate,
    // then the log files and a line callback). It is the second choice now: it
    // still feeds rgl_log and any Debug.OnPrint subscriber, but nothing in the
    // editor is known to read it. Console.WriteLine stays the last resort.
    //
    // MBDebug.PostWarningLine is [Conditional("_RGL_KEEP_ASSERTS")], which the
    // C# compiler evaluates in THIS assembly - without the symbol defined in the
    // csproj the call is stripped and channel 1 silently disappears.
    //
    // In game the same helper also pushes the line to the player through
    // InformationManager.DisplayMessage, but hard-throttled: a player wants a
    // heartbeat, not a log. It is a no-op when that API is unavailable (the
    // editor, or any build without it).
    //
    // Threading: the button, the completion handler and every "stage" line from
    // the main-thread half run on the editor main thread. The async v2 build's
    // progress arrives on its worker, so those lines are queued and printed by
    // Pump() from the editor tick - the console is only ever touched from the
    // thread that owns the UI. Nothing here ever throws into a caller.
    public static class NavMeshVisibleLog
    {
        public enum Context
        {
            Editor,   // editor console is the audience
            InGame    // also tell the player
        }

        // Console budget. The builder already logs a progress line roughly every
        // 2 s; these keep the console to a handful of lines per stage and never
        // repeat an unchanged percentage.
        private const int ProgressIntervalMs = 2500;
        private const int MaxProgressLinesPerStage = 4;

        // Shown when a progress line arrives before any stage has been announced
        // (a build that logs progress without a recognised heading).
        private const string UnknownStage = "building";

        // Every line carries this; StatusText hands out the same wording without it.
        private const string Prefix = "NavMesh: ";

        // Terminal-line markers, shared so nothing has to re-type them. The viewport
        // overlay tints on these (see NavMeshGenerator), so the wording stays in
        // exactly one place.
        public const string FailPrefix = "FAILED: ";
        public const string SkipPrefix = "import skipped: ";

        // Resting value of the editor script-panel status row between runs. It also
        // carries the one prerequisite the mod cannot satisfy for you, because the
        // engine has no managed setter for it (IConfig.SetRGLConfig takes an opaque
        // numeric id and no TaleWorlds.* assembly names show_debug_text): the
        // viewport overlay below only draws when the engine ini option
        // show_debug_text is on. The panel row itself needs nothing.
        public const string IdleStatus =
            "idle (viewport overlay needs show_debug_text = 1 in Modules\\Native\\engine_module.ini)";

        // Player budget: at most one progress message every 20 s, at most 2 per run
        // (plus the start and the finish, which always get through - those are the
        // lines that answer "is it still working?").
        private const int PlayerIntervalMs = 20000;
        private const int MaxPlayerProgressMessages = 2;

        private static readonly object Sync = new object();

        // Lines produced off the main thread, drained by Pump().
        private static readonly List<string> Pending = new List<string>();

        private static bool _enabled = true;
        private static Context _context = Context.Editor;
        private static int _mainThreadId;
        private static string _stage = "";
        private static int _stageIndex;
        private static int _stageProgressLines;
        private static int _lastPercent = -1;
        private static long _lastProgressMs;
        private static bool _progressShown;
        private static int _playerProgressMessages;
        private static long _lastPlayerMs;
        private static bool _consoleChannelBroken;
        private static bool _debugChannelBroken;
        private static bool _playerChannelBroken;

        // The one-line build status shown in the editor's script-variables panel
        // (NavMeshGenerator_DebugOptions.NavMeshBuildStatus). It is written from the SAME
        // Emit/Pump path that produces the visible lines, so the panel row and the
        // console wording cannot drift apart. Written under Sync; read via
        // StatusText, which locks.
        private static string _statusText = IdleStatus;

        // Master switch, fed from the NavMeshConsoleMessages setting at the start
        // of every run. Off means this class prints nothing at all.
        public static bool Enabled
        {
            get { lock (Sync) { return _enabled; } }
            set { lock (Sync) { _enabled = value; } }
        }

        // Called from the main thread by every run entry point, before the first
        // line. Also records which thread owns the console.
        public static void BeginRun(Context context, bool enabled)
        {
            lock (Sync)
            {
                _enabled = enabled;
                _context = context;
                _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                Pending.Clear();
                _stage = "";
                _stageIndex = 0;
                _stageProgressLines = 0;
                _lastPercent = -1;
                _lastProgressMs = 0;
                _progressShown = false;
                _playerProgressMessages = 0;
                _lastPlayerMs = 0;
                _statusText = IdleStatus;
            }
        }

        // The current one-line build status, without the "NavMesh: " prefix. This
        // is what the editor script-panel row shows. It follows the visible-log
        // line that was produced last, at the same throttled cadence, and it is
        // advanced even when NavMeshConsoleMessages is off - the panel is the
        // delivery mechanism now, so it must not depend on a console toggle.
        public static string StatusText
        {
            get { lock (Sync) { return _statusText; } }
        }

        // One line for the button press, so a build that takes minutes is never
        // a mystery: what is being built, from what, to what, in which mode.
        public static void Start(string sceneName, string prefabName, string buildTag, bool background)
        {
            string who;
            if (background)
                who = "background - the editor stays responsive; progress below";
            else if (CurrentContext() == Context.InGame)
                who = "blocking - the game will freeze until it finishes";
            else
                who = "blocking - the editor will freeze until it finishes";

            Emit("building '" + (sceneName ?? "?") + "' -> '" + (prefabName ?? "?")
                + "' (" + who + ") [" + (buildTag ?? "") + "]", true);
        }

        // A stage transition ("merging/stitching", "writing bin", ...). Repeats
        // of the current stage are swallowed, so a verbose run cannot flood the
        // console with the same heading.
        public static void Stage(string stage)
        {
            if (string.IsNullOrEmpty(stage))
                return;

            string text;
            lock (Sync)
            {
                if (string.Equals(stage, _stage, StringComparison.Ordinal))
                    return;

                _stage = stage;
                _stageIndex++;
                _stageProgressLines = 0;
                _lastPercent = -1;
                text = Heading() + stage + " ...";
                // The panel status advances whether or not the console is enabled;
                // only the printing below is gated.
                _statusText = text;
                if (!_enabled)
                    return;
            }

            Emit(text, false);
        }

        // A throttled progress line for the current stage. Prints only when the
        // percentage moved, at most once per ProgressIntervalMs, and at most
        // MaxProgressLinesPerStage times per stage.
        public static void Progress(int percent, string note)
        {
            string text;
            lock (Sync)
            {
                if (percent == _lastPercent)
                    return;
                if (_stageProgressLines >= MaxProgressLinesPerStage)
                    return;

                long now = Ms();
                if (_progressShown && now - _lastProgressMs < ProgressIntervalMs)
                    return;

                long sinceMs = _progressShown ? now - _lastProgressMs : 0;
                _lastProgressMs = now;
                _progressShown = true;
                _lastPercent = percent;
                _stageProgressLines++;
                text = Heading() + StageName() + " " + percent + "%"
                    + (string.IsNullOrEmpty(note) ? "" : " " + note)
                    + " (+" + (sinceMs / 1000.0).ToString("0.0",
                        System.Globalization.CultureInfo.InvariantCulture) + "s)";
                // As in Stage(): the panel status advances on the same throttled
                // cadence regardless of the console toggle; only printing is gated.
                _statusText = text;
                if (!_enabled)
                    return;
            }

            Emit(text, false);
        }

        // The last line of a successful run: the mesh, where it went, how long it
        // took. Always printed (force), even in the async mode.
        public static void Finish(string summary)
        {
            Emit("DONE: " + summary, true);
        }

        // The last line of a failed run, with the reason. Always printed.
        public static void Fail(string reason)
        {
            Emit(FailPrefix + Flatten(reason), true);
        }

        // A closing line that is NOT a failure: the run produced everything it
        // promised (the .bin is written) and only the *import* step was deferred,
        // because the engine resolves prefab names from an index it builds once
        // per start-up and cannot see a freshly written name yet.
        //
        // Always printed, like Start/Finish/Fail, and never budgeted away by the
        // progress throttle. It is deliberately worded as a skip, and it goes
        // out as an ordinary line, so it must never be read as an error.
        public static void Skip(string reason)
        {
            Emit(SkipPrefix + Flatten(reason), true);
        }

        // Editor tick hook: prints whatever the background build queued. A no-op
        // off the main thread and an empty-queue no-op.
        public static void Pump()
        {
            string[] items;
            lock (Sync)
            {
                if (Pending.Count == 0)
                    return;
                if (!OnMainThread())
                    return;
                items = Pending.ToArray();
                Pending.Clear();
            }

            for (int i = 0; i < items.Length; i++)
                Print(items[i]);

            // Whatever the queue just put on screen is now also the panel status.
            string last = items[items.Length - 1];
            if (last.StartsWith(Prefix, StringComparison.Ordinal))
                last = last.Substring(Prefix.Length);
            lock (Sync)
            {
                _statusText = last;
            }
        }

        // Classifies one of the pipeline's own log lines into a console stage or a
        // progress line; anything else is ignored. The line itself still goes to
        // NavMeshLog untouched, so the log FILE stays byte-identical - this is a
        // reader of that trace, not a writer into it.
        //
        // Recognised (v2 tiled voxel core + the shared write/import half):
        //   "assets: progress entities=N/M ..."  -> stage "collecting assets" + N/M %
        //   "v2: core engaged ..."                -> stage "building tiles"
        //   "v2: progress tiles=N/M (X%) ..."    -> X % of the current stage
        //   "v2: tiles=..."                       -> stage "merging/stitching"
        //   "v2: prune: ..."                      -> stage "pruning"
        //   "cleanup: welded=..."                 -> stage "cleanup"
        //   "writing bin: ..."                    -> stage "writing bin"
        //   "importing prefab '...' (shift 0)"    -> stage "importing"
        public static void Mirror(string logLine)
        {
            if (string.IsNullOrEmpty(logLine))
                return;

            if (StartsWith(logLine, "assets: progress entities="))
            {
                Stage("collecting assets");
                int pct = ParseRatioPercent(logLine, "entities=");
                if (pct >= 0)
                    Progress(pct, "");
                return;
            }

            if (StartsWith(logLine, "v2: progress tiles="))
            {
                int pct = ParseTilePercent(logLine);
                if (pct >= 0)
                    Progress(pct, "");
                return;
            }

            string stage = StageFor(logLine);
            if (stage != null)
                Stage(stage);
        }

        // The log line that belongs to a visible stage, or null.
        private static string StageFor(string line)
        {
            if (StartsWith(line, "v2: core engaged"))
                return "building tiles";
            if (StartsWith(line, "v2: tiles="))
                return "merging/stitching";
            if (StartsWith(line, "v2: prune:"))
                return "pruning";
            if (StartsWith(line, "cleanup: welded="))
                return "cleanup";
            if (StartsWith(line, "writing bin:"))
                return "writing bin";
            if (StartsWith(line, "importing prefab '"))
                return "importing";
            return null;
        }

        private static void Emit(string text, bool force)
        {
            string line = Prefix + text;
            lock (Sync)
            {
                if (!_enabled)
                    return;

                if (force || OnMainThread())
                {
                    Print(line);
                    // The status IS the visible line, minus the prefix: one
                    // derivation, so the panel row and the console cannot drift.
                    _statusText = text;
                    if (_context == Context.InGame && !_playerChannelBroken)
                        ShowPlayer(text, force);
                }
                else
                {
                    // Off the main thread: queue it, and collapse consecutive queued
                    // progress lines of the same stage into the newest one. The
                    // status moves when Pump() actually prints them, which keeps
                    // the panel showing the same line the console just showed.
                    if (Pending.Count > 0 && IsProgressText(Pending[Pending.Count - 1]))
                        Pending.RemoveAt(Pending.Count - 1);
                    Pending.Add(line);
                }
            }
        }

        // Prints one line to the editor console. Channels, best first:
        //   1. TaleWorlds.Engine.MBDebug.PostWarningLine - the native GUI message
        //      sink the editor's Qt Console panel is drawn from.
        //   2. TaleWorlds.Library.Debug.Print - the filtered rgl log channel
        //      (what V3.48 used on its own).
        //   3. Console.WriteLine.
        // Each channel that throws is remembered, so a missing engine binding
        // costs one exception per session rather than one per line. Never throws.
        private static void Print(string line)
        {
            try
            {
                if (!_consoleChannelBroken)
                {
                    TaleWorlds.Engine.MBDebug.PostWarningLine(line);
                    return;
                }
            }
            catch (Exception)
            {
                // post_warning_line is missing or refuses this thread: stop trying
                // it for the rest of the session.
                _consoleChannelBroken = true;
            }

            try
            {
                if (!_debugChannelBroken)
                {
                    TaleWorlds.Library.Debug.Print(line);
                    return;
                }
            }
            catch (Exception)
            {
                // Debug.Print is missing or refuses this thread: stop trying it for
                // the rest of the session rather than throwing on every line.
                _debugChannelBroken = true;
            }

            try
            {
                Console.WriteLine(line);
            }
            catch (Exception)
            {
            }
        }

        // The player-facing copy. Start/finish/failure always go through; a
        // progress line only every PlayerIntervalMs and at most
        // MaxPlayerProgressMessages times per run. A no-op if the API is absent.
        private static void ShowPlayer(string text, bool force)
        {
            long now = Ms();
            if (!force)
            {
                if (_playerProgressMessages >= MaxPlayerProgressMessages)
                    return;
                if (_playerProgressMessages > 0 && now - _lastPlayerMs < PlayerIntervalMs)
                    return;
                _playerProgressMessages++;
            }

            _lastPlayerMs = now;
            try
            {
                TaleWorlds.Library.InformationManager.DisplayMessage(
                    new TaleWorlds.Library.InformationMessage(text));
            }
            catch (Exception)
            {
                // No InformationManager (editor, headless, stripped build): stop asking.
                _playerChannelBroken = true;
            }
        }

        private static string Heading()
        {
            if (_stageIndex <= 0)
                return "";
            return "stage " + _stageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": ";
        }

        private static string StageName()
        {
            return string.IsNullOrEmpty(_stage) ? UnknownStage : _stage;
        }

        private static Context CurrentContext()
        {
            lock (Sync) { return _context; }
        }

        private static bool OnMainThread()
        {
            return _mainThreadId == 0
                || _mainThreadId == System.Threading.Thread.CurrentThread.ManagedThreadId;
        }

        private static bool IsProgressText(string queuedLine)
        {
            return queuedLine != null && queuedLine.Contains("% (");
        }

        private static long Ms()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp()
                * 1000L / System.Diagnostics.Stopwatch.Frequency;
        }

        private static bool StartsWith(string line, string prefix)
        {
            return line.StartsWith(prefix, StringComparison.Ordinal);
        }

        // "v2: progress tiles=12/100 (12%) elapsedMs=2000" -> 12
        private static int ParseTilePercent(string line)
        {
            int open = line.IndexOf('(');
            if (open < 0)
                return -1;
            int close = line.IndexOf(')', open + 1);
            if (close <= open)
                return -1;

            int value = 0;
            bool any = false;
            for (int i = open + 1; i < close; i++)
            {
                char c = line[i];
                if (c >= '0' && c <= '9')
                {
                    value = value * 10 + (c - '0');
                    any = true;
                    continue;
                }
                if (any)
                    break;
            }

            return any ? value : -1;
        }

        // "assets: progress entities=1200/4336 included=..." -> 27
        private static int ParseRatioPercent(string line, string key)
        {
            int at = line.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
                return -1;

            int i = at + key.Length;
            int num = 0;
            bool any = false;
            while (i < line.Length && line[i] >= '0' && line[i] <= '9')
            {
                num = num * 10 + (line[i] - '0');
                any = true;
                i++;
            }
            if (!any || i >= line.Length || line[i] != '/')
                return -1;

            i++;
            int den = 0;
            while (i < line.Length && line[i] >= '0' && line[i] <= '9')
            {
                den = den * 10 + (line[i] - '0');
                i++;
            }
            if (den <= 0)
                return -1;

            return num * 100 / den;
        }

        private static string Flatten(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "(no reason given)";
            return value.Replace("\r\n", " | ").Replace("\r", " | ").Replace("\n", " | ");
        }
    }
}