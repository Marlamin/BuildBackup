using System;
using System.Collections;
using System.Collections.Generic;

namespace BuildBackup
{
    public struct VersionsFile
    {
        public VersionsEntry[] entries;
    }

    public struct VersionsEntry
    {
        public string region;
        public string buildConfig;
        public string cdnConfig;
        public string buildId;
        public string versionsName;
        public string productConfig;
        public string keyRing;
    }

    public struct CdnsFile
    {
        public CdnsEntry[] entries;
    }

    public struct CdnsEntry
    {
        public string name;
        public string path;
        public string[] hosts;
        public string configPath;
    }

    public struct GameBlobFile
    {
        public string decryptionKeyName;
    }

    public struct BuildConfigFile
    {
        public string root;
        public string[] download;
        public string[] downloadSize;
        public string[] install;
        public string[] installSize;
        public string[] encoding;
        public string[] encodingSize;
        public string[] size;
        public string[] sizeSize;
        public string buildName;
        public string buildPlaybuildInstaller;
        public string buildProduct;
        public string buildUid;
        public string buildBranch;
        public string buildNumber;
        public string buildAttributes;
        public string buildComments;
        public string buildCreator;
        public string buildFixedHash;
        public string buildReplayHash;
        public string buildManifestVersion;
        public string buildSignatureFile;
        public string patch;
        public string patchSize;
        public string patchConfig;
        public string[] patchIndex;
        public string[] patchIndexSize;
        public string partialPriority;
        public string partialPrioritySize;
    }

    public struct CDNConfigFile
    {
        public string[] archives;
        public string archiveGroup;
        public string[] patchArchives;
        public string patchArchiveGroup;
        public string[] builds;
        public string fileIndex;
        public string fileIndexSize;
        public string patchFileIndex;
        public string patchFileIndexSize;
    }

    public struct IndexEntry
    {
        public short index;
        public uint offset;
        public uint size;
    }

    public struct IndexFooter
    {
        public byte[] tocHash;
        public byte version;
        public byte unk0;
        public byte unk1;
        public byte blockSizeKB;
        public byte offsetBytes;
        public byte sizeBytes;
        public byte keySizeInBytes;
        public byte checksumSize;
        public uint numElements;
        public byte[] footerChecksum;
    }

    public struct EncodingFile
    {
        public byte unk1;
        public byte checksumSizeA;
        public byte checksumSizeB;
        public ushort sizeA;
        public ushort sizeB;
        public uint numEntriesA;
        public uint numEntriesB;
        public byte unk2;
        public ulong stringBlockSize;
        public string[] stringBlockEntries;
        public EncodingHeaderEntry[] aHeaders;
        public EncodingFileEntry[] aEntries;
        public EncodingHeaderEntry[] bHeaders;
        public Dictionary<string, EncodingFileDescEntry> bEntries;
        public string encodingESpec;
    }

    public struct EncodingHeaderEntry
    {
        public string firstHash;
        public string checksum;
    }

    public struct EncodingFileEntry
    {
        public ushort keyCount;
        public uint size;
        public string cKey;
        public List<string> eKeys;
    }

    public struct EncodingFileDescEntry
    {
        public string key;
        public uint stringIndex;
        public ulong compressedSize;
    }

    public struct InstallFile
    {
        public byte hashSize;
        public ushort numTags;
        public uint numEntries;
        public InstallTagEntry[] tags;
        public InstallFileEntry[] entries;
    }

    public struct InstallTagEntry
    {
        public string name;
        public ushort type;
        public BitArray files;
    }

    public struct InstallFileEntry
    {
        public string name;
        public byte[] contentHash;
        public uint size;
        public List<string> tags;
    }

    public struct DownloadFile
    {
        public byte[] unk;
        public uint numEntries;
        public uint numTags;
        public DownloadEntry[] entries;
    }

    public struct DownloadEntry
    {
        public string hash;
        public byte[] unk;
    }

    public struct BLTEChunkInfo
    {
        public bool isFullChunk;
        public int compSize;
        public int decompSize;
        public byte[] checkSum;
    }

    public struct RootFile
    {
        public MultiDictionary<ulong, RootEntry> entriesLookup;
        public MultiDictionary<uint, RootEntry> entriesFDID;
    }

