using FileHasher.Models;
using FileHasher.SQL;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace FileHasher
{
    class Program
    {
        #region Program configuration

        const string VERSION_INFO = "";

#if DEBUG
        const string BUILD_CONFIGURATION = " [Debug]";
#else
        const string BUILD_CONFIGURATION = "";
#endif

        #endregion

        #region Build/program info

        public static Version Ver { get; } = new AssemblyName(Assembly.GetExecutingAssembly().FullName).Version;
        public static string ProgramVersion { get; } = $"{Ver.Major}.{Ver.Minor}.{Ver.Build}";
        public static string ProgramBaseDirectory { get; } = AppDomain.CurrentDomain.BaseDirectory;
        public static string ProgramPath { get; } = Assembly.GetEntryAssembly().Location;
        public static string ProgramName { get; } = Assembly.GetExecutingAssembly().GetName().Name;
        public static string ProgramHeader { get; } = $"{ProgramName} v{ProgramVersion}{VERSION_INFO}{BUILD_CONFIGURATION}";

        #endregion

        enum MessageType
        {
            Default,
            FileModified,
            FileDeleted,
            FileNew,
            Exception
        }

        public enum HashAlgorithm
        {
            SHA512,
            SHA384,
            SHA256,
            SHA1
        }

        static string RootFolderPath { get; set; }
        static string LogFilePath => $"{ProgramBaseDirectory}Output.log";
        static string[] FileExtensions { get; set; }
        static string LogTimestampFormat => "yyyy-MM-dd HH:mm:ss.fff";
        static List<string> LogBufer { get; } = new List<string>();
        static bool WaitBeforeExit { get; set; } = false;
        static bool FolderCleanupRequired { get; set; } = false;
        static HashAlgorithm DefaultHashAlgorithm { get; } = HashAlgorithm.SHA256;

        static void Main(string[] args)
        {
            try
            {
                // parse args
                if ((args.Length == 1 && args[0].Equals("/?")) || args.Length < 2)
                {
                    Console.WriteLine(ProgramHeader);
                    Console.WriteLine("Program usage:");
                    Console.WriteLine($"  {ProgramName} \"Root folder path\" \"File extensions list (comma separated)\" [-wait|-nowait] [-clean|-noclean]");
                    return;
                }

                ConsolePrint(ProgramHeader);

                if (args.Length >= 1) { RootFolderPath = Path.GetFullPath(args[0]); }

                if (args.Length >= 2) { FileExtensions = args[1].Split(','); }

                if (args.Length >= 3)
                {
                    if (args[2].Equals("-wait")) { WaitBeforeExit = true; }
                    else { WaitBeforeExit = false; }
                }

                if (args.Length >= 4)
                {
                    if (args[3].Equals("-clean")) { FolderCleanupRequired = true; }
                    else { FolderCleanupRequired = false; }
                }

                // print args
                ConsolePrint($"{nameof(RootFolderPath)}={RootFolderPath}");
                ConsolePrint($"{nameof(FileExtensions)}={string.Join(",", FileExtensions)}");
                ConsolePrint($"{nameof(WaitBeforeExit)}={WaitBeforeExit}");
                ConsolePrint($"{nameof(FolderCleanupRequired)}={FolderCleanupRequired}");

                if (!Directory.Exists(RootFolderPath))
                {
                    throw new DirectoryNotFoundException($"Directory '{RootFolderPath}' doesn't exist");
                }

                int filesModified = 0;
                int filesDeleted = 0;
                int filesNew = 0;

                var _sqlite = new SQLiteDBAccess();
                var dbFilePaths = _sqlite.Select_FilePaths();

                var filePaths = Directory.EnumerateFiles(RootFolderPath, "*.*", SearchOption.AllDirectories)
                                .Where(s => FileExtensions.Contains(Path.GetExtension(s).ToLower())).ToList();

                // find deleted files
                for (int i = 0; i < dbFilePaths.Count; i++)
                {
                    bool fileIsDeleted = true;

                    foreach (var path in filePaths)
                    {
                        if (dbFilePaths[i].FilePath.Equals(path))
                        {
                            fileIsDeleted = false;
                            long lastWrite = File.GetLastWriteTime(path).ToFileTimeUtc();

                            // update record, if file modification time is different
                            if (dbFilePaths[i].LastWriteTimeUtc != lastWrite)
                            {
                                dbFilePaths[i].LastWriteTimeUtc = lastWrite;
                                string tmpHash = CalculateHash(path);

                                if (dbFilePaths[i].FileHash.Equals(tmpHash))
                                {
                                    filesModified++; // increase modified files counter, so that new lastWrite value is updated in db
                                    ConsolePrint($"File '{path}' has different last write timestamp, but hashes are identical");
                                }
                                else
                                {
                                    filesModified++;
                                    dbFilePaths[i].FileHash = tmpHash;

                                    if (!dbFilePaths[i].HashAlgorithm.Equals(DefaultHashAlgorithm.ToString()))
                                    {
                                        ConsolePrint($"File '{path}' was hashed using {dbFilePaths[i].HashAlgorithm} - update using {DefaultHashAlgorithm.ToString()}");
                                        dbFilePaths[i].HashAlgorithm = DefaultHashAlgorithm.ToString();
                                    }
                                }

                                _sqlite.Update_FilePath(dbFilePaths[i]);
                                ConsolePrint($"[MODIFIED]  '{path}' ({dbFilePaths[i].GetFileHashShort()})", MessageType.FileModified);
                            }

                            break;
                        }
                    }

                    if (fileIsDeleted)
                    {
                        filesDeleted++;
                        _sqlite.Delete_FilePath(dbFilePaths[i]);
                        ConsolePrint($"[DELETED]  '{dbFilePaths[i].FilePath}' ({dbFilePaths[i].GetFileHashShort()})", MessageType.FileDeleted);
                        dbFilePaths.RemoveAt(i);
                        i--; // decreasing index, because of previosly deleted element
                    }
                }

                // find new files
                for (int i = 0; i < filePaths.Count; i++)
                {
                    bool fileIsNew = true;

                    foreach (var record in dbFilePaths)
                    {
                        if (filePaths[i].Equals(record.FilePath))
                        {
                            fileIsNew = false;
                            break;
                        }
                    }

                    if (fileIsNew)
                    {
                        filesNew++;

                        dbFilePaths.Add(new FilePathsDBModel()
                        {
                            FilePath = filePaths[i],
                            LastWriteTimeUtc = File.GetLastWriteTime(filePaths[i]).ToFileTimeUtc(),
                            HashAlgorithm = DefaultHashAlgorithm.ToString(),
                            FileHash = CalculateHash(filePaths[i])
                        });

                        _sqlite.Insert_FilePath(dbFilePaths.Last());
                        ConsolePrint($"[NEW]  '{filePaths[i]}' ({dbFilePaths.Last().GetFileHashShort()})", MessageType.FileNew);
                    }
                }

                if (filesModified > 0 || filesDeleted > 0 || filesNew > 0)
                {
                    if (filesModified > 0) { ConsolePrint($"Files modified: {filesModified}"); }
                    if (filesDeleted > 0) { ConsolePrint($"Files deleted: {filesDeleted}"); }
                    if (filesNew > 0) { ConsolePrint($"Files new: {filesNew}"); }
                }
                else
                {
                    ConsolePrint("Update not required");
                }

                ConsolePrint($"Total records in db: {_sqlite.Select_CountRecords()}");

                if (FolderCleanupRequired)
                {
                    var filePathsToDelete = Directory.EnumerateFiles(RootFolderPath, "*.*", SearchOption.AllDirectories)
                                    .Where(s => !FileExtensions.Contains(Path.GetExtension(s).ToLower())).ToList();

                    if (filePathsToDelete.Count == 0)
                    {
                        ConsolePrint("Folder cleanup not required");
                    }
                    else
                    {
                        int cleanupFilesDeleted = 0;

                        foreach (var path in filePathsToDelete)
                        {
                            cleanupFilesDeleted++;
                            File.Delete(path);
                            ConsolePrint($"File '{path}' deleted");
                        }

                        ConsolePrint($"Folder cleanup deleted files: {cleanupFilesDeleted}");
                    }
                }
            }
            catch (Exception ex)
            {
                ConsolePrint(ex.ToString(), MessageType.Exception);
            }

            try
            {
                // write log file
                File.AppendAllLines(LogFilePath, LogBufer, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            if (WaitBeforeExit)
            {
                Console.WriteLine("Press ENTER to exit");
                Console.ReadLine();
            }
        }

        private static void ConsolePrint(string message, MessageType messageType = MessageType.Default)
        {
            if (messageType != MessageType.Default)
            {
                if (messageType == MessageType.FileDeleted)
                {
                    Console.BackgroundColor = ConsoleColor.DarkRed;
                    Console.ForegroundColor = ConsoleColor.White;
                }
                else if (messageType == MessageType.FileModified)
                {
                    Console.BackgroundColor = ConsoleColor.DarkMagenta;
                    Console.ForegroundColor = ConsoleColor.White;
                }
                else if (messageType == MessageType.FileNew)
                {
                    Console.BackgroundColor = ConsoleColor.DarkGreen;
                    Console.ForegroundColor = ConsoleColor.White;
                }
                else if (messageType == MessageType.Exception)
                {
                    Console.BackgroundColor = ConsoleColor.DarkRed;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                }
            }

            Console.WriteLine(message);
            Console.ResetColor();
            LogBufer.Add($"{(DateTime.Now).ToString(LogTimestampFormat).PadRight(25)} {message}");
        }

        public static string CalculateHash(string filePath)
        {
            byte[] result = null;

            if (DefaultHashAlgorithm == HashAlgorithm.SHA512)
            {
                using (var sr = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    using (var hash = SHA512.Create())
                    {
                        result = hash.ComputeHash(sr);
                    }
                }
            }
            else if (DefaultHashAlgorithm == HashAlgorithm.SHA384)
            {
                using (var sr = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    using (var hash = SHA384.Create())
                    {
                        result = hash.ComputeHash(sr);
                    }
                }
            }
            else if (DefaultHashAlgorithm == HashAlgorithm.SHA256)
            {
                using (var sr = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    using (var hash = SHA256.Create())
                    {
                        result = hash.ComputeHash(sr);
                    }
                }
            }
            else if (DefaultHashAlgorithm == HashAlgorithm.SHA1)
            {
                using (var sr = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    using (var hash = SHA1.Create())
                    {
                        result = hash.ComputeHash(sr);
                    }
                }
            }

            return (BitConverter.ToString(result).Replace("-", string.Empty).ToLower());
        }

        //public static string CalculateSHA256(string filePath)
        //{
        //    byte[] result;

        //    using (var sr = new FileStream(filePath, FileMode.Open, FileAccess.Read))
        //    {
        //        using (var sha256 = SHA256.Create()) { result = sha256.ComputeHash(sr); }
        //    }

        //    var sb = new StringBuilder();

        //    for (int i = 0; i < result.Length; i++)
        //    {
        //        sb.Append(result[i].ToString("x2"));
        //    }

        //    return (sb.ToString());
        //}
    }
}
