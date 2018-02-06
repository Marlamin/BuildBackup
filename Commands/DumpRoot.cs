using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace BuildBackup
{
    partial class Program {
        static void DumpRoot(string rootHash) {
            var cdns = GetCDNs("wow");

            var fileNames = new Dictionary<ulong, string>();
            var hasher = new Jenkins96();
            foreach (var line in File.ReadLines("listfile.txt"))
            {
                fileNames.Add(hasher.ComputeHash(line), line);
            }

            var root = GetRoot("http://" + cdns.entries[0].hosts[0] + "/" + cdns.entries[0].path + "/", rootHash, true);

            foreach (var entry in root.entries)
            {
                foreach (var subentry in entry.Value)
                {
                    if (entry.Value.Count > 1)
                    {
                        if (subentry.contentFlags.HasFlag(ContentFlags.LowViolence)) {
                            continue;
                        }

                        if (!subentry.localeFlags.HasFlag(LocaleFlags.All_WoW) && !subentry.localeFlags.HasFlag(LocaleFlags.enUS))
                        {
                            continue;
                        }
                    }

                    if (fileNames.ContainsKey(entry.Key))
                    {
                        Console.WriteLine(fileNames[entry.Key] + ";" + entry.Key.ToString("x").PadLeft(16, '0') + ";" + subentry.fileDataID + ";" + BitConverter.ToString(subentry.md5).Replace("-", string.Empty).ToLower());
                    }
                    else
                    {
                        Console.WriteLine(";" + entry.Key.ToString("x").PadLeft(16, '0') + ";" + subentry.fileDataID + ";" + BitConverter.ToString(subentry.md5).Replace("-", string.Empty).ToLower());
                    }
                }

            }

            Environment.Exit(0);
        }
    }
}