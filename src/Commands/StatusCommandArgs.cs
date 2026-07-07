namespace SourceGit.Commands
{
    public partial class Command
    {
        // Always pass an explicit -u mode so SourceGit does not depend on the user's status.showUntrackedFiles default.
        protected const string StatusAllUntracked = "status -uall";
        protected const string StatusNoUntracked = "status -uno";

        protected static string GetStatusUntrackedArg(bool includeUntracked)
        {
            return includeUntracked ? StatusAllUntracked : StatusNoUntracked;
        }
    }
}
