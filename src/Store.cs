using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace ClipVault
{
    public enum ItemKind { Text = 0, Image = 1, Files = 2 }

    [DataContract]
    public class ClipItem
    {
        [DataMember] public string Id;
        [DataMember] public ItemKind Kind;
        [DataMember] public long Created;          // UTC ticks; refreshed when the same content is copied again
        [DataMember] public bool Pinned;
        [DataMember] public string Tag;            // null = no tag; tagged items are always pinned
        [DataMember] public string Hash;
        [DataMember] public string Text;           // full text when small, otherwise a preview (see TextInFile)
        [DataMember] public bool TextInFile;
        [DataMember] public bool HasHtml;
        [DataMember] public bool HasRtf;
        [DataMember] public int Width;
        [DataMember] public int Height;
        [DataMember] public string[] Files;
        [DataMember] public string FolderId;       // null = not in a folder; items in folders are always pinned
        [DataMember] public long Updated;          // UTC ticks of the last change that matters for sync (pin, edit, tag, folder)
    }

    /// <summary>A user folder for pinned items. Folders can contain folders (ParentId).</summary>
    [DataContract]
    public class Folder
    {
        [DataMember] public string Id;
        [DataMember] public string Name;
        [DataMember] public string ParentId;       // null = top level
        [DataMember] public string Tab;            // top-level folders: the tab they belong to (null = All); sub-folders follow their parent
        [DataMember] public long Created;
        [DataMember] public long Updated;          // UTC ticks of the last rename/move
    }

    /// <summary>Something that was synced and then deleted/unpinned here; the next Push tells the cloud.</summary>
    [DataContract]
    public class Tombstone
    {
        [DataMember] public string Path;           // "items/{id}", "tags/{id}" or "folders/{id}"
        [DataMember] public string State;          // "deleted" or "unpinned"
        [DataMember] public long At;               // UTC ticks
    }

    [DataContract]
    public class StoreIndex
    {
        [DataMember] public List<ClipItem> Items = new List<ClipItem>();
        [DataMember] public List<string> Tags = new List<string>();
        [DataMember] public List<Folder> Folders = new List<Folder>();
        [DataMember] public Dictionary<string, string> TagIcons = new Dictionary<string, string>(); // tag -> "glyph:E756" or "file:<path>"
        [DataMember] public Dictionary<string, long> TagUpdated = new Dictionary<string, long>();   // tag -> UTC ticks of last change
        [DataMember] public Dictionary<string, string> TagIds = new Dictionary<string, string>();   // tag -> random sync id (hides the name in the cloud)
        // Sync bookkeeping: fingerprint of what was last pushed/pulled per cloud path, and local removals to send.
        [DataMember] public Dictionary<string, string> Synced = new Dictionary<string, string>();
        [DataMember] public List<Tombstone> Tombstones = new List<Tombstone>();
    }

    [DataContract]
    public class Settings
    {
        [DataMember] public int MaxItems = 500;
        [DataMember] public bool StartWithWindows = true;
        [DataMember] public string ClaudeFolder;
        [DataMember] public double UiScale = 1.0;   // 1.0 = same size as the Windows clipboard panel
        [DataMember] public List<string> RecentEmoji = new List<string>();
        // Sync through ClipVault's Firebase project. Token and key are encrypted for this Windows user (DPAPI).
        [DataMember] public bool SyncEnabled;
        [DataMember] public string SyncApiKey;     // (unused since 1.2: the project is built in)
        [DataMember] public string SyncProjectId;  // (unused since 1.2)
        [DataMember] public string SyncMode;       // the account's storage: "plain", "aes", or null = not chosen yet
        [DataMember] public string SyncKey;        // derived encryption key (aes accounts)
        [DataMember] public string SyncEmail;
        [DataMember] public string SyncUid;
        [DataMember] public string SyncToken;
        [DataMember] public long LastPush;
        [DataMember] public long LastPull;

        [OnDeserializing]
        void Defaults(StreamingContext c) { MaxItems = 500; StartWithWindows = true; UiScale = 1.0; RecentEmoji = new List<string>(); }
    }

    /// <summary>Clipboard history on disk: index.json plus one data file per image / large text.</summary>
    public class Store
    {
        const int InlineTextLimit = 64 * 1024;
        public readonly string Root;
        public readonly string ItemsDir;
        public StoreIndex Index = new StoreIndex();
        public Settings Settings = new Settings();

        public event Action Changed;
        /// <summary>Raised when tags are added, removed or get a new icon (the tab bar redraws).</summary>
        public event Action TagsChanged;

        void RaiseTagsChanged() { if (TagsChanged != null) TagsChanged(); }

        public string IconsDir { get { return Path.Combine(Root, "icons"); } }

        /// <summary>The tag's icon: "glyph:XXXX" (built-in), "file:path" (custom image) or null.</summary>
        public string TagIcon(string tag)
        {
            string v;
            return tag != null && Index.TagIcons.TryGetValue(tag, out v) ? v : null;
        }

        public void SetTagIcon(string tag, string spec)
        {
            string old = TagIcon(tag);
            if (spec == null) Index.TagIcons.Remove(tag); else Index.TagIcons[tag] = spec;
            if (old != null && old != spec) DeleteCustomIcon(old);
            Index.TagUpdated[tag] = Now;
            Commit();
            RaiseTagsChanged();
        }

        /// <summary>Copies a picked image into ClipVault's own folder, so moving/deleting the original doesn't break the icon.</summary>
        public string ImportIcon(string sourcePath)
        {
            Directory.CreateDirectory(IconsDir);
            string dest = Path.Combine(IconsDir, Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath).ToLowerInvariant());
            File.Copy(sourcePath, dest);
            return "file:" + dest;
        }

        void DeleteCustomIcon(string spec)
        {
            if (spec == null || !spec.StartsWith("file:")) return;
            string path = spec.Substring(5);
            if (path.StartsWith(IconsDir, StringComparison.OrdinalIgnoreCase)) TryDelete(path); // only files we copied in
        }

        public Store() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipVault")) { }

        /// <summary>A store in another folder (used to test sync with two "PCs" on one machine).</summary>
        public Store(string root)
        {
            Root = root;
            ItemsDir = Path.Combine(Root, "items");
            Directory.CreateDirectory(ItemsDir);
        }

        public void Load()
        {
            Index = Read<StoreIndex>(Path.Combine(Root, "index.json")) ?? new StoreIndex();
            if (Index.Items == null) Index.Items = new List<ClipItem>();
            if (Index.Tags == null) Index.Tags = new List<string>();
            if (Index.Folders == null) Index.Folders = new List<Folder>();
            if (Index.TagIcons == null) Index.TagIcons = new Dictionary<string, string>();
            if (Index.TagUpdated == null) Index.TagUpdated = new Dictionary<string, long>();
            if (Index.TagIds == null) Index.TagIds = new Dictionary<string, string>();
            if (Index.Synced == null) Index.Synced = new Dictionary<string, string>();
            if (Index.Tombstones == null) Index.Tombstones = new List<Tombstone>();
            // Items or folders pointing at a folder that no longer exists go back to the top level.
            var folderIds = new HashSet<string>(Index.Folders.Select(f => f.Id));
            foreach (var i in Index.Items) if (i.FolderId != null && !folderIds.Contains(i.FolderId)) i.FolderId = null;
            foreach (var f in Index.Folders) if (f.ParentId != null && !folderIds.Contains(f.ParentId)) f.ParentId = null;
            Index.Items.RemoveAll(i => i == null || i.Id == null);
            Settings = Read<Settings>(Path.Combine(Root, "settings.json")) ?? new Settings();
            if (Settings.RecentEmoji == null) Settings.RecentEmoji = new List<string>();
            // Claude Code never remembers trust for the home folder and would ask every time,
            // so Ask Claude uses its own folder by default (trusted once, then remembered).
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(Settings.ClaudeFolder) || string.Equals(Settings.ClaudeFolder.TrimEnd('\\'), home.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                Settings.ClaudeFolder = Path.Combine(home, "ClaudeChat");
            if (Settings.MaxItems < 10) Settings.MaxItems = 10;
            if (Settings.UiScale < 0.8 || Settings.UiScale > 2) Settings.UiScale = 1.0;
        }

        public void Save() { Write(Path.Combine(Root, "index.json"), Index); }
        public void SaveSettings() { Write(Path.Combine(Root, "settings.json"), Settings); }

        public string FilePath(ClipItem item, string ext) { return Path.Combine(ItemsDir, item.Id + ext); }

        // ---------- adding ----------

        public ClipItem AddText(string text, string html, string rtf)
        {
            string hash = "T:" + Sha1(Encoding.Unicode.GetBytes(text));
            ClipItem existing = Bump(hash);
            if (existing != null) return existing;
            var item = NewItem(ItemKind.Text, hash);
            if (text.Length > InlineTextLimit)
            {
                File.WriteAllText(FilePath(item, ".txt"), text, Encoding.UTF8);
                item.TextInFile = true;
                item.Text = text.Substring(0, 2000);
            }
            else item.Text = text;
            if (!string.IsNullOrEmpty(html)) { File.WriteAllText(FilePath(item, ".html"), html, Encoding.UTF8); item.HasHtml = true; }
            if (!string.IsNullOrEmpty(rtf)) { File.WriteAllText(FilePath(item, ".rtf"), rtf, Encoding.UTF8); item.HasRtf = true; }
            return Insert(item);
        }

        /// <summary>Called on a background thread: writes the PNG and returns the item to insert on the UI thread.</summary>
        public ClipItem PrepareImage(byte[] png, int w, int h)
        {
            var item = NewItem(ItemKind.Image, "I:" + Sha1(png));
            item.Width = w;
            item.Height = h;
            File.WriteAllBytes(FilePath(item, ".png"), png);
            return item;
        }

        public ClipItem AddPreparedImage(ClipItem item)
        {
            ClipItem existing = Bump(item.Hash);
            if (existing != null)
            {
                TryDelete(FilePath(item, ".png")); // same picture is already stored under the older id
                return existing;
            }
            return Insert(item);
        }

        public ClipItem AddFiles(string[] files)
        {
            string hash = "F:" + Sha1(Encoding.Unicode.GetBytes(string.Join("\n", files)));
            ClipItem existing = Bump(hash);
            if (existing != null) return existing;
            var item = NewItem(ItemKind.Files, hash);
            item.Files = files;
            return Insert(item);
        }

        ClipItem NewItem(ItemKind kind, string hash)
        {
            return new ClipItem { Id = Guid.NewGuid().ToString("N"), Kind = kind, Hash = hash, Created = DateTime.UtcNow.Ticks };
        }

        ClipItem Bump(string hash)
        {
            var existing = Index.Items.FirstOrDefault(i => i.Hash == hash);
            if (existing == null) return null;
            existing.Created = DateTime.UtcNow.Ticks;
            Index.Items.Remove(existing);
            Index.Items.Insert(0, existing);
            Commit();
            return existing;
        }

        ClipItem Insert(ClipItem item)
        {
            Index.Items.Insert(0, item);
            Trim();
            Commit();
            return item;
        }

        // ---------- editing ----------

        public void Trim()
        {
            var unpinned = Index.Items.Where(i => !i.Pinned).OrderByDescending(i => i.Created).ToList();
            foreach (var old in unpinned.Skip(Settings.MaxItems)) RemoveFiles(old, true);
        }

        public void Delete(ClipItem item)
        {
            if (item.Kind == ItemKind.Text) MarkGone("items/" + item.Id, "deleted");
            RemoveFiles(item, true);
            Commit();
        }

        /// <summary>Replaces a text item's content after editing. The edited text is plain, so old HTML/RTF formatting is dropped.</summary>
        public void UpdateText(ClipItem item, string text)
        {
            if (GetText(item) == text) return; // nothing changed: keep the formatting and the change time
            foreach (var ext in new[] { ".txt", ".html", ".rtf" }) TryDelete(FilePath(item, ext));
            item.HasHtml = false;
            item.HasRtf = false;
            item.TextInFile = false;
            if (text.Length > InlineTextLimit)
            {
                File.WriteAllText(FilePath(item, ".txt"), text, Encoding.UTF8);
                item.TextInFile = true;
                item.Text = text.Substring(0, 2000);
            }
            else item.Text = text;
            item.Hash = "T:" + Sha1(Encoding.Unicode.GetBytes(text));
            item.Updated = Now;
            Commit();
        }

        public void ClearUnpinned()
        {
            foreach (var i in Index.Items.Where(x => !x.Pinned).ToList()) RemoveFiles(i, true);
            Commit();
        }

        public void SetPin(ClipItem item, bool pinned, string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) tag = null; // "no tag" is always null, never ""
            if (!pinned && item.Pinned && item.Kind == ItemKind.Text) MarkGone("items/" + item.Id, "unpinned");
            item.Pinned = pinned;
            item.Tag = pinned ? tag : null;
            if (!pinned) item.FolderId = null; // unpinning takes an item out of its folder
            item.Updated = Now;
            Commit();
        }

        public bool AddTag(string tag)
        {
            tag = (tag ?? "").Trim();
            if (tag.Length == 0 || Index.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))) return false;
            Index.Tags.Add(tag);
            Index.TagUpdated[tag] = Now;
            Commit();
            RaiseTagsChanged();
            return true;
        }

        // ---------- folders ----------

        public Folder FindFolder(string id) { return id == null ? null : Index.Folders.FirstOrDefault(f => f.Id == id); }

        /// <summary>The tab a folder belongs to (its top-level folder's tab); null = All.</summary>
        public string TabOf(Folder f)
        {
            for (int guard = 0; f != null && f.ParentId != null && guard < 20; guard++) f = FindFolder(f.ParentId);
            return f == null ? null : f.Tab;
        }

        public Folder AddFolder(string name, string parentId, string tab)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return null;
            if (string.IsNullOrEmpty(parentId)) parentId = null;
            if (string.IsNullOrEmpty(tab)) tab = null;
            var f = new Folder { Id = Guid.NewGuid().ToString("N"), Name = name, ParentId = parentId, Tab = parentId == null ? tab : null, Created = DateTime.UtcNow.Ticks, Updated = Now };
            Index.Folders.Add(f);
            Commit();
            return f;
        }

        public void RenameFolder(Folder f, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name == f.Name) return;
            f.Name = name;
            f.Updated = Now;
            Commit();
        }

        /// <summary>A folder plus every folder inside it, at any depth.</summary>
        public List<Folder> FolderTree(Folder f)
        {
            var all = new List<Folder> { f };
            for (int i = 0; i < all.Count && all.Count < 10000; i++)
                all.AddRange(Index.Folders.Where(x => x.ParentId == all[i].Id && !all.Contains(x)));
            return all;
        }

        /// <summary>Number of items in a folder and all its sub-folders.</summary>
        public int ItemsInTree(Folder f)
        {
            var ids = new HashSet<string>(FolderTree(f).Select(x => x.Id));
            return Index.Items.Count(i => i.FolderId != null && ids.Contains(i.FolderId));
        }

        /// <summary>Deletes a folder together with its sub-folders and every item inside them.</summary>
        public void DeleteFolder(Folder f)
        {
            var tree = FolderTree(f);
            var ids = new HashSet<string>(tree.Select(x => x.Id));
            foreach (var i in Index.Items.Where(x => x.FolderId != null && ids.Contains(x.FolderId)).ToList())
            {
                if (i.Kind == ItemKind.Text) MarkGone("items/" + i.Id, "deleted");
                RemoveFiles(i, true);
            }
            foreach (var x in tree) { MarkGone("folders/" + x.Id, "deleted"); Index.Folders.Remove(x); }
            Commit();
        }

        /// <summary>Puts an item in a folder (pinning it), or takes it out with folderId = null (it stays pinned).</summary>
        public void MoveToFolder(ClipItem item, string folderId)
        {
            if (string.IsNullOrEmpty(folderId)) folderId = null;
            if (item.FolderId == folderId && (folderId == null || item.Pinned)) return;
            item.FolderId = folderId;
            if (folderId != null) item.Pinned = true;
            item.Updated = Now;
            Commit();
        }

        /// <summary>"Work \u203A API" style path, used in menus.</summary>
        public string FolderPath(Folder f)
        {
            var parts = new List<string>();
            for (var x = f; x != null && parts.Count < 20; x = FindFolder(x.ParentId)) parts.Insert(0, x.Name);
            return string.Join(" \u203A ", parts);
        }

        /// <summary>Removes a tag. Its items stay pinned, they just lose the tag.</summary>
        public void DeleteTag(string tag)
        {
            Index.Tags.Remove(tag);
            MarkGone("tags/" + TagId(tag), "deleted");
            Index.TagIds.Remove(tag);
            DeleteCustomIcon(TagIcon(tag));
            Index.TagIcons.Remove(tag);
            Index.TagUpdated.Remove(tag);
            foreach (var f in Index.Folders.Where(x => x.Tab == tag)) { f.Tab = null; f.Updated = Now; } // its folders move to the All tab
            foreach (var i in Index.Items.Where(x => x.Tag == tag)) { i.Tag = null; i.Updated = Now; }
            Commit();
            RaiseTagsChanged();
        }

        void RemoveFiles(ClipItem item, bool removeFromIndex)
        {
            if (removeFromIndex) Index.Items.Remove(item);
            foreach (var ext in new[] { ".png", ".txt", ".html", ".rtf" }) TryDelete(FilePath(item, ext));
        }

        int batch;

        void Commit()
        {
            if (batch > 0) return; // saved once when the batch ends
            Save();
            if (Changed != null) Changed();
        }

        /// <summary>Runs many changes with a single save (used when applying a Pull).</summary>
        public void Batch(Action changes)
        {
            batch++;
            try { changes(); }
            finally { batch--; }
            if (batch == 0) { Save(); if (Changed != null) Changed(); RaiseTagsChanged(); }
        }

        public static long Now { get { return DateTime.UtcNow.Ticks; } }

        /// <summary>A tag's sync id: random, so the cloud never sees (or can guess) tag names.</summary>
        public string TagId(string tag)
        {
            string id;
            if (!Index.TagIds.TryGetValue(tag, out id)) { id = Guid.NewGuid().ToString("N"); Index.TagIds[tag] = id; }
            return id;
        }

        /// <summary>If this was synced, remember to remove (or unpin) it in the cloud on the next Push.</summary>
        void MarkGone(string path, string state)
        {
            if (!Index.Synced.Remove(path)) return;
            Index.Tombstones.RemoveAll(t => t.Path == path);
            Index.Tombstones.Add(new Tombstone { Path = path, State = state, At = Now });
        }

        /// <summary>Adds a pinned text item that came from the cloud, keeping its id.</summary>
        public ClipItem AddFromSync(string id, string text, long created)
        {
            var item = new ClipItem { Id = id, Kind = ItemKind.Text, Created = created, Pinned = true, Hash = "T:" + Sha1(Encoding.Unicode.GetBytes(text)) };
            if (text.Length > InlineTextLimit)
            {
                File.WriteAllText(FilePath(item, ".txt"), text, Encoding.UTF8);
                item.TextInFile = true;
                item.Text = text.Substring(0, 2000);
            }
            else item.Text = text;
            Index.Items.Insert(0, item);
            return item;
        }

        /// <summary>Gives a local item the id the cloud already uses for the same text (no duplicate after a Pull).</summary>
        public void ChangeId(ClipItem item, string newId)
        {
            foreach (var ext in new[] { ".png", ".txt", ".html", ".rtf" })
            {
                string from = FilePath(item, ext);
                if (File.Exists(from)) File.Move(from, Path.Combine(ItemsDir, newId + ext));
            }
            item.Id = newId;
        }

        // ---------- reading data back ----------

        public string GetText(ClipItem item)
        {
            if (item.TextInFile && File.Exists(FilePath(item, ".txt"))) return File.ReadAllText(FilePath(item, ".txt"), Encoding.UTF8);
            return item.Text;
        }

        public string GetExtra(ClipItem item, string ext)
        {
            string p = FilePath(item, ext);
            return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null;
        }

        // ---------- helpers ----------

        static string Sha1(byte[] data)
        {
            using (var sha = SHA1.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Log.Write(ex); }
        }

        static T Read<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var fs = File.OpenRead(path)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(fs);
            }
            catch (Exception ex) { Log.Write(ex); return null; }
        }

        static void Write<T>(string path, T value)
        {
            try
            {
                string tmp = path + ".tmp";
                using (var fs = File.Create(tmp)) new DataContractJsonSerializer(typeof(T)).WriteObject(fs, value);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception ex) { Log.Write(ex); }
        }
    }

    public static class Log
    {
        public static void Write(Exception ex) { Write(ex.ToString()); }

        public static void Write(string msg)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipVault");
                string path = Path.Combine(dir, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("s") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
