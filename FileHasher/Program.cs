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
    static class Program
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
            FileRestored,
            Exception
        }

        public enum HashAlgorithm
        {
            SHA512,
            SHA384,
            SHA256,
            SHA1
        }

        const string LOG_TIMESTAMP_FORMAT = "yyyy-MM-dd HH:mm:ss.fff";

        static readonly string _logFilePath = $"{ProgramBaseDirectory}Output.log";
        static readonly List<string> _logBufer = new List<string>();
        static readonly HashAlgorithm _defaultHashAlgorithm = HashAlgorithm.SHA256;

        static SQLiteDBAccess _sqlite;

        static string _rootFolderPath;
        static string[] _fileExtensions;
        static bool _waitBeforeExit = false;
        static bool _folderCleanupScheduled = false;
        static bool _dbUpdatePrompt = false;
        static bool _dbOptimizationScheduled = false;
        static bool _restoreFilesFromDB = false;

        static void Main(string[] args)
        {
            try
            {
                // parse args
                if ((args.Length == 1 && args[0].Equals("/?")) || args.Length < 2)
                {
                    Console.WriteLine(ProgramHeader);
                    Console.WriteLine("Program usage:");
                    Console.WriteLine($"  {ProgramName} \"Root folder path\" \"File extensions list (comma separated)\" [-wait] [-clean] [-prompt] [-optimize] [-restore]");
                    return;
                }

                ConsolePrint(ProgramHeader);

                if (args.Length >= 1) { _rootFolderPath = Path.GetFullPath(args[0]); }

                if (args.Length >= 2) { _fileExtensions = args[1].Split(','); }

                if (args.Length >= 3)
                {
                    for (int i = 2; i < args.Length; i++)
                    {
                        if (args[i].Equals("-wait")) { _waitBeforeExit = true; }
                        else if (args[i].Equals("-clean")) { _folderCleanupScheduled = true; }
                        else if (args[i].Equals("-prompt")) { _dbUpdatePrompt = true; }
                        else if (args[i].Equals("-optimize")) { _dbOptimizationScheduled = true; }
                        else if (args[i].Equals("-restore")) { _restoreFilesFromDB = true; }
                    }
                }

                if (!Directory.Exists(_rootFolderPath))
                {
                    throw new DirectoryNotFoundException($"Directory '{_rootFolderPath}' doesn't exist");
                }

                // print args
                ConsolePrint($"Root folder path = {_rootFolderPath}");
                ConsolePrint($"File extensions = {string.Join(",", _fileExtensions)}");
                ConsolePrint($"Restore files from DB = {_restoreFilesFromDB}");
                ConsolePrint($"DB update prompt = {_dbUpdatePrompt}");
                ConsolePrint($"Folder cleanup scheduled = {_folderCleanupScheduled}");
                ConsolePrint($"DB optimization scheduled = {_dbOptimizationScheduled}");
                ConsolePrint($"Wait before exit = {_waitBeforeExit}");

                _sqlite = new SQLiteDBAccess();

                if (_restoreFilesFromDB) { RestoreFilesFromDB(); }
                else { HashAndBackupFilesToDB(); }

                int records = _sqlite.Select_CountRecords_FilePaths();
                ConsolePrint($"Total file paths in db: {records}");

                // compact db
                if (_dbOptimizationScheduled)
                {
                    ConsolePrint("Optimizing db");
                    _sqlite.CompactDatabase();
                }

                // clean root folder
                if (_folderCleanupScheduled)
                {
                    var filePathsToDelete = Directory.EnumerateFiles(_rootFolderPath, "*.*", SearchOption.AllDirectories)
                                    .Where(s => !_fileExtensions.Contains(Path.GetExtension(s).ToLower())).ToList();

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

                if (_waitBeforeExit)
                {
                    Console.WriteLine("Press ENTER to exit");
                    Console.ReadLine();
                }
            }
            catch (Exception ex)
            {
                ConsolePrint(ex.ToString(), MessageType.Exception);
            }
            finally
            {
                try
                {
                    // write log file
                    File.AppendAllLines(_logFilePath, _logBufer, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
        }

        private static void RestoreFilesFromDB()
        {
            int filesRestored = 0;

            var dbFilePaths = _sqlite.Select_FilePaths();

            var filePaths = Directory.EnumerateFiles(_rootFolderPath, "*.*", SearchOption.AllDirectories)
                            .Where(s => _fileExtensions.Contains(Path.GetExtension(s).ToLower())).ToList();

            for (int i = 0; i < dbFilePaths.Count; i++)
            {
                bool fileDeleted = true;

                foreach (var path in filePaths)
                {
                    if (dbFilePaths[i].FilePath.Equals(path))
                    {
                        fileDeleted = false;
                        long lastWrite = File.GetLastWriteTime(dbFilePaths[i].FilePath).ToFileTimeUtc();

                        if (dbFilePaths[i].LastWriteTimeUtc != lastWrite)
                        {
                            if (DisplayPromptYesNo($"'{dbFilePaths[i].FilePath}' was modified. Restore from db? (y/n):"))
                            {
                                filesRestored++;
                                string targetDir = Path.GetDirectoryName(dbFilePaths[i].FilePath);

                                if (!Directory.Exists(targetDir)) { Directory.CreateDirectory(targetDir); }

                                string base64 = Encoding.ASCII.GetString(_sqlite.Select_Base64String(dbFilePaths[i]));
                                File.WriteAllBytes(dbFilePaths[i].FilePath, ConvertBase64StringToBytes(base64));

                                // update last write timestamp
                                dbFilePaths[i].LastWriteTimeUtc = File.GetLastWriteTime(dbFilePaths[i].FilePath).ToFileTimeUtc();
                                _sqlite.Update_LastWriteTimeUtc(dbFilePaths[i]);

                                ConsolePrint($"[RESTORED - MODIFIED]  '{dbFilePaths[i].FilePath}'", MessageType.FileRestored);
                            }
                        }

                        break;
                    }
                }

                if (fileDeleted)
                {
                    if (DisplayPromptYesNo($"'{dbFilePaths[i].FilePath}' was deleted. Restore from db? (y/n):"))
                    {
                        filesRestored++;
                        string targetDir = Path.GetDirectoryName(dbFilePaths[i].FilePath);

                        if (!Directory.Exists(targetDir)) { Directory.CreateDirectory(targetDir); }

                        string base64 = Encoding.ASCII.GetString(_sqlite.Select_Base64String(dbFilePaths[i]));
                        File.WriteAllBytes(dbFilePaths[i].FilePath, ConvertBase64StringToBytes(base64));

                        dbFilePaths[i].LastWriteTimeUtc = File.GetLastWriteTime(dbFilePaths[i].FilePath).ToFileTimeUtc();
                        _sqlite.Update_LastWriteTimeUtc(dbFilePaths[i]);

                        ConsolePrint($"[RESTORED - DELETED]  '{dbFilePaths[i].FilePath}' ({dbFilePaths[i].GetFileHashShort()})", MessageType.FileRestored);
                    }
                }
            }

            if (filesRestored > 0)
            {
                ConsolePrint($"Files restored: {filesRestored}");
            }
            else { ConsolePrint("Restore not required"); }
        }

        private static void HashAndBackupFilesToDB()
        {
            int filesModified = 0;
            int filesDeleted = 0;
            int filesNew = 0;

            var dbFilePaths = _sqlite.Select_FilePaths();

            var filePaths = Directory.EnumerateFiles(_rootFolderPath, "*.*", SearchOption.AllDirectories)
                            .Where(s => _fileExtensions.Contains(Path.GetExtension(s).ToLower())).ToList();

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
                            if (DisplayPromptYesNo($"'{dbFilePaths[i].FilePath}' was modified. Update db? (y/n):"))
                            {
                                dbFilePaths[i].LastWriteTimeUtc = lastWrite;
                                string tmpHash = CalculateHash(path);

                                if (dbFilePaths[i].FileHash.Equals(tmpHash))
                                {
                                    filesModified++; // increase modified files counter, so that new lastWrite value is updated in db
                                    ConsolePrint($"'{path}' has different last write timestamp, but hashes are identical");
                                }
                                else
                                {
                                    filesModified++;
                                    dbFilePaths[i].FileHash = tmpHash;

                                    if (!dbFilePaths[i].HashAlgorithm.Equals(_defaultHashAlgorithm.ToString()))
                                    {
                                        ConsolePrint($"'{path}' was hashed using {dbFilePaths[i].HashAlgorithm} - update using {_defaultHashAlgorithm.ToString()}");
                                        dbFilePaths[i].HashAlgorithm = _defaultHashAlgorithm.ToString();
                                    }
                                }

                                _sqlite.Update_FilePath(dbFilePaths[i]);

                                // update file as base64 string
                                _sqlite.Update_Base64String(new Base64StringsDBModel() { Base64String = ConvertBytesToBase64String(File.ReadAllBytes(dbFilePaths[i].FilePath)), FK_FilePathID = dbFilePaths[i].FilePathID });

                                ConsolePrint($"[MODIFIED]  '{path}' ({dbFilePaths[i].GetFileHashShort()})", MessageType.FileModified);
                            }
                        }

                        break;
                    }
                }

                if (fileIsDeleted)
                {
                    if (DisplayPromptYesNo($"'{dbFilePaths[i].FilePath}' was deleted. Update db? (y/n):"))
                    {
                        filesDeleted++;
                        _sqlite.Delete_FilePath(dbFilePaths[i]);

                        // delete base64 string
                        _sqlite.Delete_Base64String(dbFilePaths[i]);

                        ConsolePrint($"[DELETED]  '{dbFilePaths[i].FilePath}' ({dbFilePaths[i].GetFileHashShort()})", MessageType.FileDeleted);
                        dbFilePaths.RemoveAt(i);
                        i--; // decreasing index, because of previosly deleted element
                    }
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
                    if (DisplayPromptYesNo($"'{filePaths[i]}' was added. Update db? (y/n):"))
                    {
                        filesNew++;

                        dbFilePaths.Add(new FilePathsDBModel()
                        {
                            FilePath = filePaths[i],
                            LastWriteTimeUtc = File.GetLastWriteTime(filePaths[i]).ToFileTimeUtc(),
                            HashAlgorithm = _defaultHashAlgorithm.ToString(),
                            FileHash = CalculateHash(filePaths[i])
                        });

                        _sqlite.Insert_FilePath(dbFilePaths.Last());

                        // select FilePathID from last insert operation
                        int filePathID = _sqlite.Select_FilePathID(dbFilePaths.Last());

                        // add file as base64 string
                        _sqlite.Insert_Base64String(new Base64StringsDBModel() { Base64String = ConvertBytesToBase64String(File.ReadAllBytes(dbFilePaths.Last().FilePath)), FK_FilePathID = filePathID });

                        ConsolePrint($"[NEW]  '{filePaths[i]}' ({dbFilePaths.Last().GetFileHashShort()})", MessageType.FileNew);
                    }
                }
            }

            if (filesModified > 0 || filesDeleted > 0 || filesNew > 0)
            {
                if (filesModified > 0) { ConsolePrint($"Files modified: {filesModified}"); }
                if (filesDeleted > 0) { ConsolePrint($"Files deleted: {filesDeleted}"); }
                if (filesNew > 0) { ConsolePrint($"Files new: {filesNew}"); }
            }
            else { ConsolePrint("Update not required"); }
        }

        private static bool DisplayPromptYesNo(string message)
        {
            bool result = true;

            if (_dbUpdatePrompt)
            {
                Console.WriteLine(message);

                if (Console.ReadKey(true).Key == ConsoleKey.Y) { result = true; }
                else { result = false; }
            }

            return (result);
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
                else if (messageType == MessageType.FileRestored)
                {
                    Console.BackgroundColor = ConsoleColor.Cyan;
                    Console.ForegroundColor = ConsoleColor.Black;
                }
                else if (messageType == MessageType.Exception)
                {
                    Console.BackgroundColor = ConsoleColor.DarkRed;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                }
            }

            Console.WriteLine(message);
            Console.ResetColor();
            _logBufer.Add($"{DateTime.Now.ToString(LOG_TIMESTAMP_FORMAT).PadRight(25)} {message}");
        }

        private static string ConvertBytesToBase64String(byte[] bytes) => (Convert.ToBase64String(bytes));

        private static byte[] ConvertBase64StringToBytes(string base64String) => (Convert.FromBase64String(base64String));

        public static string CalculateHash(string filePath)
        {
            byte[] result = null;

            using (var sr = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                if (_defaultHashAlgorithm == HashAlgorithm.SHA512)
                {
                    using (var hash = SHA512.Create())
                    {
                        result = hash.ComputeHash(sr);
                    }
                }
                else if (_defaultHashAlgorithm == HashAlgorithm.SHA384)
                {
                    using (var hash = SHA384.Create())
                    {
                        result = hash.ComputeHash(sr);
                    }
                }
                else if (_defaultHashAlgorithm == HashAlgorithm.SHA256)
                {
                    using (var hash = SHA256.Create())
                    {
                        result = hash.ComputeHash(sr);
                    }
                }
                else if (_defaultHashAlgorithm == HashAlgorithm.SHA1)
                {
                    using (var hash = SHA1.Create())
                    {
                        result = hash.ComputeHash(sr);
                    }
                }
            }

            return (BitConverter.ToString(result).Replace("-", string.Empty).ToLower());
        }
    }
}
