namespace FileHasher.Models
{
    public class Base64StringsDBModel
    {
        public int Base64StringID { get; set; }
        public string Base64String { get; set; }
        public int FK_FilePathID { get; set; }
    }
}
