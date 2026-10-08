using System;
using System.Globalization;
using System.IO;

namespace BetterBannerlordNavmeshing
{
    // Session log file for navmesh runs. In-editor Console.WriteLine is invisible
    // (not shown in the editor console and not written to rgl_log), so the whole
    // run trace is written to a real file as well as mirrored to the console.
    //
    // Thread-safe, and every file operation is wrapped: logging must never throw
    // into the caller or interrupt a run. Write/Initialize/Shutdown take the same
    // lock, so the async v2 build thread can log progress concurrently with the
    // editor main thread (the writer is AutoFlush, and each line is written under
    // the lock, so lines never interleave).
    public static class NavMeshLog
    {
        private static readonly object Sync = new object();
        private static StreamWriter _writer;
        private static string _path;
        private static bool _enabled;
        // Clock seam: resolved once per Initialize call and used by the banner, the filename and every
        // line prefix, so all three agree. Defaults to the wall clock, which keeps the no-stamp behaviour
        // byte-identical to before.
        private static Func<DateTime> _now = delegate { return DateTime.Now; };
        private static DateTime _runStamp;

        // Seam for tests/tools that want to drive the clock without stamping a whole run.
        public static Func<DateTime> NowProvider
        {
            get { return _now; }
            set { _now = (value != null) ? value : (delegate { return DateTime.Now; }); }
        }

        public static bool IsEnabled
        {
            get { lock (Sync) { return _enabled && _writer != null; } }
        }

        public static string CurrentFilePath
        {
            get { lock (Sync) { return _path; } }
        }

        // Opens the per-session file on first use (or reopens after a disable);
        // every call also starts a new run with a separator banner. A folder
        // override is honoured on the opening call; the default is
        // <Documents>\Mount and Blade II Bannerlord\BetterBannerlordNavmeshing\Logs\.
        //
        // CLOCK SEAM. This class wrote DateTime.Now in three places - the run banner, the log FILENAME,
        // and every single line prefix - so two artefact runs differed in the banner, the filename and
        // every line, no matter how deterministic the mesh was. That is the "editor outputs differ by
        // KB" symptom, and it is independent of NavMeshCleanup.
        //
        // `stamp` resolves all three from ONE value: supplied, the filename, the banner and every line
        // prefix are all derived from it, so two runs of the same mesh are byte-comparable after the
        // filename. Not supplied (the default, and every existing caller), behaviour is byte-identical to
        // before. NowProvider is the same seam for callers that want to inject a clock without
        // stamping a whole run.
        public static void Initialize(bool enabled, string folderOverride, DateTime? stamp = null)
        {
            _runStamp = stamp.HasValue ? stamp.Value : _now();
            lock (Sync)
            {
                try
                {
                    if (!enabled)
                    {
                        CloseLocked();
                        _enabled = false;
                        return;
                    }

                    if (_writer == null)
                    {
                        _enabled = true;
                        if (!OpenLocked(folderOverride))
                        {
                            _enabled = false;
                            return;
                        }
                    }

                    WriteLocked("===== run @ " + _runStamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " =====");
                    WriteLocked("log file: " + _path);
                    Console.WriteLine("NavMesh: log file: " + _path);
                }
                catch (Exception)
                {
                    // Logging must never break a run.
                }
            }
        }

        // File line "[HH:mm:ss.fff] <message>" (CR/LF flattened to " | "), plus a
        // console mirror "NavMesh: <message>". Never throws.
        public static void Write(string message)
        {
            string flat = Flatten(message);
            try
            {
                Console.WriteLine("NavMesh: " + flat);
            }
            catch (Exception)
            {
            }

            lock (Sync)
            {
                WriteLocked(flat);
            }
        }

        public static void Shutdown()
        {
            lock (Sync)
            {
                CloseLocked();
                _enabled = false;
            }
        }

        private static bool OpenLocked(string folderOverride)
        {
            string folder = string.IsNullOrEmpty(folderOverride) ? DefaultFolder() : folderOverride;
            if (!TryEnsureFolder(folder))
            {
                folder = Path.GetTempPath();
                if (!TryEnsureFolder(folder))
                    return false;
            }

            try
            {
                string file = Path.Combine(folder,
                    "NavMesh_" + _runStamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".log");
                var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(stream);
                _writer.AutoFlush = true;
                _path = file;
                return true;
            }
            catch (Exception)
            {
                CloseLocked();
                return false;
            }
        }

        private static string DefaultFolder()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(documents, "Mount and Blade II Bannerlord",
                "BetterBannerlordNavmeshing", "Logs");
        }

        private static bool TryEnsureFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void WriteLocked(string flat)
        {
            if (_writer == null)
                return;

            try
            {
                _writer.WriteLine("[" + _runStamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] " + flat);
            }
            catch (Exception)
            {
            }
        }

        private static void CloseLocked()
        {
            if (_writer == null)
                return;

            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch (Exception)
            {
            }

            _writer = null;
        }

        private static string Flatten(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("\r\n", " | ").Replace("\r", " | ").Replace("\n", " | ");
        }
    }
}
