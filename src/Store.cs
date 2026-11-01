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
    }

    /// <summary>A user folder for pinned items. Folders can contain folders (ParentId).</summary>
    [DataContract]
    public class Folder
    {
        [DataMember] public string Id;
        [DataMember] public string Name;
        [DataMember] public string ParentId;       // null = top level
        [DataMember] public long Created;
    }

    [DataContract]
    public class StoreIndex
    {
        [DataMember] public List<ClipItem> Items = new List<ClipItem>();
        [DataMember] public List<string> Tags = new List<string>();
        [DataMember] public List<Folder> Folders = new List<Folder>();
    }

    [DataContract]
    public class Settings
    {
        [DataMember] public int MaxItems = 500;
        [DataMember] public bool StartWithWindows = true;
        [DataMember] public string ClaudeFolder;
        [DataMember] public double UiScale = 1.0;   // 1.0 = same size as the Windows clipboard panel
        [DataMember] public List<string> RecentEmoji = new List<string>();

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

        public Store()
        {
            Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipVault");
            ItemsDir = Path.Combine(Root, "items");
            Directory.CreateDirectory(ItemsDir);
        }

        public void Load()
        {
            Index = Read<StoreIndex>(Path.Combine(Root, "index.json")) ?? new StoreIndex();
            if (Index.Items == null) Index.Items = new List<ClipItem>();
            if (Index.Tags == null) Index.Tags = new List<string>();
            if (Index.Folders == null) Index.Folders = new List<Folder>();
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

        public void Delete(ClipItem item) { RemoveFiles(item, true); Commit(); }

        /// <summary>Replaces a text item's content after editing. The edited text is plain, so old HTML/RTF formatting is dropped.</summary>
        public void UpdateText(ClipItem item, string text)
        {
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
            Commit();
        }

        public void ClearUnpinned()
        {
            foreach (var i in Index.Items.Where(x => !x.Pinned).ToList()) RemoveFiles(i, true);
            Commit();
        }

        public void SetPin(ClipItem item, bool pinned, string tag)
        {
            item.Pinned = pinned;
            item.Tag = pinned ? tag : null;
            if (!pinned) item.FolderId = null; // unpinning takes an item out of its folder
            Commit();
        }

        public bool AddTag(string tag)
        {
            tag = (tag ?? "").Trim();
            if (tag.Length == 0 || Index.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))) return false;
            Index.Tags.Add(tag);
            Commit();
            return true;
        }

        // ---------- folders ----------

        public Folder FindFolder(string id) { return id == null ? null : Index.Folders.FirstOrDefault(f => f.Id == id); }

        public Folder AddFolder(string name, string parentId)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return null;
            var f = new Folder { Id = Guid.NewGuid().ToString("N"), Name = name, ParentId = parentId, Created = DateTime.UtcNow.Ticks };
            Index.Folders.Add(f);
            Commit();
            return f;
        }

        public void RenameFolder(Folder f, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name == f.Name) return;
            f.Name = name;
            Commit();
        }

        /// <summary>Deletes a folder. Nothing inside is deleted: its items and sub-folders move up one level.</summary>
        public void DeleteFolder(Folder f)
        {
            foreach (var i in Index.Items.Where(x => x.FolderId == f.Id)) i.FolderId = f.ParentId;
            foreach (var sub in Index.Folders.Where(x => x.ParentId == f.Id)) sub.ParentId = f.ParentId;
            Index.Folders.Remove(f);
            Commit();
        }

        /// <summary>Puts an item in a folder (pinning it), or takes it out with folderId = null (it stays pinned).</summary>
        public void MoveToFolder(ClipItem item, string folderId)
        {
            if (item.FolderId == folderId && (folderId == null || item.Pinned)) return;
            item.FolderId = folderId;
            if (folderId != null) item.Pinned = true;
            Commit();
        }

        /// <summary>"Work › API" style path, used in menus.</summary>
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
            foreach (var i in Index.Items.Where(x => x.Tag == tag)) i.Tag = null;
            Commit();
        }

        void RemoveFiles(ClipItem item, bool removeFromIndex)
        {
            if (removeFromIndex) Index.Items.Remove(item);
            foreach (var ext in new[] { ".png", ".txt", ".html", ".rtf" }) TryDelete(FilePath(item, ext));
        }

        void Commit()
        {
            Save();
            if (Changed != null) Changed();
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
