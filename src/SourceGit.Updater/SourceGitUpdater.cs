using System.Diagnostics;
using System.IO.Compression;

namespace SourceGit.Updater;

public class SourceGitUpdater
{
    private const string DefaultExeName = "SourceGit.exe";
    private const string KeepSubDir = "data";
    private const string PackageSearchPattern = "sourcegit_*.zip";
    private const string UpdaterExeName = "sourcegit-updater.exe";
    private const string UpdaterSearchPattern = "sourcegit-updater*.exe";
    private const string PackageSourceGitRoot = "SourceGit/";

    private readonly string[] _args;
    private readonly UpdaterUI _ui;
    private string _packagePath = string.Empty;
    private string _exeName = DefaultExeName;
    private string _targetPath = string.Empty;
    private string _tempPath = string.Empty;
    private string _backupPath = string.Empty;
    private string _newVersionPath = string.Empty;

    public SourceGitUpdater(string[] args)
    {
        _args = args;
        _ui = new UpdaterUI();
    }

    public void Run()
    {
        _ui.CreateWindow();

        Task.Run(async () =>
        {
            try
            {
                await OnFormLoaded();
            }
            catch (Exception ex)
            {
                _ui.ShowMessageBox($"SourceGit 更新发生严重错误：{ex.Message}\n\n请尝试重新运行 SourceGit 更新程序。", "错误");
                RestoreTargetDirThenExit(-1);
            }
        });

        _ui.RunMessageLoop();
    }

