namespace SourceGit.Commands
{
    public class MultiPackIndexWrite : Command
    {
        public MultiPackIndexWrite(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
            RaiseError = false;
            Args = "multi-pack-index write";
        }
    }
}
