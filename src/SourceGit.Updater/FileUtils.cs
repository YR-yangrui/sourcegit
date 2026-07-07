namespace SourceGit.Updater;

public static class FileUtils
{
    public static void AutoRetryAction(Action action, int retryCount = 10, int delayMilliseconds = 100)
    {
        for (int i = 0; i < retryCount; i++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception)
            {
                if (i == retryCount - 1)
                    throw;
                Thread.Sleep(delayMilliseconds);
            }
        }
    }

    public static void MoveDirectoryWithRetry(string srcPath, string destPath, bool onlyWhenExists, int retryCount = 10)
    {
        AutoRetryAction(() =>
        {
            if (onlyWhenExists)
            {
                if (Directory.Exists(srcPath))
                    Directory.Move(srcPath, destPath);
            }
            else
            {
                Directory.Move(srcPath, destPath);
            }
        }, retryCount);
    }

    public static void FileDeleteNoExcept(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (Exception)
        {
            // ignore
        }
    }

    public static void MoveDirectoryNoExcept(string srcPath, string destPath)
    {
        try
        {
            MoveDirectoryWithRetry(srcPath, destPath, false);
        }
        catch (Exception)
        {
            // ignore
        }
    }
}
