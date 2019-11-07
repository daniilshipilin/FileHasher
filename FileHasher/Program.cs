using FileHasher.Models;
using FileHasher.SQL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

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

        enum ExitCode
        {
            Success,
            Exception
        }

        enum MessageType
        {
            DEFAULT,
            FILE_MODIFIED,
            FILE_DELETED,
            FILE_NEW,
            FILE_RESTORED,
            EXCEPTION
        }

        const string LOG_TIMESTAMP_FORMAT = "yyyy-MM-dd HH:mm:ss.fff";

        static readonly string _logFilePath = $"{ProgramBaseDirectory}Output.log";
        static List<string> _logBuffer = new List<string>();

        static List<string> LogBuffer
        {
            get => _logBuffer;
            set { lock (_logLocker) { _logBuffer = value; } }
        }

        static List<FilePathsDBModel> DBFilePaths
        {
            get => _dbFilePaths;
            set { lock (_dbFilePathsLocker) { _dbFilePaths = value; } }
        }

        static SQLiteDBAccess _sqlite;

        static string _rootFolderPath;
        static string[] _fileExtensions;
        static bool _waitBeforeExit = false;
        static bool _folderCleanupScheduled = false;
        static bool _dbUpdatePrompt = false;
        static bool _dbOptimizationScheduled = false;
        static bool _hashAndBackupFilesToDB = false;
        static bool _restoreFilesFromDB = false;
        static int _maxThreadCount = 4;

        static Task[] _queue;
        static readonly object _inputLocker = new object();
        static readonly object _logLocker = new object();
        static readonly object _dbFilePathsLocker = new object();

        static List<FilePathsDBModel> _dbFilePaths;
        static string[] _filePaths;
        static int _filesRestored;
        static int _filesModified;
        static int _filesDeleted;
        static int _filesNew;

        static int _programExitCode = (int)ExitCode.Success;

        static int Main(string[] args)
        {
            // set console output encoding
            Console.OutputEncoding = Encoding.Unicode;

            try
            {
                // parse args
                if ((args.Length == 1 && args[0].Equals("/?")) || args.Length < 2)
                {
                    Console.WriteLine(ProgramHeader);
                    Console.WriteLine("Program usage:");
                    Console.WriteLine($"  {ProgramName} \"Root folder path\" \"File extensions list (comma separated)\" [-backup] [-restore] [-threads 'qty'] [-wait] [-clean] [-prompt] [-optimize]");
                    return ((int)ExitCode.Success);
                }

                ConsolePrint(ProgramHeader);

                if (args.Length >= 1) { _rootFolderPath = FormatPath(args[0]); }

                if (args.Length >= 2) { _fileExtensions = args[1].Split(','); }

                if (args.Length >= 3)
                {
                    for (int i = 2; i < args.Length; i++)
                    {
                        if (args[i].Equals("-wait")) { _waitBeforeExit = true; }
                        else if (args[i].Equals("-clean")) { _folderCleanupScheduled = true; }
                        else if (args[i].Equals("-prompt")) { _dbUpdatePrompt = true; }
                        else if (args[i].Equals("-optimize")) { _dbOptimizationScheduled = true; }
                        else if (args[i].Equals("-backup")) { _hashAndBackupFilesToDB = true; }
                        else if (args[i].Equals("-restore")) { _restoreFilesFromDB = true; }
                        else if (args[i].Equals("-threads") && int.TryParse(args[i + 1], out int threads))
                        {
                            if (threads < 1 || threads > 16)
                            {
                                throw new Exception("Thread qty should be between 1 and 16");
                            }

                            _maxThreadCount = threads;
                            ++i;
                        }
                    }
                }

                if (!Directory.Exists(_rootFolderPath))
                {
                    throw new DirectoryNotFoundException($"Directory '{_rootFolderPath}' doesn't exist");
                }

                // set root folder path in the FilePathsDBModel class
                FilePathsDBModel.SetRootFolderPath(_rootFolderPath);

                // print args
                ConsolePrint($"Root folder path = {_rootFolderPath}");
                ConsolePrint($"File extensions = {string.Join(",", _fileExtensions)}");
                ConsolePrint($"Threads = {_maxThreadCount}");
                ConsolePrint($"Hash and backup files to DB = {_hashAndBackupFilesToDB}");
                ConsolePrint($"Restore files from DB = {_restoreFilesFromDB}");
                ConsolePrint($"DB update prompt = {_dbUpdatePrompt}");
                ConsolePrint($"Folder cleanup scheduled = {_folderCleanupScheduled}");
                ConsolePrint($"DB optimization scheduled = {_dbOptimizationScheduled}");
                ConsolePrint($"Wait before exit = {_waitBeforeExit}");

                _sqlite = new SQLiteDBAccess();

                // get all the file paths from db and enumerate all existing file paths from root folder
                DBFilePaths = _sqlite.Select_FilePaths();
                _filePaths = Directory.EnumerateFiles(_rootFolderPath, "*.*", SearchOption.AllDirectories)
                                .Where(s => _fileExtensions.Contains(Path.GetExtension(s).ToLower())).ToArray();

                var sw = Stopwatch.StartNew();

                // two modes - restore or hash and backup
                if (_hashAndBackupFilesToDB) { HashAndBackupFilesToDB(); }
                else if (_restoreFilesFromDB) { RestoreFilesFromDB(); }

                sw.Stop();
                ConsolePrint($"Time elapsed: {sw.ElapsedMilliseconds} ms.");
                ConsolePrint($"Total file paths in DB: {_sqlite.Select_CountRecords_FilePaths()}");
                ConsolePrint($"DB revision: {_sqlite.Select_DatabaseRevision()}");

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
            }
            catch (Exception ex)
            {
                ConsolePrint(ex.ToString(), MessageType.EXCEPTION);
                _programExitCode = (int)ExitCode.Exception;
            }
            finally
            {
                try
                {
                    if (LogBuffer.Count != 0)
                    {
                        // write log file
                        File.AppendAllLines(_logFilePath, LogBuffer, new UTF8Encoding(false));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }

            if (_waitBeforeExit)
            {
                Console.WriteLine("Press ENTER to exit");
                Console.ReadLine();
            }

            Console.WriteLine($"Exitcode: {_programExitCode}");
            return (_programExitCode);
        }

        private static void RestoreFilesFromDB()
        {
            _queue = (DBFilePaths.Count < _maxThreadCount) ? new Task[DBFilePaths.Count] : new Task[_maxThreadCount];

            foreach (var db in DBFilePaths)
            {
                bool taskCreated = false;

                while (!taskCreated)
                {
                    // iterate through task queue to find free position
                    for (int i = 0; i < _queue.Length; i++)
                    {
                        if (_queue[i] == null || _queue[i].Status == TaskStatus.RanToCompletion)
                        {
                            _queue[i] = Task.Run(() => RestoreFilesFromDB_Task(db));
                            taskCreated = true;
                            break;
                        }
                    }

                    // queue is full - wait till one of the tasks is completed
                    if (!taskCreated)
                    {
                        Task.WaitAny(_queue);
                    }
                }
            }

            // wait till all tasks are completed
            Task.WaitAll(_queue);

            if (_filesRestored > 0)
            {
                ConsolePrint($"Files restored: {_filesRestored}");
            }
            else { ConsolePrint("Restore not required"); }
        }

        private static void HashAndBackupFilesToDB()
        {
            _queue = (DBFilePaths.Count < _maxThreadCount) ? new Task[DBFilePaths.Count] : new Task[_maxThreadCount];

            // find deleted/modified files
            for (int i = 0; i < DBFilePaths.Count; i++)
            {
                bool taskCreated = false;

                while (!taskCreated)
                {
                    // iterate through task queue to find free position
                    for (int x = 0; x < _queue.Length; x++)
                    {
                        if (_queue[x] == null || _queue[x].Status == TaskStatus.RanToCompletion)
                        {
                            int index = i; // save copy of index variable
                            _queue[x] = Task.Run(() => FindDeletedFile_Task(DBFilePaths[index]));
                            taskCreated = true;
                            break;
                        }
                    }

                    // queue is full - wait till one of the tasks is completed
                    if (!taskCreated)
                    {
                        Task.WaitAny(_queue);
                    }
                }
            }

            // wait till all tasks are completed
            Task.WaitAll(_queue);

            // update _dbFilePaths list elements (remove null elements)
            for (int i = 0; i < DBFilePaths.Count; i++)
            {
                if (DBFilePaths[i] == null)
                {
                    DBFilePaths.RemoveAt(i);
                    --i;
                }
            }

            _queue = (_filePaths.Length < _maxThreadCount) ? new Task[_filePaths.Length] : new Task[_maxThreadCount];

            // find new files
            foreach (var path in _filePaths)
            {
                bool taskCreated = false;

                while (!taskCreated)
                {
                    // iterate through task queue to find free position
                    for (int i = 0; i < _queue.Length; i++)
                    {
                        if (_queue[i] == null || _queue[i].Status == TaskStatus.RanToCompletion)
                        {
                            _queue[i] = Task.Run(() => FindNewFile_Task(path));
                            taskCreated = true;
                            break;
                        }
                    }

                    // queue is full - wait till one of the tasks is completed
                    if (!taskCreated)
                    {
                        Task.WaitAny(_queue);
                    }
                }
            }

            Task.WaitAll(_queue);

            if (_filesModified > 0 || _filesDeleted > 0 || _filesNew > 0)
            {
                if (_filesModified > 0) { ConsolePrint($"Files modified: {_filesModified}"); }
                if (_filesDeleted > 0) { ConsolePrint($"Files deleted: {_filesDeleted}"); }
                if (_filesNew > 0) { ConsolePrint($"Files new: {_filesNew}"); }
            }
            else { ConsolePrint("Update not required"); }
        }

        private static void RestoreFilesFromDB_Task(FilePathsDBModel db)
        {
            bool fileDeleted = true;

            foreach (var path in _filePaths)
            {
                if (db.FileFullPath.Equals(path))
                {
                    fileDeleted = false;

                    long lastWrite = db.LastWriteTimeUtc;
                    db.GetLastWriteTime();

                    if (db.LastWriteTimeUtc != lastWrite)
                    {
                        string tmpHash = db.FileHash;
                        db.CalculateHash();

                        if (db.FileHash.Equals(tmpHash))
                        {
                            _sqlite.Update_LastWriteTimeUtc(db);
                            ConsolePrint($"'{db.FileFullPath}' has different last write timestamp, but hashes are identical");
                        }
                        else
                        {
                            if (DisplayPromptYesNo($"'{db.FileFullPath}' was modified. Restore from db? (y/n):"))
                            {
                                _filesRestored++;
                                RestoreFile(db.FileFullPath, _sqlite.Select_Blob(db));

                                // update last write timestamp
                                db.GetLastWriteTime();
                                _sqlite.Update_LastWriteTimeUtc(db);

                                ConsolePrint($"'{db.FileFullPath}' ({db.FileHashShort})", MessageType.FILE_RESTORED);
                            }
                        }
                    }

                    break;
                }
            }

            if (fileDeleted)
            {
                if (DisplayPromptYesNo($"'{db.FileFullPath}' was deleted. Restore from db? (y/n):"))
                {
                    _filesRestored++;
                    RestoreFile(db.FileFullPath, _sqlite.Select_Blob(db));

                    // update last write timestamp
                    db.GetLastWriteTime();
                    _sqlite.Update_LastWriteTimeUtc(db);

                    ConsolePrint($"'{db.FileFullPath}' ({db.FileHashShort})", MessageType.FILE_RESTORED);
                }
            }
        }

        private static void FindDeletedFile_Task(FilePathsDBModel db)
        {
            bool fileIsDeleted = true;

            foreach (var path in _filePaths)
            {
                if (db.FileFullPath.Equals(path))
                {
                    fileIsDeleted = false;

                    long lastWrite = db.LastWriteTimeUtc;
                    db.GetLastWriteTime();

                    // update record, if file modification time is different
                    if (db.LastWriteTimeUtc != lastWrite)
                    {
                        string tmpHash = db.FileHash;
                        db.CalculateHash();

                        if (db.FileHash.Equals(tmpHash))
                        {
                            _sqlite.Update_LastWriteTimeUtc(db);
                            ConsolePrint($"'{db.FileFullPath}' has different last write timestamp, but hashes are identical");
                        }
                        else
                        {
                            if (DisplayPromptYesNo($"'{db.FileFullPath}' was modified. Update db? (y/n):"))
                            {
                                _filesModified++;

                                if (!db.HashAlgorithm.Equals(FilePathsDBModel.HASH_ALGORITHM))
                                {
                                    ConsolePrint($"'{db.FileFullPath}' was hashed using {db.HashAlgorithm} - update using {FilePathsDBModel.HASH_ALGORITHM}");
                                    db.HashAlgorithm = FilePathsDBModel.HASH_ALGORITHM;
                                }

                                _sqlite.Update_FilePath(db);

                                // update file blob
                                _sqlite.Update_Blob(new BlobsDBModel() { FK_FilePathID = db.FilePathID, BlobData = File.ReadAllBytes(db.FileFullPath) });

                                ConsolePrint($"'{db.FileFullPath}' ({db.FileHashShort})", MessageType.FILE_MODIFIED);
                            }
                        }
                    }

                    break;
                }
            }

            if (fileIsDeleted)
            {
                if (DisplayPromptYesNo($"'{db.FileFullPath}' was deleted. Update db? (y/n):"))
                {
                    _filesDeleted++;
                    _sqlite.Delete_FilePath(db);

                    // delete file blob
                    _sqlite.Delete_Blob(db);

                    ConsolePrint($"'{db.FileFullPath}' ({db.FileHashShort})", MessageType.FILE_DELETED);

                    // set list element to null in order to remove it later
                    DBFilePaths[DBFilePaths.IndexOf(db)] = null;
                }
            }
        }

        private static void FindNewFile_Task(string path)
        {
            bool fileIsNew = true;

            for (int i = 0; i < DBFilePaths.Count; i++)
            {
                if (path.Equals(DBFilePaths[i].FileFullPath))
                {
                    fileIsNew = false;
                    break;
                }
            }

            if (fileIsNew)
            {
                if (DisplayPromptYesNo($"'{path}' was added. Update db? (y/n):"))
                {
                    _filesNew++;

                    var tmp = new FilePathsDBModel(RemovePathRootFromPath(path));

                    DBFilePaths.Add(tmp);

                    _sqlite.Insert_FilePath(tmp);

                    // select FilePathID from last insert operation
                    int filePathID = _sqlite.Select_FilePathID(tmp);

                    // write file blob to the db
                    _sqlite.Insert_Blob(new BlobsDBModel() { FK_FilePathID = filePathID, BlobData = File.ReadAllBytes(tmp.FileFullPath) });

                    ConsolePrint($"'{tmp.FileFullPath}' ({tmp.FileHashShort})", MessageType.FILE_NEW);
                }
            }
        }

        private static string FormatPath(string path)
        {
            string[] split = Path.GetFullPath(path).Split(new char[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();

            for (int i = 0; i < split.Length; i++)
            {
                sb.Append(split[i]);

                if (i < split.Length - 1 || i == 0)
                {
                    sb.Append(Path.DirectorySeparatorChar);
                }
            }

            return (sb.ToString());
        }

        private static string RemovePathRootFromPath(string path)
        {
            // full path string 'C:\Music\Chipzel\Super Hexagon\01 - Courtesy.mp3'
            // path root string 'C:\Music'
            // return string 'Chipzel\Super Hexagon\01 - Courtesy.mp3'
            return (path.Substring(_rootFolderPath.Length + 1));
        }

        private static bool DisplayPromptYesNo(string message)
        {
            bool result = true;

            if (_dbUpdatePrompt)
            {
                // ensure, that input prompt appears one at a time
                lock (_inputLocker)
                {
                    Console.WriteLine(message);

                    if (Console.ReadKey(true).Key != ConsoleKey.Y)
                    {
                        result = false;
                    }
                }
            }

            return (result);
        }

        private static void ConsolePrint(string message, MessageType messageType = MessageType.DEFAULT)
        {
            if (messageType != MessageType.DEFAULT)
            {
                if (messageType == MessageType.EXCEPTION)
                {
                    Console.BackgroundColor = ConsoleColor.DarkRed;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                }
            }

            if (messageType == MessageType.DEFAULT)
            {
                Console.WriteLine(message);
                LogBuffer.Add($"{DateTime.Now.ToString(LOG_TIMESTAMP_FORMAT).PadRight(25)} {string.Empty.PadRight(15)} {message}");
            }
            else
            {
                Console.WriteLine($"{messageType.ToString()} {message}");
                LogBuffer.Add($"{DateTime.Now.ToString(LOG_TIMESTAMP_FORMAT).PadRight(25)} {messageType.ToString().PadRight(15)} {message}");
            }

            Console.ResetColor();
        }

        private static void RestoreFile(string path, byte[] bytes)
        {
            string targetDir = Path.GetDirectoryName(path);

            if (!Directory.Exists(targetDir)) { Directory.CreateDirectory(targetDir); }

            File.WriteAllBytes(path, bytes);
        }
    }
}
