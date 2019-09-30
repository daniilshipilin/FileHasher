namespace FileHasher
{
    public class CSVFile
    {
        public string FilePath { get; set; }
        public long LastWriteTimeUtc { get; set; }
        public string FileSHA256 { get; set; }
        public string FileSHA256Short => FileSHA256.Substring(0, 8);

        public string GetRecordString() => $"{FilePath};{LastWriteTimeUtc};{FileSHA256}";
    }
}
