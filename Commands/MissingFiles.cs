using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;

namespace BuildBackup {
    partial class Program {
        // TODO: Clean this up, this doesn't seem to do anything.
        static void MissingFiles(String buildConfigHash, String cdnConfigHash) 
        {
            BuildConfigFile buildConfig = GetBuildConfig("wow", Path.Combine(cacheDir, "tpr", "wow"), buildConfigHash);
            if (string.IsNullOrWhiteSpace(buildConfig.buildName)) { Console.WriteLine("Invalid buildConfig!"); }

            cdnConfig = GetCDNconfig("wow", Path.Combine(cacheDir, "tpr", "wow"), buildConfigHash);
            if (cdnConfig.archives == null) { Console.WriteLine("Invalid cdnConfig"); }

            encoding = GetEncoding(Path.Combine(cacheDir, "tpr", "wow"), buildConfig.encoding[1]);

            Dictionary<string, string> hashes = new Dictionary<string, string>();

            foreach (var entry in encoding.aEntries)
            {
                if (entry.hash == buildConfig.root.ToUpper()) { root = GetRoot(Path.Combine(cacheDir, "tpr", "wow"), entry.hash.ToLower()); }
                if (!hashes.ContainsKey(entry.key)) { hashes.Add(entry.key, entry.hash); }
            }

            indexes = GetIndexes(Path.Combine(cacheDir, "tpr", "wow"), cdnConfig.archives);

            foreach (var index in indexes)
            {
                // If respective archive does not exist, add to separate list

                // Remove from list as usual
                foreach (var entry in index.archiveIndexEntries)
                {
                    hashes.Remove(entry.headerHash);
                }
            }

            // Run through root to see which file hashes belong to which missing file and put those in a list
            // Run through listfile to see if files are known
            Environment.Exit(0);
        }
    }
}