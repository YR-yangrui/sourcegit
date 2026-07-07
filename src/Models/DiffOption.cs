using System;
using System.Collections.Generic;
using System.Text;

namespace SourceGit.Models
{
    public readonly record struct DiffContentSource(string Revision, string Path, bool IsWorktree)
    {
        public static DiffContentSource FromRevision(string revision, string path)
        {
            return new DiffContentSource(revision, path, false);
        }

        public static DiffContentSource FromWorktree(string path)
        {
            return new DiffContentSource(string.Empty, path, true);
        }
    }

    public enum DiffOptionContext
    {
        WorkingCopy,
        Commit,
        FileHistory,
        FileHistoryCompare,
        RevisionCompare,
        Stash,
    }

    public class DiffOption
    {
        public bool IsLocalChange => _revisions.Count == 0;
        public bool IsUnstaged => _isUnstaged;
        public DiffOptionContext Context => _context;
        public string CustomDiffMode => _customDiffMode;
        public List<string> Revisions => _revisions;
        public string Path => _path;
        public string OrgPath => _orgPath;
        public DiffContentSource OldContent => _oldContent;
        public DiffContentSource NewContent => _newContent;

        /// <summary>
        ///     Only used for working copy changes
        /// </summary>
        /// <param name="change"></param>
        /// <param name="isUnstaged"></param>
        public DiffOption(Change change, bool isUnstaged)
        {
            _context = DiffOptionContext.WorkingCopy;
            _isUnstaged = isUnstaged;
            _path = change.Path;
            _orgPath = change.OriginalPath;
            var oldPath = GetOldPath();

            if (isUnstaged)
            {
                if (change.IsResolvedConflict)
                {
                    _revisions.Add($":2:{change.Path}".Quoted());
                    _oldContent = DiffContentSource.FromRevision(_revisions[0], oldPath);
                    _newContent = DiffContentSource.FromWorktree(_path);
                    return;
                }

                switch (change.WorkTree)
                {
                    case ChangeState.Added:
                    case ChangeState.Untracked:
                        _extra = "--no-index";
                        _orgPath = "/dev/null";
                        oldPath = GetOldPath();
                        break;
                }

                _oldContent = DiffContentSource.FromRevision(":0", oldPath);
                _newContent = DiffContentSource.FromWorktree(_path);
                return;
            }

            var baseRevision = change.DataForAmend != null ? change.DataForAmend.ParentSHA : "HEAD";
            _extra = change.DataForAmend != null ? $"--cached {baseRevision}" : "--cached";
            _oldContent = DiffContentSource.FromRevision(baseRevision, oldPath);
            _newContent = DiffContentSource.FromRevision(":0", _path);
        }

        /// <summary>
        ///     Only used for commit changes.
        /// </summary>
        /// <param name="commit"></param>
        /// <param name="change"></param>
        public DiffOption(Commit commit, Change change)
        {
            _context = DiffOptionContext.Commit;
            _revisions.Add(commit.FirstParentToCompare);
            _revisions.Add(commit.SHA);
            _path = change.Path;
            _orgPath = change.OriginalPath;
            _oldContent = DiffContentSource.FromRevision(_revisions[0], GetOldPath());
            _newContent = DiffContentSource.FromRevision(_revisions[1], _path);
        }

        /// <summary>
        ///     Used to diff in `FileHistory`
        /// </summary>
        /// <param name="ver"></param>
        public DiffOption(FileVersion ver)
        {
            _context = DiffOptionContext.FileHistory;
            if (string.IsNullOrEmpty(ver.OriginalPath))
            {
                _revisions.Add(ver.HasParent ? $"{ver.SHA}^" : EmptyTreeHash.Guess(ver.SHA));
                _revisions.Add(ver.SHA);
                _path = ver.Path;
                _oldContent = DiffContentSource.FromRevision(_revisions[0], _path);
                _newContent = DiffContentSource.FromRevision(_revisions[1], _path);
            }
            else
            {
                var baseRevision = ver.HasParent ? $"{ver.SHA}^" : EmptyTreeHash.Guess(ver.SHA);
                _revisions.Add($"{baseRevision}:{ver.OriginalPath.Quoted()}");
                _revisions.Add($"{ver.SHA}:{ver.Path.Quoted()}");
                _path = ver.Path;
                _orgPath = ver.Change.OriginalPath;
                _ignorePaths = true;
                _oldContent = DiffContentSource.FromRevision(baseRevision, _orgPath);
                _newContent = DiffContentSource.FromRevision(ver.SHA, _path);
            }
        }

