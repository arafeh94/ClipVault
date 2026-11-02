using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace ClipVault
{
    public class SyncException : Exception { public SyncException(string message) : base(message) { } }

    /// <summary>One document read from the cloud: users/{uid}/{collection}/{id}. Fields are already decoded (and decrypted).</summary>
    public class RemoteDoc
    {
        public string Path;                       // "items/{id}", "tags/{id}" or "folders/{id}"
        public string Id;
        public string State;                      // "active", "deleted" or "unpinned"
        public long Updated;                      // UTC ticks
        public Dictionary<string, object> Fields;
    }

    /// <summary>What a Push or Pull would do, shown to the user before anything changes.</summary>
    public class SyncPlan
    {
        public bool IsPush;
        public int Added, Changed, Removed, Unpinned, SkippedNewer;
        internal readonly List<object> Writes = new List<object>();
        internal readonly List<Action> LocalChanges = new List<Action>();
        internal readonly Dictionary<string, string> RecordSynced = new Dictionary<string, string>();
        internal readonly List<string> ForgetSynced = new List<string>();
        internal readonly List<Tombstone> TombstonesDone = new List<Tombstone>();

        public bool HasChanges { get { return Writes.Count > 0 || LocalChanges.Count > 0; } }

        public string Summary
        {
            get
            {
                var parts = new List<string>();
                if (Added > 0) parts.Add(Added + " new");
                if (Changed > 0) parts.Add(Changed + " changed");
                if (Unpinned > 0) parts.Add(Unpinned + " unpinned");
                if (Removed > 0) parts.Add(Removed + " deleted");
                string what = parts.Count == 0 ? "Nothing to " + (IsPush ? "push" : "pull") : string.Join(", ", parts.ToArray());
                if (SkippedNewer > 0)
                    what += ". " + SkippedNewer + (IsPush ? " kept as is: the cloud has a newer change" : " kept: this PC has a newer change");
                return what;
            }
        }
    }

    /// <summary>Where the sync setup stands, which decides what the Settings page shows.</summary>
    public enum SyncStage
    {
        SignedOut,        // sign in or create an account
        ChooseEncryption, // first PC on this account: optional passphrase (empty = no encryption)
        Unlock,           // the account's data is encrypted: enter the passphrase on this PC
        Ready             // Push / Pull
    }

    /// <summary>
    /// Manual sync of pinned text, tags and folders through ClipVault's Firebase project (Firestore + Email/Password).
    /// Nothing happens automatically: Push and Pull are started from Settings and show a preview first.
    /// Conflicts: the newer change wins. Optional end-to-end encryption with a passphrase that never leaves the PC
    /// (PBKDF2-SHA256 -> AES-256-CBC + HMAC-SHA256 bound to each document's path). Without a passphrase, data is
    /// stored gzip-compressed but readable.
    /// </summary>
    public class SyncService
    {
        // ClipVault's shared Firebase project. Not secret: access is controlled by sign-in and the Firestore rules.
        const string ApiKey = "AIzaSyC6JQxbqsfGEyDjjzq79ldTgu9ip4WufAQ";
        const string ProjectId = "clipvault-a24db";
        const int Iterations = 310000;
        const string MetaPath = "meta/key";

        readonly Store store;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        string idToken, refreshToken;
        DateTime idExpires;
        byte[] encKey, macKey;         // null = this account syncs without encryption (when Mode == "plain")
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClipVault sync");

        public SyncService(Store store)
        {
            this.store = store;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            refreshToken = Unprotect(S.SyncToken);
            var key = UnprotectBytes(S.SyncKey);
            if (key != null && key.Length == 64) { encKey = key.Take(32).ToArray(); macKey = key.Skip(32).ToArray(); }
        }

        Settings S { get { return store.Settings; } }
        public bool SignedIn { get { return !string.IsNullOrEmpty(refreshToken) && !string.IsNullOrEmpty(S.SyncUid); } }
        public string Email { get { return S.SyncEmail; } }
        public bool Encrypted { get { return S.SyncMode == "aes"; } }

        public SyncStage Stage
        {
            get
            {
                if (!SignedIn) return SyncStage.SignedOut;
                if (S.SyncMode == null) return SyncStage.ChooseEncryption;
                if (S.SyncMode == "aes" && encKey == null) return SyncStage.Unlock;
                return SyncStage.Ready;
            }
        }

        // ---------------- account ----------------

        /// <summary>Signs in (or creates the account), then reads how the account's data is stored. Network: off the UI thread.</summary>
        public void SignIn(string email, string password, bool create)
        {
            var r = Post("https://identitytoolkit.googleapis.com/v1/accounts:" + (create ? "signUp" : "signInWithPassword") + "?key=" + ApiKey,
                json.Serialize(new Dictionary<string, object> { { "email", email }, { "password", password }, { "returnSecureToken", true } }),
                "application/json", false);
            idToken = (string)r["idToken"];
            refreshToken = (string)r["refreshToken"];
            idExpires = DateTime.UtcNow.AddSeconds(int.Parse(r["expiresIn"].ToString()));
            S.SyncUid = (string)r["localId"];
            S.SyncEmail = email;
            S.SyncToken = Protect(refreshToken);
            S.SyncMode = null;
            SetKey(null);
            store.SaveSettings();
            ReadMode();
        }

        /// <summary>Reads the account's storage mode from the cloud ("plain" or "aes"); null when it hasn't been chosen yet.</summary>
        void ReadMode()
        {
            var meta = GetMeta();
            S.SyncMode = meta == null ? null : (GetStr(meta, "mode") ?? "plain");
            store.SaveSettings();
        }

        public void SignOut()
        {
            idToken = refreshToken = null;
            S.SyncUid = S.SyncEmail = S.SyncToken = S.SyncMode = null;
            S.LastPush = S.LastPull = 0;
            SetKey(null);
            store.Index.Synced.Clear();       // another account starts fresh
            store.Index.Tombstones.Clear();
            store.SaveSettings();
            store.Save();
        }

        /// <summary>Deletes this account's cloud data and the account itself.</summary>
        public void DeleteAccount()
        {
            EnsureToken();
            var writes = new List<object>();
            foreach (var coll in new[] { "items", "tags", "folders" })
                foreach (var d in ListRaw(coll)) writes.Add(Delete(d.Key));
            writes.Add(Delete(MetaPath));
            Commit(writes);
            Post("https://identitytoolkit.googleapis.com/v1/accounts:delete?key=" + ApiKey,
                json.Serialize(new Dictionary<string, object> { { "idToken", idToken } }), "application/json", false);
            SignOut();
        }

        // ---------------- encryption ----------------

        /// <summary>
        /// Chooses how this account's data is stored: with a passphrase (end-to-end encrypted) or without (empty passphrase).
        /// Used on the first PC, and later to turn encryption on/off or change the passphrase: everything already in the
        /// cloud is re-uploaded in the new mode. Network: off the UI thread.
        /// </summary>
        public void ChooseEncryption(string passphrase)
        {
            EnsureToken();
            // Read what's there with the current key first, so it can be rewritten in the new mode.
            var existing = new List<RemoteDoc>();
            if (GetMeta() != null)
            {
                if (Stage == SyncStage.Unlock) throw new SyncException("Unlock with the current passphrase first.");
                foreach (var coll in new[] { "tags", "folders", "items" }) existing.AddRange(List(coll));
            }

            var meta = new Dictionary<string, object> { { "updated", Int(DateTime.UtcNow.Ticks) } };
            byte[] newEnc = null, newMac = null;
            if (string.IsNullOrEmpty(passphrase)) meta["mode"] = Str("plain");
            else
            {
                byte[] salt = new byte[16];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
                Derive(passphrase, salt, out newEnc, out newMac);
                meta["mode"] = Str("aes");
                meta["salt"] = Bytes(salt);
                meta["iter"] = Int(Iterations);
                meta["check"] = Bytes(Seal(MetaPath, Encoding.UTF8.GetBytes("clipvault-ok"), newEnc, newMac));
            }

            var writes = new List<object> { new Dictionary<string, object> { { "update", new Dictionary<string, object> { { "name", DocName(MetaPath) }, { "fields", meta } } } } };
            foreach (var d in existing)
            {
                if (d.State == "active") writes.Add(Update(d.Path, d.Fields, d.Updated, newEnc, newMac));
            }
            Commit(writes);
            S.SyncMode = newEnc == null ? "plain" : "aes";
            SetKey(newEnc == null ? null : newEnc.Concat(newMac).ToArray());
            store.SaveSettings();
        }

        /// <summary>Enters the account's passphrase on this PC. Throws if it's wrong. Network: off the UI thread.</summary>
        public void Unlock(string passphrase)
        {
            EnsureToken();
            var meta = GetMeta();
            if (meta == null || GetStr(meta, "mode") != "aes") { ReadMode(); return; }
            byte[] e, m;
            Derive(passphrase, GetBytes(meta, "salt"), out e, out m, (int)GetLong(meta, "iter"));
            byte[] check = Open(MetaPath, GetBytes(meta, "check"), e, m);
            if (check == null || Encoding.UTF8.GetString(check) != "clipvault-ok") throw new SyncException("Wrong passphrase.");
            S.SyncMode = "aes";
            SetKey(e.Concat(m).ToArray());
            store.SaveSettings();
        }

        void SetKey(byte[] key64)
        {
            encKey = key64 == null ? null : key64.Take(32).ToArray();
            macKey = key64 == null ? null : key64.Skip(32).ToArray();
            S.SyncKey = key64 == null ? null : Convert.ToBase64String(ProtectedData.Protect(key64, Entropy, DataProtectionScope.CurrentUser));
        }

        static void Derive(string passphrase, byte[] salt, out byte[] enc, out byte[] mac, int iterations = Iterations)
        {
            using (var kdf = new Rfc2898DeriveBytes(passphrase, salt, iterations, HashAlgorithmName.SHA256))
            {
                byte[] k = kdf.GetBytes(64);
                enc = k.Take(32).ToArray();
                mac = k.Skip(32).ToArray();
            }
        }

        /// <summary>AES-256-CBC with a random IV, then HMAC-SHA256 over path + IV + ciphertext (so a blob can't be moved to another entry).</summary>
        static byte[] Seal(string path, byte[] plain, byte[] enc, byte[] mac)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = enc; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.GenerateIV();
                byte[] ct;
                using (var e = aes.CreateEncryptor()) ct = e.TransformFinalBlock(plain, 0, plain.Length);
                byte[] body = aes.IV.Concat(ct).ToArray();
                byte[] tag = Mac(mac, path, body);
                return body.Concat(tag).ToArray();
            }
        }

        /// <summary>Checks the HMAC and decrypts; null if it doesn't verify (wrong key or tampered).</summary>
        static byte[] Open(string path, byte[] blob, byte[] enc, byte[] mac)
        {
            if (blob == null || blob.Length < 16 + 16 + 32) return null;
            byte[] body = blob.Take(blob.Length - 32).ToArray(), tag = blob.Skip(blob.Length - 32).ToArray();
            byte[] expect = Mac(mac, path, body);
            int diff = 0;
            for (int i = 0; i < 32; i++) diff |= tag[i] ^ expect[i]; // constant-time compare
            if (diff != 0) return null;
            using (var aes = Aes.Create())
            {
                aes.Key = enc; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.IV = body.Take(16).ToArray();
                using (var d = aes.CreateDecryptor()) return d.TransformFinalBlock(body, 16, body.Length - 16);
            }
        }

        static byte[] Mac(byte[] key, string path, byte[] body)
        {
            using (var h = new HMACSHA256(key)) return h.ComputeHash(Encoding.UTF8.GetBytes(path).Concat(new byte[] { 0 }).Concat(body).ToArray());
        }

        // ---------------- push ----------------

        /// <summary>Reads the cloud state. Network: off the UI thread.</summary>
        public Dictionary<string, RemoteDoc> FetchRemote()
        {
            RequireReady();
            EnsureToken();
            var all = new Dictionary<string, RemoteDoc>();
            foreach (var coll in new[] { "tags", "folders", "items" })
                foreach (var d in List(coll)) all[d.Path] = d;
            return all;
        }

        void RequireReady()
        {
            if (Stage == SyncStage.SignedOut) throw new SyncException("Sign in first.");
            if (Stage == SyncStage.ChooseEncryption) throw new SyncException("Choose whether to encrypt first.");
            if (Stage == SyncStage.Unlock) throw new SyncException("Enter your sync passphrase first.");
        }

        /// <summary>Works out what a Push would send (UI thread: reads the store).</summary>
        public SyncPlan PlanPush(Dictionary<string, RemoteDoc> remote)
        {
            var plan = new SyncPlan { IsPush = true };
            foreach (var e in LocalEntities())
            {
                string synced;
                if (store.Index.Synced.TryGetValue(e.Path, out synced) && synced == e.Hash) continue;
                RemoteDoc r;
                remote.TryGetValue(e.Path, out r);
                if (r != null && r.State == "active" && RemoteHash(r) == e.Hash) { plan.RecordSynced[e.Path] = e.Hash; continue; }
                if (r != null && r.Updated > e.Updated) { plan.SkippedNewer++; continue; }
                plan.Writes.Add(Update(e.Path, e.Fields(), e.Updated, encKey, macKey));
                plan.RecordSynced[e.Path] = e.Hash;
                if (r != null && r.State == "active") plan.Changed++; else plan.Added++;
            }
            foreach (var t in store.Index.Tombstones)
            {
                RemoteDoc r;
                remote.TryGetValue(t.Path, out r);
                plan.TombstonesDone.Add(t);
                if (r == null || r.State != "active") continue;
                if (r.Updated > t.At) { plan.SkippedNewer++; continue; }
                plan.Writes.Add(Marker(t.Path, t.State, t.At));
                if (t.State == "unpinned") plan.Unpinned++; else plan.Removed++;
            }
            long cutoff = DateTime.UtcNow.AddDays(-30).Ticks; // removal markers that every regularly syncing PC has seen
            foreach (var r in remote.Values.Where(x => x.State != "active" && x.Updated < cutoff)) plan.Writes.Add(Delete(r.Path));
            return plan;
        }

        public void ExecutePush(SyncPlan plan) { EnsureToken(); Commit(plan.Writes); }

        public void FinishPush(SyncPlan plan)
        {
            foreach (var kv in plan.RecordSynced) store.Index.Synced[kv.Key] = kv.Value;
            foreach (var t in plan.TombstonesDone) store.Index.Tombstones.Remove(t);
            S.LastPush = DateTime.UtcNow.Ticks;
            store.Save();
            store.SaveSettings();
        }

        // ---------------- pull ----------------

        /// <summary>Works out what a Pull would change here (UI thread: reads the store).</summary>
        public SyncPlan PlanPull(Dictionary<string, RemoteDoc> remote)
        {
            var plan = new SyncPlan { IsPush = false };
            var local = LocalEntities().ToDictionary(e => e.Path);
            foreach (var r in remote.Values.OrderBy(d => d.Path.StartsWith("tags/") ? 0 : d.Path.StartsWith("folders/") ? 1 : 2))
            {
                LocalEntity l;
                local.TryGetValue(r.Path, out l);
                string synced;
                store.Index.Synced.TryGetValue(r.Path, out synced);
                bool changedHere = l != null && l.Hash != synced;
                var doc = r;

                if (r.State == "active")
                {
                    string rh = RemoteHash(r);
                    if (l != null && l.Hash == rh) { plan.RecordSynced[r.Path] = rh; continue; }
                    if (l == null && AdoptTwin(plan, r, rh)) continue;
                    if (changedHere && l.Updated > r.Updated) { plan.SkippedNewer++; continue; }
                    plan.LocalChanges.Add(() => ApplyActive(doc));
                    plan.RecordSynced[r.Path] = rh;
                    if (l == null && !(r.Path.StartsWith("items/") && store.Index.Items.Any(i => i.Id == r.Id))) plan.Added++; else plan.Changed++;
                }
                else
                {
                    if (l == null) { plan.ForgetSynced.Add(r.Path); continue; }
                    if (changedHere && l.Updated > r.Updated) { plan.SkippedNewer++; continue; }
                    plan.LocalChanges.Add(() => ApplyRemoved(doc));
                    plan.ForgetSynced.Add(r.Path);
                    if (r.State == "unpinned") plan.Unpinned++; else plan.Removed++;
                }
            }
            return plan;
        }

        public void ApplyPull(SyncPlan plan)
        {
            store.Batch(() =>
            {
                foreach (var change in plan.LocalChanges) change();
                foreach (var kv in plan.RecordSynced) store.Index.Synced[kv.Key] = kv.Value;
                foreach (var p in plan.ForgetSynced) store.Index.Synced.Remove(p);
            });
            S.LastPull = DateTime.UtcNow.Ticks;
            store.SaveSettings();
        }

        /// <summary>
        /// The same thing was made separately on both PCs (same pinned text, or a tag with the same name):
        /// use the cloud's id for the local one instead of adding a copy.
        /// </summary>
        bool AdoptTwin(SyncPlan plan, RemoteDoc r, string rh)
        {
            var doc = r;
            if (r.Path.StartsWith("items/"))
            {
                string text = GetStr(r.Fields, "text") ?? "";
                var twin = store.Index.Items.FirstOrDefault(i => i.Kind == ItemKind.Text && i.Pinned
                    && !store.Index.Synced.ContainsKey("items/" + i.Id) && store.GetText(i) == text);
                if (twin == null) return false;
                plan.LocalChanges.Add(() => { store.ChangeId(twin, doc.Id); ApplyActive(doc); });
            }
            else if (r.Path.StartsWith("tags/"))
            {
                string name = GetStr(r.Fields, "name");
                string twin = store.Index.Tags.FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
                if (twin == null || store.Index.Synced.ContainsKey("tags/" + store.TagId(twin))) return false;
                plan.LocalChanges.Add(() => { store.Index.TagIds[twin] = doc.Id; ApplyActive(doc); });
            }
            else return false;
            plan.RecordSynced[r.Path] = rh;
            plan.Changed++;
            return true;
        }

        void ApplyActive(RemoteDoc r)
        {
            var f = r.Fields;
            if (r.Path.StartsWith("tags/"))
            {
                string name = GetStr(f, "name") ?? "Tag";
                string tag = store.Index.Tags.FirstOrDefault(t => store.TagId(t) == r.Id)
                    ?? store.Index.Tags.FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
                if (tag == null) { store.Index.Tags.Add(name); tag = name; }
                store.Index.TagIds[tag] = r.Id;
                string icon = GetStr(f, "icon"), localIcon = store.TagIcon(tag);
                if (icon != null) store.Index.TagIcons[tag] = icon;                                              // built-in icons sync
                else if (localIcon != null && !localIcon.StartsWith("file:")) store.Index.TagIcons.Remove(tag); // a custom image stays
                store.Index.TagUpdated[tag] = r.Updated;
            }
            else if (r.Path.StartsWith("folders/"))
            {
                var folder = store.FindFolder(r.Id);
                if (folder == null) { folder = new Folder { Id = r.Id, Created = GetLong(f, "created") }; store.Index.Folders.Add(folder); }
                folder.Name = GetStr(f, "name") ?? "Folder";
                folder.ParentId = GetStr(f, "parent");
                folder.Tab = TagNameForId(GetStr(f, "tab"));
                folder.Updated = r.Updated;
            }
            else
            {
                string text = GetStr(f, "text") ?? "";
                var item = store.Index.Items.FirstOrDefault(i => i.Id == r.Id);
                if (item == null) item = store.AddFromSync(r.Id, text, GetLong(f, "created"));
                else if (store.GetText(item) != text) store.UpdateText(item, text);
                item.Pinned = true;
                item.Tag = TagNameForId(GetStr(f, "tag"));
                string folderId = GetStr(f, "folder");
                item.FolderId = folderId != null && store.FindFolder(folderId) != null ? folderId : null;
                item.Updated = r.Updated;
            }
        }

        /// <summary>Items and folders refer to tags by their sync id; turn it back into this PC's tag name.</summary>
        string TagNameForId(string tagId)
        {
            return tagId == null ? null : store.Index.Tags.FirstOrDefault(t => store.TagId(t) == tagId);
        }

        void ApplyRemoved(RemoteDoc r)
        {
            store.Index.Synced.Remove(r.Path); // so the local change below isn't sent back as a new removal
            if (r.Path.StartsWith("tags/"))
            {
                string tag = store.Index.Tags.FirstOrDefault(t => store.TagId(t) == r.Id);
                if (tag != null) store.DeleteTag(tag);
            }
            else if (r.Path.StartsWith("folders/"))
            {
                var folder = store.FindFolder(r.Id);
                if (folder == null) return;
                foreach (var i in store.Index.Items.Where(x => x.FolderId == folder.Id)) i.FolderId = folder.ParentId;
                foreach (var sub in store.Index.Folders.Where(x => x.ParentId == folder.Id)) sub.ParentId = folder.ParentId;
                store.Index.Folders.Remove(folder);
            }
            else
            {
                var item = store.Index.Items.FirstOrDefault(i => i.Id == r.Id);
                if (item == null) return;
                if (r.State == "unpinned") store.SetPin(item, false, null);
                else store.Delete(item);
            }
        }

        // ---------------- what syncs ----------------

        class LocalEntity
        {
            public string Path, Hash;
            public long Updated;
            public Func<Dictionary<string, object>> Fields;
        }

        IEnumerable<LocalEntity> LocalEntities()
        {
            foreach (var t in store.Index.Tags.ToList())
            {
                string tag = t, id = store.TagId(t), icon = GlyphOnly(store.TagIcon(t));
                long upd;
                store.Index.TagUpdated.TryGetValue(t, out upd);
                yield return new LocalEntity
                {
                    Path = "tags/" + id, Hash = Fingerprint("tag", tag, icon), Updated = upd,
                    Fields = () => new Dictionary<string, object> { { "name", Str(tag) }, { "icon", Str(icon) } }
                };
            }
            foreach (var x in store.Index.Folders.ToList())
            {
                var f = x;
                string tab = f.Tab == null ? null : store.TagId(f.Tab);
                yield return new LocalEntity
                {
                    Path = "folders/" + f.Id, Hash = Fingerprint("folder", f.Name, f.ParentId, tab), Updated = f.Updated,
                    Fields = () => new Dictionary<string, object> { { "name", Str(f.Name) }, { "parent", Str(f.ParentId) }, { "tab", Str(tab) }, { "created", Int(f.Created) } }
                };
            }
            foreach (var x in store.Index.Items.Where(i => i.Kind == ItemKind.Text && i.Pinned).ToList())
            {
                var i = x;
                string text = store.GetText(i) ?? "", tag = i.Tag == null ? null : store.TagId(i.Tag);
                yield return new LocalEntity
                {
                    Path = "items/" + i.Id, Hash = Fingerprint("item", text, tag, i.FolderId), Updated = i.Updated,
                    Fields = () => new Dictionary<string, object> { { "text", Str(text) }, { "tag", Str(tag) }, { "folder", Str(i.FolderId) }, { "created", Int(i.Created) } }
                };
            }
        }

        static string GlyphOnly(string icon) { return icon != null && icon.StartsWith("glyph:") ? icon : null; }

        string RemoteHash(RemoteDoc r)
        {
            var f = r.Fields;
            if (r.Path.StartsWith("tags/")) return Fingerprint("tag", GetStr(f, "name"), GetStr(f, "icon"));
            if (r.Path.StartsWith("folders/")) return Fingerprint("folder", GetStr(f, "name"), GetStr(f, "parent"), GetStr(f, "tab"));
            return Fingerprint("item", GetStr(f, "text") ?? "", GetStr(f, "tag"), GetStr(f, "folder"));
        }

        static string Fingerprint(params string[] parts)
        {
            using (var sha = SHA1.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\u001F", parts.Select(p => p ?? "\u0000").ToArray()))));
        }

        // ---------------- document encoding ----------------
        // A document is { data: bytes, state, updated }. data = 1 flag byte + payload, where the payload is the
        // gzip-compressed JSON of the entry's fields, encrypted (flag 2) or not (flag 1). state/updated stay
        // readable: they're needed to merge, and say nothing about the content.

        Dictionary<string, object> Update(string path, Dictionary<string, object> fields, long updated, byte[] enc, byte[] mac)
        {
            byte[] packed = Gzip(Encoding.UTF8.GetBytes(json.Serialize(fields)));
            byte[] data = enc == null ? new byte[] { 1 }.Concat(packed).ToArray() : new byte[] { 2 }.Concat(Seal(path, packed, enc, mac)).ToArray();
            var doc = new Dictionary<string, object> { { "data", Bytes(data) }, { "state", Str("active") }, { "updated", Int(updated) } };
            return new Dictionary<string, object> { { "update", new Dictionary<string, object> { { "name", DocName(path) }, { "fields", doc } } } };
        }

        Dictionary<string, object> Marker(string path, string state, long at)
        {
            var doc = new Dictionary<string, object> { { "state", Str(state) }, { "updated", Int(at) } };
            return new Dictionary<string, object> { { "update", new Dictionary<string, object> { { "name", DocName(path) }, { "fields", doc } } } };
        }

        Dictionary<string, object> Delete(string path) { return new Dictionary<string, object> { { "delete", DocName(path) } }; }

        Dictionary<string, object> Decode(string path, Dictionary<string, object> raw)
        {
            byte[] data = GetBytes(raw, "data");
            if (data == null || data.Length < 2) return new Dictionary<string, object>();
            byte[] packed;
            if (data[0] == 1) packed = data.Skip(1).ToArray();
            else if (data[0] == 2)
            {
                if (encKey == null) throw new SyncException("This account's data is encrypted. Enter your sync passphrase.");
                packed = Open(path, data.Skip(1).ToArray(), encKey, macKey);
                if (packed == null) throw new SyncException("Some synced data couldn't be decrypted. If the passphrase was changed on another PC, sign out and sign in again.");
            }
            else throw new SyncException("This synced data was made by a newer ClipVault. Update ClipVault.");
            return json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Gunzip(packed)));
        }

        static byte[] Gzip(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }

        static byte[] Gunzip(byte[] data)
        {
            using (var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress))
            using (var ms = new MemoryStream()) { gz.CopyTo(ms); return ms.ToArray(); }
        }

        // ---------------- Firestore values ----------------

        static Dictionary<string, object> Str(string s)
        {
            return s == null ? new Dictionary<string, object> { { "nullValue", null } } : new Dictionary<string, object> { { "stringValue", s } };
        }
        static Dictionary<string, object> Int(long v) { return new Dictionary<string, object> { { "integerValue", v.ToString() } }; }
        static Dictionary<string, object> Bytes(byte[] b) { return new Dictionary<string, object> { { "bytesValue", Convert.ToBase64String(b) } }; }

        static string GetStr(Dictionary<string, object> f, string key)
        {
            object v;
            if (f == null || !f.TryGetValue(key, out v)) return null;
            var d = v as Dictionary<string, object>;
            string s = d != null && d.ContainsKey("stringValue") ? (string)d["stringValue"] : null;
            return string.IsNullOrEmpty(s) && key != "text" ? null : s; // empty tag/folder/name means "none"
        }

        static long GetLong(Dictionary<string, object> f, string key)
        {
            object v;
            if (f == null || !f.TryGetValue(key, out v)) return 0;
            var d = v as Dictionary<string, object>;
            long n;
            return d != null && d.ContainsKey("integerValue") && long.TryParse(d["integerValue"].ToString(), out n) ? n : 0;
        }

        static byte[] GetBytes(Dictionary<string, object> f, string key)
        {
            object v;
            if (f == null || !f.TryGetValue(key, out v)) return null;
            var d = v as Dictionary<string, object>;
            return d != null && d.ContainsKey("bytesValue") ? Convert.FromBase64String((string)d["bytesValue"]) : null;
        }

        // ---------------- Firebase REST ----------------

        string DocsBase { get { return "https://firestore.googleapis.com/v1/projects/" + ProjectId + "/databases/(default)/documents"; } }
        string DocName(string path) { return "projects/" + ProjectId + "/databases/(default)/documents/users/" + S.SyncUid + "/" + path; }

        Dictionary<string, object> GetMeta()
        {
            EnsureToken();
            try { var r = Request("GET", DocsBase + "/users/" + S.SyncUid + "/" + MetaPath, null, null, true); return r.ContainsKey("fields") ? (Dictionary<string, object>)r["fields"] : null; }
            catch (NotFound) { return null; }
        }

        /// <summary>Raw documents of a collection: path -> raw Firestore fields.</summary>
        List<KeyValuePair<string, Dictionary<string, object>>> ListRaw(string collection)
        {
            var docs = new List<KeyValuePair<string, Dictionary<string, object>>>();
            string pageToken = null;
            do
            {
                string url = DocsBase + "/users/" + S.SyncUid + "/" + collection + "?pageSize=300" + (pageToken != null ? "&pageToken=" + Uri.EscapeDataString(pageToken) : "");
                var r = Request("GET", url, null, null, true);
                object arr;
                if (r.TryGetValue("documents", out arr))
                    foreach (Dictionary<string, object> d in (IEnumerable)arr)
                    {
                        string name = (string)d["name"];
                        var fields = d.ContainsKey("fields") ? (Dictionary<string, object>)d["fields"] : new Dictionary<string, object>();
                        docs.Add(new KeyValuePair<string, Dictionary<string, object>>(collection + "/" + name.Substring(name.LastIndexOf('/') + 1), fields));
                    }
                object next;
                pageToken = r.TryGetValue("nextPageToken", out next) ? (string)next : null;
            } while (pageToken != null);
            return docs;
        }

        List<RemoteDoc> List(string collection)
        {
            return ListRaw(collection).Select(kv =>
            {
                string state = GetStr(kv.Value, "state") ?? "active";
                return new RemoteDoc
                {
                    Path = kv.Key, Id = kv.Key.Substring(kv.Key.IndexOf('/') + 1), State = state, Updated = GetLong(kv.Value, "updated"),
                    Fields = state == "active" ? Decode(kv.Key, kv.Value) : new Dictionary<string, object>()
                };
            }).ToList();
        }

        void Commit(List<object> writes)
        {
            for (int i = 0; i < writes.Count; i += 400) // Firestore allows up to 500 writes per commit
                Request("POST", DocsBase + ":commit", json.Serialize(new Dictionary<string, object> { { "writes", writes.Skip(i).Take(400).ToList() } }), "application/json", true);
        }

        void EnsureToken()
        {
            if (!SignedIn) throw new SyncException("Sign in first.");
            if (idToken != null && DateTime.UtcNow < idExpires.AddMinutes(-2)) return;
            var r = Post("https://securetoken.googleapis.com/v1/token?key=" + ApiKey,
                "grant_type=refresh_token&refresh_token=" + Uri.EscapeDataString(refreshToken), "application/x-www-form-urlencoded", false);
            idToken = (string)r["id_token"];
            refreshToken = (string)r["refresh_token"];
            idExpires = DateTime.UtcNow.AddSeconds(int.Parse(r["expires_in"].ToString()));
            S.SyncToken = Protect(refreshToken);
            store.SaveSettings();
        }

        class NotFound : Exception { }

        Dictionary<string, object> Post(string url, string body, string type, bool auth) { return Request("POST", url, body, type, auth); }

        Dictionary<string, object> Request(string method, string url, string body, string contentType, bool auth)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            if (auth) req.Headers["Authorization"] = "Bearer " + idToken;
            if (body != null)
            {
                byte[] b = Encoding.UTF8.GetBytes(body);
                req.ContentType = contentType;
                req.ContentLength = b.Length;
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string text = rd.ReadToEnd();
                    return text.Trim().Length == 0 ? new Dictionary<string, object>() : json.Deserialize<Dictionary<string, object>>(text);
                }
            }
            catch (WebException ex)
            {
                string text = "";
                if (ex.Response != null) using (var rd = new StreamReader(ex.Response.GetResponseStream())) text = rd.ReadToEnd();
                var http = ex.Response as HttpWebResponse;
                if (http != null && http.StatusCode == HttpStatusCode.NotFound && method == "GET" && url.Contains("/documents/")) throw new NotFound();
                throw new SyncException(Friendly(ex, text));
            }
        }

        string Friendly(WebException ex, string body)
        {
            string code = "";
            try
            {
                var e = (Dictionary<string, object>)json.Deserialize<Dictionary<string, object>>(body)["error"];
                code = (e.ContainsKey("message") ? e["message"] : "") + " " + (e.ContainsKey("status") ? e["status"] : "");
            }
            catch { }
            if (ex.Response == null) return "Couldn't reach the sync server. Check your internet connection.";
            if (code.Contains("INVALID_LOGIN_CREDENTIALS") || code.Contains("INVALID_PASSWORD") || code.Contains("EMAIL_NOT_FOUND")) return "Wrong email or password.";
            if (code.Contains("EMAIL_EXISTS")) return "An account with this email already exists. Use Sign in.";
            if (code.Contains("WEAK_PASSWORD")) return "The password must be at least 6 characters.";
            if (code.Contains("INVALID_EMAIL")) return "That email address isn't valid.";
            if (code.Contains("TOO_MANY_ATTEMPTS")) return "Too many attempts. Wait a few minutes and try again.";
            if (code.Contains("TOKEN_EXPIRED") || code.Contains("INVALID_REFRESH_TOKEN") || code.Contains("USER_NOT_FOUND") || code.Contains("USER_DISABLED")) return "Your sign-in expired. Sign in again.";
            if (code.Contains("PERMISSION_DENIED")) return "The sync server refused the request (an entry may be too large).";
            if (code.Contains("RESOURCE_EXHAUSTED") || code.Contains("QUOTA")) return "The sync server is over its free daily limit. Try again tomorrow.";
            Log.Write("Sync error: " + ex.Message + " " + body);
            return "Sync error: " + (code.Trim().Length > 0 ? code.Trim() : ex.Message);
        }

        // ---------------- local secret storage (Windows DPAPI, this user only) ----------------

        static string Protect(string s)
        {
            return string.IsNullOrEmpty(s) ? null : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), Entropy, DataProtectionScope.CurrentUser));
        }

        static string Unprotect(string s)
        {
            var b = UnprotectBytes(s);
            return b == null ? null : Encoding.UTF8.GetString(b);
        }

        static byte[] UnprotectBytes(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try { return ProtectedData.Unprotect(Convert.FromBase64String(s), Entropy, DataProtectionScope.CurrentUser); }
            catch { return null; }
        }
    }
}
