using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class AIConflictResolver : ObservableObject
    {
        public List<AI.Service> Services { get; }
        public AI.Service SelectedService { get; set; }
        public bool AutoApply { get; set; }
        public bool AutoStart { get; }
        public bool Succeeded { get; private set; }

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                SetProperty(ref _isRunning, value);
                OnPropertyChanged(nameof(CanStart));
            }
        }

        public bool CanStart => !IsRunning && !Succeeded && Services.Count > 0;

        public bool CanApply
        {
            get => _canApply;
            private set => SetProperty(ref _canApply, value);
        }

        public string Status
        {
            get => _status;
            private set => SetProperty(ref _status, value);
        }

        public AI.ConflictResolution Preview
        {
            get => _preview;
            private set => SetProperty(ref _preview, value);
        }

        public AIConflictResolver(Repository repo, string path = null, AI.Service service = null, bool autoApply = false, bool continueRebase = false)
        {
            _repo = repo;
            _path = path;
            _continueRebase = continueRebase;
            Services = repo.GetPreferredOpenAIServices();
            SelectedService = service ?? Services.FirstOrDefault();
            AutoApply = autoApply;
            AutoStart = service != null;
            Status = App.Text(Services.Count == 0 ? "AIConflict.NoService" : "AIConflict.Ready");
        }

        public async Task RunAsync()
        {
            if (!CanStart || SelectedService == null)
                return;

            _cancel?.Dispose();
            _cancel = new CancellationTokenSource();
            var cancellation = _cancel.Token;
            var service = SelectedService;
            var autoApply = AutoApply;
            IsRunning = true;
            var log = _repo.CreateLog("AI Conflict Resolution");
            _repo.BeginRemoteSync();
            using var watcher = _repo.LockWatcher();
            try
            {
                // Each continue can stop at a different commit; always reload the unmerged index.
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var entries = await AI.ConflictResolution.QueryEntriesAsync(_repo.FullPath, cancellation);
                    var paths = entries.Select(x => x.Path).Distinct().Where(x => _path == null || x == _path).ToList();
                    if (paths.Count == 0)
                        throw new Exception(App.Text("AIConflict.NoConflicts"));

                    // Process sequentially so manual preview and cancellation have an exact file boundary.
                    foreach (var path in paths)
                    {
                        Status = App.Text("AIConflict.Generating", path);
                        Preview = null;
                        Preview = await AI.ConflictResolution.GenerateAsync(_repo.FullPath, _repo.GitDir, path, service, cancellation);
                        if (!autoApply)
                        {
                            Status = App.Text("AIConflict.Review");
                            _adoption = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            CanApply = true;
                            try
                            {
                                await _adoption.Task.WaitAsync(cancellation);
                            }
                            finally
                            {
                                CanApply = false;
                                _adoption = null;
                            }
                        }
                        cancellation.ThrowIfCancellationRequested();
                        Status = App.Text("AIConflict.Applying", path);
                        await Preview.ApplyAsync(_repo.FullPath, log, cancellation);
                        log.AppendLine($"AI resolved: {path}");
                    }

                    if (!_continueRebase)
                        break;

                    cancellation.ThrowIfCancellationRequested();
                    if (!AI.ConflictResolution.IsRebasing(_repo.GitDir))
                        throw new Exception(App.Text("AIConflict.Changed"));
                    if ((await AI.ConflictResolution.QueryEntriesAsync(_repo.FullPath, cancellation)).Count != 0)
                        throw new Exception(App.Text("AIConflict.Changed"));

                    Status = App.Text("AIConflict.Continuing");
                    var command = new Commands.Command
                    {
                        WorkingDirectory = _repo.FullPath, Context = _repo.FullPath, Log = log,
                        Editor = Commands.Command.EditorType.None, RaiseError = false,
                        Args = "-c commit.cleanup=verbatim -c commit.status=false rebase --continue",
                    };
                    // Let Git finish each mutation; cancellation stops before the next AI/file operation.
                    var success = await command.ExecAsync();
                    if (success && !AI.ConflictResolution.IsRebasing(_repo.GitDir))
                        break;
                    if (!AI.ConflictResolution.IsRebasing(_repo.GitDir) ||
                        (await AI.ConflictResolution.QueryEntriesAsync(_repo.FullPath, cancellation)).Count == 0)
                        throw new Exception(App.Text("AIConflict.ContinueFailed"));
                }

                Succeeded = true;
                Status = App.Text("AIConflict.Done");
            }
            catch (OperationCanceledException)
            {
                Status = App.Text("AIConflict.Cancelled");
            }
            catch (Exception ex)
            {
                Status = ex.Message;
                log.AppendLine(ex.Message);
            }
            finally
            {
                CanApply = false;
                _repo.MarkWorkingCopyDirtyManually();
                _repo.EndRemoteSync();
                log.Complete();
                IsRunning = false;
            }
        }

        public void Apply() => _adoption?.TrySetResult(true);
        public void Cancel() => _cancel?.Cancel();

        private readonly Repository _repo;
        private readonly string _path;
        private readonly bool _continueRebase;
        private CancellationTokenSource _cancel;
        private TaskCompletionSource<bool> _adoption;
        private bool _isRunning;
        private bool _canApply;
        private string _status;
        private AI.ConflictResolution _preview;
    }
}