        /// <summary>
        ///     Used to diff two revisions in `FileHistory`
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        public DiffOption(FileVersion start, FileVersion end)
        {
            _context = DiffOptionContext.FileHistoryCompare;
            if (start.Change.Index == ChangeState.Deleted)
            {
                _revisions.Add(EmptyTreeHash.Guess(end.SHA));
                _revisions.Add(end.SHA);
                _path = end.Path;
                _oldContent = DiffContentSource.FromRevision(_revisions[0], _path);
                _newContent = DiffContentSource.FromRevision(_revisions[1], _path);
            }
            else if (end.Change.Index == ChangeState.Deleted)
            {
                _revisions.Add(start.SHA);
                _revisions.Add(EmptyTreeHash.Guess(start.SHA));
                _path = start.Path;
                _oldContent = DiffContentSource.FromRevision(_revisions[0], _path);
                _newContent = DiffContentSource.FromRevision(_revisions[1], _path);
            }
            else if (!end.Path.Equals(start.Path, StringComparison.Ordinal))
            {
                _revisions.Add($"{start.SHA}:{start.Path.Quoted()}");
                _revisions.Add($"{end.SHA}:{end.Path.Quoted()}");
                _path = end.Path;
                _orgPath = start.Path;
                _ignorePaths = true;
                _oldContent = DiffContentSource.FromRevision(start.SHA, _orgPath);
                _newContent = DiffContentSource.FromRevision(end.SHA, _path);
            }
            else
            {
                _revisions.Add(start.SHA);
                _revisions.Add(end.SHA);
                _path = start.Path;
                _oldContent = DiffContentSource.FromRevision(_revisions[0], _path);
                _newContent = DiffContentSource.FromRevision(_revisions[1], _path);
            }
        }

        /// <summary>
        ///     Used to show differences between two revisions.
        /// </summary>
        /// <param name="baseRevision"></param>
        /// <param name="targetRevision"></param>
        /// <param name="change"></param>
        public DiffOption(string baseRevision, string targetRevision, Change change) :
            this(baseRevision, targetRevision, change, DiffOptionContext.RevisionCompare, string.Empty)
        {
        }

        public static DiffOption ForStash(string baseRevision, string targetRevision, Change change, bool isUntracked)
        {
            return new DiffOption(baseRevision, targetRevision, change, DiffOptionContext.Stash, isUntracked ? "stash-untracked" : "stash");
        }

        private DiffOption(string baseRevision, string targetRevision, Change change, DiffOptionContext context, string customDiffMode)
        {
            _context = context;
            _customDiffMode = customDiffMode;
            _revisions.Add(string.IsNullOrEmpty(baseRevision) ? "-R" : baseRevision);
            _revisions.Add(targetRevision);
            _path = change.Path;
            _orgPath = change.OriginalPath;

            if (string.IsNullOrEmpty(baseRevision))
            {
                _oldContent = DiffContentSource.FromWorktree(GetOldPath());
                _newContent = DiffContentSource.FromRevision(targetRevision, _path);
            }
            else if (string.IsNullOrEmpty(targetRevision))
            {
                _oldContent = DiffContentSource.FromRevision(baseRevision, GetOldPath());
                _newContent = DiffContentSource.FromWorktree(_path);
            }
            else
            {
                _oldContent = DiffContentSource.FromRevision(baseRevision, GetOldPath());
                _newContent = DiffContentSource.FromRevision(targetRevision, _path);
            }
        }

        /// <summary>
        ///     Converts to diff command arguments.
        /// </summary>
        public override string ToString()
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrEmpty(_extra))
                builder.Append($"{_extra} ");
            foreach (var r in _revisions)
                builder.Append($"{r} ");

            if (_ignorePaths)
                return builder.ToString();

            builder.Append("-- ");
            if (!string.IsNullOrEmpty(_orgPath))
                builder.Append($"{_orgPath.Quoted()} ");
            builder.Append(_path.Quoted());

            return builder.ToString();
        }

        public bool IsSame(DiffOption other)
        {
            if (other == null)
                return false;

            if (_isUnstaged != other._isUnstaged ||
                _context != other._context ||
                !_customDiffMode.Equals(other._customDiffMode, StringComparison.Ordinal) ||
                !_path.Equals(other._path, StringComparison.Ordinal) ||
                !_orgPath.Equals(other._orgPath, StringComparison.Ordinal) ||
                !_extra.Equals(other._extra, StringComparison.Ordinal) ||
                _ignorePaths != other._ignorePaths ||
                !_oldContent.Equals(other._oldContent) ||
                !_newContent.Equals(other._newContent) ||
                _revisions.Count != other._revisions.Count)
                return false;

            for (var i = 0; i < _revisions.Count; i++)
            {
                if (!_revisions[i].Equals(other._revisions[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private string GetOldPath()
        {
            return string.IsNullOrEmpty(_orgPath) ? _path : _orgPath;
        }

        private readonly bool _isUnstaged = false;
        private readonly DiffOptionContext _context;
        private readonly string _customDiffMode = string.Empty;
        private readonly string _path;
        private readonly string _orgPath = string.Empty;
        private readonly string _extra = string.Empty;
        private readonly List<string> _revisions = [];
        private readonly bool _ignorePaths = false;
        private readonly DiffContentSource _oldContent;
        private readonly DiffContentSource _newContent;
    }
}
