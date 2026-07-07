namespace SourceGit.Models
{
    public interface IRepository
    {
        bool IsActive { get; }
        bool IsForeground { get; }

        bool MayHaveSubmodules();

        void MarkBranchesDirty();
        void MarkWorktreesDirty();
        void MarkTagsDirty();
        void MarkCommitsDirty();
        void MarkSubmodulesDirty();
        void RefreshBranches();
        void RefreshWorktrees();
        void RefreshTags();
        void RefreshCommits();
        void RefreshSubmodules();
        void MarkWorkingCopyDirty();
        void RefreshWorkingCopyChangesIfDirty();
        void MarkStashesDirty();
        bool RefreshWorkingCopyChanges();
        void RefreshStashes();
    }
}
