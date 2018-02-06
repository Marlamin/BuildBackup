using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace BuildBackup {
    partial class Program {
        static void DumpEncrypted(string program, string buildConfigHash) {

            if (program != "wow") {
                throw new System.Exception("Only WoW is currently supported due to root/fileDataID usage");
            }

            var cdns = GetCDNs(program);
            var buildConfig = GetBuildConfig (program, Path.Combine (cacheDir, cdns.entries[0].path), buildConfigHash);
            var encoding = GetEncoding (Path.Combine (cacheDir, cdns.entries[0].path), buildConfig.encoding[1], 0, true);

            var encryptedKeys = new Dictionary<string, string> ();
            var encryptedSizes = new Dictionary<string, ulong> ();
            foreach (var entry in encoding.bEntries) {
                var stringBlockEntry = encoding.stringBlockEntries[entry.stringIndex];
                if (stringBlockEntry.Contains ("e:")) {
                    encryptedKeys.Add (entry.key, stringBlockEntry);
                    encryptedSizes.Add (entry.key, entry.compressedSize);
                }
            }

            string rootKey = "";
            var encryptedContentHashes = new Dictionary<string, string>();
            var encryptedContentSizes = new Dictionary<string, ulong>();
            foreach (var entry in encoding.aEntries) {
                if (encryptedKeys.ContainsKey (entry.key)) {
                    encryptedContentHashes.Add (entry.hash, encryptedKeys[entry.key]);
                    encryptedContentSizes.Add (entry.hash, encryptedSizes[entry.key]);
                }

                if (entry.hash == buildConfig.root.ToUpper()) { rootKey = entry.key.ToLower(); }
            }

            root = GetRoot (Path.Combine (cacheDir, cdns.entries[0].path), rootKey, true);

            foreach (var entry in root.entries) {
                foreach (var subentry in entry.Value) {
                    if (encryptedContentHashes.ContainsKey (BitConverter.ToString (subentry.md5).Replace ("-", ""))) {
                        var stringBlock = encryptedContentHashes[BitConverter.ToString (subentry.md5).Replace ("-", "")];
                        var encryptionKey = stringBlock.Substring (stringBlock.IndexOf ("e:{") + 3, 16);
                        Console.WriteLine (subentry.fileDataID + " " + encryptionKey + " " + stringBlock + " " + encryptedContentSizes[BitConverter.ToString (subentry.md5).Replace ("-", "")]);
                        break;
                    }
                }
            }

            Environment.Exit(0);
        }
    }
}