namespace FileHasher.Models
{
    public class BlobsDBModel
    {
        public int BlobID { get; set; }
        public int FK_FilePathID { get; set; }
        public byte[] BlobData { get; set; }
    }
}
