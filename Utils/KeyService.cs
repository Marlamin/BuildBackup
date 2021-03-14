using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;

namespace BuildBackup
{
    public static class KeyService
    {
        static KeyService()
        {
            if (keys.Count == 0)
            {
                LoadKeys();
            }
        }

        private static Dictionary<ulong, byte[]> keys = new Dictionary<ulong, byte[]>();

        private static Salsa20 salsa = new Salsa20();

        public static Salsa20 SalsaInstance => salsa;

        public static byte[] GetKey(ulong keyName)
        {
            keys.TryGetValue(keyName, out byte[] key);
            return key;
        }

        public static void LoadKeys()
        {
            if (!File.Exists("../tactkeys/TACTKeys/WoW.txt")) return;

            foreach (var line in File.ReadAllLines("../tactkeys/TACTKeys/WoW.txt"))
            {
                var splitLine = line.Split(' ');
                var lookup = ulong.Parse(splitLine[0], System.Globalization.NumberStyles.HexNumber);
                byte[] key = splitLine[1].Trim().ToByteArray();
                if (!keys.ContainsKey(lookup))
                {
                    keys.Add(lookup, key);
                }
            }
        }
    }
}