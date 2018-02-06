using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace BuildBackup {
    partial class Program {
        static void DiffRoot (string fromRootHash, string toRootHash) {
            var cdns = GetCDNs ("wow");
            var fileNames = new Dictionary<ulong, string> ();
            var hasher = new Jenkins96 ();

            foreach (var line in File.ReadLines ("listfile.txt")) {
                fileNames.Add (hasher.ComputeHash (line), line);
            }

            var root1 = GetRoot ("http://" + cdns.entries[0].hosts[0] + "/" + cdns.entries[0].path + "/", fromRootHash, true);
            var root2 = GetRoot ("http://" + cdns.entries[0].hosts[0] + "/" + cdns.entries[0].path + "/", toRootHash, true);

            var unkFilenames = new List<ulong> ();

            foreach (var entry in root2.entries) {
                if (!root1.entries.ContainsKey (entry.Key)) {
                    // Added
                    if (fileNames.ContainsKey (entry.Key)) {
                        Console.WriteLine ("[ADDED] <b>" + fileNames[entry.Key] + "</b> (lookup: " + entry.Key.ToString ("x").PadLeft (16, '0') + ", content md5: " + BitConverter.ToString (entry.Value[0].md5).Replace ("-", string.Empty).ToLower () + ", FileData ID: " + entry.Value[0].fileDataID + ")");
                    } else {
                        Console.WriteLine ("[ADDED] <b>Unknown filename: " + entry.Key.ToString ("x").PadLeft (16, '0') + "</b> (content md5: " + BitConverter.ToString (entry.Value[0].md5).Replace ("-", string.Empty).ToLower () + ", FileData ID: " + entry.Value[0].fileDataID + ")");
                        unkFilenames.Add (entry.Key);
                    }
                }
            }

            foreach (var entry in root1.entries) {
                if (!root2.entries.ContainsKey (entry.Key)) {
                    // Removed
                    if (fileNames.ContainsKey (entry.Key)) {

                        Console.WriteLine ("[REMOVED] <b>" + fileNames[entry.Key] + "</b> (lookup: " + entry.Key.ToString ("x").PadLeft (16, '0') + ", content md5: " + BitConverter.ToString (entry.Value[0].md5).Replace ("-", string.Empty).ToLower () + ", FileData ID: " + entry.Value[0].fileDataID + ")");
                    } else {
                        Console.WriteLine ("[REMOVED] <b>Unknown filename: " + entry.Key.ToString ("x").PadLeft (16, '0') + "</b> (content md5: " + BitConverter.ToString (entry.Value[0].md5).Replace ("-", string.Empty).ToLower () + ", FileData ID: " + entry.Value[0].fileDataID + ")");
                        unkFilenames.Add (entry.Key);
                    }
                } else {
                    var r1md5 = BitConverter.ToString (entry.Value[0].md5).Replace ("-", string.Empty).ToLower ();
                    var r2md5 = BitConverter.ToString (root2.entries[entry.Key][0].md5).Replace ("-", string.Empty).ToLower ();
                    if (r1md5 != r2md5) {
                        if (fileNames.ContainsKey (entry.Key)) {
                            Console.WriteLine ("[MODIFIED] <b>" + fileNames[entry.Key] + "</b> (lookup: " + entry.Key.ToString ("x").PadLeft (16, '0') + ", FileData ID: " + entry.Value[0].fileDataID + ")");
                        } else {
                            Console.WriteLine ("[MODIFIED] <b>Unknown filename: " + entry.Key.ToString ("x").PadLeft (16, '0') + "</b> (content md5: " + BitConverter.ToString (entry.Value[0].md5).Replace ("-", string.Empty).ToLower () + ", FileData ID: " + entry.Value[0].fileDataID + ")");
                            unkFilenames.Add (entry.Key);
                        }
                    }
                }
            }

            Environment.Exit (0);
        }
    }
}