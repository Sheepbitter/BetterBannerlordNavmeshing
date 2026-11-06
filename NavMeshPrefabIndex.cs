using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BetterBannerlordNavmeshing
{
    // ==================================================================
    // Navmesh-prefab index (managed mirror of the engine's own index)
    // ==================================================================
    // Why this file exists
    // --------------------
    // "No navigation mesh prefab was found with name \"<name>\"!" is NOT our
    // message. It is TaleWorlds.Native.dll's (file offset 0x140F7E0 in
    // bin\Win64_Shipping_wEditor, rva 0x14107E0), printed by the native
    // load_nav_mesh_prefab / load_nav_mesh_prefab_with_frame bodies
    // (rva [0x9795F0,0x979D72) and [0x979D80,0x97A49F)). Those are the bodies
    // behind TaleWorlds.Engine.IScene.LoadNavMeshPrefab /
    // LoadNavMeshPrefabWithFrame ([EngineMethod("load_nav_mesh_prefab", ...)],
    // RID 1176/1177), which TaleWorlds.Engine.Scene.ImportNavigationMeshPrefab
    // (RID 2971) calls.
    //
    // What the native loader actually does:
    //
    //   1. Look the requested name up in an in-memory tree. Keys are compared
    //      with a byte-wise memcmp, so the match is case-SENSITIVE and exact.
    //      The value is an int32 index into the loaded-module list
    //      ([module_data_singleton+0x888 .. +0x890], 32-byte stride).
    //      A miss (or a negative payload) jumps straight to the error above.
    //   2. Build the tail "NavMeshPrefabs/" + name + ".bin"
    //      (rva 0x310DB0; literals at rva 0x13A32A8 and 0x13985F0).
    //   3. Prefix it with the module directory the tree pointed at and open it
    //      with rglFile::open(path, "rb"). A miss logs
    //      "file not found: %s" and then the "No navigation mesh prefab ...".
    //
    //   => The path layout is exactly Modules\<Module>\NavMeshPrefabs\<name>.bin,
    //      which is where we already write. Nothing about the layout needs fixing.
    //
    // What DOES need fixing is step 1: the tree is populated once, by
    // rva [0x4E BCE0,0x4ECA2B) ("rglModule_data::modules_of_nav_mesh_prefabs",
    // literal "NavMeshPrefabs" + ".bin" + the 512-character file-name limit). It
    // walks the loaded modules, and for each module that HAS a NavMeshPrefabs
    // folder registers every *.bin stem it finds at that point in time. There is
    // no managed API that re-runs it, and no filesystem fallback if it misses.
    //
    // "Loaded modules" is the decisive word, and it is exactly the set the engine
    // itself exposes as Utilities.GetModulesNames() ->
    // IUtil.GetModulesCode() ([EngineMethod("get_modules_code", ...)]), i.e. the
    // `_MODULES_*A*B*_MODULES_` argument the launcher writes on the command line
    // (rgl_log*.txt: "Command Args: /singleplayer _MODULES_*Native*SandBoxCore*
    // Sandbox*CustomBattle*StoryMode*BetterBannerlordNavmeshing*_MODULES_"). The
    // managed ModuleHelper._loadedModules dictionary is filled from that very
    // array (Module.Initialize -> ModuleHelper.InitializeModules(Utilities
    // .GetModulesNames(), null)), and the editor activates every entry of it
    // (Module.OnInitialModuleScreenActivated), so
    // ModuleHelper.GetActiveModules() is the same set as a List<ModuleInfo>.
    //
    // A module directory that is NOT in that set can never contribute a stem:
    // Modules\SomeFolder\NavMeshPrefabs\<name>.bin may sit right there on disk
    // and the name still is not a key, no matter how often it is rewritten and
    // no matter how often the editor is restarted. That is the case a procedural
    // generator hits whenever a scene lives in a module that is not enabled in
    // the launcher (or in a folder that is not a module at all - no
    // SubModule.xml, so it can never be loaded).
    //
    // One trap that made the earlier mirror wrong: the engine keys modules by
    // their SubModule.xml <Id value="..."/>, which is NOT always the folder
    // name. On this install Modules\MyMaps has Id "My Maps" and Modules\SandBox
    // has Id "Sandbox", so IsModuleActive("MyMaps") returns false even while
    // My Maps is loaded. Hence ReadModuleIdFromSubModuleXml() below, and hence
    // matching by Id as well as by folder.
    //
    // The two ways that bites a procedural generator, both of which this class
    // makes visible instead of mysterious:
    //
    //   a) Our module has no NavMeshPrefabs folder until the first export that
    //      resolves to it, so it is not in the index for that whole session.
    //      build.ps1 now creates the folder at deploy time.
    //   b) A name that did not exist when the index was built can never be
    //      resolved in that session, no matter how many times the .bin is
    //      rewritten. Rewrite = fine (same name, same module). New name = miss.
    //   c) A name that only exists in a module the engine did not load is never
    //      a key, in any session, until that module is enabled. This class then
    //      also writes the same bin into a loaded module (ours), so the name
    //      becomes resolvable from the NEXT editor start - see FallbackFolder.
    //   d) The bin went to the WRONG module, because the scene's name does not
    //      say which module it is in and more than one module folder can hold
    //      SceneObj\<that name>. See "Step 0" below and ResolveFolder.
    //
    // One more native quirk worth guarding: load_nav_mesh_prefab copies the name
    // into a fixed 0x80-byte buffer (rva 0x97988A-0x979892) and trips
    // "str_length < size" (rglConstString.inl) when it does not fit, so a name
    // of 128 characters or more is truncated before the lookup and can never
    // match the file stem. Generated scene names can easily reach that.
    public static class NavMeshPrefabIndex
    {
        // Folder the engine appends to a module directory (native literal
        // "NavMeshPrefabs", rva 0x13C3CB8).
        public const string FolderName = "NavMeshPrefabs";

        // load_nav_mesh_prefab copies the name into a 0x80-byte buffer, so
        // anything at or above 128 characters is truncated before the lookup.
        public const int LoaderNameLimit = 127;

        // The index's own guard: "Navigation mesh prefab file name length cannot
        // be greater than 512!\nFile Name: " (rva 0x14CC55C).
        public const int FileNameLimit = 512;

        // This module's own id. It is loaded whenever this code can run at all
        // (the DLL only loads with the module), and build.ps1 provisions its
        // NavMeshPrefabs folder before the game ever starts, which makes it the
        // only folder we may safely use as the home of a fallback copy: a
        // base-game or third-party module must never be written into, and a
        // folder that does not exist at start-up is not in the index either.
        public const string SelfModuleId = "BetterBannerlordNavmeshing";

        // ------------------------------------------------------------------
        // Bins this session wrote that the engine's index cannot know about
        // yet.
        //
        // "bin existed when the engine built its index" cannot be answered from
        // the filesystem alone: the index is built once, at start-up, and a bin
        // that did not exist then stays unknown for the rest of the session even
        // after a second export rewrites it. So every bin we write that was NOT
        // already indexed is remembered here, and the mirror treats it as
        // unresolvable until the process restarts. Process-scoped on purpose:
        // that is exactly the lifetime of one engine index.
        // ------------------------------------------------------------------
        private static readonly object PendingLock = new object();
        private static readonly HashSet<string> PendingIndex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static bool IsPendingIndex(string binPath)
        {
            if (string.IsNullOrEmpty(binPath))
                return false;

            lock (PendingLock)
                return PendingIndex.Contains(binPath);
        }

        // Called by the writer after it has written a bin. alreadyIndexed is the
        // mirror's verdict for that path taken BEFORE the write; a bin that was
        // already a key stays a key when it is rewritten, so only a genuinely
        // new one is remembered as pending.
        public static void MarkBinWritten(string binPath, bool alreadyIndexed)
        {
            if (string.IsNullOrEmpty(binPath) || alreadyIndexed)
                return;

            lock (PendingLock)
                PendingIndex.Add(binPath);
        }

        public sealed class Candidate
        {
            public string ModuleDir;      // ...\Modules\<Module>
            public string ModuleId;       // <Module> - the FOLDER leaf, as logged
            public string ModuleSubModuleId; // <Id value="..."/> from SubModule.xml (null when absent)
            public string Folder;         // ...\Modules\<Module>\NavMeshPrefabs
            public string BinPath;        // ...\NavMeshPrefabs\<name>.bin
            public bool FolderExists;
            public bool BinExistedBefore; // snapshot taken before this run wrote anything
            public bool ModuleActive;     // is this module in the engine's loaded set?
            public bool ModuleActiveKnown;

            // Managed mirror of "is <name> a key in rglModule_data's tree?":
            // a bin that existed when the index was built, in a module the
            // engine actually loaded. An unknown module set must not veto an
            // import that used to work, so unknown is treated as active.
            public bool Resolvable
            {
                get
                {
                    if (!FolderExists || !BinExistedBefore)
                        return false;
                    if (ModuleActiveKnown && !ModuleActive)
                        return false;
                    return !IsPendingIndex(BinPath);
                }
            }

            // Why Resolvable is false, for the log.
            public string NotResolvableBecause
            {
                get
                {
                    if (!FolderExists)
                        return "no-" + FolderName + "-folder";
                    if (!BinExistedBefore)
                        return "no-bin";
                    if (ModuleActiveKnown && !ModuleActive)
                        return "module-not-loaded";
                    if (IsPendingIndex(BinPath))
                        return "written-this-session";
                    return "unknown";
                }
            }
        }

        // Everything one run needs to know about "will the engine resolve this
        // name, and if not, where can we put a copy that it will?". Built by
        // Analyze() before a single byte is written.
        public sealed class Analysis
        {
            public string PrefabName;
            public string FolderUsed;               // the canonical NavMeshPrefabs folder
            public string CanonicalModuleId;        // its folder leaf
            public string CanonicalSubModuleId;     // its SubModule.xml <Id>
            public bool FolderExistedBefore;
            public string CanonicalBinPath;
            public bool CanonicalBinExistedBefore;
            public bool CanonicalModuleActive;
            public bool CanonicalModuleActiveKnown;
            public bool ActiveSetKnown;
            public string ActiveSetSource;
            public List<Candidate> Candidates = new List<Candidate>();
            public Candidate Canonical;              // null when FolderUsed is outside the scan
            public string FallbackFolder;            // null when no fallback copy is needed
            public string FallbackModuleId;          // its module id
            public string FallbackBinPath;
            public bool FallbackFolderExistedBefore; // false = this run has to create it
            public string NameProblem;               // ValidateName result
        }

        // The set of modules the engine actually loaded, i.e. exactly the modules
        // whose NavMeshPrefabs folder the index builder scanned.
        public sealed class ActiveModules
        {
            public bool Known;
            public string Source;
            public List<string> Ids = new List<string>();     // engine module ids
            public List<string> Folders = new List<string>(); // engine module folders (normalized)

            public bool ContainsId(string id)
            {
                if (string.IsNullOrEmpty(id))
                    return false;

                for (int i = 0; i < Ids.Count; i++)
                {
                    if (string.Equals(Ids[i], id, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            public bool ContainsFolder(string folder)
            {
                if (string.IsNullOrEmpty(folder))
                    return false;

                string key = NormalizePath(folder);
                for (int i = 0; i < Folders.Count; i++)
                {
                    if (string.Equals(Folders[i], key, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
        }

        // The single path the engine will try, given the module the index picked.
        public static string BinPath(string navMeshPrefabsFolder, string prefabName)
        {
            if (string.IsNullOrEmpty(navMeshPrefabsFolder))
                return null;
            return Path.Combine(navMeshPrefabsFolder, prefabName + ".bin");
        }

        // "<...>\Modules\<Module>\NavMeshPrefabs" -> "<Module>" (the folder leaf,
        // which is what the module folders are called; NOT necessarily the
        // module id - see ModuleSubModuleId / ParseModuleId).
        public static string ModuleIdOf(string navMeshPrefabsFolder)
        {
            if (string.IsNullOrEmpty(navMeshPrefabsFolder))
                return null;

            string folder = navMeshPrefabsFolder.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(Path.GetFileName(folder), FolderName, StringComparison.OrdinalIgnoreCase))
                folder = Path.GetDirectoryName(folder);

            return string.IsNullOrEmpty(folder) ? null : Path.GetFileName(folder);
        }

        // "Modules\MyMaps\SubModule.xml" -> "My Maps". The <Id> element is what
        // the engine and ModuleHelper key on, and it is NOT always the folder
        // name (MyMaps -> "My Maps", SandBox -> "Sandbox", "Full Invasion 3
        // mapping" -> "FullInvasion3"). Returns null when the folder is not a
        // module at all (no SubModule.xml), which also means the engine can
        // never load it.
        public static string ParseModuleId(string subModuleXml)
        {
            if (string.IsNullOrEmpty(subModuleXml))
                return null;

            // Deliberately not a full XML parse: a SubModule.xml is a module
            // descriptor we only read for one attribute, and System.Xml would
            // drag in failures for the many hand-edited ones out there.
            int at = subModuleXml.IndexOf("<Id", StringComparison.Ordinal);
            while (at >= 0)
            {
                int after = at + 3;
                if (after < subModuleXml.Length)
                {
                    char c = subModuleXml[after];
                    if (char.IsWhiteSpace(c) || c == '>' || c == '/')
                    {
                        int valueAt = subModuleXml.IndexOf("value", at, StringComparison.Ordinal);
                        if (valueAt >= 0)
                        {
                            int q1 = subModuleXml.IndexOf('"', valueAt);
                            if (q1 > 0)
                            {
                                int q2 = subModuleXml.IndexOf('"', q1 + 1);
                                if (q2 > q1)
                                    return subModuleXml.Substring(q1 + 1, q2 - q1 - 1);
                            }
                        }
                    }
                }

                at = subModuleXml.IndexOf("<Id", at + 3, StringComparison.Ordinal);
            }

            return null;
        }

        public static string ReadModuleIdFromSubModuleXml(string moduleDir)
        {
            if (string.IsNullOrEmpty(moduleDir))
                return null;

            try
            {
                string xml = Path.Combine(moduleDir, "SubModule.xml");
                if (!File.Exists(xml))
                    return null;

                return ParseModuleId(File.ReadAllText(xml));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Null when the name can be looked up; otherwise the reason it cannot.
        public static string ValidateName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return "the prefab name is empty";

            if (prefabName.Length >= 128)
            {
                return "the name is " + prefabName.Length.ToString(CultureInfo.InvariantCulture)
                    + " characters; the engine truncates it to 127 before the lookup, so it can never match "
                    + "the file name";
            }

            if (prefabName.IndexOf('/') >= 0 || prefabName.IndexOf('\\') >= 0)
                return "the name contains a path separator";

            if (Path.GetInvalidFileNameChars().Length > 0)
            {
                for (int i = 0; i < prefabName.Length; i++)
                {
                    for (int j = 0; j < Path.GetInvalidFileNameChars().Length; j++)
                    {
                        if (prefabName[i] == Path.GetInvalidFileNameChars()[j])
                            return "the name contains a character that is invalid in a file name";
                    }
                }
            }

            if ((prefabName + ".bin").Length > FileNameLimit)
            {
                return "the file name would exceed the engine's " + FileNameLimit.ToString(CultureInfo.InvariantCulture)
                    + "-character limit";
            }

            return null;
        }

        // Deterministic, collision-resistant name for scenes whose derived name
        // would not survive the engine's 128-byte copy. FNV-1a keeps it stable
        // across runs and machines, so an existing export stays valid.
        public static string DeriveBoundedName(string sceneName, string suffix)
        {
            string full = (sceneName ?? string.Empty) + (suffix ?? string.Empty);
            if (full.Length <= LoaderNameLimit)
                return full;

            const string digestChars = "0123456789abcdef";
            uint hash = 2166136261u; // FNV-1a offset basis
            for (int i = 0; i < full.Length; i++)
            {
                hash ^= full[i];
                hash *= 16777619u;
            }

            string digest = "~";
            for (int i = 0; i < 8; i++)
            {
                digest += digestChars[(int)((hash >> ((7 - i) * 4)) & 0xFu)];
            }

            int keep = LoaderNameLimit - digest.Length;
            if (keep < 1)
                keep = 1;

            return full.Substring(0, keep) + digest;
        }

        // ==================================================================
        // Step 0: which module folder does this scene actually live in?
        // ==================================================================
        // The engine resolves a scene by NAME, not by module: the loader
        // composes the relative tail "SceneObj/<scene>" and lets the module
        // manager find it (FUN_7ff8d00ab1f0, and FUN_7ff8d00ab450 which stores
        // both that tail and "<module_path_>SceneEditData/<scene>"). Two module
        // folders can therefore hold SceneObj\<the same name> - and on this
        // install they do: Modules\DedicatedCustomServerHelper\SceneObj\
        // CC_20_siege AND Modules\MyMaps\SceneObj\CC_20_siege. "First match in
        // Directory.GetDirectories order" is then decided by NTFS ordering, i.e.
        // alphabetically, which is how the export folder used to end up in the
        // wrong one: DedicatedCustomServerHelper has no SubModule.xml, so it is
        // not a module at all and nothing written there can ever be indexed
        // (see 3.5).
        //
        // The engine's own answer exists and is used first (see
        // TerrainNavMeshGenerator.ResolveFolder): Scene.GetModulePath(), i.e.
        // IScene.GetModulePath RID 1217, [EngineMethod("get_module_path", ...)],
        // which returns rglScene::module_path_ - the module directory the scene
        // was loaded from, and the very field load_nav_mesh_prefab asserts
        // non-empty and composes <module_path_>NavMeshPrefabs against. It is
        // filled by the scene-load path (FUN_7ff8d015d3e0, which then goes on to
        // produce "<module_path_>SceneEditData/<scene>", observed on disk as
        // Modules/MapConverter/SceneEditData/scn_village_Boras/terrain_ed.bin).
        //
        // When it is unavailable, empty, or points at something that is not a
        // module, the SceneObj search below still runs - but on a rank instead
        // of on folder order, and every match is logged.
        public sealed class SceneModuleMatch
        {
            public string ModuleDir;
            public string ModuleId;           // the folder leaf
            public string ModuleSubModuleId;  // <Id value="..."/>, null when there is no SubModule.xml
            public bool IsModule;             // has a SubModule.xml, i.e. the engine can load it
            public bool Active;
            public bool ActiveKnown;

            public string Folder { get { return Path.Combine(ModuleDir, FolderName); } }

            //   3 = a module the engine loaded in this session. Nothing can beat
            //       it: this is the only kind of folder whose bins the engine's
            //       index can hold, so this is where a prefab has to go to be
            //       importable here.
            //   2 = loaded but not a module. Unreachable while the loaded set is
            //       known (the engine cannot load a folder with no
            //       SubModule.xml); kept so an unexpected set cannot be ignored.
            //   1 = a real module this session does not load. Still the right
            //       place for a correct install, just not resolvable right now.
            //   0 = not a module, and not loaded: a prefab written here can never
            //       be indexed, in this session or any later one.
            public int Rank
            {
                get { return (ActiveKnown && Active ? 2 : 0) + (IsModule ? 1 : 0); }
            }

            public string ActiveText
            {
                get { return ActiveKnown ? (Active ? "True" : "False") : "unknown"; }
            }
        }

        // Every Modules\<Module> that holds SceneObj\<sceneName>, with everything
        // the ranking needs. One directory listing per game root and one small
        // SubModule.xml read per matching module; no engine call. The game roots
        // are passed in so the offline tests can point this at a fixture.
        public static List<SceneModuleMatch> FindSceneModuleMatches(string sceneName,
            ActiveModules active, IList<string> gameRoots)
        {
            var results = new List<SceneModuleMatch>();
            if (string.IsNullOrEmpty(sceneName) || gameRoots == null)
                return results;

            var seen = new List<string>();
            for (int r = 0; r < gameRoots.Count; r++)
            {
                try
                {
                    string modulesDir = Path.Combine(gameRoots[r], "Modules");
                    if (!Directory.Exists(modulesDir))
                        continue;

                    string[] moduleDirs = Directory.GetDirectories(modulesDir);
                    for (int i = 0; i < moduleDirs.Length; i++)
                    {
                        string moduleDir = moduleDirs[i];
                        string key = NormalizePath(moduleDir);
                        if (ContainsIgnoreCase(seen, key))
                            continue;

                        bool holds;
                        try
                        {
                            holds = Directory.Exists(Path.Combine(moduleDir, "SceneObj", sceneName));
                        }
                        catch (Exception)
                        {
                            holds = false;
                        }

                        if (!holds)
                            continue;

                        // Only a folder that actually holds the scene is recorded,
                        // so a module that appears in two game roots without the
                        // scene in the first one is still found in the second.
                        seen.Add(key);

                        var match = new SceneModuleMatch();
                        match.ModuleDir = moduleDir;
                        match.ModuleId = Path.GetFileName(key);
                        match.ModuleSubModuleId = ReadModuleIdFromSubModuleXml(moduleDir);
                        match.IsModule = !string.IsNullOrEmpty(match.ModuleSubModuleId);
                        ApplyActive(match, active);
                        results.Add(match);
                    }
                }
                catch (Exception)
                {
                    // An unreadable game root is skipped, exactly as in
                    // FindCandidates: the next root may still have the scene.
                }
            }

            return results;
        }

        private static void ApplyActive(SceneModuleMatch match, ActiveModules active)
        {
            if (active == null || !active.Known)
            {
                match.ActiveKnown = false;
                match.Active = false;
                return;
            }

            match.ActiveKnown = true;
            match.Active = active.ContainsId(match.ModuleSubModuleId)  // the real module id
                || active.ContainsId(match.ModuleId)                   // the folder leaf, as a fallback
                || active.ContainsFolder(match.ModuleDir);
        }

        // The ranking, and why the winner won. Pure - the same list always gives
        // the same answer - so it is what the offline tests drive. rule is a
        // short token (see DescribeSceneModuleRule) and tied is the number of
        // folders that share the winning rank.
        public static SceneModuleMatch ChooseSceneModule(List<SceneModuleMatch> matches,
            out string rule, out int tied)
        {
            rule = null;
            tied = 0;
            if (matches == null || matches.Count == 0)
                return null;

            SceneModuleMatch best = null;
            int bestRank = -1;
            for (int i = 0; i < matches.Count; i++)
            {
                SceneModuleMatch m = matches[i];
                if (m == null)
                    continue;

                int rank = m.Rank;
                if (rank > bestRank)
                {
                    bestRank = rank;
                    best = m;
                    tied = 1;
                }
                else if (rank == bestRank)
                {
                    tied++;
                    if (best == null)
                        best = m;
                }
            }

            if (best == null)
                return null;

            if (bestRank >= 3)
                rule = "loaded-module";
            else if (bestRank == 2)
                rule = "loaded-not-a-module";
            else if (bestRank == 1)
                rule = "module-not-loaded";
            else
                rule = "not-a-module";

            return best;
        }

        // Prose for ChooseSceneModule's rule token, so the log states the rule
        // rather than just its name.
        public static string DescribeSceneModuleRule(string rule)
        {
            if (rule == null)
                return "no rule applied (nothing matched)";

            if (rule == "loaded-module")
            {
                return "prefer the module folder the engine loaded in this session - it is the only"
                    + " kind of folder whose " + FolderName + " bins the engine's index can hold";
            }

            if (rule == "loaded-not-a-module")
            {
                return "prefer a loaded folder over a module that is not loaded (only reachable when"
                    + " the loaded-module set could not be asked, so nothing is really known)";
            }

            if (rule == "module-not-loaded")
            {
                return "no match is loaded in this session, so the best real module wins over a folder"
                    + " that is not a module at all (the write still cannot be resolved here, but it is"
                    + " where a correct install - game, dedicated server - will read it)";
            }

            return "no match is a module at all, so the first in Modules\\ listing order was taken;"
                + " nothing written into such a folder can ever be indexed";
        }

        // The "folder:" block that lists EVERY matching module, not just the one
        // that won. Without it the resolution is invisible, and the whole point
        // is that more than one module folder can hold SceneObj\<the same name>.
        public static void DescribeSceneModuleMatches(System.Action<string> log, string sceneName,
            List<SceneModuleMatch> matches, SceneModuleMatch chosen, string rule, int tied,
            string engineModulePath)
        {
            if (log == null || matches == null || matches.Count == 0)
                return;

            log("folder: scene '" + sceneName + "' is in " + matches.Count
                + " module folder(s). The engine resolves a scene by name, not by module, so every one"
                + " of them matches; they are ranked, not taken in folder order:");

            for (int i = 0; i < matches.Count; i++)
            {
                SceneModuleMatch m = matches[i];
                if (m == null)
                    continue;

                bool isChosen = chosen != null
                    && string.Equals(NormalizePath(m.ModuleDir), NormalizePath(chosen.ModuleDir),
                        StringComparison.OrdinalIgnoreCase);

                log("folder:   " + (isChosen ? "CHOSEN  " : "ignored ")
                    + m.ModuleDir
                    + " module=" + m.ModuleId
                    + " moduleId='" + (m.ModuleSubModuleId ?? "") + "'"
                    + (m.IsModule ? "" : " NOT-A-MODULE (no SubModule.xml)")
                    + " loaded=" + m.ActiveText
                    + " rank=" + m.Rank + RankMeaning(m.Rank)
                    + (isChosen ? "  <- this run's write folder" : ""));
            }

            log("folder:   rule: " + DescribeSceneModuleRule(rule)
                + (chosen != null ? "; winner module '" + chosen.ModuleId + "'" : "; no winner"));

            if (tied > 1)
            {
                log("folder:   AMBIGUOUS: " + tied + " folder(s) share the winning rank and cannot be told"
                    + " apart from here, so the first in Modules\\ listing order was taken. Set"
                    + " NavMeshExportFolder to pin the module explicitly.");
            }

            if (!string.IsNullOrEmpty(engineModulePath))
            {
                bool engineWon = chosen != null
                    && string.Equals(NormalizePath(engineModulePath), NormalizePath(chosen.ModuleDir),
                        StringComparison.OrdinalIgnoreCase);

                log("folder:   cross-check: Scene.GetModulePath() said '" + engineModulePath + "'"
                    + (engineWon
                        ? ", which is the folder chosen above - the engine's own answer and the ranking agree."
                        : ", which was REJECTED above (it is not a module folder), so the ranking decided"
                          + " instead."));
            }
        }

        private static string RankMeaning(int rank)
        {
            if (rank >= 3)
                return " (a module this session loaded)";
            if (rank == 2)
                return " (loaded, but the loaded set could not be asked)";
            if (rank == 1)
                return " (a module this session does not load)";
            return " (not a module: a prefab written here can never be indexed)";
        }

        // ==================================================================
        // Step 1: which modules did the engine actually load?
        // ==================================================================
        // Primary source is the engine's own answer, not an inference:
        // TaleWorlds.Engine.Utilities.GetModulesNames() splits
        // IUtil.GetModulesCode() on '*', and that same string array is what
        // Module.Initialize hands to ModuleHelper.InitializeModules, which is
        // what the native module manager was built from. Two managed
        // cross-checks are used when it is available, because folder names and
        // module ids disagree (see the header comment):
        //   * ModuleHelper.GetActiveModules() -> ModuleInfo.FolderPath
        //   * ModuleHelper.GetModuleFullPath(id) -> FolderPath
        // Anything that fails simply narrows the answer; Known stays false only
        // when nothing worked, and the caller then does not veto imports.
        public static ActiveModules QueryActiveModules(System.Action<string> log)
        {
            var result = new ActiveModules();

            // (a) The engine's own module code list.
            try
            {
                string[] names = TaleWorlds.Engine.Utilities.GetModulesNames();
                if (names != null && names.Length > 0)
                {
                    for (int i = 0; i < names.Length; i++)
                    {
                        string id = names[i];
                        if (string.IsNullOrEmpty(id))
                            continue;

                        id = id.Trim();
                        if (id.Length == 0)
                            continue;

                        if (!result.ContainsId(id))
                            result.Ids.Add(id);
                    }

                    if (result.Ids.Count > 0)
                    {
                        result.Known = true;
                        result.Source = "TaleWorlds.Engine.Utilities.GetModulesNames()"
                            + " (native get_modules_code, the same _MODULES_*...*_MODULES_ list the"
                            + " engine's module manager was built from)";
                    }
                }
            }
            catch (Exception ex)
            {
                if (log != null)
                    log("prefab index: GetModulesNames() unavailable (" + ex.GetType().Name
                        + "); falling back to ModuleHelper");
            }

            // (b) The managed mirror of the same dictionary, used to learn which
            //     FOLDER each id sits in.
            try
            {
                Type helper = LoadModuleHelperType();
                if (helper != null)
                {
                    System.Reflection.MethodInfo getActive = helper.GetMethod(
                        "GetActiveModules",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                        null, Type.EmptyTypes, null);

                    if (getActive != null)
                    {
                        object list = getActive.Invoke(null, null);
                        var enumerable = list as System.Collections.IEnumerable;
                        if (enumerable != null)
                        {
                            foreach (object moduleInfo in enumerable)
                            {
                                if (moduleInfo == null)
                                    continue;

                                Type t = moduleInfo.GetType();
                                object id = t.GetProperty("Id", System.Reflection.BindingFlags.Public
                                    | System.Reflection.BindingFlags.Instance).GetValue(moduleInfo, null);
                                object folderPath = t.GetProperty("FolderPath", System.Reflection.BindingFlags.Public
                                    | System.Reflection.BindingFlags.Instance).GetValue(moduleInfo, null);
                                AddActiveEntry(result, id as string, folderPath as string);
                            }
                        }
                    }

                    System.Reflection.MethodInfo fullPath = helper.GetMethod(
                        "GetModuleFullPath",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                        null, new[] { typeof(string) }, null);

                    if (fullPath != null)
                    {
                        for (int i = 0; i < result.Ids.Count; i++)
                        {
                            try
                            {
                                AddActiveEntry(result, result.Ids[i],
                                    fullPath.Invoke(null, new object[] { result.Ids[i] }) as string);
                            }
                            catch (Exception)
                            {
                                // Not in the dictionary: the id came from the
                                // engine list but ModuleHelper has not seen it.
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (log != null)
                    log("prefab index: ModuleHelper unavailable (" + ex.GetType().Name
                        + "); the loaded-module set falls back to module-id matching only");
            }

            return result;
        }

        private static void AddActiveEntry(ActiveModules result, string id, string folderPath)
        {
            if (!string.IsNullOrEmpty(id) && !result.ContainsId(id))
                result.Ids.Add(id);

            if (!string.IsNullOrEmpty(folderPath))
            {
                string key = NormalizePath(folderPath);
                bool known = false;
                for (int i = 0; i < result.Folders.Count; i++)
                {
                    if (string.Equals(result.Folders[i], key, StringComparison.OrdinalIgnoreCase))
                    {
                        known = true;
                        break;
                    }
                }
                if (!known)
                    result.Folders.Add(key);
            }
        }

        private static Type LoadModuleHelperType()
        {
            try
            {
                return Type.GetType(
                    "TaleWorlds.ModuleManager.ModuleHelper, TaleWorlds.ModuleManager", false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Is this module in the engine's loaded set? Ids are matched
        // case-insensitively because ModuleHelper keys them with ToLower().
        public static bool IsCandidateActive(Candidate candidate, ActiveModules active)
        {
            if (candidate == null)
                return false;

            if (active == null || !active.Known)
            {
                candidate.ModuleActiveKnown = false;
                candidate.ModuleActive = false;
                return false;
            }

            candidate.ModuleActiveKnown = true;
            candidate.ModuleActive =
                active.ContainsId(candidate.ModuleSubModuleId)      // the real module id
                || active.ContainsId(candidate.ModuleId)            // the folder leaf as a fallback
                || active.ContainsFolder(candidate.ModuleDir);

            return candidate.ModuleActive;
        }

        // ==================================================================
        // Step 2: the scan
        // ==================================================================
        // Every Modules\<Module>\NavMeshPrefabs we can see, with the state the
        // engine's index cares about. Cheap: one directory listing per game root
        // plus one small SubModule.xml read per module. MUST run before anything
        // is written: BinExistedBefore is the snapshot that decides whether the
        // name was already a key.
        public static List<Candidate> FindCandidates(System.Action<string> log, string prefabName, ActiveModules active)
        {
            var results = new List<Candidate>();
            var seen = new List<string>();

            List<string> roots = CollectGameRoots();
            for (int r = 0; r < roots.Count; r++)
            {
                string modulesDir;
                try
                {
                    modulesDir = Path.Combine(roots[r], "Modules");
                    if (!Directory.Exists(modulesDir))
                        continue;

                    string[] moduleDirs = Directory.GetDirectories(modulesDir);
                    for (int i = 0; i < moduleDirs.Length; i++)
                    {
                        string moduleDir = moduleDirs[i];
                        string key = NormalizePath(moduleDir);
                        if (ContainsIgnoreCase(seen, key))
                            continue;
                        seen.Add(key);

                        string folder = Path.Combine(moduleDir, FolderName);
                        var candidate = new Candidate();
                        candidate.ModuleDir = moduleDir;
                        candidate.ModuleId = Path.GetFileName(key);
                        candidate.ModuleSubModuleId = ReadModuleIdFromSubModuleXml(moduleDir);
                        candidate.Folder = folder;
                        candidate.BinPath = BinPath(folder, prefabName);

                        try
                        {
                            candidate.FolderExists = Directory.Exists(folder);
                            if (candidate.FolderExists && !string.IsNullOrEmpty(candidate.BinPath))
                                candidate.BinExistedBefore = File.Exists(candidate.BinPath);
                        }
                        catch (Exception)
                        {
                            // An unreadable module folder is reported as "not there"
                            // rather than aborting the whole lookup.
                        }

                        IsCandidateActive(candidate, active);

                        results.Add(candidate);
                    }
                }
                catch (Exception ex)
                {
                    if (log != null)
                        log("prefab index: skipped game root '" + roots[r] + "' (" + ex.Message + ")");
                }
            }

            return results;
        }

        // ==================================================================
        // Step 3: the decision
        // ==================================================================
        public static Analysis Analyze(string prefabName, string folderUsed)
        {
            return Analyze(prefabName, folderUsed, QueryActiveModules(null), null);
        }

        // The overload that takes its inputs is the one the offline tests drive:
        // no engine call, no filesystem beyond the candidates it is handed.
        public static Analysis Analyze(string prefabName, string folderUsed,
            ActiveModules active, List<Candidate> candidates)
        {
            var analysis = new Analysis();
            analysis.PrefabName = prefabName;
            analysis.FolderUsed = folderUsed;
            analysis.CanonicalModuleId = ModuleIdOf(folderUsed);
            analysis.CanonicalBinPath = BinPath(folderUsed, prefabName);
            analysis.NameProblem = ValidateName(prefabName);

            if (active == null)
                active = new ActiveModules();
            analysis.ActiveSetKnown = active.Known;
            analysis.ActiveSetSource = active.Source;

            if (candidates == null)
                candidates = FindCandidates(null, prefabName, active);
            analysis.Candidates = candidates;

            if (!string.IsNullOrEmpty(folderUsed))
            {
                try
                {
                    analysis.FolderExistedBefore = Directory.Exists(folderUsed);
                }
                catch (Exception)
                {
                    analysis.FolderExistedBefore = false;
                }
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate c = candidates[i];
                if (SamePath(c.Folder, folderUsed))
                {
                    analysis.Canonical = c;
                    break;
                }
            }

            if (analysis.Canonical != null)
            {
                analysis.CanonicalModuleActive = analysis.Canonical.ModuleActive;
                analysis.CanonicalModuleActiveKnown = analysis.Canonical.ModuleActiveKnown;
                analysis.CanonicalSubModuleId = analysis.Canonical.ModuleSubModuleId;
                analysis.CanonicalBinExistedBefore = analysis.Canonical.BinExistedBefore;
            }
            else
            {
                // The folder is outside every game root we can see (a hand-set
                // NavMeshExportFolder). Nothing can be said about it, so nothing
                // is vetoed.
                analysis.CanonicalModuleActiveKnown = false;
                if (analysis.CanonicalBinPath != null)
                {
                    try
                    {
                        analysis.CanonicalBinExistedBefore = File.Exists(analysis.CanonicalBinPath);
                    }
                    catch (Exception)
                    {
                        analysis.CanonicalBinExistedBefore = false;
                    }
                }
            }

            // A copy of the bin is worth writing into a loaded module only when
            // the canonical module is not loaded: that is the one case where the
            // name can never become a key no matter how often the editor is
            // restarted. When the canonical module is fine (or unknown), the
            // existing write is enough and no second copy is made.
            if (analysis.CanonicalModuleActiveKnown && !analysis.CanonicalModuleActive)
            {
                string fallbackId = ChooseFallbackModuleId(analysis.CanonicalSubModuleId
                    ?? analysis.CanonicalModuleId, candidates);
                if (!string.IsNullOrEmpty(fallbackId))
                {
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        if (string.Equals(candidates[i].ModuleSubModuleId, fallbackId, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(candidates[i].ModuleId, fallbackId, StringComparison.OrdinalIgnoreCase))
                        {
                            analysis.FallbackModuleId = fallbackId;
                            analysis.FallbackFolder = candidates[i].Folder;
                            analysis.FallbackBinPath = candidates[i].BinPath;
                            analysis.FallbackFolderExistedBefore = candidates[i].FolderExists;
                            break;
                        }
                    }
                }
            }

            return analysis;
        }

        // Where a fallback copy goes. Only ever this module: it is loaded
        // whenever this code runs, build.ps1 creates its NavMeshPrefabs folder
        // before the game starts (so the stem is indexed from the first start),
        // and writing into a base-game or third-party module would be
        // unacceptable. If it is somehow not loaded there is no safe home, and
        // the caller says so instead of guessing.
        public static string ChooseFallbackModuleId(string canonicalModuleId, List<Candidate> candidates)
        {
            if (candidates == null)
                return null;

            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate c = candidates[i];
                if (!c.ModuleActiveKnown || !c.ModuleActive)
                    continue;
                if (!string.Equals(c.ModuleSubModuleId, SelfModuleId, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(c.ModuleId, SelfModuleId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (canonicalModuleId != null
                    && (string.Equals(c.ModuleSubModuleId, canonicalModuleId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.ModuleId, canonicalModuleId, StringComparison.OrdinalIgnoreCase)))
                    continue; // it IS the canonical module

                return SelfModuleId;
            }

            return null;
        }

        public static bool AnyResolvable(Analysis analysis)
        {
            if (analysis == null)
                return false;

            for (int i = 0; i < analysis.Candidates.Count; i++)
            {
                if (analysis.Candidates[i].Resolvable)
                    return true;
            }
            return false;
        }

        // ==================================================================
        // Logging
        // ==================================================================
        // One "prefab index:" block per run: what the engine will look up, where
        // it will look, and whether this session can possibly find it. This is
        // the log counterpart of the engine's "file not found: %s" line.
        public static void DescribeLookup(System.Action<string> log, Analysis analysis)
        {
            if (log == null || analysis == null)
                return;

            string prefabName = analysis.PrefabName;
            log("prefab index: name='" + prefabName + "' length="
                + (prefabName == null ? 0 : prefabName.Length).ToString(CultureInfo.InvariantCulture)
                + (analysis.NameProblem == null ? " ok" : " UNUSABLE: " + analysis.NameProblem));

            log("prefab index: loaded modules: "
                + (analysis.ActiveSetKnown
                    ? analysis.ActiveSetSource
                    : "UNKNOWN (neither TaleWorlds.Engine.Utilities.GetModulesNames() nor"
                      + " TaleWorlds.ModuleManager.ModuleHelper answered) - the engine's loaded-module set"
                      + " cannot be mirrored, so an import is never vetoed on account of it"));

            if (analysis.NameProblem != null)
                return;

            int scanned = 0;
            int found = 0;
            int resolvable = 0;
            for (int i = 0; i < analysis.Candidates.Count; i++)
            {
                Candidate c = analysis.Candidates[i];
                if (!c.FolderExists)
                    continue;

                scanned++;
                if (c.BinExistedBefore)
                    found++;
                if (c.Resolvable)
                    resolvable++;

                log("prefab index:   " + StateTag(c)
                    + " " + c.BinPath
                    + " module=" + c.ModuleId
                    + " moduleId='" + (c.ModuleSubModuleId ?? "") + "'"
                    + " loaded=" + ActiveText(c)
                    + " indexed=" + (c.Resolvable ? "True" : "False")
                    + (c.Resolvable ? "" : " (" + c.NotResolvableBecause + ")")
                    + (c.BinPath != null && analysis.CanonicalBinPath != null
                        && string.Equals(c.BinPath, analysis.CanonicalBinPath, StringComparison.OrdinalIgnoreCase)
                        ? "  <- this run" : ""));
            }

            if (scanned == 0)
            {
                if (analysis.Candidates.Count == 0)
                {
                    log("prefab index:   could not locate the game install (no <root>/Modules found from"
                        + " our assembly or the loaded TaleWorlds assemblies), so the scan below is"
                        + " empty. The engine itself still resolves against its own install.");
                }
                else
                {
                    log("prefab index:   no module under " + analysis.Candidates.Count + " scanned folder(s) has a "
                        + FolderName + " folder; the engine cannot resolve any prefab name this session.");
                }
            }
            else
            {
                log("prefab index: verdict: " + resolvable + " of " + found
                    + " existing copies of '" + prefabName + "' are in the engine's index ("
                    + scanned + " module folder(s) scanned)");
            }

            // Which folder receives the write, and why.
            if (analysis.CanonicalModuleActiveKnown && !analysis.CanonicalModuleActive)
            {
                log("prefab index: write folder: " + analysis.FolderUsed
                    + " (module '" + analysis.CanonicalModuleId + "'"
                    + (string.IsNullOrEmpty(analysis.CanonicalSubModuleId)
                        ? " is NOT loaded in this session, and it is not even a module"
                          + " (no SubModule.xml), so it never can be"
                        : "', moduleId '" + analysis.CanonicalSubModuleId
                          + "', is NOT loaded in this session")
                    + ", and the engine's index can therefore never contain a prefab written here"
                    + (string.IsNullOrEmpty(analysis.FallbackModuleId)
                        ? "; and this module is not loaded either, so no loaded module can carry the name"
                        : "; a copy also goes to '" + analysis.FallbackModuleId + "'") + ")");
            }
            else if (analysis.CanonicalModuleActiveKnown)
            {
                log("prefab index: write folder: " + analysis.FolderUsed
                    + " (module '" + analysis.CanonicalModuleId + "'"
                    + (string.IsNullOrEmpty(analysis.CanonicalSubModuleId)
                        ? " is loaded in this session, so the engine indexes prefabs written here; no"
                          + " fallback copy is needed)"
                        : "', moduleId '" + analysis.CanonicalSubModuleId
                          + "', is loaded in this session, so the engine indexes prefabs written here;"
                          + " no fallback copy is needed)"));
            }
            else
            {
                log("prefab index: write folder: " + analysis.FolderUsed
                    + " (module '" + analysis.CanonicalModuleId
                    + "': loaded state unknown, so it is neither confirmed nor vetoed; no fallback copy)");
            }

            if (!string.IsNullOrEmpty(analysis.FallbackModuleId))
            {
                log("prefab index: fallback copy: " + analysis.FallbackBinPath
                    + " (module '" + analysis.FallbackModuleId + "' is loaded"
                    + (analysis.FallbackFolderExistedBefore
                        ? " and build.ps1 creates its " + FolderName + " folder before the game starts"
                        : ", but its " + FolderName + " folder does not exist yet - this run creates it,"
                          + " so it still needs a restart either way")
                    + ", so the name becomes resolvable from"
                    + " the NEXT editor start; it cannot resolve in this session, whose index was already"
                    + " built)");
            }

            if (!string.IsNullOrEmpty(analysis.FolderUsed) && !analysis.FolderExistedBefore)
            {
                log("prefab index:   target folder does not exist yet: " + analysis.FolderUsed
                    + " (created by this run; the engine's index was built before it existed,"
                    + " so this prefab name stays unknown until the index is rebuilt - restart the editor"
                    + " or reload modules, then Export again)");
            }
        }

        private static string StateTag(Candidate c)
        {
            // Fixed width so the paths line up in the log.
            if (!c.FolderExists || !c.BinExistedBefore)
                return Pad("absent");

            if (c.ModuleActiveKnown && !c.ModuleActive)
                return Pad("FOUND-inactive");

            if (!c.ModuleActiveKnown)
                return Pad("FOUND-unchecked");

            if (IsPendingIndex(c.BinPath))
                return Pad("FOUND-new");

            return Pad("FOUND");
        }

        private static string Pad(string tag)
        {
            return tag.Length >= 14 ? tag : tag.PadRight(14);
        }

        private static string ActiveText(Candidate c)
        {
            return c.ModuleActiveKnown ? (c.ModuleActive ? "True" : "False") : "unknown";
        }

        // A short, single-line verdict the exporter logs before it decides whether
        // to call ImportNavigationMeshPrefab at all. null when the import is worth
        // attempting.
        public static string ExplainUnresolvable(System.Action<string> log, Analysis analysis)
        {
            if (analysis == null)
                return null;

            if (analysis.NameProblem != null)
                return "prefab name cannot be looked up (" + analysis.NameProblem + ")";

            // The whole point of the mirror: is the name a key in the engine's
            // index? If any loaded module already had this bin when the index was
            // built, yes, and the import will work.
            if (AnyResolvable(analysis))
                return null;

            // The scene's own module is not loaded: the name can never be a key,
            // in this session or in any later one, while it stays unloaded. A copy
            // in a loaded module is the only way out, and it needs one restart.
            if (analysis.CanonicalModuleActiveKnown && !analysis.CanonicalModuleActive)
            {
                if (!string.IsNullOrEmpty(analysis.FallbackModuleId))
                {
                    return "scene module not active (module '" + analysis.CanonicalModuleId
                        + "' is not loaded in this session, so the engine's prefab index will never contain '"
                        + analysis.PrefabName + "'); wrote a copy to '" + analysis.FallbackModuleId
                        + "' - restart the editor once, then Import Only";
                }

                return "scene module not active (module '" + analysis.CanonicalModuleId
                    + "' is not loaded in this session, and this module is not loaded either, so no loaded"
                    + " module can provide '" + analysis.PrefabName + "'); enable the scene's module in the"
                    + " launcher and restart the editor";
            }

            // The bin is on disk in a loaded module, but it did not exist when the
            // index was built - either this session just wrote it, or it was
            // written by an earlier export in this same session.
            string writtenHere = FindPendingBinPath(analysis);
            if (writtenHere != null)
            {
                return writtenHere + " was written in this session; the engine's prefab index was built when"
                    + " the modules loaded, so '" + analysis.PrefabName + "' is not a key yet - restart the"
                    + " editor (or reload modules) and run Import Only";
            }

            if (!analysis.FolderExistedBefore)
            {
                return "the " + FolderName + " folder did not exist before this run ("
                    + analysis.FolderUsed + "); the engine built its prefab index before that folder existed, so it"
                    + " cannot know this name this session. Restart the editor (or reload modules) and run"
                    + " Export again - the .bin is already written and will then be found.";
            }

            return "no " + analysis.CanonicalBinPath + " existed before this run, so the engine's"
                + " prefab index (built when it loaded the modules) does not contain '" + analysis.PrefabName
                + "'. A brand new name cannot be resolved in the same session; restart the editor and run"
                + " Export again, or pick an existing prefab name.";
        }

        private static string FindPendingBinPath(Analysis analysis)
        {
            string canonical = analysis.CanonicalBinPath;
            string fallback = analysis.FallbackBinPath;

            for (int i = 0; i < analysis.Candidates.Count; i++)
            {
                Candidate c = analysis.Candidates[i];
                if (!c.FolderExists || !c.BinExistedBefore)
                    continue;
                if (c.ModuleActiveKnown && !c.ModuleActive)
                    continue;
                if (!IsPendingIndex(c.BinPath))
                    continue;

                if (fallback != null && string.Equals(c.BinPath, fallback, StringComparison.OrdinalIgnoreCase))
                    return c.BinPath;
            }

            // Nothing indexed: fall back to the canonical path when the folder
            // itself is the one that has a pending bin.
            if (canonical != null && IsPendingIndex(canonical))
            {
                return canonical;
            }

            return null;
        }

        // ==================================================================
        // Game roots
        // ==================================================================
        // Candidate game roots, from two independent sources so a missing one is
        // simply skipped:
        //   a) TerrainNavMeshGenerator's own resolver (walks up from our assembly
        //      to a folder named "Modules", plus TaleWorlds.Library.BasePath);
        //   b) the folder the loaded TaleWorlds assemblies were loaded from
        //      (.../bin/Win64_Shipping_wEditor or .../bin/Win64_Shipping_Client), whose
        //      grandparent is the game root. (b) matters because our assembly can sit
        //      anywhere - a dev build under bin\, a test harness, a relocated module -
        //      and the engine's index is always about the real game install.
        private static List<string> CollectGameRoots()
        {
            var roots = new List<string>();

            List<string> fromResolver = TerrainNavMeshGenerator.GetGameRootCandidates();
            for (int i = 0; i < fromResolver.Count; i++)
                AddDistinct(roots, fromResolver[i]);

            AddDistinct(roots, RootFromAssembly(typeof(TaleWorlds.Engine.Scene)));
            AddDistinct(roots, RootFromAssembly(typeof(TaleWorlds.Library.Vec3)));
            AddDistinct(roots, RootFromAssembly(typeof(TaleWorlds.MountAndBlade.Mission)));

            return roots;
        }

        // ".../Mount & Blade II Bannerlord/bin/Win64_Shipping_wEditor/TaleWorlds.Engine.dll"
        // -> ".../Mount & Blade II Bannerlord", accepted only if it really has a
        // Modules folder.
        private static string RootFromAssembly(Type type)
        {
            try
            {
                string location = type.Assembly.Location;
                if (string.IsNullOrEmpty(location))
                    return null;

                string dir = Path.GetDirectoryName(location);
                for (int depth = 0; depth < 4 && !string.IsNullOrEmpty(dir); depth++)
                {
                    try
                    {
                        if (Directory.Exists(Path.Combine(dir, "Modules")))
                            return dir;
                    }
                    catch (Exception)
                    {
                        // Unreadable ancestor: keep walking.
                    }

                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch (Exception)
            {
                // Assembly location unavailable.
            }

            return null;
        }

        private static void AddDistinct(List<string> list, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            list.Add(value);
        }

        private static bool ContainsIgnoreCase(List<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed.Length == 0 ? path : trimmed;
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;

            return string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);
        }
    }
}
