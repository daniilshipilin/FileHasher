namespace FileHasher
{
    public class CSVFile
    {
        public string FilePath { get; }
        public long LastWriteTimeUtc { get; set; }
        public string HashAlgorithm { get; set; }
        public string FileHash { get; set; }
        public string FileHashShort => FileHash.Substring(0, 8);

        public CSVFile(string filePath)
        {
            FilePath = filePath;
        }

        public string GetRecordString() => $"{FilePath};{LastWriteTimeUtc};{HashAlgorithm};{FileHash}";
    }
}