    private async Task OnFormLoaded()
    {
        InitializePaths();

        using var fileLock = await TryAcquireFileLock(Path.Combine(RootPath, "sourcegit_updater"));

        await RetryAction(() =>
        {
            FileUtils.AutoRetryAction(CleanupOldUpdaters);
            return Task.CompletedTask;
        }, "清理旧版本 SourceGit 更新程序失败，请检查是否正在运行。");

        await RunUpdateProcess();

        string exePath = Path.Combine(_targetPath, _exeName);
        if (File.Exists(exePath))
        {
            UpdateUI("更新完成，正在启动 SourceGit...", 100);
            await Task.Delay(500);
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath)
            });
        }
        else
        {
            _ui.ShowMessageBox("错误：找不到 SourceGit 主程序 " + exePath, "错误");
        }

        Environment.Exit(0);
    }

    private void InitializePaths()
    {
        var options = ParseArguments(_args);

        _exeName = options.ExeName;
        _packagePath = ResolvePackagePath(options.PackagePath);
        _targetPath = ResolveTargetPath(options.TargetPath);

        _tempPath = _targetPath + "_Temp";
        _backupPath = _targetPath + "_Backup";
        _newVersionPath = _targetPath + "_New";

        if (!File.Exists(_packagePath))
            throw new FileNotFoundException($"未找到 SourceGit 更新包：{_packagePath}");

        bool canRecover = Directory.Exists(_backupPath) || Directory.Exists(_newVersionPath);
        if (!Directory.Exists(_targetPath) && !canRecover)
            throw new DirectoryNotFoundException($"未找到 SourceGit 安装目录：{_targetPath}");
    }

    private async Task<IDisposable> TryAcquireFileLock(string fileName)
    {
        IDisposable? lockFile = null;
        await RetryAction(() =>
        {
            var lockFileName = $"{fileName}.lock";
            var dir = Path.GetDirectoryName(lockFileName);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            FileUtils.AutoRetryAction(() =>
            {
                try
                {
                    lockFile = new FileStream(lockFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
                }
                catch (Exception)
                {
                    throw new InvalidOperationException("其他 SourceGit 更新程序正在运行中");
                }
            });
            return Task.CompletedTask;
        }, "其他 SourceGit 更新程序正在运行，无法同时进行多个更新。请关闭其他更新程序后重试。");

        Debug.Assert(lockFile != null);
        return lockFile;
    }

    private async Task RunUpdateProcess()
    {
        string backupDataPath = Path.Combine(_backupPath, KeepSubDir);
        string targetDataPath = Path.Combine(_targetPath, KeepSubDir);

        int startStep = DetermineStartStep(_targetPath, _backupPath, _newVersionPath, backupDataPath);

        if (startStep <= 1)
        {
            await RetryAction(async () =>
            {
                if (Directory.Exists(_tempPath))
                    Directory.Delete(_tempPath, true);

                await Task.Run(() => ExtractZipFiles(_packagePath, _tempPath));

                var tempDataPath = Path.Combine(_tempPath, KeepSubDir);
                if (Directory.Exists(tempDataPath))
                    Directory.Delete(tempDataPath, true);

                UpdateUI("解压完成", 90);
            }, "解压 SourceGit 更新包失败，请检查磁盘空间或权限。");
        }

        if (startStep <= 2)
        {
            UpdateUI("正在准备 SourceGit 新版本目录...", 91);
            await RetryAction(() =>
            {
                FileUtils.MoveDirectoryWithRetry(_tempPath, _newVersionPath, false);
                return Task.CompletedTask;
            }, "无法重命名 SourceGit 临时目录。");
        }

        if (startStep <= 3)
        {
            UpdateUI("正在备份当前 SourceGit 版本...", 92);
            await RetryAction(() =>
            {
                FileUtils.MoveDirectoryWithRetry(_targetPath, _backupPath, false);
                return Task.CompletedTask;
            }, "无法备份旧版本文件。请确保 SourceGit 主程序已经完全关闭！");
        }

        if (startStep <= 4)
        {
            UpdateUI("正在部署 SourceGit 新版本...", 93);
            await RetryAction(() =>
            {
                FileUtils.MoveDirectoryWithRetry(_newVersionPath, _targetPath, false);
                return Task.CompletedTask;
            }, "无法部署 SourceGit 新版本文件。");
        }

        if (startStep <= 5)
        {
            UpdateUI("正在移动 SourceGit 用户数据...", 94);
            await RetryAction(() =>
            {
                FileUtils.MoveDirectoryWithRetry(backupDataPath, targetDataPath, true);
                return Task.CompletedTask;
            }, "移动 SourceGit 用户数据失败。");
        }

        if (startStep <= 6)
        {
            UpdateUI("正在清理 SourceGit 临时文件...", 94);
            await RetryAction(() =>
            {
                int totalFiles = 0;
                int deletedFiles = 0;

                var dirsToDelete = new List<string>();
                if (Directory.Exists(_tempPath))
                    dirsToDelete.Add(_tempPath);
                if (Directory.Exists(_newVersionPath))
                    dirsToDelete.Add(_newVersionPath);
                if (Directory.Exists(_backupPath))
                    dirsToDelete.Add(_backupPath);

                foreach (string dir in dirsToDelete)
                {
                    if (Directory.Exists(dir))
                        totalFiles += Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
                }

                foreach (string dir in dirsToDelete)
                    DeleteDirectoryWithProgress(dir, totalFiles, ref deletedFiles);

                UpdateUI("SourceGit 临时文件清理完成", 100);
                return Task.CompletedTask;
            }, "清理 SourceGit 临时文件失败（可手动删除）。");
        }
    }

    private void ExtractZipFiles(string zipPath, string tempPath)
    {
        UpdateUI("正在读取 SourceGit 更新包...", 0);

        using MemoryStream memoryStream = new();
        using (FileStream fileStream = new(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            long totalBytes = Math.Max(1, fileStream.Length);
            long readBytes = 0;
            byte[] buffer = new byte[81920];
            int bytesRead;

            while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                memoryStream.Write(buffer, 0, bytesRead);
                readBytes += bytesRead;
                double progress = readBytes * 5.0 / totalBytes;
                UpdateUI("正在读取 SourceGit 更新包...", progress);
            }
        }

        memoryStream.Position = 0;
        UpdateUI("SourceGit 更新包读取完成，正在准备解压...", 5);

        using var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read, leaveOpen: false);
        ValidateArchiveLayout(archive);

        var updaterEntry = archive.Entries.First(e => NormalizeZipPath(e.FullName).Equals(UpdaterExeName, StringComparison.OrdinalIgnoreCase));
        UpdateUpdaterIfNeeded(updaterEntry);

        string fullTempPath = Path.GetFullPath(tempPath);
        string fullTempRoot = EnsureTrailingSeparator(fullTempPath);
        int totalFiles = archive.Entries.Count;
        int currentFile = 0;

        foreach (var entry in archive.Entries)
        {
            currentFile++;

            string normalizedName = NormalizeZipPath(entry.FullName);
            if (!normalizedName.StartsWith(PackageSourceGitRoot, StringComparison.OrdinalIgnoreCase) ||
                normalizedName.Equals(PackageSourceGitRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relativeName = normalizedName[PackageSourceGitRoot.Length..];
            relativeName = ValidateZipRelativeName(relativeName, string.IsNullOrEmpty(entry.Name));

            double progress = 5 + currentFile * 85.0 / totalFiles;
            UpdateUI($"正在解压 SourceGit: ({currentFile}/{totalFiles}) {TruncateFileName(relativeName, 50)}", progress);

            string destinationPath = Path.GetFullPath(Path.Combine(tempPath, relativeName));
            if (!destinationPath.Equals(fullTempPath, StringComparison.OrdinalIgnoreCase) &&
                !destinationPath.StartsWith(fullTempRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("SourceGit 更新包解压路径不合法");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, true);
            File.SetLastWriteTimeUtc(destinationPath, DateTime.UtcNow);
        }
    }

    private void ValidateArchiveLayout(ZipArchive archive)
    {
        var allEntries = archive.Entries
            .Select(e => NormalizeZipPath(e.FullName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!allEntries.Contains(UpdaterExeName))
            throw new InvalidOperationException($"SourceGit 更新包缺少根目录 {UpdaterExeName}");

        if (!allEntries.Contains($"{PackageSourceGitRoot}{DefaultExeName}"))
            throw new InvalidOperationException($"SourceGit 更新包缺少 {PackageSourceGitRoot}{DefaultExeName}");
    }

    private void UpdateUpdaterIfNeeded(ZipArchiveEntry updaterEntry)
    {
        string currentExePath = CurrentExeFilePath;
        string currentExeFileName = Path.GetFileName(currentExePath);
        string version = ExtractPackageVersion(_packagePath);
        string newUpdaterName = $"sourcegit-updater_{version}.exe";

        if (currentExeFileName.Equals(newUpdaterName, StringComparison.OrdinalIgnoreCase))
            return;

        UpdateUI("正在检查 SourceGit 更新程序版本...", 5);

        byte[] currentExeBytes = File.ReadAllBytes(currentExePath);
        byte[] newExeBytes;
        using (var entryStream = updaterEntry.Open())
        using (var ms = new MemoryStream())
        {
            entryStream.CopyTo(ms);
            newExeBytes = ms.ToArray();
        }

        if (currentExeBytes.SequenceEqual(newExeBytes))
            return;

        UpdateUI("正在解压新版本 SourceGit 更新程序...", 5);

        string newUpdaterPath = Path.Combine(RootPath, newUpdaterName);
        string tmpFilePath = Path.GetTempFileName();
        File.WriteAllBytes(tmpFilePath, newExeBytes);
        File.Move(tmpFilePath, newUpdaterPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = newUpdaterPath,
            UseShellExecute = true,
            WorkingDirectory = RootPath
        };

        foreach (string arg in _args)
            startInfo.ArgumentList.Add(arg);

        Process.Start(startInfo);
        Environment.Exit(0);
    }

    private void DeleteDirectoryWithProgress(string path, int totalFiles, ref int deletedFiles)
    {
        if (!Directory.Exists(path))
            return;

        DirectoryInfo dir = new(path);
        double oldProgress = -1;
        foreach (FileInfo file in dir.GetFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                file.Delete();
                deletedFiles++;

                if (totalFiles > 0)
                {
                    double progress = 94 + deletedFiles * 6.0 / totalFiles;
                    progress = Math.Min(progress, 100.0);
                    if (Math.Abs(progress - oldProgress) >= 0.1)
                    {
                        oldProgress = progress;
                        UpdateUI("正在清理 SourceGit 旧版本和临时文件", progress);
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        Directory.Delete(path, true);
    }

    private void RestoreTargetDirThenExit(int exitCode)
    {
        if (!string.IsNullOrEmpty(_targetPath) && !Directory.Exists(_targetPath))
        {
            if (!string.IsNullOrEmpty(_backupPath) && Directory.Exists(_backupPath))
            {
                FileUtils.MoveDirectoryNoExcept(_backupPath, _targetPath);
                Environment.Exit(exitCode);
            }

            if (!string.IsNullOrEmpty(_newVersionPath) && Directory.Exists(_newVersionPath))
            {
                FileUtils.MoveDirectoryNoExcept(_newVersionPath, _targetPath);
                Environment.Exit(exitCode);
            }
        }

        Environment.Exit(exitCode);
    }

    private static int DetermineStartStep(string targetPath, string backupPath, string newVersionPath, string backupDataPath)
    {
        bool targetExists = Directory.Exists(targetPath);
        bool backupExists = Directory.Exists(backupPath);
        bool newVersionExists = Directory.Exists(newVersionPath);

        if (targetExists)
        {
            if (!backupExists && !newVersionExists)
                return 1;

            if (backupExists)
                return Directory.Exists(backupDataPath) ? 5 : 6;

            return 3;
        }

        if (backupExists && newVersionExists)
            return 4;

        if (backupExists)
            FileUtils.MoveDirectoryWithRetry(backupPath, targetPath, false);
        else if (newVersionExists)
            FileUtils.MoveDirectoryWithRetry(newVersionPath, targetPath, false);
        else
            Directory.CreateDirectory(targetPath);

        return 1;
    }

    private async Task RetryAction(Func<Task> action, string errorMessage)
    {
        while (true)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex)
            {
                int result = _ui.ShowRetryDialog(
                    $"{errorMessage}\n具体错误：{ex.Message}\n\n点击【重试】再次尝试，点击【取消】退出程序。",
                    "SourceGit 更新被中断");

                if (result != 4)
                    RestoreTargetDirThenExit(-2);
            }
        }
    }

    private void UpdateUI(string status, double progress)
    {
        _ui.UpdateStatus(status, progress);
    }

    private static void CleanupOldUpdaters()
    {
        var currentExeName = Path.GetFileName(CurrentExeFilePath);
        var updaterFiles = Directory.GetFiles(RootPath, UpdaterSearchPattern, SearchOption.TopDirectoryOnly);

        foreach (var file in updaterFiles)
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals(currentExeName, StringComparison.OrdinalIgnoreCase))
                continue;

            File.Delete(file);
        }
    }

    private static (string? PackagePath, string? TargetPath, string ExeName) ParseArguments(string[] args)
    {
        string? packagePath = null;
        string? targetPath = null;
        string exeName = DefaultExeName;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--package", StringComparison.OrdinalIgnoreCase))
            {
                packagePath = ReadOptionValue(args, ref i, "--package");
            }
            else if (arg.Equals("--target", StringComparison.OrdinalIgnoreCase))
            {
                targetPath = ReadOptionValue(args, ref i, "--target");
            }
            else if (arg.Equals("--exe", StringComparison.OrdinalIgnoreCase))
            {
                exeName = ReadOptionValue(args, ref i, "--exe");
            }
            else
            {
                throw new ArgumentException($"未知 SourceGit 更新参数：{arg}");
            }
        }

        return (packagePath, targetPath, exeName);
    }

    private static string ReadOptionValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{optionName} 缺少参数值");

        index++;
        return args[index];
    }

    private static string ResolvePackagePath(string? packagePath)
    {
        if (!string.IsNullOrWhiteSpace(packagePath))
            return GetFullPathFromRoot(packagePath);

        var zipFiles = Directory.GetFiles(RootPath, PackageSearchPattern, SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (zipFiles.Count == 0)
            throw new FileNotFoundException($"未找到 SourceGit 更新包：{Path.Combine(RootPath, PackageSearchPattern)}");

        return zipFiles[^1].FullName;
    }

    private static string ResolveTargetPath(string? targetPath)
    {
        if (!string.IsNullOrWhiteSpace(targetPath))
            return TrimDirectoryPath(GetFullPathFromRoot(targetPath));

        return TrimDirectoryPath(Path.Combine(RootPath, "SourceGit"));
    }

    private static string RootPath
    {
        get
        {
            var module = Process.GetCurrentProcess().MainModule;
            Debug.Assert(module != null);
            return Path.GetDirectoryName(module.FileName) ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static string CurrentExeFilePath
    {
        get
        {
            var module = Process.GetCurrentProcess().MainModule;
            Debug.Assert(module != null);
            return module.FileName;
        }
    }

    private static string GetFullPathFromRoot(string path)
    {
        return Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(RootPath, path));
    }

    private static string TrimDirectoryPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeZipPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static string ValidateZipRelativeName(string relativeName, bool isDirectory)
    {
        if (isDirectory)
            relativeName = relativeName.TrimEnd('/');

        if (string.IsNullOrEmpty(relativeName))
            throw new InvalidOperationException("SourceGit 更新包包含空路径");

        string[] segments = relativeName.Split('/');
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment) || segment == "." || segment == "..")
                throw new InvalidOperationException($"SourceGit 更新包包含非法路径：{relativeName}");

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
                throw new InvalidOperationException($"SourceGit 更新包包含非法 Windows 路径：{relativeName}");

            if (segment.Contains(':'))
                throw new InvalidOperationException($"SourceGit 更新包包含非法 Windows 路径：{relativeName}");

            if (IsWindowsReservedDeviceName(segment))
                throw new InvalidOperationException($"SourceGit 更新包包含 Windows 保留设备名：{relativeName}");
        }

        return relativeName;
    }

    private static bool IsWindowsReservedDeviceName(string segment)
    {
        string name = segment;
        int dotIndex = name.IndexOf('.');
        if (dotIndex >= 0)
            name = name[..dotIndex];

        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Length != 4)
            return false;

        char prefix0 = char.ToUpperInvariant(name[0]);
        char prefix1 = char.ToUpperInvariant(name[1]);
        char prefix2 = char.ToUpperInvariant(name[2]);
        char suffix = name[3];

        return suffix is >= '1' and <= '9' &&
            ((prefix0 == 'C' && prefix1 == 'O' && prefix2 == 'M') ||
             (prefix0 == 'L' && prefix1 == 'P' && prefix2 == 'T'));
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static string ExtractPackageVersion(string packagePath)
    {
        string name = Path.GetFileNameWithoutExtension(packagePath);
        if (name.StartsWith("sourcegit_", StringComparison.OrdinalIgnoreCase))
            name = name["sourcegit_".Length..];

        if (string.IsNullOrWhiteSpace(name))
            name = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

        foreach (char invalidChar in Path.GetInvalidFileNameChars())
            name = name.Replace(invalidChar, '_');

        return name;
    }

    private static string TruncateFileName(string fileName, int maxLength)
    {
        if (string.IsNullOrEmpty(fileName) || fileName.Length <= maxLength)
            return fileName;

        int headLength = (maxLength - 3) / 3;
        int tailLength = maxLength - 3 - headLength;
        return $"{fileName[..headLength]}...{fileName[^tailLength..]}";
    }
}
