using System;

namespace FileHasher.Models
{
    public class BlobsDBModel : IDisposable
    {
        public int BlobID { get; set; }
        public int FK_FilePathID { get; set; }
        public byte[] BlobData { get; set; }

        public void Dispose()
        {
            // set array to null
            BlobData = null;
            // initiate garbage collection
            GC.Collect();
        }
    }
}
