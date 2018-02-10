using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace BuildBackup
{
    partial class Program {
        static void DumpInfo(string program, string buildConfigHash, string cdnConfigHash) {
            var cdns = GetCDNs(program);
            
            var buildConfig = GetBuildConfig(program, Path.Combine(cacheDir, cdns.entries[0].path), buildConfigHash);
            if (string.IsNullOrWhiteSpace(buildConfig.buildName)) { Console.WriteLine("Invalid buildConfig!"); }

            var cdnConfig = GetCDNconfig(program, Path.Combine(cacheDir, cdns.entries[0].path), cdnConfigHash);
            if (cdnConfig.archives == null) { Console.WriteLine("Invalid cdnConfig"); }

            var encoding = GetEncoding(Path.Combine(cacheDir, cdns.entries[0].path), buildConfig.encoding[1]);

            string rootKey = "";
            string downloadKey = "";
            string installKey = "";

            var hashes = new Dictionary<string, string>();

            foreach (var entry in encoding.aEntries)
            {
                if (entry.hash == buildConfig.root.ToUpper()) { rootKey = entry.key; Console.WriteLine("root = " + entry.key.ToLower()); }
                if (entry.hash == buildConfig.download[0].ToUpper()) { downloadKey = entry.key; Console.WriteLine("download = " + entry.key.ToLower()); }
                if (entry.hash == buildConfig.install[0].ToUpper()) { installKey = entry.key; Console.WriteLine("install = " + entry.key.ToLower()); }
                if (!hashes.ContainsKey(entry.key)) { hashes.Add(entry.key, entry.hash); }
            }

            var indexes = GetIndexes(Path.Combine(cacheDir, cdns.entries[0].path), cdnConfig.archives);

            foreach (var index in indexes)
            {
                //Console.WriteLine("Checking " + index.name + " " + index.archiveIndexEntries.Count() + " entries");
                foreach (var entry in index.archiveIndexEntries)
                {
                    hashes.Remove(entry.headerHash);
                    //Console.WriteLine("Removing " + entry.headerHash.ToLower() + " from list");
                }
            }

            int h = 1;
            var tot = hashes.Count;

            foreach (var entry in hashes)
            {
                //Console.WriteLine("[" + h + "/" + tot + "] Downloading " + entry.Key);
                Console.WriteLine("unarchived = " + entry.Key.ToLower());
                h++;
            }

            Environment.Exit(1);
        }
    }
}