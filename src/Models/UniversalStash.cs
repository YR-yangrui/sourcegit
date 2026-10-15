using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SourceGit.Models
{
    public class UniversalStash
    {
        public string Id { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public ulong Time { get; set; }
        public string SourceRepository { get; set; } = string.Empty;
        public string Subject => Message.Split('\n', 2)[0].Trim();
    }

    public sealed class UniversalStashStore
    {
        public static UniversalStashStore Instance { get; } = new();

        public ObservableCollection<UniversalStash> Items { get; } = [];

        public string GetPatchFile(UniversalStash stash)
        {
            return Path.Combine(_storageDir, $"{stash.Id}.patch");
        }

        public bool Add(UniversalStash stash, string generatedPatch, out string error)
        {
            try
            {
                Directory.CreateDirectory(_storageDir);
                var patchFile = GetPatchFile(stash);
                File.Move(generatedPatch, patchFile);

                // Publish the metadata only after the patch is safely in its final location.
                var updated = Items.ToList();
                updated.Insert(0, stash);
                SaveManifest(updated);
                Items.Insert(0, stash);
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                var patchFile = GetPatchFile(stash);
                if (File.Exists(patchFile))
                    File.Delete(patchFile);
                if (File.Exists(generatedPatch))
                    File.Delete(generatedPatch);
                return false;
            }
        }

        public bool Remove(UniversalStash stash, out string error)
        {
            try
            {
                // Commit the manifest first so a failed patch cleanup only leaves an unused file.
                var updated = Items.Where(x => !ReferenceEquals(x, stash)).ToList();
                SaveManifest(updated);
                Items.Remove(stash);

                var patchFile = GetPatchFile(stash);
                if (File.Exists(patchFile))
                    File.Delete(patchFile);

                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private UniversalStashStore()
        {
            _storageDir = Path.Combine(Native.OS.DataDir, "universal_stashes");
            _manifestFile = Path.Combine(_storageDir, "manifest.json");

            try
            {
                if (!File.Exists(_manifestFile))
                    return;

                using var stream = File.OpenRead(_manifestFile);
                var loaded = JsonSerializer.Deserialize(stream, JsonCodeGen.Default.ListUniversalStash) ?? [];

                // Ignore entries whose patch was removed outside SourceGit.
                // Restore only entries that still have an applicable payload.
                foreach (var stash in loaded.Where(x => File.Exists(GetPatchFile(x))))
                    Items.Add(stash);
            }
            catch
            {
                // Keep the application usable if the optional global stash list cannot be read.
            }
        }

        private void SaveManifest(List<UniversalStash> items)
        {
            Directory.CreateDirectory(_storageDir);
            var content = JsonSerializer.Serialize(items, JsonCodeGen.Default.ListUniversalStash);
            var tempFile = $"{_manifestFile}.tmp";
            File.WriteAllText(tempFile, content);
            File.Move(tempFile, _manifestFile, true);
        }

        private readonly string _storageDir;
        private readonly string _manifestFile;
    }
}
