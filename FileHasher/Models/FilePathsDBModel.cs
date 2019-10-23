using System;
using System.IO;
using System.Security.Cryptography;

namespace FileHasher.Models
{
    public class FilePathsDBModel
    {
        public const string HASH_ALGORITHM = "SHA256";

        public int FilePathID { get; set; }
        public string FilePath { get; set; }
        public long LastWriteTimeUtc { get; set; }
        public string HashAlgorithm { get; set; } = HASH_ALGORITHM;
        public string FileHash { get; set; }

        public string FileHashShort => FileHash.Substring(0, 8);
        public string FileFullPath => $"{_rootFolderPath}{Path.DirectorySeparatorChar}{FilePath}";

        static string _rootFolderPath;
        static bool _rootFolderPathIsSet;

        public FilePathsDBModel()
        {
            // check if static SetRootFolderPath method was used before creating class object
            if (!_rootFolderPathIsSet)
            {
                throw new Exception("Root folder path not set");
            }
        }

        public FilePathsDBModel(string filePath) : this()
        {
            FilePath = filePath;
            GetLastWriteTime();
            CalculateHash();
        }

        public static void SetRootFolderPath(string rootFolderPath)
        {
            // set root folder path only once
            if (!_rootFolderPathIsSet)
            {
                _rootFolderPath = rootFolderPath;
                _rootFolderPathIsSet = true;
            }
        }

        public void GetLastWriteTime()
        {
            LastWriteTimeUtc = File.GetLastWriteTime(FileFullPath).ToFileTimeUtc();
        }

        public void CalculateHash()
        {
            using (var fs = new FileStream(FileFullPath, FileMode.Open, FileAccess.Read))
            {
                using (var hash = SHA256.Create())
                {
                    FileHash = BitConverter.ToString(hash.ComputeHash(fs)).Replace("-", string.Empty).ToLower();
                }
            }
        }
    }
}
