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
    // What the native loader actually does (see NavMeshPrefabIndex.md for the
    // full disassembly record):
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
    //      "file not found: %s" and then the "No navigation mesh prefab ..."
    //      message.
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
    // The two ways that bites a procedural generator, both of which this class
    // makes visible instead of mysterious:
    //
    //   a) Our module has no NavMeshPrefabs folder until the first export that
    //      resolves to it, so it is not in the index for that whole session.
    //      build.ps1 now creates the folder at deploy time.
    //   b) A name that did not exist when the index was built can never be
    //      resolved in that session, no matter how many times the .bin is
    //      rewritten. Rewrite = fine (same name, same module). New name = miss.
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

        public sealed class Candidate
        {
            public string ModuleDir;      // ...\Modules\<Module>
            public string ModuleId;       // <Module>
            public string Folder;         // ...\Modules\<Module>\NavMeshPrefabs
            public string BinPath;        // ...\NavMeshPrefabs\<name>.bin
            public bool FolderExists;
            public bool BinExists;
            public bool ModuleActive;     // as far as ModuleManager can tell
            public bool ModuleActiveKnown;
        }

        // The single path the engine will try, given the module the index picked.
        public static string BinPath(string navMeshPrefabsFolder, string prefabName)
        {
            if (string.IsNullOrEmpty(navMeshPrefabsFolder))
                return null;
            return Path.Combine(navMeshPrefabsFolder, prefabName + ".bin");
        }

        // "<...>\Modules\<Module>\NavMeshPrefabs" -> "<Module>" (the id the
        // module manager knows, and the leaf the launcher lists).
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

        // Every Modules\<Module>\NavMeshPrefabs we can see, with the state the
        // engine's index cares about. Cheap: one directory listing per game root.
        public static List<Candidate> FindCandidates(System.Action<string> log, string prefabName)
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
                        string key = moduleDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (ContainsIgnoreCase(seen, key))
                            continue;
                        seen.Add(key);

                        string folder = Path.Combine(moduleDir, FolderName);
                        var candidate = new Candidate();
                        candidate.ModuleDir = moduleDir;
                        candidate.ModuleId = Path.GetFileName(key);
                        candidate.Folder = folder;
                        candidate.BinPath = BinPath(folder, prefabName);

                        try
                        {
                            candidate.FolderExists = Directory.Exists(folder);
                            if (candidate.FolderExists && !string.IsNullOrEmpty(candidate.BinPath))
                                candidate.BinExists = File.Exists(candidate.BinPath);
                        }
                        catch (Exception)
                        {
                            // An unreadable module folder is reported as "not there"
                            // rather than aborting the whole lookup.
                        }

                        bool active;
                        if (TryIsModuleActive(candidate.ModuleId, out active))
                        {
                            candidate.ModuleActive = active;
                            candidate.ModuleActiveKnown = true;
                        }

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

        // One "prefab index:" block per run: what the engine will look up, where
        // it will look, and whether this session can possibly find it. This is
        // the log counterpart of the engine's "file not found: %s" line.
        public static void DescribeLookup(System.Action<string> log, string prefabName, string folderUsed)
        {
            if (log == null)
                return;

            string problem = ValidateName(prefabName);
            log("prefab index: name='" + prefabName + "' length="
                + (prefabName == null ? 0 : prefabName.Length).ToString(CultureInfo.InvariantCulture)
                + (problem == null ? " ok" : " UNUSABLE: " + problem));

            if (problem != null)
                return;

            List<Candidate> candidates = FindCandidates(null, prefabName);
            int found = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate c = candidates[i];
                if (!c.FolderExists)
                    continue;

                found++;
                log("prefab index:   " + (c.BinExists ? "FOUND   " : "absent  ")
                    + " " + c.BinPath
                    + " module=" + c.ModuleId
                    + " moduleActive=" + (c.ModuleActiveKnown ? c.ModuleActive.ToString() : "unknown")
                    + (c.BinPath != null && folderUsed != null
                        && string.Equals(c.BinPath, folderUsed, StringComparison.OrdinalIgnoreCase) ? "  <- this run" : ""));
            }

            if (found == 0)
            {
                if (candidates.Count == 0)
                {
                    log("prefab index:   could not locate the game install (no <root>/Modules found from"
                        + " our assembly or the loaded TaleWorlds assemblies), so the scan below is"
                        + " empty. The engine itself still resolves against its own install.");
                }
                else
                {
                    log("prefab index:   no module under " + candidates.Count + " scanned folder(s) has a "
                        + FolderName + " folder; the engine cannot resolve any prefab name this session.");
                }
            }

            if (!string.IsNullOrEmpty(folderUsed) && !Directory.Exists(folderUsed))
            {
                log("prefab index:   target folder does not exist yet: " + folderUsed
                    + " (created by this run; the engine's index was built before it existed,"
                    + " so this prefab name stays unknown until the index is rebuilt - restart the editor"
                    + " or reload modules, then Export again)");
            }
        }

        // A short, single-line verdict the exporter logs before it decides whether
        // to call ImportNavigationMeshPrefab at all.
        public static string ExplainUnresolvable(System.Action<string> log, string prefabName,
            string folderUsed, bool folderExisted, bool binExistedBefore)
        {
            string problem = ValidateName(prefabName);
            if (problem != null)
                return "prefab name cannot be looked up (" + problem + ")";

            if (binExistedBefore)
                return null; // the name was in the index; the rewrite keeps it there

            if (!folderExisted)
            {
                return "the " + FolderName + " folder did not exist before this run ("
                    + folderUsed + "); the engine built its prefab index before that folder existed, so it"
                    + " cannot know this name this session. Restart the editor (or reload modules) and run"
                    + " Export again - the .bin is already written and will then be found.";
            }

            return "no " + BinPath(folderUsed, prefabName) + " existed before this run, so the engine's"
                + " prefab index (built when it loaded the modules) does not contain '" + prefabName
                + "'. A brand new name cannot be resolved in the same session; restart the editor and run"
                + " Export again, or pick an existing prefab name.";
        }

        // Candidate game roots, from two independent sources so a missing one is
        // simply skipped:
        //   a) TerrainNavMeshGenerator's own resolver (walks up from our assembly
        //      to a folder named "Modules", plus TaleWorlds.Library.BasePath);
        //   b) the folder the loaded TaleWorlds assemblies were loaded from
        //      (.../bin/Win64_Shipping_wEditor or .../Win64_Shipping_Client), whose
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

        // TaleWorlds.ModuleManager is not a compile-time reference (it is not in
        // the csproj and is not present in every build), so the module manager is
        // asked by reflection. Any failure just means "unknown".
        private static bool TryIsModuleActive(string moduleId, out bool active)
        {
            active = false;
            if (string.IsNullOrEmpty(moduleId))
                return false;

            try
            {
                Type helper = Type.GetType(
                    "TaleWorlds.ModuleManager.ModuleHelper, TaleWorlds.ModuleManager", false);
                if (helper == null)
                    return false;

                System.Reflection.MethodInfo method = helper.GetMethod(
                    "IsModuleActive",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(string) }, null);
                if (method == null)
                    return false;

                object result = method.Invoke(null, new object[] { moduleId });
                if (result is bool)
                {
                    active = (bool)result;
                    return true;
                }
            }
            catch (Exception)
            {
                // Not available in this build; the caller reports "unknown".
            }

            return false;
        }
    }
}