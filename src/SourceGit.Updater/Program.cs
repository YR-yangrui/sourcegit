namespace SourceGit.Updater;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        new SourceGitUpdater(args).Run();
    }
}
