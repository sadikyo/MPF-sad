﻿using System;
using System.Linq;
using System.Security.Cryptography;

namespace MPF.Core.Hashing
{
    /// <summary>
    /// Available hashing types
    /// </summary>
    [Flags]
    public enum Hash
    {
        CRC = 1 << 0,
        MD5 = 1 << 1,
        SHA1 = 1 << 2,
        SHA256 = 1 << 3,
        SHA384 = 1 << 4,
        SHA512 = 1 << 5,

        // Special combinations
        Standard = CRC | MD5 | SHA1,
        All = CRC | MD5 | SHA1 | SHA256 | SHA384 | SHA512,
    }

    /// <summary>
    /// Async hashing class wraper
    /// </summary>
    public class Hasher
    {
        public Hash HashType { get; private set; }
        private IDisposable _hasher; 

        public Hasher(Hash hashType)
        {
            this.HashType = hashType;
            GetHasher();
        }

        /// <summary>
        /// Generate the correct hashing class based on the hash type
        /// </summary>
        private void GetHasher()
        {
            switch (HashType)
            {
                case Hash.CRC:
                    _hasher = new OptimizedCRC();
                    break;

                case Hash.MD5:
                    _hasher = MD5.Create();
                    break;

                case Hash.SHA1:
                    _hasher = SHA1.Create();
                    break;

                case Hash.SHA256:
                    _hasher = SHA256.Create();
                    break;

                case Hash.SHA384:
                    _hasher = SHA384.Create();
                    break;

                case Hash.SHA512:
                    _hasher = SHA512.Create();
                    break;
            }
        }

        public void Dispose()
        {
            _hasher.Dispose();
        }

        /// <summary>
        /// Process a buffer of some length with the internal hash algorithm
        /// </summary>
        public void Process(byte[] buffer, int size)
        {
            switch (HashType)
            {
                case Hash.CRC:
                    (_hasher as OptimizedCRC).Update(buffer, 0, size);
                    break;

                case Hash.MD5:
                case Hash.SHA1:
                case Hash.SHA256:
                case Hash.SHA384:
                case Hash.SHA512:
                    (_hasher as HashAlgorithm).TransformBlock(buffer, 0, size, null, 0);
                    break;
            }
        }

        /// <summary>
        /// Finalize the internal hash algorigthm
        /// </summary>
        public void Terminate()
        {
            byte[] emptyBuffer = new byte[0];
            switch (HashType)
            {
                case Hash.CRC:
                    (_hasher as OptimizedCRC).Update(emptyBuffer, 0, 0);
                    break;

                case Hash.MD5:
                case Hash.SHA1:
                case Hash.SHA256:
                case Hash.SHA384:
                case Hash.SHA512:
                    (_hasher as HashAlgorithm).TransformFinalBlock(emptyBuffer, 0, 0);
                    break;
            }
        }

        /// <summary>
        /// Get internal hash as a byte array
        /// </summary>
        public byte[] GetHash()
        {
            switch (HashType)
            {
                case Hash.CRC:
                    return BitConverter.GetBytes((_hasher as OptimizedCRC).Value).Reverse().ToArray();

                case Hash.MD5:
                case Hash.SHA1:
                case Hash.SHA256:
                case Hash.SHA384:
                case Hash.SHA512:
                    return (_hasher as HashAlgorithm).Hash;
            }

            return null;
        }

        /// <summary>
        /// Get internal hash as a string
        /// </summary>
        public string GetHashString()
        {
            byte[] hash = GetHash();
            if (hash == null)
                return null;
            
            return ByteArrayToString(hash);
        }

        /// <summary>
        /// Convert a byte array to a hex string
        /// </summary>
        /// <param name="bytes">Byte array to convert</param>
        /// <returns>Hex string representing the byte array</returns>
        /// <link>http://stackoverflow.com/questions/311165/how-do-you-convert-byte-array-to-hexadecimal-string-and-vice-versa</link>
        private static string ByteArrayToString(byte[] bytes)
        {
            // If we get null in, we send null out
            if (bytes == null)
                return null;

            try
            {
                string hex = BitConverter.ToString(bytes);
                return hex.Replace("-", string.Empty).ToLowerInvariant();
            }
            catch
            {
                return null;
            }
        }
    }
}
