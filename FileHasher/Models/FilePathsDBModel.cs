using System;
using System.IO;

namespace FileHasher.Models
{
    public class FilePathsDBModel
    {
        public int FilePathID { get; set; }
        public string FilePath { get; set; }
        public long LastWriteTimeUtc { get; set; }
        public string HashAlgorithm { get; set; }
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

        public static void SetRootFolderPath(string rootFolderPath)
        {
            // set root folder path only once
            if (!_rootFolderPathIsSet)
            {
                _rootFolderPath = rootFolderPath;
                _rootFolderPathIsSet = true;
            }
        }
    }
}
