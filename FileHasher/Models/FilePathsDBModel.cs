namespace FileHasher.Models
{
    public class FilePathsDBModel
    {
        public int FilePathID { get; set; }
        public string FilePath { get; set; }
        public long LastWriteTimeUtc { get; set; }
        public string HashAlgorithm { get; set; }
        public string FileHash { get; set; }

        public string GetFileHashShort() => FileHash.Substring(0, 8);
    }
}