    public struct RootEntry
    {
        public ContentFlags contentFlags;
        public LocaleFlags localeFlags;
        public ulong lookup;
        public uint fileDataID;
        public byte[] md5;
    }

    public struct PatchFile
    {
        public byte version;
        public byte fileKeySize;
        public byte sizeB;
        public byte patchKeySize;
        public byte blockSizeBits;
        public ushort blockCount;
        public byte flags;
        public byte[] encodingContentKey;
        public byte[] encodingEncodingKey;
        public uint decodedSize;
        public uint encodedSize;
        public byte especLength;
        public string encodingSpec;
        public PatchBlock[] blocks;
    }

    public struct PatchBlock
    {
        public byte[] lastFileContentKey;
        public byte[] blockMD5;
        public uint blockOffset;
        public BlockFile[] files;
    }

    public struct BlockFile
    {
        public byte numPatches;
        public byte[] targetFileContentKey;
        public ulong decodedSize;
        public FilePatch[] patches;
    }

    public struct FilePatch
    {
        public byte[] sourceFileEncodingKey;
        public ulong decodedSize;
        public byte[] patchEncodingKey;
        public uint patchSize;
        public byte patchIndex;
    }

    [Flags]
    public enum LocaleFlags : uint
    {
        All = 0xFFFFFFFF,
        None = 0,
        Unk_1 = 0x1,
        enUS = 0x2,
        koKR = 0x4,
        Unk_8 = 0x8,
        frFR = 0x10,
        deDE = 0x20,
        zhCN = 0x40,
        esES = 0x80,
        zhTW = 0x100,
        enGB = 0x200,
        enCN = 0x400,
        enTW = 0x800,
        esMX = 0x1000,
        ruRU = 0x2000,
        ptBR = 0x4000,
        itIT = 0x8000,
        ptPT = 0x10000,
        enSG = 0x20000000, // custom
        plPL = 0x40000000, // custom
        All_WoW = enUS | koKR | frFR | deDE | zhCN | esES | zhTW | enGB | esMX | ruRU | ptBR | itIT | ptPT
    }

    [Flags]
    public enum ContentFlags : uint
    {
        None = 0,
        F00000001 = 0x1,            // unused in 9.0.5
        F00000002 = 0x2,            // unused in 9.0.5
        F00000004 = 0x4,            // unused in 9.0.5
        LoadOnWindows = 0x8,        // added in 7.2.0.23436
        LoadOnMacOS = 0x10,         // added in 7.2.0.23436
        LowViolence = 0x80,         // many models have this flag
        DoNotLoad = 0x100,          // unused in 9.0.5
        F00000200 = 0x200,          // unused in 9.0.5
        F00000400 = 0x400,          // unused in 9.0.5
        UpdatePlugin = 0x800,       // UpdatePlugin.dll / UpdatePlugin.dylib only
        F00001000 = 0x1000,         // unused in 9.0.5
        F00002000 = 0x2000,         // unused in 9.0.5
        F00004000 = 0x4000,         // unused in 9.0.5
        F00008000 = 0x8000,         // unused in 9.0.5
        F00010000 = 0x10000,        // unused in 9.0.5
        F00020000 = 0x20000,        // 1173911 uses in 9.0.5        
        F00040000 = 0x40000,        // 1329023 uses in 9.0.5
        F00080000 = 0x80000,        // 682817 uses in 9.0.5
        F00100000 = 0x100000,       // 1231299 uses in 9.0.5
        F00200000 = 0x200000,       // 7398 uses in 9.0.5: updateplugin, .bls, .lua, .toc, .xsd
        F00400000 = 0x400000,       // 156302 uses in 9.0.5
        F00800000 = 0x800000,       // .skel & .wwf
        F01000000 = 0x1000000,      // unused in 9.0.5
        F02000000 = 0x2000000,      // 969369 uses in 9.0.5
        F04000000 = 0x4000000,      // 1101698 uses in 9.0.5
        Encrypted = 0x8000000,      // File is encrypted
        NoNames = 0x10000000,       // No lookup hash
        UncommonRes = 0x20000000,   // added in 7.0.3.21737
        Bundle = 0x40000000,        // unused in 9.0.5
        NoCompression = 0x80000000  // sounds have this flag
    }
}
