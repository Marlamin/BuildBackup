using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BuildBackup
{
    class Program
    {
        private static readonly Uri baseUrl = new("https://us.version.battle.net/");

        private static string[] checkPrograms;
        private static string[] backupPrograms;

        private static VersionsFile versions;
        private static CdnsFile cdns;
        private static GameBlobFile productConfig;
        private static BuildConfigFile buildConfig;
        private static BuildConfigFile[] cdnBuildConfigs;
        private static CDNConfigFile cdnConfig;
        private static EncodingFile encoding;
        private static InstallFile install;
        private static DownloadFile download;
        private static RootFile root;
        private static PatchFile patch;

        private static bool fullDownload = true;

        private static bool overrideVersions;
        private static string overrideBuildconfig;
        private static string overrideCDNconfig;

        private static Dictionary<string, IndexEntry> indexDictionary = [];
        private static Dictionary<string, IndexEntry> patchIndexDictionary = [];
        private static Dictionary<string, IndexEntry> fileIndexList = [];
        private static Dictionary<string, IndexEntry> patchFileIndexList = [];
        private static ReaderWriterLockSlim cacheLock = new();

        public static Dictionary<string, byte[]> cachedArmadilloKeys = [];

        private static readonly CDN cdn = new();

        static async Task Main(string[] args)
        {
            cdn.cacheDir = SettingsManager.cacheDir;
            cdn.client = new HttpClient
            {
                Timeout = new TimeSpan(0, 5, 0)
            };

            cdn.cdnList = [
                //"blzddist1-a.akamaihd.net",     // Akamai first
                "level3.blizzard.com",        // Level3
                "eu.cdn.blizzard.com",        // Official EU CDN
                "us.cdn.blizzard.com",        // Official US CDN
                //"kr.cdn.blizzard.com",        // Official KR CDN
                "cdn.blizzard.com",             // Official regionless CDN
                //"blizzard.nefficient.co.kr",  // Korea 
                //"cdn.arctium.tools",            // Arctium archive
                //"casc.wago.tools",            // Wago archive
                //"tact.mirror.reliquaryhq.com",  // ReliquaryHQ archive
            ];

            // Check if cache/backup directory exists
            try
            {
                if (!Directory.Exists(cdn.cacheDir)) { Directory.CreateDirectory(cdn.cacheDir); }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error creating cache directory: " + e.Message);
                Console.ReadKey();
                return;
            }

            #region Commands
            if (args.Length > 0)
            {
                if (args[0] == "dumpinfo")
                {
                    if (args.Length != 4) throw new Exception("Not enough arguments. Need mode, product, buildconfig, cdnconfig");

                    cdns = GetCDNs(args[1]);

                    buildConfig = GetBuildConfig(cdns.entries[0].path, args[2]);
                    if (string.IsNullOrWhiteSpace(buildConfig.buildName)) { Console.WriteLine("Invalid buildConfig!"); }

                    cdnConfig = GetCDNconfig(cdns.entries[0].path, args[3]);
                    if (cdnConfig.archives == null) { Console.WriteLine("Invalid cdnConfig"); }

                    encoding = GetEncoding(cdns.entries[0].path, buildConfig.encoding[1]).Result;

                    string rootKey = "";
                    string downloadKey = "";
                    string installKey = "";

                    if (buildConfig.download.Length == 2)
                    {
                        downloadKey = buildConfig.download[1];
                        Console.WriteLine("download = " + downloadKey.ToLower());
                    }

                    if (buildConfig.install.Length == 2)
                    {
                        installKey = buildConfig.install[1];
                        Console.WriteLine("install = " + installKey.ToLower());
                    }

                    Dictionary<string, string> hashes = [];

                    foreach (var entry in encoding.aEntries)
                    {
                        if (entry.cKey.Equals(buildConfig.root, StringComparison.OrdinalIgnoreCase)) { rootKey = entry.eKeys[0]; Console.WriteLine("root = " + entry.eKeys[0].ToLower()); }
                        if (string.IsNullOrEmpty(downloadKey) && entry.cKey.Equals(buildConfig.download[0], StringComparison.OrdinalIgnoreCase)) { downloadKey = entry.eKeys[0]; Console.WriteLine("download = " + entry.eKeys[0].ToLower()); }
                        if (string.IsNullOrEmpty(installKey) && entry.cKey.Equals(buildConfig.install[0], StringComparison.OrdinalIgnoreCase)) { installKey = entry.eKeys[0]; Console.WriteLine("install = " + entry.eKeys[0].ToLower()); }
                        hashes.TryAdd(entry.eKeys[0], entry.cKey);
                    }

                    GetIndexes(cdns.entries[0].path, cdnConfig.archives);

                    foreach (var entry in indexDictionary)
                    {
                        hashes.Remove(entry.Key.ToUpper());
                    }

                    int h = 1;
                    var tot = hashes.Count;

                    foreach (var entry in hashes)
                    {
                        Console.WriteLine("unarchived = " + entry.Key.ToLower());
                        h++;
                    }

                    Environment.Exit(0);
                }
                if (args[0] == "dumproot")
                {
                    if (args.Length != 2) throw new Exception("Not enough arguments. Need mode, root");
                    cdns = GetCDNs("wow");

                    var fileNames = new Dictionary<ulong, string>();
                    UpdateListfile();
                    var hasher = new Jenkins96();
                    foreach (var line in File.ReadLines("listfile.txt"))
                    {
                        fileNames.Add(hasher.ComputeHash(line), line);
                    }

                    var root = GetRoot(cdns.entries[0].path + "/", args[1], true);

                    foreach (var entry in root.entriesFDID)
                    {
                        foreach (var subentry in entry.Value)
                        {
                            if (subentry.contentFlags.HasFlag(ContentFlags.LowViolence)) continue;

                            if (!subentry.localeFlags.HasFlag(LocaleFlags.All_WoW) && !subentry.localeFlags.HasFlag(LocaleFlags.enUS))
                            {
                                continue;
                            }

                            if (entry.Key > 0 && fileNames.ContainsKey(entry.Key))
                            {
                                Console.WriteLine(fileNames[entry.Key] + ";" + entry.Key.ToString("x").PadLeft(16, '0') + ";" + subentry.fileDataID + ";" + Convert.ToHexString(subentry.md5).ToLower());
                            }
                            else
                            {
                                Console.WriteLine("unknown;" + entry.Key.ToString("x").PadLeft(16, '0') + ";" + subentry.fileDataID + ";" + Convert.ToHexString(subentry.md5).ToLower());
                            }
                        }

                    }

                    Environment.Exit(0);
                }
                if (args[0] == "dumproot2")
                {
                    if (args.Length < 2) throw new Exception("Not enough arguments. Need mode, root");

                    var product = "wow";
                    if (args.Length == 3)
                    {
                        product = args[2];
                    }

                    cdns = GetCDNs(product);

                    var hasher = new Jenkins96();
                    UpdateListfile();
                    var hashes = File
                        .ReadLines("listfile.txt")
                        .Select<string, Tuple<ulong, string>>(fileName => new Tuple<ulong, string>(hasher.ComputeHash(fileName), fileName))
                        .ToDictionary(key => key.Item1, value => value.Item2);

                    var root = GetRoot(cdns.entries[0].path + "/", args[1], true);

                    Action<RootEntry> print = delegate (RootEntry entry)
                    {
                        var lookup = "";
                        var fileName = "";

                        if (entry.lookup > 0)
                        {
                            lookup = entry.lookup.ToString("x").PadLeft(16, '0');
                            fileName = hashes.TryGetValue(entry.lookup, out string value) ? value : "";
                        }

                        var md5 = Convert.ToHexString(entry.md5).ToLower();
                        var dataId = entry.fileDataID;
                        Console.WriteLine("{0};{1};{2};{3}", fileName, lookup, dataId, md5);
                    };

                    foreach (var entry in root.entriesFDID)
                    {
                        RootEntry? prioritizedEntry = entry.Value.FirstOrDefault(subentry =>
                            subentry.contentFlags.HasFlag(ContentFlags.LowViolence) == false && (subentry.localeFlags.HasFlag(LocaleFlags.All_WoW) || subentry.localeFlags.HasFlag(LocaleFlags.enUS))
                        );

                        var selectedEntry = (prioritizedEntry.Value.md5 != null) ? prioritizedEntry.Value : entry.Value.First();
                        print(selectedEntry);
                    }

                    Environment.Exit(0);
                }
                if (args[0] == "dumproot3")
                {
                    if (args.Length != 2) throw new Exception("Not enough arguments. Need mode, root");
                    cdns = GetCDNs("wow");

                    var root = GetRoot(cdns.entries[0].path + "/", args[1], true);

                    foreach (var entry in root.entriesFDID)
                    {
                        foreach (var subentry in entry.Value)
                        {
                            Console.WriteLine(subentry.fileDataID + ";" + Convert.ToHexString(subentry.md5).ToLower() + ";" + subentry.localeFlags.ToString() + ";" + subentry.contentFlags.ToString());
                        }
                    }

                    Environment.Exit(0);
                }
                if (args[0] == "dumproot4")
                {
                    if (args.Length != 3) throw new Exception("Not enough arguments. Need mode, product, root");

                    var root = GetRoot("tpr/" + args[1] + "/", args[2], true);

                    foreach (var entry in root.entriesFDID)
                    {
                        foreach (var subentry in entry.Value)
                        {
                            Console.WriteLine(subentry.fileDataID + ";" + Convert.ToHexString(subentry.md5).ToLower() + ";" + subentry.localeFlags.ToString() + ";" + subentry.contentFlags.ToString());
                        }
                    }

                    Environment.Exit(0);
                }
                if (args[0] == "calchash")
                {
                    var hasher = new Jenkins96();
                    var hash = hasher.ComputeHash(args[1]);
                    Console.WriteLine(hash + " " + hash.ToString("x").PadLeft(16, '0'));
                    Environment.Exit(0);
                }
                if (args[0] == "calchashlistfile")
                {
                    string target = "";

                    if (args.Length == 2 && File.Exists(args[1]))
                    {
                        target = args[1];
                    }
                    else
                    {
                        UpdateListfile();
                        target = "listfile.txt";
                    }

                    var hasher = new Jenkins96();

                    foreach (var line in File.ReadLines(target))
                    {
                        if (string.IsNullOrEmpty(line)) continue;
                        var hash = hasher.ComputeHash(line);
                        Console.WriteLine(line + " = " + hash.ToString("x").PadLeft(16, '0'));
                    }
                    Environment.Exit(0);
                }
                if (args[0] == "dumpinstall")
                {
                    if (args.Length != 3) throw new Exception("Not enough arguments. Need mode, product, install");

                    cdns = GetCDNs(args[1]);
                    install = GetInstall(cdns.entries[0].path + "/", args[2], true);
                    foreach (var entry in install.entries)
                    {
                        Console.WriteLine(entry.name + " (size: " + entry.size + ", md5: " + Convert.ToHexString(entry.contentHash).ToLower() + ", tags: " + string.Join(",", entry.tags) + ")");
                    }
                    Environment.Exit(0);
                }
                if (args[0] == "dumpdownload")
                {
                    if (args.Length != 3) throw new Exception("Not enough arguments. Need mode, product, download");

                    cdns = GetCDNs(args[1]);
                    var download = GetDownload(cdns.entries[0].path + "/", args[2], true);
                    foreach (var entry in download.entries)
                    {
                        Console.WriteLine(entry.eKey + " (size: " + entry.size + ", priority: " + entry.priority + ", flags: " + entry.flags + ")");
                    }
                    Environment.Exit(0);
                }
                if (args[0] == "dumpdecodedencoding")
                {
                    if (args.Length != 3) throw new Exception("Not enough arguments. Need mode, product, encoding");

                    encoding = GetEncoding(args[1], args[2], 0, true, false, false).Result;
                    foreach (var entry in encoding.aEntries)
                    {
                        for (var i = 0; i < entry.keyCount; i++)
                        {
                            var table2Entry = encoding.bEntries[entry.eKeys[i]];
                            Console.WriteLine(entry.cKey.ToLower() + " " + entry.eKeys[i].ToLower() + " " + entry.keyCount + " " + entry.size + " " + encoding.stringBlockEntries[table2Entry.stringIndex]);
                        }
                    }
                    Console.WriteLine("ENCODINGESPEC " + encoding.encodingESpec);
                    Environment.Exit(0);
                }
                if (args[0] == "dumpencoding")
                {
                    if (args.Length != 3) throw new Exception("Not enough arguments. Need mode, product, encoding");

                    encoding = GetEncoding("tpr/" + args[1] + "/", args[2], 0, true).Result;
                    foreach (var entry in encoding.aEntries)
                    {
                        for (var i = 0; i < entry.keyCount; i++)
                        {
                            var table2Entry = encoding.bEntries[entry.eKeys[i]];
                            Console.WriteLine(entry.cKey.ToLower() + " " + entry.eKeys[i].ToLower() + " " + entry.keyCount + " " + entry.size + " " + encoding.stringBlockEntries[table2Entry.stringIndex]);
                        }
                    }
                    Console.WriteLine("ENCODINGESPEC " + encoding.encodingESpec);
                    Environment.Exit(0);
                }
                if (args[0] == "dumpconfig")
                {
                    if (args.Length != 3) throw new Exception("Not enough arguments. Need mode, product, hash");
                    var product = args[1];
                    var hash = Path.GetFileNameWithoutExtension(args[2]);
                    var content = Encoding.UTF8.GetString(cdn.Get("tpr/" + product + "/config/" + hash[0] + hash[1] + "/" + hash[2] + hash[3] + "/" + hash).Result);
                    Console.WriteLine(content);
                    Environment.Exit(0);
                }
                if (args[0] == "extractfilebycontenthash" || args[0] == "extractrawfilebycontenthash")
                {
                    if (args.Length != 6) throw new Exception("Not enough arguments. Need mode, product, buildconfig, cdnconfig, contenthash, outname");

                    cdns = GetCDNs(args[1]);

                    args[4] = args[4].ToLower();

                    buildConfig = GetBuildConfig(cdns.entries[0].path, args[2]);
                    if (string.IsNullOrWhiteSpace(buildConfig.buildName)) { Console.WriteLine("Invalid buildConfig!"); }

                    encoding = GetEncoding(cdns.entries[0].path, buildConfig.encoding[1]).Result;

                    string target = "";

                    foreach (var entry in encoding.aEntries)
                    {
                        if (entry.cKey.Equals(args[4], StringComparison.OrdinalIgnoreCase))
                        {
                            target = entry.eKeys[0].ToLower();
                            break;
                        }
                    }

                    if (string.IsNullOrEmpty(target))
                    {
                        throw new Exception("File not found in encoding!");
                    }

                    cdnConfig = GetCDNconfig(cdns.entries[0].path, args[3]);

                    GetIndexes(cdns.entries[0].path, cdnConfig.archives);

                    if (args[0] == "extractrawfilebycontenthash")
                    {
                        var unarchivedName = Path.Combine(cdn.cacheDir, cdns.entries[0].path, "data", target[0] + "" + target[1], target[2] + "" + target[3], target);

                        Directory.CreateDirectory(Path.GetDirectoryName(unarchivedName));

                        File.WriteAllBytes(unarchivedName, RetrieveFileBytes(target, true, cdns.entries[0].path));
                    }
                    else
                    {
                        File.WriteAllBytes(args[5], RetrieveFileBytes(target, false, cdns.entries[0].path));
                    }

                    Environment.Exit(0);
                }
                if (args[0] == "extractfilebyencodingkey")
                {
                    if (args.Length != 5) throw new Exception("Not enough arguments. Need mode, product, cdnconfig, contenthash, outname");

                    cdns = GetCDNs(args[1]);
                    cdnConfig = GetCDNconfig(cdns.entries[0].path, args[2]);

                    var target = args[3];

                    GetIndexes(cdns.entries[0].path, cdnConfig.archives);

                    File.WriteAllBytes(args[4], RetrieveFileBytes(target, false, cdns.entries[0].path, true));

                    Environment.Exit(0);
                }
                if (args[0] == "extractfilesbylist")
                {
                    if (args.Length != 5) throw new Exception("Not enough arguments. Need mode, buildconfig, cdnconfig, basedir, list");

                    buildConfig = GetBuildConfig(Path.Combine("tpr", "wow"), args[1]);
                    if (string.IsNullOrWhiteSpace(buildConfig.buildName)) { Console.WriteLine("Invalid buildConfig!"); }

                    encoding = GetEncoding(Path.Combine("tpr", "wow"), buildConfig.encoding[1]).Result;

                    var basedir = args[3];

                    var lines = File.ReadLines(args[4]);

                    cdnConfig = GetCDNconfig(Path.Combine("tpr", "wow"), args[2]);

                    GetIndexes(Path.Combine("tpr", "wow"), cdnConfig.archives);

                    foreach (var line in lines)
                    {
                        var splitLine = line.Split(',');
                        var contenthash = splitLine[0];
                        var filename = splitLine[1];

                        if (!Directory.Exists(Path.Combine(basedir, Path.GetDirectoryName(filename))))
                        {
                            Directory.CreateDirectory(Path.Combine(basedir, Path.GetDirectoryName(filename)));
                        }

                        Console.WriteLine(filename);

                        string target = "";

                        foreach (var entry in encoding.aEntries)
                        {
                            if (entry.cKey.Equals(contenthash, StringComparison.OrdinalIgnoreCase)) { target = entry.eKeys[0].ToLower(); Console.WriteLine("Found target: " + target); break; }
                        }

                        if (string.IsNullOrEmpty(target))
                        {
                            Console.WriteLine("File " + filename + " (" + contenthash + ") not found in encoding!");
                            continue;
                        }

                        try
                        {
                            File.WriteAllBytes(Path.Combine(basedir, filename), RetrieveFileBytes(target));
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.Message);
                        }
                    }

                    Environment.Exit(0);
                }
                if (args[0] == "cachebuild")
                {
                    if (args.Length != 4) throw new Exception("Not enough arguments. Need mode, buildconfig, cdnconfig, basedir");

                    buildConfig = GetBuildConfig(Path.Combine("tpr", "wow"), args[1]);
                    if (string.IsNullOrWhiteSpace(buildConfig.buildName)) { Console.WriteLine("Invalid buildConfig!"); }

                    encoding = GetEncoding(Path.Combine("tpr", "wow"), buildConfig.encoding[1]).Result;

                    cdnConfig = GetCDNconfig(Path.Combine("tpr", "wow"), args[2]);

                    GetIndexes(Path.Combine("tpr", "wow"), cdnConfig.archives);

                    var basedir = args[3];
                    var rootHash = "";

                    foreach (var entry in encoding.aEntries)
                    {
                        if (entry.cKey.Equals(buildConfig.root, StringComparison.OrdinalIgnoreCase)) { rootHash = entry.eKeys[0].ToLower(); break; }
                    }

                    var fdidList = new List<uint>();

                    root = GetRoot(Path.Combine("tpr", "wow"), rootHash, true);

                    if (File.Exists(Path.Combine(basedir, "lastextractedroot.txt")))
                    {
                        var oldRootHash = File.ReadAllLines(Path.Combine(basedir, "lastextractedroot.txt"))[0];
                        var oldRoot = GetRoot(Path.Combine("tpr", "wow"), oldRootHash, true);

                        var rootFromEntries = oldRoot.entriesFDID;
                        var fromEntries = rootFromEntries.Keys.ToHashSet();

                        var rootToEntries = root.entriesFDID;
                        var toEntries = rootToEntries.Keys.ToHashSet();

                        var commonEntries = fromEntries.Intersect(toEntries);
                        var removedEntries = fromEntries.Except(commonEntries);
                        var addedEntries = toEntries.Except(commonEntries);

                        static RootEntry prioritize(List<RootEntry> entries)
                        {
                            var prioritized = entries.FirstOrDefault(subentry =>
                                   subentry.contentFlags.HasFlag(ContentFlags.LowViolence) == false && (subentry.localeFlags.HasFlag(LocaleFlags.All_WoW) || subentry.localeFlags.HasFlag(LocaleFlags.enUS))
                            );

                            if (prioritized.fileDataID != 0)
                            {
                                return prioritized;
                            }
                            else
                            {
                                return entries.First();
                            }
                        }

                        var addedFiles = addedEntries.Select(entry => rootToEntries[entry]).Select(prioritize);
                        var removedFiles = removedEntries.Select(entry => rootFromEntries[entry]).Select(prioritize);

                        var modifiedFiles = new List<RootEntry>();

                        foreach (var entry in commonEntries)
                        {
                            var originalFile = prioritize(rootFromEntries[entry]);
                            var patchedFile = prioritize(rootToEntries[entry]);

                            if (!originalFile.md5.SequenceEqual(patchedFile.md5))
                            {
                                modifiedFiles.Add(patchedFile);
                            }
                            else if (originalFile.contentFlags.HasFlag(ContentFlags.Encrypted))
                            {
                                modifiedFiles.Add(patchedFile);
                            }
                        }

                        Console.WriteLine($"Added: {addedEntries.Count()}, removed: {removedFiles.Count()}, modified: {modifiedFiles.Count}, common: {commonEntries.Count()}");

                        fdidList.AddRange(addedFiles.Select(x => x.fileDataID));
                        fdidList.AddRange(modifiedFiles.Select(x => x.fileDataID));
                    }
                    else
                    {
                        fdidList.AddRange(root.entriesFDID.Keys);
                    }

                    Console.WriteLine("Looking up in root..");

                    var encodingList = new Dictionary<string, List<string>>();

                    foreach (var entry in root.entriesFDID)
                    {
                        foreach (var subentry in entry.Value)
                        {
                            if (subentry.contentFlags.HasFlag(ContentFlags.LowViolence))
                                continue;

                            if (!subentry.localeFlags.HasFlag(LocaleFlags.All_WoW) && !subentry.localeFlags.HasFlag(LocaleFlags.enUS))
                                continue;

                            if (fdidList.Contains(subentry.fileDataID))
                            {
                                var cleanContentHash = Convert.ToHexString(subentry.md5).ToLower();

                                if (encodingList.TryGetValue(cleanContentHash, out List<string> value))
                                {
                                    value.Add(subentry.fileDataID.ToString());
                                }
                                else
                                {
                                    encodingList.Add(cleanContentHash, [subentry.fileDataID.ToString()]);
                                }
                            }
                            continue;
                        }
                    }

                    var fileList = new Dictionary<string, List<string>>();

                    Console.WriteLine("Looking up in encoding..");
                    foreach (var encodingEntry in encoding.aEntries)
                    {
                        string target = "";

                        if (encodingList.ContainsKey(encodingEntry.cKey.ToLower()))
                        {
                            target = encodingEntry.eKeys[0].ToLower();
                            //Console.WriteLine(target);
                            foreach (var subName in encodingList[encodingEntry.cKey.ToLower()])
                            {
                                if (fileList.TryGetValue(target, out List<string> value))
                                {
                                    value.Add(subName);
                                }
                                else
                                {
                                    fileList.Add(target, [subName]);
                                }
                            }
                            encodingList.Remove(encodingEntry.cKey.ToLower());
                        }
                    }

                    var archivedFileList = new Dictionary<string, Dictionary<string, List<string>>>();
                    var unarchivedFileList = new Dictionary<string, List<string>>();

                    Console.WriteLine("Looking up in indexes..");
                    foreach (var fileEntry in fileList)
                    {
                        if (!indexDictionary.TryGetValue(fileEntry.Key.ToUpper(), out IndexEntry entry))
                        {
                            unarchivedFileList.Add(fileEntry.Key, fileEntry.Value);
                        }

                        var index = cdnConfig.archives[entry.index];
                        if (!archivedFileList.TryGetValue(index, out Dictionary<string, List<string>> value))
                        {
                            value = [];
                            archivedFileList.Add(index, value);
                        }

                        value.Add(fileEntry.Key, fileEntry.Value);
                    }

                    var extractedFiles = 0;
                    var totalFiles = fileList.Count;

                    Console.WriteLine("Extracting " + unarchivedFileList.Count + " unarchived files..");
                    foreach (var fileEntry in unarchivedFileList)
                    {
                        var target = fileEntry.Key;

                        foreach (var filename in fileEntry.Value)
                        {
                            if (!Directory.Exists(Path.Combine(basedir, rootHash, Path.GetDirectoryName(filename))))
                            {
                                Directory.CreateDirectory(Path.Combine(basedir, rootHash, Path.GetDirectoryName(filename)));
                            }
                        }

                        var unarchivedName = Path.Combine(cdn.cacheDir, "tpr", "wow", "data", target[0] + "" + target[1], target[2] + "" + target[3], target);
                        if (File.Exists(unarchivedName))
                        {
                            foreach (var filename in fileEntry.Value)
                            {
                                try
                                {
                                    File.WriteAllBytes(Path.Combine(basedir, rootHash, filename), BLTE.Parse(File.ReadAllBytes(unarchivedName)));
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine(e.Message);
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine("Unarchived file does not exist " + unarchivedName + ", cannot extract " + string.Join(',', fileEntry.Value));
                        }

                        extractedFiles++;

                        if (extractedFiles % 100 == 0)
                        {
                            Console.WriteLine("[" + DateTime.Now.ToString() + "] Extracted " + extractedFiles + " out of " + totalFiles + " files");
                        }
                    }

                    foreach (var archiveEntry in archivedFileList)
                    {
                        var archiveName = Path.Combine(cdn.cacheDir, "tpr", "wow", "data", archiveEntry.Key[0] + "" + archiveEntry.Key[1], archiveEntry.Key[2] + "" + archiveEntry.Key[3], archiveEntry.Key);
                        Console.WriteLine("[" + DateTime.Now.ToString() + "] Extracting " + archiveEntry.Value.Count + " files from archive " + archiveEntry.Key + "..");

                        using (var stream = new MemoryStream(File.ReadAllBytes(archiveName)))
                        {
                            foreach (var fileEntry in archiveEntry.Value)
                            {
                                var target = fileEntry.Key;

                                foreach (var filename in fileEntry.Value)
                                {
                                    if (!Directory.Exists(Path.Combine(basedir, rootHash, Path.GetDirectoryName(filename))))
                                    {
                                        Directory.CreateDirectory(Path.Combine(basedir, rootHash, Path.GetDirectoryName(filename)));
                                    }
                                }

                                if (indexDictionary.TryGetValue(target.ToUpper(), out IndexEntry entry))
                                {
                                    foreach (var filename in fileEntry.Value)
                                    {
                                        try
                                        {
                                            stream.Seek(entry.offset, SeekOrigin.Begin);

                                            if (entry.offset > stream.Length || entry.offset + entry.size > stream.Length)
                                            {
                                                throw new Exception("File is beyond archive length, incomplete archive!");
                                            }

                                            var archiveBytes = new byte[entry.size];
                                            stream.Read(archiveBytes, 0, (int)entry.size);
                                            File.WriteAllBytes(Path.Combine(basedir, rootHash, filename), BLTE.Parse(archiveBytes));
                                        }
                                        catch (Exception e)
                                        {
                                            Console.WriteLine(e.Message);
                                        }
                                    }
                                }
                                else
                                {
                                    Console.WriteLine("!!!!! Unable to find " + fileEntry.Key + " (" + fileEntry.Value[0] + ") in archives!");
                                }

                                extractedFiles++;

                                if (extractedFiles % 1000 == 0)
                                {
                                    Console.WriteLine("[" + DateTime.Now.ToString() + "] Extracted " + extractedFiles + " out of " + totalFiles + " files");
                                }
                            }
                        }
                    }

                    File.WriteAllText(Path.Combine(basedir, "lastextractedroot.txt"), rootHash);

                    Environment.Exit(0);
                }
                if (args[0] == "extractfilesbyfnamelist" || args[0] == "extractfilesbyfdidlist")
                {
                    if (args.Length < 5) throw new Exception("Not enough arguments. Need mode, buildconfig, cdnconfig, basedir, list, (product)");

                    var product = "wow";
                    if (args.Length == 6)
                        product = args[5];

                    buildConfig = GetBuildConfig("tpr/" + product, args[1]);
                    if (string.IsNullOrWhiteSpace(buildConfig.buildName)) { Console.WriteLine("Invalid buildConfig!"); }

                    encoding = GetEncoding("tpr/" + product, buildConfig.encoding[1]).Result;

                    cdnConfig = GetCDNconfig("tpr/" + product, args[2]);

                    GetIndexes("tpr/" + product, cdnConfig.archives);

                    var basedir = args[3];

                    var lines = File.ReadLines(args[4]);

                    var rootHash = "";

                    foreach (var entry in encoding.aEntries)
                    {
                        if (entry.cKey.Equals(buildConfig.root, StringComparison.OrdinalIgnoreCase)) { rootHash = entry.eKeys[0].ToLower(); break; }
                    }

                    var hasher = new Jenkins96();
                    var nameList = new Dictionary<ulong, string>();
                    var fdidList = new Dictionary<uint, string>();

                    if (args[0] == "extractfilesbyfnamelist")
                    {
                        foreach (var line in lines)
                        {
                            var hash = hasher.ComputeHash(line);
                            nameList.Add(hash, line);
                        }
                    }
                    else if (args[0] == "extractfilesbyfdidlist")
                    {
                        foreach (var line in lines)
                        {
                            if (string.IsNullOrEmpty(line))
                                continue;

                            var expl = line.Split(';');
                            if (expl.Length == 1)
                            {
                                fdidList.Add(uint.Parse(expl[0]), expl[0]);
                            }
                            else
                            {
                                fdidList.Add(uint.Parse(expl[0]), expl[1]);
                            }
                        }
                    }

                    Console.WriteLine("Looking up " + nameList.Count + " named files and " + fdidList.Count + " unnamed files in root..");

                    root = GetRoot("tpr/" + product, rootHash, true);

                    var encodingList = new Dictionary<string, List<string>>();
                    var chashList = new Dictionary<uint, string>();

                    foreach (var entry in root.entriesFDID)
                    {
                        var subentry = entry.Value.Where(x => x.contentFlags.HasFlag(ContentFlags.LowViolence) == false && x.localeFlags.HasFlag(LocaleFlags.All_WoW) || x.localeFlags.HasFlag(LocaleFlags.enUS)).FirstOrDefault();

                        if (subentry.md5 == null)
                            subentry = entry.Value.First();

                        if (args[0] == "extractfilesbyfnamelist")
                        {
                            if (nameList.ContainsKey(subentry.lookup))
                            {
                                var cleanContentHash = Convert.ToHexString(subentry.md5).ToLower();

                                if (encodingList.TryGetValue(cleanContentHash, out List<string> value))
                                {
                                    value.Add(nameList[subentry.lookup]);
                                }
                                else
                                {
                                    encodingList.Add(cleanContentHash, [nameList[subentry.lookup]]);
                                }
                            }
                        }
                        else if (args[0] == "extractfilesbyfdidlist")
                        {
                            if (fdidList.ContainsKey(subentry.fileDataID))
                            {
                                var cleanContentHash = Convert.ToHexString(subentry.md5).ToLower();

                                if (encodingList.TryGetValue(cleanContentHash, out List<string> value))
                                {
                                    value.Add(fdidList[subentry.fileDataID]);
                                }
                                else
                                {
                                    encodingList.Add(cleanContentHash, [fdidList[subentry.fileDataID]]);
                                }

                                chashList.TryAdd(subentry.fileDataID, cleanContentHash);
                            }
                        }
                    }

                    var fileList = new Dictionary<string, List<string>>();

                    Console.WriteLine("Looking up " + encodingList.Count + " files in encoding..");
                    foreach (var encodingEntry in encoding.aEntries)
                    {
                        string target = "";

                        if (encodingList.ContainsKey(encodingEntry.cKey.ToLower()))
                        {
                            target = encodingEntry.eKeys[0].ToLower();
                            //Console.WriteLine(target);
                            foreach (var subName in encodingList[encodingEntry.cKey.ToLower()])
                            {
                                if (fileList.TryGetValue(target, out List<string> value))
                                {
                                    value.Add(subName);
                                }
                                else
                                {
                                    fileList.Add(target, [subName]);
                                }
                            }
                            encodingList.Remove(encodingEntry.cKey.ToLower());
                        }
                    }

                    var archivedFileList = new Dictionary<string, Dictionary<string, List<string>>>();
                    var unarchivedFileList = new Dictionary<string, List<string>>();

                    Console.WriteLine("Looking up in indexes..");
                    foreach (var fileEntry in fileList)
                    {
                        if (!indexDictionary.TryGetValue(fileEntry.Key.ToUpper(), out IndexEntry entry))
                        {
                            unarchivedFileList.Add(fileEntry.Key, fileEntry.Value);
                            continue;
                        }

                        var index = cdnConfig.archives[entry.index];
                        if (!archivedFileList.TryGetValue(index, out Dictionary<string, List<string>> value))
                        {
                            value = [];
                            archivedFileList.Add(index, value);
                        }

                        value.Add(fileEntry.Key, fileEntry.Value);
                    }

                    var extractedFiles = 0;
                    var totalFiles = fileList.Count;

                    if (unarchivedFileList.Count > 0)
                        Console.WriteLine("Extracting " + unarchivedFileList.Count + " unarchived files..");

                    foreach (var fileEntry in unarchivedFileList)
                    {
                        var target = fileEntry.Key;

                        foreach (var filename in fileEntry.Value)
                        {
                            if (!Directory.Exists(Path.Combine(basedir, Path.GetDirectoryName(filename))))
                            {
                                Directory.CreateDirectory(Path.Combine(basedir, Path.GetDirectoryName(filename)));
                            }
                        }

                        var unarchivedName = Path.Combine(cdn.cacheDir, "tpr", product, "data", target[0] + "" + target[1], target[2] + "" + target[3], target);
                        if (File.Exists(unarchivedName))
                        {
                            foreach (var filename in fileEntry.Value)
                            {
                                try
                                {
                                    if (product == "wowdev")
                                    {
                                        File.WriteAllBytes(Path.Combine(basedir, filename), BLTE.Parse(BLTE.DecryptFile(target, File.ReadAllBytes(unarchivedName), "wowdevalpha")));
                                    }
                                    else
                                    {
                                        File.WriteAllBytes(Path.Combine(basedir, filename), BLTE.Parse(File.ReadAllBytes(unarchivedName)));
                                    }
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine(e.Message);
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine("Unarchived file does not exist " + unarchivedName + ", cannot extract " + string.Join(',', fileEntry.Value));
                        }

                        extractedFiles++;

                        if (extractedFiles % 100 == 0)
                        {
                            Console.WriteLine("[" + DateTime.Now.ToString() + "] Extracted " + extractedFiles + " out of " + totalFiles + " files");
                        }
                    }

                    foreach (var archiveEntry in archivedFileList)
                    {
                        var archiveName = Path.Combine(cdn.cacheDir, "tpr", product, "data", archiveEntry.Key[0] + "" + archiveEntry.Key[1], archiveEntry.Key[2] + "" + archiveEntry.Key[3], archiveEntry.Key);
                        Console.WriteLine("[" + DateTime.Now.ToString() + "] Extracting " + archiveEntry.Value.Count + " files from archive " + archiveEntry.Key + "..");

                        using (var stream = new MemoryStream(await cdn.Get("tpr/" + product + "/data/" + archiveEntry.Key[0] + "" + archiveEntry.Key[1] + "/" + archiveEntry.Key[2] + "" + archiveEntry.Key[3] + "/" + archiveEntry.Key), true))
                        {
                            foreach (var fileEntry in archiveEntry.Value)
                            {
                                var target = fileEntry.Key;

                                foreach (var filename in fileEntry.Value)
                                {
                                    if (!Directory.Exists(Path.Combine(basedir, Path.GetDirectoryName(filename))))
                                    {
                                        Directory.CreateDirectory(Path.Combine(basedir, Path.GetDirectoryName(filename)));
                                    }
                                }

                                if (indexDictionary.TryGetValue(target.ToUpper(), out IndexEntry entry))
                                {
                                    foreach (var filename in fileEntry.Value)
                                    {
                                        try
                                        {
                                            stream.Seek(entry.offset, SeekOrigin.Begin);

                                            if (entry.offset > stream.Length || entry.offset + entry.size > stream.Length)
                                            {
                                                throw new Exception("File is beyond archive length, incomplete archive!");
                                            }

                                            var archiveBytes = new byte[entry.size];
                                            stream.Read(archiveBytes, 0, (int)entry.size);
                                            File.WriteAllBytes(Path.Combine(basedir, filename), BLTE.Parse(archiveBytes));
                                        }
                                        catch (Exception e)
                                        {
                                            Console.WriteLine(e.Message);
                                        }
                                    }
                                }
                                else
                                {
                                    Console.WriteLine("!!!!! Unable to find " + fileEntry.Key + " (" + fileEntry.Value[0] + ") in archives!");
                                }

                                extractedFiles++;

                                if (extractedFiles % 1000 == 0)
                                {
                                    Console.WriteLine("[" + DateTime.Now.ToString() + "] Extracted " + extractedFiles + " out of " + totalFiles + " files");
                                }
                            }
                        }
                    }

                    Environment.Exit(0);
                }
                if (args[0] == "forcebuild")
                {
                    if (args.Length == 4)
                    {
                        checkPrograms = [args[1]];
                        backupPrograms = [args[1]];
                        overrideBuildconfig = args[2];
                        overrideCDNconfig = args[3];
                        overrideVersions = true;
                    }
                }
                if (args[0] == "forceprogram")
                {
                    if (args.Length == 2)
                    {
                        checkPrograms = [args[1]];
                        backupPrograms = [args[1]];
                    }
                }
                if (args[0] == "dumpencrypted")
                {
                    if (args.Length != 3) throw new Exception("Not enough arguments. Need mode, product, buildconfig");

                    if (args[1] != "wow")
                    {
                        Console.WriteLine("Only WoW is currently supported due to root/fileDataID usage");
                        return;
                    }

                    cdns = GetCDNs(args[1]);

                    buildConfig = GetBuildConfig(cdns.entries[0].path, args[2]);

                    encoding = GetEncoding(cdns.entries[0].path, buildConfig.encoding[1], 0, true).Result;

                    var encryptedKeys = new Dictionary<string, string>();
                    var encryptedSizes = new Dictionary<string, ulong>();
                    foreach (var entry in encoding.bEntries)
                    {
                        var stringBlockEntry = encoding.stringBlockEntries[entry.Value.stringIndex];
                        if (stringBlockEntry.Contains("e:"))
                        {
                            encryptedKeys.Add(entry.Key, stringBlockEntry);
                            encryptedSizes.Add(entry.Key, entry.Value.compressedSize);
                        }
                    }

                    string rootKey = "";
                    var encryptedContentHashes = new Dictionary<string, List<string>>();
                    var encryptedContentSizes = new Dictionary<string, ulong>();
                    foreach (var entry in encoding.aEntries)
                    {
                        for (var i = 0; i < entry.eKeys.Count; i++)
                        {
                            if (encryptedKeys.ContainsKey(entry.eKeys[i]))
                            {
                                if (encryptedContentHashes.TryGetValue(entry.cKey, out List<string> value))
                                {
                                    value.Add(encryptedKeys[entry.eKeys[i]]);
                                }
                                else
                                {
                                    encryptedContentHashes.Add(entry.cKey, [encryptedKeys[entry.eKeys[i]]]);
                                    encryptedContentSizes.Add(entry.cKey, entry.size);
                                }
                            }

                            if (entry.cKey.Equals(buildConfig.root, StringComparison.OrdinalIgnoreCase)) { rootKey = entry.eKeys[i].ToLower(); }
                        }
                    }

                    root = GetRoot(cdns.entries[0].path, rootKey, true);

                    foreach (var entry in root.entriesFDID)
                    {
                        foreach (var subentry in entry.Value)
                        {
                            var md5string = Convert.ToHexString(subentry.md5);
                            if (encryptedContentHashes.TryGetValue(md5string, out List<string> stringBlocks))
                            {
                                foreach (var rawStringBlock in stringBlocks)
                                {
                                    var stringBlock = rawStringBlock;
                                    var keyList = new List<string>();
                                    while (stringBlock.Contains("e:"))
                                    {
                                        var keyName = stringBlock.Substring(stringBlock.IndexOf("e:{") + 3, 16);
                                        if (!keyList.Contains(keyName))
                                        {
                                            keyList.Add(keyName);
                                        }
                                        stringBlock = stringBlock.Remove(stringBlock.IndexOf("e:{"), 19);
                                    }

                                    Console.WriteLine(subentry.fileDataID + " " + string.Join(',', keyList) + " " + rawStringBlock + " " + encryptedContentSizes[md5string]);
                                }
                            }
                            break;
                        }
                    }

                    Environment.Exit(0);
                }
                if (args[0] == "dumpbadlyencrypted")
                {
                    if (args.Length != 3) throw new Exception("Not enough arguments. Need mode, product, buildconfig");

                    if (args[1] != "wow")
                    {
                        Console.WriteLine("Only WoW is currently supported due to root/fileDataID usage");
                        return;
                    }

                    cdns = GetCDNs(args[1]);

                    buildConfig = GetBuildConfig(cdns.entries[0].path, args[2]);
                    encoding = GetEncoding(cdns.entries[0].path, buildConfig.encoding[1], 0, true).Result;

                    var encryptedKeys = new HashSet<string>();
                    var notEncryptedKeys = new HashSet<string>();
                    foreach (var entry in encoding.bEntries)
                    {
                        var stringBlockEntry = encoding.stringBlockEntries[entry.Value.stringIndex];
                        if (stringBlockEntry.Contains("e:"))
                        {
                            encryptedKeys.Add(entry.Key);
                        }
                        else
                        {
                            notEncryptedKeys.Add(entry.Key);
                        }
                    }

                    string rootKey = "";
                    var encryptedContentHashes = new HashSet<string>();

                    foreach (var entry in encoding.aEntries)
                    {
                        for (var i = 0; i < entry.eKeys.Count; i++)
                        {
                            if (encryptedKeys.Contains(entry.eKeys[i]) && !encryptedContentHashes.Contains(entry.cKey))
                            {
                                encryptedContentHashes.Add(entry.cKey);
                            }

                            if (entry.cKey.Equals(buildConfig.root, StringComparison.OrdinalIgnoreCase)) { rootKey = entry.eKeys[i].ToLower(); }
                        }

                        for (var i = 0; i < entry.eKeys.Count; i++)
                        {
                            if (!encryptedKeys.Contains(entry.eKeys[i]))
                            {
                                encryptedContentHashes.Remove(entry.cKey);
                            }
                        }
                    }

                    root = GetRoot(cdns.entries[0].path, rootKey, true);

                    foreach (var entry in root.entriesFDID)
                    {
                        foreach (var subentry in entry.Value)
                        {
                            var contenthash = Convert.ToHexString(subentry.md5);

                            if (encryptedContentHashes.Contains(contenthash))
                            {
                                break;
                            }

                            if (subentry.contentFlags.HasFlag(ContentFlags.Encrypted))
                            {
                                Console.WriteLine(subentry.fileDataID);
                            }
                        }
                    }

                    Environment.Exit(0);
                }
                if (args[0] == "dumpsizes")
                {
                    if (args.Length != 3) throw new Exception("Not enough arguments. Need mode, product, buildconfig");

                    if (args[1] != "wow")
                    {
                        Console.WriteLine("Only WoW is currently supported due to root/fileDataID usage");
                        return;
                    }

                    cdns = GetCDNs(args[1]);

                    buildConfig = GetBuildConfig(cdns.entries[0].path, args[2]);

                    encoding = GetEncoding(cdns.entries[0].path, buildConfig.encoding[1], 0, true).Result;

                    foreach (var entry in encoding.aEntries)
                    {
                        Console.WriteLine(entry.cKey.ToLower() + " " + entry.size);
                    }

                    Environment.Exit(0);
                }
                if (args[0] == "dumprawfile")
                {
                    if (args.Length < 2) throw new Exception("Not enough arguments. Need mode, path, (numbytes)");

                    var file = BLTE.Parse(File.ReadAllBytes(args[1]));

                    if (args.Length == 3)
                    {
                        file = [.. file.Take(int.Parse(args[2]))];
                    }

                    Console.Write(Encoding.UTF8.GetString(file));
                    Environment.Exit(0);
                }
                if (args[0] == "dumprawfiletofile" || File.Exists(args[0]))
                {
                    if (args.Length == 1)
                    {
                        File.WriteAllBytes(args[0] + ".dump", BLTE.Parse(File.ReadAllBytes(args[0])));
                    }
                    else if (args.Length == 3)
                    {
                        File.WriteAllBytes(args[2], BLTE.Parse(File.ReadAllBytes(args[1])));
                    }
                    else
                    {
                        throw new Exception("Not enough arguments. Need mode, path, outfile");
                    }
                    Environment.Exit(0);
                }
                if (args[0] == "dumpencrypteddirtodir")
                {

                    if (args.Length < 3) throw new Exception("Not enough arguments. Need mode, src, dest");

                    var whiteList = new List<string>() { "" };
                    var allFiles = Directory.GetFiles(args[1], "*", SearchOption.AllDirectories).ToList();
                    allFiles.Sort();

                    var fileCount = 0;
                    var totalCount = allFiles.Count;
                    if (!args[1].Contains("wowdev"))
                    {
                        throw new Exception("unk encryptedproduct");
                    }

                    Parallel.ForEach(allFiles, new ParallelOptions { MaxDegreeOfParallelism = 4 }, file =>
                    {
                        //if (!file.EndsWith(".index"))
                        //    continue;

                        var newName = file.Replace(args[1], args[2]);

                        if (File.Exists(newName))
                        {
                            var newFileInfo = new FileInfo(newName);
                            var oldFileInfo = new FileInfo(file);

                            if (oldFileInfo.Length != newFileInfo.Length)
                            {
                                Console.WriteLine("Length mismatch for " + newName + ", re-extracting");
                            }
                            else
                            {
                                fileCount++;
                                return;
                            }
                        }

                        Console.WriteLine("[" + fileCount + "/" + totalCount + "] " + newName);

                        Directory.CreateDirectory(Path.GetDirectoryName(newName));

                        try
                        {
                            File.WriteAllBytes(newName, BLTE.DecryptFile(Path.GetFileNameWithoutExtension(file), File.ReadAllBytes(file), "wowdev3"));
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Failed to decrypt/write " + Path.GetFileNameWithoutExtension(newName) + ": " + e.Message);
                        }

                        fileCount++;
                    });

                    Environment.Exit(0);
                }
                if (args[0] == "dumpencryptedfiletofile")
                {
                    if (args.Length < 3) throw new Exception("Not enough arguments. Need mode, path, dest");

                    byte[] fileBytes = File.ReadAllBytes(args[1]);

                    if (args[1].Contains("wowdev"))
                    {
                        fileBytes = BLTE.DecryptFile(Path.GetFileNameWithoutExtension(args[1]), fileBytes, "wowdevalpha");
                    }
                    else
                    {
                        throw new Exception("unk encryptedproduct");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(args[2]));
                    File.WriteAllBytes(args[2], fileBytes);
                    Environment.Exit(0);
                }
                if (args[0] == "dumprawinstall")
                {
                    var install = new InstallFile();

                    byte[] content = File.ReadAllBytes(args[1]);


                    using (BinaryReader bin = new(new MemoryStream(BLTE.Parse(content))))
                    {
                        if (Encoding.UTF8.GetString(bin.ReadBytes(2)) != "IN") { throw new Exception("Error while parsing install file. Did BLTE header size change?"); }

                        bin.ReadByte();

                        install.hashSize = bin.ReadByte();
                        if (install.hashSize != 16) throw new Exception("Unsupported install hash size!");

                        install.numTags = bin.ReadUInt16(true);
                        install.numEntries = bin.ReadUInt32(true);

                        int bytesPerTag = ((int)install.numEntries + 7) / 8;

                        install.tags = new InstallTagEntry[install.numTags];

                        for (var i = 0; i < install.numTags; i++)
                        {
                            install.tags[i].name = bin.ReadCString();
                            install.tags[i].type = bin.ReadUInt16(true);

                            var filebits = bin.ReadBytes(bytesPerTag);

                            for (int j = 0; j < bytesPerTag; j++)
                                filebits[j] = (byte)((filebits[j] * 0x0202020202 & 0x010884422010) % 1023);

                            install.tags[i].files = new BitArray(filebits);
                        }

                        install.entries = new InstallFileEntry[install.numEntries];

                        for (var i = 0; i < install.numEntries; i++)
                        {
                            install.entries[i].name = bin.ReadCString();
                            install.entries[i].contentHash = bin.ReadBytes(install.hashSize);
                            install.entries[i].size = bin.ReadUInt32(true);
                            install.entries[i].tags = [];
                            for (var j = 0; j < install.numTags; j++)
                            {
                                if (install.tags[j].files[i] == true)
                                {
                                    install.entries[i].tags.Add(install.tags[j].type + "=" + install.tags[j].name);
                                }
                            }
                        }
                    }

                    foreach (var entry in install.entries)
                    {
                        Console.WriteLine(entry.name + " (size: " + entry.size + ", md5: " + Convert.ToHexString(entry.contentHash).ToLower() + ", tags: " + string.Join(",", entry.tags) + ")");
                    }
                    Environment.Exit(0);
                }
                if (args[0] == "dumpindex")
                {
                    if (args.Length < 3) throw new Exception("Not enough arguments. Need mode, product, hash, (folder)");

                    cdns = GetCDNs(args[1]);

                    var folder = "data";

                    if (args.Length == 4)
                    {
                        folder = args[3];
                    }

                    var index = ParseIndex(cdns.entries[0].path + "/", args[2], folder);

                    foreach (var entry in index)
                    {
                        Console.WriteLine(entry.Key + " " + entry.Value.size);
                    }
                    Environment.Exit(0);
                }
                if (args[0] == "partialdl")
                {
                    fullDownload = false;
                }
                if (args[0] == "dumparchive")
                {
                    var indexContent = File.ReadAllBytes(args[1]);
                    using (var ms = new MemoryStream(indexContent))
                    using (BinaryReader bin = new(ms))
                    {
                        int indexEntries = indexContent.Length / 4096;

                        for (var b = 0; b < indexEntries; b++)
                        {
                            for (var bi = 0; bi < 170; bi++)
                            {
                                var headerHash = Convert.ToHexString(bin.ReadBytes(16));

                                var entry = new IndexEntry()
                                {
                                    index = 0,
                                    size = bin.ReadUInt32(true),
                                    offset = bin.ReadUInt32(true)
                                };
                                if (!indexDictionary.ContainsKey(headerHash) && headerHash != "00000000000000000000000000000000")
                                {
                                    if (!indexDictionary.TryAdd(headerHash, entry))
                                    {
                                        Console.WriteLine("Duplicate index entry for " + headerHash + " " + "(size: " + entry.size + ", offset: " + entry.offset);
                                    }
                                }
                            }
                            bin.ReadBytes(16);
                        }
                    }

                    var outPath = Path.Combine(Path.GetDirectoryName(args[1]), Path.GetFileNameWithoutExtension(args[1]) + "_extract");
                    if (!Directory.Exists(outPath))
                        Directory.CreateDirectory(outPath);

                    var archivePath = Path.Combine(Path.GetDirectoryName(args[1]), Path.GetFileNameWithoutExtension(args[1]));
                    using (var ms = new MemoryStream(File.ReadAllBytes(archivePath)))
                    using (var bin = new BinaryReader(ms))
                    {
                        foreach (var entry in indexDictionary)
                        {
                            bin.BaseStream.Seek(entry.Value.offset, SeekOrigin.Begin);
                            var data = BLTE.Parse(bin.ReadBytes((int)entry.Value.size));
                            var outFilePath = Path.Combine(outPath, entry.Key);
                            if (data[0] == 'B' && data[1] == 'L' && data[2] == 'P' && data[3] == '2')
                                outFilePath += ".blp";
                            else if (data[0] == 'M' && data[1] == 'Z')
                                outFilePath += ".exe";
                            else if (data[0] == 'W' && data[1] == 'D' && data[2] == 'C')
                                outFilePath += ".db2";
                            else if (data[0] == 'M' && data[1] == '3')
                                outFilePath += ".m3lib";

                            File.WriteAllBytes(outFilePath, data);
                        }
                    }

                    return;
                }
            }
            #endregion

            // Load programs
            checkPrograms ??= SettingsManager.checkProducts;
            backupPrograms ??= SettingsManager.backupProducts;

            var finishedCDNConfigs = new List<string>();
            var finishedEncodings = new List<string>();

            var downloadThrottler = new SemaphoreSlim(initialCount: 10);

            foreach (string program in checkPrograms)
            {
                var archiveSizes = new Dictionary<string, uint>();

                if (File.Exists("archiveSizes.txt"))
                {
                    foreach (var line in File.ReadAllLines("archiveSizes.txt"))
                    {
                        var split = line.Split(' ');
                        if (uint.TryParse(split[1], out uint archiveSize))
                        {
                            archiveSizes.Add(split[0], archiveSize);
                        }
                    }
                }

                Console.WriteLine("Using program " + program);

                try
                {
                    cdns = GetCDNs(program);
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error parsing CDNs: " + e.Message);
                }

                if (!overrideVersions)
                {
                    try
                    {
                        versions = GetVersions(program);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Error parsing versions: " + e.Message);
                    }

                    if (versions.entries == null || versions.entries.Length == 0) { Console.WriteLine("Invalid versions file for " + program + ", skipping!"); continue; }

                    Console.WriteLine("Loaded " + versions.entries.Length + " versions");

                    // Retrieve keyring
                    if (!string.IsNullOrEmpty(versions.entries[0].keyRing))
                        await cdn.Get(cdns.entries[0].path + "/config/" + versions.entries[0].keyRing[0] + versions.entries[0].keyRing[1] + "/" + versions.entries[0].keyRing[2] + versions.entries[0].keyRing[3] + "/" + versions.entries[0].keyRing);
                }

                if (cdns.entries == null || cdns.entries.Length == 0) { Console.WriteLine("Invalid CDNs file for " + program + ", skipping!"); continue; }

                Console.WriteLine("Loaded " + cdns.entries.Length + " cdns");
                
                var decryptionKeyName = "";

                if (versions.entries != null)
                {
                    if (!string.IsNullOrEmpty(versions.entries[0].productConfig))
                    {
                        productConfig = GetProductConfig(cdns.entries[0].configPath + "/", versions.entries[0].productConfig);
                    }


                    if (productConfig.decryptionKeyName != null && productConfig.decryptionKeyName != string.Empty)
                    {
                        decryptionKeyName = productConfig.decryptionKeyName;
                    }

                    cdn.decryptionKeyName = decryptionKeyName;

                    // Retrieve all buildconfigs
                    for (var i = 0; i < versions.entries.Length; i++)
                    {
                        GetBuildConfig(cdns.entries[0].path + "/", versions.entries[i].buildConfig);
                    }
                }

                if (overrideVersions && !string.IsNullOrEmpty(overrideBuildconfig))
                {
                    buildConfig = GetBuildConfig(cdns.entries[0].path + "/", overrideBuildconfig);
                }
                else
                {
                    buildConfig = GetBuildConfig(cdns.entries[0].path + "/", versions.entries[0].buildConfig);
                }

                if (string.IsNullOrWhiteSpace(buildConfig.buildName))
                {
                    Console.WriteLine("Missing buildname in buildConfig for " + program + ", setting build name!");
                    buildConfig.buildName = "UNKNOWN";
                }

                var currentCDNConfig = "";

                if (overrideVersions && !string.IsNullOrEmpty(overrideCDNconfig))
                {
                    cdnConfig = GetCDNconfig(cdns.entries[0].path + "/", overrideCDNconfig);
                    currentCDNConfig = overrideCDNconfig;
                }
                else
                {
                    cdnConfig = GetCDNconfig(cdns.entries[0].path + "/", versions.entries[0].cdnConfig);
                    currentCDNConfig = versions.entries[0].cdnConfig;
                }

                if (cdnConfig.builds != null)
                {
                    cdnBuildConfigs = new BuildConfigFile[cdnConfig.builds.Length];
                }
                else if (cdnConfig.archives != null)
                {
                    //Console.WriteLine("CDNConfig loaded, " + cdnConfig.archives.Count() + " archives");
                }
                else if (cdnConfig.fileIndex != null)
                {

                }
                else
                {
                    Console.WriteLine("Invalid cdnConfig for " + program + "!");
                    continue;
                }

                if (!backupPrograms.Contains(program))
                {
                    Console.WriteLine("No need to backup, moving on..");
                    continue;
                }

                if (!string.IsNullOrEmpty(decryptionKeyName) && cdnConfig.archives == null) // Let us ignore this whole encryption thing if archives are set, surely this will never break anything and it'll back it up perfectly fine.
                {
                    if (!File.Exists(decryptionKeyName + ".ak"))
                    {
                        Console.WriteLine("Decryption key is set and not available on disk, skipping.");
                        continue;
                    }
                }

                Console.Write("Downloading patch files..");
                try
                {
                    if (!string.IsNullOrEmpty(buildConfig.patch))
                        patch = GetPatch(cdns.entries[0].path + "/", buildConfig.patch, true);

                    if (!string.IsNullOrEmpty(buildConfig.patchConfig))
                        await cdn.Get(cdns.entries[0].path + "/config/" + buildConfig.patchConfig[0] + buildConfig.patchConfig[1] + "/" + buildConfig.patchConfig[2] + buildConfig.patchConfig[3] + "/" + buildConfig.patchConfig);

                    if (buildConfig.patchIndex != null && buildConfig.patchIndex.Length == 2 && !string.IsNullOrEmpty(buildConfig.patchIndex[1]))
                        await cdn.Get(cdns.entries[0].path + "/data/" + buildConfig.patchIndex[1][0] + buildConfig.patchIndex[1][1] + "/" + buildConfig.patchIndex[1][2] + buildConfig.patchIndex[1][3] + "/" + buildConfig.patchIndex[1]);

                }
                catch (Exception e)
                {
                    Console.WriteLine("Failed to download patch files: " + e.Message);
                }

                Console.Write("..done\n");

                if (!finishedCDNConfigs.Contains(currentCDNConfig))
                {
                    Console.WriteLine("CDN config " + currentCDNConfig + " has not been loaded yet, loading..");

                    if (cdnConfig.archives != null)
                    {
                        Console.Write("Loading " + cdnConfig.archives.Length + " indexes..");
                        try
                        {
                            GetIndexes(cdns.entries[0].path + "/", cdnConfig.archives);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Failed to get indexes: " + e.Message);
                        }
                        Console.Write("..done\n");

                        if (fullDownload)
                        {
                            Console.Write("Fetching and saving archive sizes..");

                            for (short i = 0; i < cdnConfig.archives.Length; i++)
                            {
                                var archive = cdnConfig.archives[i];
                                if (!archiveSizes.ContainsKey(archive))
                                {
                                    try
                                    {
                                        var remoteFileSize = await cdn.GetRemoteFileSize(cdns.entries[0].path + "/data/" + archive[0] + archive[1] + "/" + archive[2] + archive[3] + "/" + archive);
                                        archiveSizes.Add(archive, remoteFileSize);
                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine("Failed to get remote file size for " + archive + ": " + e.Message);
                                        archiveSizes.Add(archive, 0);
                                    }
                                }
                            }

                            var archiveSizesLines = new List<string>();
                            foreach (var archiveSize in archiveSizes)
                            {
                                archiveSizesLines.Add(archiveSize.Key + " " + archiveSize.Value);
                            }

                            await File.WriteAllLinesAsync("archiveSizes.txt", archiveSizesLines);

                            Console.WriteLine("..done");

                            Console.Write("Downloading " + cdnConfig.archives.Length + " archives..");

                            var archiveTasks = new List<Task>();
                            for (short i = 0; i < cdnConfig.archives.Length; i++)
                            {
                                var archive = cdnConfig.archives[i];
                                await downloadThrottler.WaitAsync();
                                archiveTasks.Add(
                                    Task.Run(async () =>
                                    {
                                        try
                                        {
                                            uint archiveSize = 0;
                                            if (archiveSizes.TryGetValue(archive, out uint value))
                                            {
                                                archiveSize = value;
                                            }

                                            await cdn.Get(cdns.entries[0].path + "/data/" + archive[0] + archive[1] + "/" + archive[2] + archive[3] + "/" + archive, false, false, archiveSize, true);
                                        }
                                        finally
                                        {
                                            downloadThrottler.Release();
                                        }
                                    }));
                            }
                            try
                            {
                                await Task.WhenAll(archiveTasks);
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine("Failed to download archives: " + e.Message);
                            }
                            Console.Write("..done\n");
                        }
                        else
                        {
                            Console.WriteLine("Not a full run, skipping archive downloads..");
                            if (!finishedCDNConfigs.Contains(currentCDNConfig)) { finishedCDNConfigs.Add(currentCDNConfig); }
                        }
                    }
                }

                string rootKey = "";
                string downloadKey = "";
                string installKey = "";
                Dictionary<string, string> hashes = [];

                if (buildConfig.encoding != null && buildConfig.encoding.Length == 2)
                {
                    //if (finishedEncodings.Contains(buildConfig.encoding[1]))
                    //{
                    //    Console.WriteLine("Encoding file " + buildConfig.encoding[1] + " already loaded, skipping rest of product loading..");
                    //    continue;
                    //}

                    Console.Write("Loading encoding..");

                    try
                    {
                        if (buildConfig.encodingSize == null || buildConfig.encodingSize.Length < 2)
                        {
                            encoding = await GetEncoding(cdns.entries[0].path + "/", buildConfig.encoding[1], 0);
                        }
                        else
                        {
                            encoding = await GetEncoding(cdns.entries[0].path + "/", buildConfig.encoding[1], int.Parse(buildConfig.encodingSize[1]));
                        }

                        finishedEncodings.Add(buildConfig.encoding[1]);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Fatal error occurred during encoding parsing: " + e.Message);
                    }

                    if (buildConfig.install.Length == 2)
                    {
                        installKey = buildConfig.install[1];
                    }

                    if (buildConfig.download.Length == 2)
                    {
                        downloadKey = buildConfig.download[1];
                    }

                    if (encoding.aEntries != null)
                    {
                        foreach (var entry in encoding.aEntries)
                        {
                            if (entry.cKey.Equals(buildConfig.root, StringComparison.OrdinalIgnoreCase)) { rootKey = entry.eKeys[0].ToLower(); }
                            if (downloadKey == "" && entry.cKey.Equals(buildConfig.download[0], StringComparison.OrdinalIgnoreCase)) { downloadKey = entry.eKeys[0].ToLower(); }
                            if (installKey == "" && entry.cKey.Equals(buildConfig.install[0], StringComparison.OrdinalIgnoreCase)) { installKey = entry.eKeys[0].ToLower(); }
                            hashes.TryAdd(entry.eKeys[0], entry.cKey);
                        }
                    }

                    Console.Write("..done\n");
                }


                if (program.StartsWith("wow")) // Only these are supported right now
                {
                    Console.Write("Loading root..");
                    if (rootKey == "")
                    {
                        Console.WriteLine("Unable to find root key in encoding!");
                    }
                    else
                    {
                        try
                        {
                            root = GetRoot(cdns.entries[0].path + "/", rootKey);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Error loading root: " + e.Message);
                        }
                    }
                    Console.Write("..done\n");

                    Console.Write("Loading download..");
                    if (downloadKey == "")
                    {
                        Console.WriteLine("Unable to find download key in encoding!");
                    }
                    else
                    {
                        try
                        {
                            download = GetDownload(cdns.entries[0].path + "/", downloadKey);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Error loading download: " + e.Message);
                        }
                    }
                    Console.Write("..done\n");

                    Console.Write("Loading install..");
                    try
                    {
                        if (installKey == "")
                        {
                            Console.WriteLine("Unable to find install key in encoding!");
                        }
                        else
                        {
                            try
                            {
                                install = GetInstall(cdns.entries[0].path + "/", installKey);
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine("Error loading install: " + e.Message);
                            }
                        }

                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Error loading install: " + e.Message);
                    }
                }
                Console.Write("..done\n");


                if (!fullDownload)
                {
                    Console.WriteLine("Not a full run, skipping rest of download..");
                    continue;
                }

                foreach (var entry in indexDictionary)
                {
                    hashes.Remove(entry.Key.ToUpper());
                }

                if (!finishedCDNConfigs.Contains(currentCDNConfig))
                {
                    if (!string.IsNullOrEmpty(cdnConfig.fileIndex))
                    {
                        Console.Write("Parsing file index..");
                        try
                        {
                            fileIndexList = ParseIndex(cdns.entries[0].path + "/", cdnConfig.fileIndex);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Unable to get file index: " + e.Message);
                        }
                        Console.Write("..done\n");
                    }

                    if (!string.IsNullOrEmpty(cdnConfig.fileIndex) && fileIndexList.Count > 0)
                    {
                        Console.Write("Downloading " + fileIndexList.Count + " unarchived files from file index..");

                        var fileIndexTasks = new List<Task>();
                        foreach (var entry in fileIndexList.Keys)
                        {
                            await downloadThrottler.WaitAsync();
                            fileIndexTasks.Add(
                                Task.Run(async () =>
                                {
                                    try
                                    {
                                        await cdn.Get(
                                            cdns.entries[0].path + "/data/" + entry[0] + entry[1] + "/" + entry[2] +
                                            entry[3] + "/" + entry, false, false, fileIndexList[entry].size, true);
                                    }
                                    finally
                                    {
                                        downloadThrottler.Release();
                                    }
                                }));
                        }

                        await Task.WhenAll(fileIndexTasks);

                        Console.Write("..done\n");
                    }

                    if (!string.IsNullOrEmpty(cdnConfig.patchFileIndex))
                    {
                        Console.Write("Parsing patch file index..");
                        try
                        {
                            patchFileIndexList = ParseIndex(cdns.entries[0].path + "/", cdnConfig.patchFileIndex, "patch");
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Error retrieving patch file index: " + e.Message);
                        }
                        Console.Write("..done\n");
                    }

                    if (!string.IsNullOrEmpty(cdnConfig.patchFileIndex) && SettingsManager.downloadPatchFiles && patchFileIndexList.Count > 0)
                    {
                        Console.Write("Downloading " + patchFileIndexList.Count + " unarchived patch files from patch file index..");

                        var patchFileTasks = new List<Task>();
                        foreach (var entry in patchFileIndexList.Keys)
                        {
                            await downloadThrottler.WaitAsync();
                            patchFileTasks.Add(
                                Task.Run(async () =>
                                {
                                    try
                                    {
                                        await cdn.Get(
                                            cdns.entries[0].path + "/patch/" + entry[0] + entry[1] + "/" + entry[2] +
                                            entry[3] + "/" + entry, false);
                                    }
                                    finally
                                    {
                                        downloadThrottler.Release();
                                    }
                                }));
                        }

                        await Task.WhenAll(patchFileTasks);

                        Console.Write("..done\n");
                    }

                    if (SettingsManager.downloadPatchFiles && cdnConfig.patchArchives != null)
                    {
                        Console.Write("Downloading " + cdnConfig.patchArchives.Length + " patch archives..");

                        var patchArchiveTasks = new List<Task>();
                        foreach (var archive in cdnConfig.patchArchives)
                        {
                            await downloadThrottler.WaitAsync();
                            patchArchiveTasks.Add(
                                Task.Run(async () =>
                                {
                                    try
                                    {
                                        await cdn.Get(
                                            cdns.entries[0].path + "/patch/" + archive[0] + archive[1] + "/" +
                                            archive[2] + archive[3] + "/" + archive, false, false, 0, true);
                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine("Failed to download patch archive: " + e.Message);
                                    }
                                    finally
                                    {
                                        downloadThrottler.Release();
                                    }
                                }));
                        }

                        await Task.WhenAll(patchArchiveTasks);
                        Console.Write("..done\n");

                        Console.Write("Downloading " + cdnConfig.patchArchives.Length + " patch archive indexes..");
                        GetPatchIndexes(cdns.entries[0].path + "/", cdnConfig.patchArchives);
                        Console.Write("..done\n");
                    }

                    finishedCDNConfigs.Add(currentCDNConfig);
                }

                // Unarchived files -- files in encoding but not in indexes. Can vary per build!
                Console.Write("Downloading " + hashes.Count + " unarchived files..");

                var unarchivedFileTasks = new List<Task>();
                foreach (var entry in hashes)
                {
                    await downloadThrottler.WaitAsync();
                    unarchivedFileTasks.Add(
                        Task.Run(async () =>
                        {
                            try
                            {
                                await cdn.Get(cdns.entries[0].path + "/data/" + entry.Key[0] + entry.Key[1] + "/" + entry.Key[2] + entry.Key[3] + "/" + entry.Key, false);
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine("Unable to download unarchived file: " + e.Message);
                            }
                            finally
                            {
                                downloadThrottler.Release();
                            }
                        }));
                }
                await Task.WhenAll(unarchivedFileTasks);
                Console.Write("..done\n");

                if (SettingsManager.downloadPatchFiles)
                {
                    if (cdnConfig.patchArchives != null)
                    {
                        if (patch.blocks != null)
                        {
                            var unarchivedPatchKeyList = new List<string>();
                            foreach (var block in patch.blocks)
                            {
                                foreach (var fileBlock in block.files)
                                {
                                    foreach (var patch in fileBlock.patches)
                                    {
                                        var pKey = Convert.ToHexString(patch.patchEncodingKey);
                                        if (!patchIndexDictionary.ContainsKey(pKey))
                                        {
                                            unarchivedPatchKeyList.Add(pKey);
                                        }
                                    }
                                }
                            }

                            if (unarchivedPatchKeyList.Count > 0)
                            {
                                Console.Write("Downloading " + unarchivedPatchKeyList.Count + " unarchived patch files..");

                                var unarchivedPatchFileTasks = new List<Task>();
                                foreach (var entry in unarchivedPatchKeyList)
                                {
                                    await downloadThrottler.WaitAsync();
                                    unarchivedPatchFileTasks.Add(
                                        Task.Run(async () =>
                                        {
                                            try
                                            {
                                                await cdn.Get(cdns.entries[0].path + "/patch/" + entry[0] + entry[1] + "/" + entry[2] + entry[3] + "/" + entry, false);
                                            }
                                            finally
                                            {
                                                downloadThrottler.Release();
                                            }
                                        }));
                                }
                                await Task.WhenAll(unarchivedPatchFileTasks);

                                Console.Write("..done\n");
                            }
                        }
                    }
                }

                GC.Collect();
            }
        }

        private static byte[] RetrieveFileBytes(string target, bool raw = false, string cdndir = "tpr/wow", bool tryUnarchived = false)
        {
            var unarchivedName = Path.Combine(cdn.cacheDir, cdndir, "data", target[0] + "" + target[1], target[2] + "" + target[3], target);

            if (File.Exists(unarchivedName))
            {
                if (!raw)
                {
                    if (cdndir == "tpr/wowdev")
                    {
                        return BLTE.Parse(BLTE.DecryptFile(target, File.ReadAllBytes(unarchivedName), "wowdevalpha"));
                    }
                    else if (cdndir == "tpr/hse")
                    {
                        return BLTE.Parse(BLTE.DecryptFile(target, File.ReadAllBytes(unarchivedName), "hse1"));
                    }
                    else
                    {
                        return BLTE.Parse(File.ReadAllBytes(unarchivedName));
                    }
                }
                else
                {
                    if (cdndir == "tpr/wowdev")
                    {
                        return BLTE.DecryptFile(target, File.ReadAllBytes(unarchivedName), "wowdevalpha");
                    }
                    else if (cdndir == "tpr/hse")
                    {
                        return BLTE.DecryptFile(target, File.ReadAllBytes(unarchivedName), "hse1");
                    }
                    else
                    {
                        return File.ReadAllBytes(unarchivedName);
                    }
                }
            }
            else
            {
                if (tryUnarchived)
                {
                    try
                    {
                        var file = cdn.Get(cdndir + "/" + "data" + "/" + target[0] + target[1] + "/" + target[2] + target[3] + "/" + target, true, false, 0, true).Result;
                        return BLTE.Parse(file);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Warn: Unable to retrieve file as unarchived from CDN: " + e.Message);
                    }
                }
            }

            if (!indexDictionary.TryGetValue(target.ToUpper(), out IndexEntry entry))
            {
                throw new Exception("Unable to find file in archives. File is not available!?");
            }
            else
            {
                var index = cdnConfig.archives[entry.index];

                using (BinaryReader bin = new(new MemoryStream(cdn.Get(cdndir + "/data/" + index[0] + "" + index[1] + "/" + index[2] + "" + index[3] + "/" + index).Result, true)))
                {
                    bin.BaseStream.Position = entry.offset;
                    try
                    {
                        if (!raw)
                        {
                            return BLTE.Parse(bin.ReadBytes((int)entry.size));
                        }
                        else
                        {
                            return bin.ReadBytes((int)entry.size);
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                    }
                }
            }

            return [];
        }

        private static CDNConfigFile GetCDNconfig(string url, string hash)
        {
            string content;
            var cdnConfig = new CDNConfigFile();

            try
            {
                content = Encoding.UTF8.GetString(cdn.Get(url + "/config/" + hash[0] + hash[1] + "/" + hash[2] + hash[3] + "/" + hash).Result);
            }
            catch (Exception e)
            {
                Console.WriteLine("Error retrieving CDN config: " + e.Message);
                return cdnConfig;
            }

            var cdnConfigLines = content.Split(["\n"], StringSplitOptions.RemoveEmptyEntries);

            for (var i = 0; i < cdnConfigLines.Length; i++)
            {
                if (cdnConfigLines[i].StartsWith('#') || cdnConfigLines[i].Length == 0) { continue; }
                var cols = cdnConfigLines[i].Split([" = "], StringSplitOptions.RemoveEmptyEntries);
                switch (cols[0])
                {
                    case "archives":
                        var archives = cols[1].Split(' ');
                        cdnConfig.archives = archives;
                        break;
                    case "archive-group":
                        cdnConfig.archiveGroup = cols[1];
                        break;
                    case "patch-archives":
                        if (cols.Length > 1)
                        {
                            var patchArchives = cols[1].Split(' ');
                            cdnConfig.patchArchives = patchArchives;
                        }
                        break;
                    case "patch-archive-group":
                        cdnConfig.patchArchiveGroup = cols[1];
                        break;
                    case "builds":
                        var builds = cols[1].Split(' ');
                        cdnConfig.builds = builds;
                        break;
                    case "file-index":
                        cdnConfig.fileIndex = cols[1];
                        break;
                    case "file-index-size":
                        cdnConfig.fileIndexSize = cols[1];
                        break;
                    case "patch-file-index":
                        cdnConfig.patchFileIndex = cols[1];
                        break;
                    case "patch-file-index-size":
                        cdnConfig.patchFileIndexSize = cols[1];
                        break;
                    default:
                        //Console.WriteLine("!!!!!!!! Unknown cdnconfig variable '" + cols[0] + "'");
                        break;
                }
            }

            return cdnConfig;
        }

        private static VersionsFile GetVersions(string program)
        {
            string content;
            var versions = new VersionsFile();

            using (HttpResponseMessage response = cdn.client.GetAsync(new Uri(baseUrl + "v2/products/" + program + "/" + "versions")).Result)
            {
                if (response.IsSuccessStatusCode)
                {
                    using (HttpContent res = response.Content)
                    {
                        content = res.ReadAsStringAsync().Result;
                    }
                }
                else
                {
                    Console.WriteLine("Error during retrieving HTTP versions: Received bad HTTP code " + response.StatusCode);
                    return versions;
                }
            }

            content = content.Replace("\0", "");
            var lines = content.Split(["\n"], StringSplitOptions.RemoveEmptyEntries);

            var lineList = new List<string>();

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i][0] != '#')
                {
                    lineList.Add(lines[i]);
                }
            }

            lines = [.. lineList];

            if (lines.Length > 0)
            {
                versions.entries = new VersionsEntry[lines.Length - 1];

                var cols = lines[0].Split('|');

                for (var c = 0; c < cols.Length; c++)
                {
                    var friendlyName = cols[c].Split('!').ElementAt(0);

                    for (var i = 1; i < lines.Length; i++)
                    {
                        var row = lines[i].Split('|');

                        switch (friendlyName)
                        {
                            case "Region":
                                versions.entries[i - 1].region = row[c];
                                break;
                            case "BuildConfig":
                                versions.entries[i - 1].buildConfig = row[c];
                                break;
                            case "CDNConfig":
                                versions.entries[i - 1].cdnConfig = row[c];
                                break;
                            case "Keyring":
                            case "KeyRing":
                                versions.entries[i - 1].keyRing = row[c];
                                break;
                            case "BuildId":
                                versions.entries[i - 1].buildId = row[c];
                                break;
                            case "VersionName":
                            case "VersionsName":
                                versions.entries[i - 1].versionsName = row[c].Trim('\r');
                                break;
                            case "ProductConfig":
                                versions.entries[i - 1].productConfig = row[c];
                                break;
                            default:
                                Console.WriteLine("!!!!!!!! Unknown versions variable '" + friendlyName + "'");
                                break;
                        }
                    }
                }
            }

            return versions;
        }

        private static CdnsFile GetCDNs(string program)
        {
            string content;

            var cdns = new CdnsFile();

            if (program == "gryphon")
            {
                cdns.entries = new CdnsEntry[1];
                cdns.entries[0].hosts = ["http://cdn.blizzard.com", "http://blzddist1-a.akamaihd.net"];
                cdns.entries[0].path = "tpr/gryphon";
                cdns.entries[0].configPath = "configs/data/";
                return cdns;
            }

            using (HttpResponseMessage response = cdn.client.GetAsync(new Uri(baseUrl + "v2/products/" + program + "/" + "cdns")).Result)
            {
                if (response.IsSuccessStatusCode)
                {
                    using (HttpContent res = response.Content)
                    {
                        content = res.ReadAsStringAsync().Result;
                    }
                }
                else
                {
                    Console.WriteLine("Error during retrieving HTTP cdns: Received bad HTTP code " + response.StatusCode);
                    return cdns;
                }
            }

            var lines = content.Split(["\n", "\r"], StringSplitOptions.RemoveEmptyEntries);

            var lineList = new List<string>();

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i][0] != '#')
                {
                    lineList.Add(lines[i]);
                }
            }

            lines = [.. lineList];

            if (lines.Length > 0)
            {
                cdns.entries = new CdnsEntry[lines.Length - 1];

                var cols = lines[0].Split('|');

                for (var c = 0; c < cols.Length; c++)
                {
                    var friendlyName = cols[c].Split('!').ElementAt(0);

                    for (var i = 1; i < lines.Length; i++)
                    {
                        var row = lines[i].Split('|');

                        switch (friendlyName)
                        {
                            case "Name":
                                cdns.entries[i - 1].name = row[c];
                                break;
                            case "Path":
                                cdns.entries[i - 1].path = row[c];
                                break;
                            case "Hosts":
                                var hosts = row[c].Split(' ');
                                cdns.entries[i - 1].hosts = new string[hosts.Length];
                                for (var h = 0; h < hosts.Length; h++)
                                {
                                    cdns.entries[i - 1].hosts[h] = hosts[h];
                                }
                                break;
                            case "ConfigPath":
                                cdns.entries[i - 1].configPath = row[c];
                                break;
                            default:
                                //Console.WriteLine("!!!!!!!! Unknown cdns variable '" + friendlyName + "'");
                                break;
                        }
                    }
                }

                foreach (var subcdn in cdns.entries)
                {
                    foreach (var cdnHost in subcdn.hosts)
                    {
                        if (!cdn.cdnList.Contains(cdnHost))
                        {
                            //cdn.cdnList.Add(cdnHost);
                        }
                    }
                }
            }

            return cdns;
        }

        private static GameBlobFile GetProductConfig(string url, string hash)
        {
            string content;

            var gblob = new GameBlobFile();

            try
            {
                content = Encoding.UTF8.GetString(cdn.Get(url + hash[0] + hash[1] + "/" + hash[2] + hash[3] + "/" + hash).Result);
            }
            catch (Exception e)
            {
                Console.WriteLine("Error retrieving product config: " + e.Message);
                return gblob;
            }

            if (string.IsNullOrEmpty(content))
            {
                Console.WriteLine("Error reading product config!");
                return gblob;
            }

            dynamic json = Newtonsoft.Json.JsonConvert.DeserializeObject(content);
            if (json.all.config.decryption_key_name != null)
                gblob.decryptionKeyName = json.all.config.decryption_key_name.Value;

            return gblob;
        }

        private static BuildConfigFile GetBuildConfig(string url, string hash)
        {
            string content;

            var buildConfig = new BuildConfigFile();

            try
            {
                if (!File.Exists("fakebuildconfig"))
                {
                    content = Encoding.UTF8.GetString(cdn.Get(url + "/config/" + hash[0] + hash[1] + "/" + hash[2] + hash[3] + "/" + hash).Result);
                }
                else
                {
                    Console.WriteLine("!!!!!!!!! LOADING FAKE BUILDCONFIG");
                    content = File.ReadAllText("fakebuildconfig").Replace("\r", "");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error retrieving build config: " + e.Message);
                return buildConfig;
            }

            if (string.IsNullOrEmpty(content) || !content.StartsWith("# Build"))
            {
                Console.WriteLine("Error reading build config");
                return buildConfig;
            }

            var lines = content.Split(["\n"], StringSplitOptions.RemoveEmptyEntries);

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith('#') || lines[i].Length == 0) { continue; }
                var cols = lines[i].Split([" = "], StringSplitOptions.RemoveEmptyEntries);
                switch (cols[0])
                {
                    case "root":
                        buildConfig.root = cols[1];
                        break;
                    case "download":
                        buildConfig.download = cols[1].Split(' ');
                        break;
                    case "install":
                        buildConfig.install = cols[1].Split(' ');
                        break;
                    case "encoding":
                        buildConfig.encoding = cols[1].Split(' ');
                        break;
                    case "encoding-size":
                        var encodingSize = cols[1].Split(' ');
                        buildConfig.encodingSize = encodingSize;
                        break;
                    case "size":
                        buildConfig.size = cols[1].Split(' ');
                        break;
                    case "size-size":
                        buildConfig.sizeSize = cols[1].Split(' ');
                        break;
                    case "build-name":
                        buildConfig.buildName = cols[1];
                        break;
                    case "build-playbuild-installer":
                        buildConfig.buildPlaybuildInstaller = cols[1];
                        break;
                    case "build-product":
                        buildConfig.buildProduct = cols[1];
                        break;
                    case "build-uid":
                        buildConfig.buildUid = cols[1];
                        break;
                    case "patch":
                        buildConfig.patch = cols[1];
                        break;
                    case "patch-size":
                        buildConfig.patchSize = cols[1];
                        break;
                    case "patch-config":
                        buildConfig.patchConfig = cols[1];
                        break;
                    case "build-branch": // Overwatch
                        buildConfig.buildBranch = cols[1];
                        break;
                    case "build-num": // Agent
                    case "build-number": // Overwatch
                    case "build-version": // Catalog
                        buildConfig.buildNumber = cols[1];
                        break;
                    case "build-attributes": // Agent
                        buildConfig.buildAttributes = cols[1];
                        break;
                    case "build-comments": // D3
                        buildConfig.buildComments = cols[1];
                        break;
                    case "build-creator": // D3
                        buildConfig.buildCreator = cols[1];
                        break;
                    case "build-fixed-hash": // S2
                        buildConfig.buildFixedHash = cols[1];
                        break;
                    case "build-replay-hash": // S2
                        buildConfig.buildReplayHash = cols[1];
                        break;
                    case "build-t1-manifest-version":
                        buildConfig.buildManifestVersion = cols[1];
                        break;
                    case "install-size":
                        buildConfig.installSize = cols[1].Split(' ');
                        break;
                    case "download-size":
                        buildConfig.downloadSize = cols[1].Split(' ');
                        break;
                    case "build-partial-priority":
                    case "partial-priority":
                        buildConfig.partialPriority = cols[1];
                        break;
                    case "partial-priority-size":
                        buildConfig.partialPrioritySize = cols[1];
                        break;
                    case "build-signature-file":
                        buildConfig.buildSignatureFile = cols[1];
                        break;
                    case "patch-index":
                        buildConfig.patchIndex = cols[1].Split(' ');
                        break;
                    case "patch-index-size":
                        buildConfig.patchIndexSize = cols[1].Split(' ');
                        break;
                    default:
                        //Console.WriteLine("!!!!!!!! Unknown buildconfig variable '" + cols[0] + "'");
                        break;
                }
            }

            return buildConfig;
        }

        private static Dictionary<string, IndexEntry> ParseIndex(string url, string hash, string folder = "data")
        {
            byte[] indexContent = cdn.Get(url + folder + "/" + hash[0] + hash[1] + "/" + hash[2] + hash[3] + "/" + hash + ".index").Result;

            var returnDict = new Dictionary<string, IndexEntry>();

            using (MemoryStream ms = new(indexContent))
            using (BinaryReader bin = new(ms))
            {
                bin.BaseStream.Position = bin.BaseStream.Length - 28;

                var footer = new IndexFooter
                {
                    tocHash = bin.ReadBytes(8),
                    version = bin.ReadByte(),
                    unk0 = bin.ReadByte(),
                    unk1 = bin.ReadByte(),
                    blockSizeKB = bin.ReadByte(),
                    offsetBytes = bin.ReadByte(),
                    sizeBytes = bin.ReadByte(),
                    keySizeInBytes = bin.ReadByte(),
                    checksumSize = bin.ReadByte(),
                    numElements = bin.ReadUInt32()
                };

                footer.footerChecksum = bin.ReadBytes(footer.checksumSize);

                // TODO: Read numElements as BE if it is wrong as LE
                if ((footer.numElements & 0xff000000) != 0)
                {
                    bin.BaseStream.Position -= footer.checksumSize + 4;
                    footer.numElements = bin.ReadUInt32(true);
                }

                bin.BaseStream.Position = 0;

                var indexBlockSize = 1024 * footer.blockSizeKB;
                var recordSize = footer.keySizeInBytes + footer.sizeBytes + footer.offsetBytes;
                var recordsPerBlock = indexBlockSize / recordSize;
                var recordsRead = 0;

                while (recordsRead != footer.numElements)
                {
                    var blockRecordsRead = 0;

                    for (var blockIndex = 0; blockIndex < recordsPerBlock && recordsRead < footer.numElements; blockIndex++, recordsRead++)
                    {
                        var headerHash = Convert.ToHexString(bin.ReadBytes(footer.keySizeInBytes));
                        var entry = new IndexEntry();

                        if (footer.sizeBytes == 4)
                        {
                            entry.size = bin.ReadUInt32(true);
                        }
                        else
                        {
                            throw new NotImplementedException("Index size reading other than 4 is not implemented!");
                        }

                        if (footer.offsetBytes == 4)
                        {
                            // Archive index
                            entry.offset = bin.ReadUInt32(true);
                        }
                        else if (footer.offsetBytes == 6)
                        {
                            // Group index
                            throw new NotImplementedException("Group index reading is not implemented!");
                        }
                        else if (footer.offsetBytes == 0)
                        {
                            // File index
                        }
                        else
                        {
                            throw new NotImplementedException("Offset size reading other than 4/6/0 is not implemented!");
                        }

                        returnDict.Add(headerHash, entry);

                        blockRecordsRead++;
                    }

                    bin.ReadBytes(indexBlockSize - (blockRecordsRead * recordSize));
                }
            }

            return returnDict;
        }

        private static List<string> ParsePatchFileIndex(string url, string hash)
        {
            byte[] indexContent = cdn.Get(url + "/patch/" + hash[0] + hash[1] + "/" + hash[2] + hash[3] + "/" + hash + ".index").Result;

            var list = new List<string>();

            using (BinaryReader bin = new(new MemoryStream(indexContent)))
            {
                int indexEntries = indexContent.Length / 4096;

                for (var b = 0; b < indexEntries; b++)
                {
                    for (var bi = 0; bi < 170; bi++)
                    {
                        var headerHash = Convert.ToHexString(bin.ReadBytes(16));

                        var size = bin.ReadUInt32(true);

                        list.Add(headerHash);
                    }
                    bin.ReadBytes(16);
                }
            }

            return list;
        }

        private static void GetIndexes(string url, string[] archives)
        {
            Parallel.ForEach(archives, (archive, state, i) =>
            {
                try
                {
                    byte[] indexContent = cdn.Get(url + "/data/" + archives[i][0] + archives[i][1] + "/" + archives[i][2] + archives[i][3] + "/" + archives[i] + ".index").Result;

                    using (BinaryReader bin = new(new MemoryStream(indexContent)))
                    {
                        int indexEntries = indexContent.Length / 4096;

                        for (var b = 0; b < indexEntries; b++)
                        {
                            for (var bi = 0; bi < 170; bi++)
                            {
                                var headerHash = Convert.ToHexString(bin.ReadBytes(16));

                                var entry = new IndexEntry()
                                {
                                    index = (short)i,
                                    size = bin.ReadUInt32(true),
                                    offset = bin.ReadUInt32(true)
                                };

                                cacheLock.EnterUpgradeableReadLock();
                                try
                                {
                                    if (!indexDictionary.ContainsKey(headerHash))
                                    {
                                        cacheLock.EnterWriteLock();
                                        try
                                        {
                                            if (!indexDictionary.TryAdd(headerHash, entry))
                                            {
                                                Console.WriteLine("Duplicate index entry for " + headerHash + " " + "(index: " + archives[i] + ", size: " + entry.size + ", offset: " + entry.offset);
                                            }
                                        }
                                        finally
                                        {
                                            cacheLock.ExitWriteLock();
                                        }
                                    }
                                }
                                finally
                                {
                                    cacheLock.ExitUpgradeableReadLock();
                                }
                            }
                            bin.ReadBytes(16);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error retrieving index: " + e.Message);
                }
            });
        }
        private static void GetPatchIndexes(string url, string[] archives)
        {
            Parallel.ForEach(archives, (archive, state, i) =>
            {
                try
                {
                    byte[] indexContent = cdn.Get(url + "/patch/" + archives[i][0] + archives[i][1] + "/" + archives[i][2] + archives[i][3] + "/" + archives[i] + ".index").Result;

                    using (BinaryReader bin = new(new MemoryStream(indexContent)))
                    {
                        int indexEntries = indexContent.Length / 4096;

                        for (var b = 0; b < indexEntries; b++)
                        {
                            for (var bi = 0; bi < 170; bi++)
                            {
                                var headerHash = Convert.ToHexString(bin.ReadBytes(16));

                                var entry = new IndexEntry()
                                {
                                    index = (short)i,
                                    size = bin.ReadUInt32(true),
                                    offset = bin.ReadUInt32(true)
                                };

                                cacheLock.EnterUpgradeableReadLock();
                                try
                                {
                                    if (!patchIndexDictionary.ContainsKey(headerHash))
                                    {
                                        cacheLock.EnterWriteLock();
                                        try
                                        {
                                            if (!patchIndexDictionary.TryAdd(headerHash, entry))
                                            {
                                                Console.WriteLine("Duplicate patch index entry for " + headerHash + " " + "(index: " + archives[i] + ", size: " + entry.size + ", offset: " + entry.offset);
                                            }
                                        }
                                        finally
                                        {
                                            cacheLock.ExitWriteLock();
                                        }
                                    }
                                }
                                finally
                                {
                                    cacheLock.ExitUpgradeableReadLock();
                                }
                            }
                            bin.ReadBytes(16);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Unable to retrieve patch index: " + e.Message);
                }

            });
        }

        private static RootFile GetRoot(string url, string hash, bool parseIt = false)
        {
            var root = new RootFile
            {
                entriesLookup = [],
                entriesFDID = []
            };

            byte[] content = cdn.Get(url + "/data/" + hash[0] + hash[1] + "/" + hash[2] + hash[3] + "/" + hash).Result;
            if (!parseIt) return root;

            var namedCount = 0;
            var unnamedCount = 0;
            uint totalFiles = 0;
            uint namedFiles = 0;
            var newRoot = false;

            uint dfHeaderSize = 0;
            uint dfVersion = 0;

            using (BinaryReader bin = new(new MemoryStream(BLTE.Parse(content))))
            {
                var header = bin.ReadUInt32();

                if (header == 1296454484)
                {
                    totalFiles = bin.ReadUInt32();
                    namedFiles = bin.ReadUInt32();

                    if (namedFiles == 1 || namedFiles == 2)
                    {
                        // Post 10.1.7
                        dfHeaderSize = totalFiles;
                        dfVersion = namedFiles;

                        if (dfVersion == 1 || dfVersion == 2)
                        {
                            totalFiles = bin.ReadUInt32();
                            namedFiles = bin.ReadUInt32();
                        }
                        else
                        {
                            throw new Exception("Unsupported root version: " + dfVersion);
                        }

                        bin.BaseStream.Position = dfHeaderSize;
                    }

                    newRoot = true;
                }
                else
                {
                    bin.BaseStream.Position = 0;
                }

                var blockCount = 0;

                while (bin.BaseStream.Position < bin.BaseStream.Length)
                {
                    uint count = 0;
                    ContentFlags contentFlags = 0;
                    LocaleFlags localeFlags = 0;

                    if (dfVersion == 2)
                    {
                        count = bin.ReadUInt32();
                        localeFlags = (LocaleFlags)bin.ReadUInt32();
                        var unkFlags = bin.ReadUInt32();
                        contentFlags = (ContentFlags)bin.ReadUInt32();
                        var unkByte = bin.ReadByte();
                    }
                    else
                    {
                        count = bin.ReadUInt32();
                        contentFlags = (ContentFlags)bin.ReadUInt32();
                        localeFlags = (LocaleFlags)bin.ReadUInt32();
                    }

                    //Console.WriteLine("[Block " + blockCount + "] " + count + " entries. Content flags: " + contentFlags.ToString() + ", Locale flags: " + localeFlags.ToString());
                    var entries = new RootEntry[count];
                    var filedataIds = new int[count];

                    var fileDataIndex = 0;
                    for (var i = 0; i < count; ++i)
                    {
                        entries[i].localeFlags = localeFlags;
                        entries[i].contentFlags = contentFlags;

                        filedataIds[i] = fileDataIndex + bin.ReadInt32();
                        entries[i].fileDataID = (uint)filedataIds[i];
                        fileDataIndex = filedataIds[i] + 1;
                    }

                    var blockFdids = new List<string>();
                    if (!newRoot)
                    {
                        for (var i = 0; i < count; ++i)
                        {
                            entries[i].md5 = bin.ReadBytes(16);
                            entries[i].lookup = bin.ReadUInt64();
                            root.entriesLookup.Add(entries[i].lookup, entries[i]);
                            root.entriesFDID.Add(entries[i].fileDataID, entries[i]);
                            blockFdids.Add(entries[i].fileDataID.ToString());
                        }
                    }
                    else
                    {
                        for (var i = 0; i < count; ++i)
                        {
                            entries[i].md5 = bin.ReadBytes(16);
                        }

                        for (var i = 0; i < count; ++i)
                        {
                            if (contentFlags.HasFlag(ContentFlags.NoNames))
                            {
                                entries[i].lookup = 0;
                                unnamedCount++;
                            }
                            else
                            {
                                entries[i].lookup = bin.ReadUInt64();
                                root.entriesLookup.Add(entries[i].lookup, entries[i]);
                                namedCount++;
                            }

                            root.entriesFDID.Add(entries[i].fileDataID, entries[i]);
                            blockFdids.Add(entries[i].fileDataID.ToString());
                        }
                    }

                    //File.WriteAllLinesAsync("blocks/Block" + blockCount + ".txt", blockFdids);
                    blockCount++;
                }
            }

            if ((namedFiles > 0) && namedFiles != namedCount)
                throw new Exception("Didn't read correct amount of named files! Read " + namedCount + " but expected " + namedFiles);

            if ((totalFiles > 0) && totalFiles != (namedCount + unnamedCount))
                throw new Exception("Didn't read correct amount of total files! Read " + (namedCount + unnamedCount) + " but expected " + totalFiles);

            return root;
        }

        private static DownloadFile GetDownload(string url, string hash, bool parseIt = false)
        {
            var download = new DownloadFile();

            byte[] content = cdn.Get(url + "/data/" + hash[0] + hash[1] + "/" + hash[2] + hash[3] + "/" + hash).Result;

            if (!parseIt) return download;

            using (BinaryReader bin = new(new MemoryStream(BLTE.Parse(content))))
            {
                if (Encoding.UTF8.GetString(bin.ReadBytes(2)) != "DL") { throw new Exception("Error while parsing download file. Did BLTE header size change?"); }
                download.version = bin.ReadByte();
                download.hashSizeEKey = bin.ReadByte();
                download.hasChecksumInEntry = bin.ReadBoolean();
                download.numEntries = bin.ReadUInt32(true);
                download.numTags = bin.ReadUInt16(true);
                download.flagSize = bin.ReadByte();

                download.entries = new DownloadEntry[download.numEntries];
                for (int i = 0; i < download.numEntries; i++)
                {
                    download.entries[i].eKey = Convert.ToHexString(bin.ReadBytes(download.hashSizeEKey));
                    download.entries[i].size = bin.ReadUInt40(true);
                    download.entries[i].priority = bin.ReadByte();

                    if (download.hasChecksumInEntry)
                    {
                        download.entries[i].checksum = bin.ReadUInt32(true);
                    }

                    if (download.flagSize == 1)
                    {
                        download.entries[i].flags = bin.ReadByte();
                    }
                    else
                    {
                        throw new Exception("Unexpected download flag size");
                    }
                }
            }

            return download;
        }

        private static InstallFile GetInstall(string url, string hash, bool parseIt = false)
        {
            var install = new InstallFile();

            byte[] content = cdn.Get(url + "/data/" + hash[0] + hash[1] + "/" + hash[2] + hash[3] + "/" + hash).Result;

            if (!parseIt) return install;

            using (BinaryReader bin = new(new MemoryStream(BLTE.Parse(content))))
            {
                if (Encoding.UTF8.GetString(bin.ReadBytes(2)) != "IN") { throw new Exception("Error while parsing install file. Did BLTE header size change?"); }

                bin.ReadByte();

                install.hashSize = bin.ReadByte();
                if (install.hashSize != 16) throw new Exception("Unsupported install hash size!");

                install.numTags = bin.ReadUInt16(true);
                install.numEntries = bin.ReadUInt32(true);

                int bytesPerTag = ((int)install.numEntries + 7) / 8;

                install.tags = new InstallTagEntry[install.numTags];

                for (var i = 0; i < install.numTags; i++)
                {
                    install.tags[i].name = bin.ReadCString();
                    install.tags[i].type = bin.ReadUInt16(true);

                    var filebits = bin.ReadBytes(bytesPerTag);

                    for (int j = 0; j < bytesPerTag; j++)
                        filebits[j] = (byte)((filebits[j] * 0x0202020202 & 0x010884422010) % 1023);

                    install.tags[i].files = new BitArray(filebits);
                }

                install.entries = new InstallFileEntry[install.numEntries];

                for (var i = 0; i < install.numEntries; i++)
                {
                    install.entries[i].name = bin.ReadCString();
                    install.entries[i].contentHash = bin.ReadBytes(install.hashSize);
                    install.entries[i].size = bin.ReadUInt32(true);
                    install.entries[i].tags = [];
                    for (var j = 0; j < install.numTags; j++)
                    {
                        if (install.tags[j].files[i] == true)
                        {
                            install.entries[i].tags.Add(install.tags[j].type + "=" + install.tags[j].name);
                        }
                    }
                }
            }

            return install;
        }

        private static async Task<EncodingFile> GetEncoding(string url, string hash, int encodingSize = 0, bool parseTableB = false, bool checkStuff = false, bool encoded = true)
        {
            var encoding = new EncodingFile();

            byte[] content;
            BinaryReader bin;
            if (encoded)
            {
                content = await cdn.Get(url + "/data/" + hash[0] + hash[1] + "/" + hash[2] + hash[3] + "/" + hash);

                if (encodingSize != 0 && encodingSize != content.Length)
                {
                    content = await cdn.Get(url + "/data/" + hash[0] + hash[1] + "/" + hash[2] + hash[3] + "/" + hash, true);

                    if (encodingSize != content.Length && encodingSize != 0)
                    {
                        throw new Exception("File corrupt/not fully downloaded! Remove " + "data / " + hash[0] + hash[1] + " / " + hash[2] + hash[3] + " / " + hash + " from cache.");
                    }
                }

                bin = new BinaryReader(new MemoryStream(BLTE.Parse(content)));
            }
            else
            {
                bin = new BinaryReader(new MemoryStream(File.ReadAllBytes(url)));
            }


            if (Encoding.UTF8.GetString(bin.ReadBytes(2)) != "EN") { throw new Exception("Error while parsing encoding file. Did BLTE header size change?"); }
            encoding.unk1 = bin.ReadByte();
            encoding.checksumSizeA = bin.ReadByte();
            encoding.checksumSizeB = bin.ReadByte();
            encoding.sizeA = bin.ReadUInt16(true);
            encoding.sizeB = bin.ReadUInt16(true);
            encoding.numEntriesA = bin.ReadUInt32(true);
            encoding.numEntriesB = bin.ReadUInt32(true);
            bin.ReadByte(); // unk
            encoding.stringBlockSize = bin.ReadUInt32(true);

            var headerLength = bin.BaseStream.Position;
            var stringBlockEntries = new List<string>();

            if (parseTableB)
            {
                while ((bin.BaseStream.Position - headerLength) != (long)encoding.stringBlockSize)
                {
                    stringBlockEntries.Add(bin.ReadCString());
                }

                encoding.stringBlockEntries = [.. stringBlockEntries];
            }
            else
            {
                bin.BaseStream.Position += (long)encoding.stringBlockSize;
            }

            /* Table A */
            if (checkStuff)
            {
                encoding.aHeaders = new EncodingHeaderEntry[encoding.numEntriesA];

                for (int i = 0; i < encoding.numEntriesA; i++)
                {
                    encoding.aHeaders[i].firstHash = Convert.ToHexString(bin.ReadBytes(16));
                    encoding.aHeaders[i].checksum = Convert.ToHexString(bin.ReadBytes(16));
                }
            }
            else
            {
                bin.BaseStream.Position += encoding.numEntriesA * 32;
            }

            var tableAstart = bin.BaseStream.Position;

            List<EncodingFileEntry> entries = [];

            for (int i = 0; i < encoding.numEntriesA; i++)
            {
                ushort keysCount;
                while ((keysCount = bin.ReadUInt16()) != 0)
                {
                    EncodingFileEntry entry = new()
                    {
                        keyCount = keysCount,
                        size = bin.ReadUInt32(true),
                        cKey = Convert.ToHexString(bin.ReadBytes(16)),
                        eKeys = []
                    };

                    for (int key = 0; key < entry.keyCount; key++)
                    {
                        entry.eKeys.Add(Convert.ToHexString(bin.ReadBytes(16)));
                    }

                    entries.Add(entry);
                }

                var remaining = 4096 - ((bin.BaseStream.Position - tableAstart) % 4096);
                if (remaining > 0) { bin.BaseStream.Position += remaining; }
            }

            encoding.aEntries = [.. entries];

            if (!parseTableB)
            {
                return encoding;
            }

            /* Table B */
            if (checkStuff)
            {
                encoding.bHeaders = new EncodingHeaderEntry[encoding.numEntriesB];

                for (int i = 0; i < encoding.numEntriesB; i++)
                {
                    encoding.bHeaders[i].firstHash = Convert.ToHexString(bin.ReadBytes(16));
                    encoding.bHeaders[i].checksum = Convert.ToHexString(bin.ReadBytes(16));
                }
            }
            else
            {
                bin.BaseStream.Position += encoding.numEntriesB * 32;
            }

            var tableBstart = bin.BaseStream.Position;

            encoding.bEntries = [];

            while (bin.BaseStream.Position < tableBstart + 4096 * encoding.numEntriesB)
            {
                var remaining = 4096 - (bin.BaseStream.Position - tableBstart) % 4096;

                if (remaining < 25)
                {
                    bin.BaseStream.Position += remaining;
                    continue;
                }

                var key = Convert.ToHexString(bin.ReadBytes(16));

                EncodingFileDescEntry entry = new()
                {
                    stringIndex = bin.ReadUInt32(true),
                    compressedSize = bin.ReadUInt40(true)
                };

                if (entry.stringIndex == uint.MaxValue) break;

                encoding.bEntries.Add(key, entry);
            }

            // Go to the end until we hit a non-NUL byte
            while (bin.BaseStream.Position < bin.BaseStream.Length)
            {
                if (bin.ReadByte() != 0)
                    break;
            }

            bin.BaseStream.Position -= 1;
            var eespecSize = bin.BaseStream.Length - bin.BaseStream.Position;
            encoding.encodingESpec = new string(bin.ReadChars(int.Parse(eespecSize.ToString())));

            return encoding;
        }

        private static PatchFile GetPatch(string url, string hash, bool parseIt = false)
        {
            var patchFile = new PatchFile();

            byte[] content = cdn.Get(url + "/patch/" + hash[0] + hash[1] + "/" + hash[2] + hash[3] + "/" + hash).Result;

            if (!parseIt) return patchFile;

            using (BinaryReader bin = new(new MemoryStream(content)))
            {
                if (Encoding.UTF8.GetString(bin.ReadBytes(2)) != "PA") { throw new Exception("Error while parsing patch file!"); }

                patchFile.version = bin.ReadByte();
                patchFile.fileKeySize = bin.ReadByte();
                patchFile.sizeB = bin.ReadByte();
                patchFile.patchKeySize = bin.ReadByte();
                patchFile.blockSizeBits = bin.ReadByte();
                patchFile.blockCount = bin.ReadUInt16(true);
                patchFile.flags = bin.ReadByte();
                patchFile.encodingContentKey = bin.ReadBytes(16);
                patchFile.encodingEncodingKey = bin.ReadBytes(16);
                patchFile.decodedSize = bin.ReadUInt32(true);
                patchFile.encodedSize = bin.ReadUInt32(true);
                patchFile.especLength = bin.ReadByte();
                patchFile.encodingSpec = new string(bin.ReadChars(patchFile.especLength));

                patchFile.blocks = new PatchBlock[patchFile.blockCount];
                for (var i = 0; i < patchFile.blockCount; i++)
                {
                    patchFile.blocks[i].lastFileContentKey = bin.ReadBytes(patchFile.fileKeySize);
                    patchFile.blocks[i].blockMD5 = bin.ReadBytes(16);
                    patchFile.blocks[i].blockOffset = bin.ReadUInt32(true);

                    var prevPos = bin.BaseStream.Position;

                    var files = new List<BlockFile>();

                    bin.BaseStream.Position = patchFile.blocks[i].blockOffset;
                    while (bin.BaseStream.Position <= patchFile.blocks[i].blockOffset + 0x10000)
                    {
                        var file = new BlockFile
                        {
                            numPatches = bin.ReadByte()
                        };

                        if (file.numPatches == 0) break;
                        file.targetFileContentKey = bin.ReadBytes(patchFile.fileKeySize);
                        file.decodedSize = bin.ReadUInt40(true);

                        var filePatches = new List<FilePatch>();

                        for (var j = 0; j < file.numPatches; j++)
                        {
                            var filePatch = new FilePatch
                            {
                                sourceFileEncodingKey = bin.ReadBytes(patchFile.fileKeySize),
                                decodedSize = bin.ReadUInt40(true),
                                patchEncodingKey = bin.ReadBytes(patchFile.patchKeySize),
                                patchSize = bin.ReadUInt32(true),
                                patchIndex = bin.ReadByte()
                            };
                            filePatches.Add(filePatch);
                        }

                        file.patches = [.. filePatches];

                        files.Add(file);
                    }

                    patchFile.blocks[i].files = [.. files];
                    bin.BaseStream.Position = prevPos;
                }
            }

            return patchFile;
        }
        private static void UpdateListfile()
        {
            if (!File.Exists("listfile.txt") || DateTime.Now.AddHours(-1) > File.GetLastWriteTime("listfile.txt"))
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip");
                    client.DefaultRequestHeaders.Add("User-Agent", "BuildBackup");

                    var response = client.GetAsync("https://github.com/wowdev/wow-listfile/releases/latest/download/verified-listfile.csv").Result;
                    response.EnsureSuccessStatusCode();

                    using (var responseStream = response.Content.ReadAsStreamAsync().Result)
                    using (var decompressedStream = new System.IO.Compression.GZipStream(responseStream, System.IO.Compression.CompressionMode.Decompress))
                    using (var stream = new MemoryStream())
                    {
                        decompressedStream.CopyTo(stream);
                        File.WriteAllBytes("listfile.txt", stream.ToArray());
                    }
                }
            }
        }
    }
}
