namespace SourceGit.ViewModels
{
    public class ConflictHistoryPlan
    {
        public string SessionSeed { get; init; } = string.Empty;
        public string MineTitle { get; init; } = "MINE";
        public string TheirsTitle { get; init; } = "THEIRS";
        public string MergeBaseLeft { get; init; } = "HEAD";
        public string MergeBaseRight { get; init; } = string.Empty;
        public string MineTip { get; init; } = "HEAD";
        public string TheirsTip { get; init; } = string.Empty;
        public bool TheirsIsSingleCommit { get; init; } = false;

        public bool IsValid =>
            !string.IsNullOrEmpty(SessionSeed) &&
            !string.IsNullOrEmpty(MergeBaseLeft) &&
            !string.IsNullOrEmpty(MergeBaseRight) &&
            !string.IsNullOrEmpty(MineTip) &&
            !string.IsNullOrEmpty(TheirsTip);

        public string BuildMineRange(string mergeBase)
        {
            return $"{mergeBase}..{MineTip}";
        }

        public string BuildTheirsRange(string mergeBase)
        {
            return TheirsIsSingleCommit ? $"{TheirsTip}^!" : $"{mergeBase}..{TheirsTip}";
        }
    }
}
