using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace BuildBackup {
    partial class Program {
        static void DumpInstall(string program, string installHash) {
            var cdns = GetCDNs(program);
            var install = GetInstall("http://" + cdns.entries[0].hosts[0] + "/" + cdns.entries[0].path + "/", installHash, true);
            foreach (var entry in install.entries)
            {
                Console.WriteLine(entry.name + " (size: " + entry.size + ", md5: " + BitConverter.ToString(entry.contentHash).Replace("-", string.Empty).ToLower() + ", tags: " + string.Join(",", entry.tags) + ")");
            }
            Environment.Exit(0);
        }
    }
}