using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ClipVault
{
    public class SyncException : Exception { public SyncException(string message) : base(message) { } }

    /// <summary>One document read from the cloud: users/{uid}/{collection}/{id}.</summary>
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
        internal readonly List<object> Writes = new List<object>();                       // push: Firestore writes
        internal readonly List<Action> LocalChanges = new List<Action>();                 // pull: changes to apply here
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

    /// <summary>
    /// Manual sync of pinned text, tags and folders through the user's own Firebase project (Firestore + Email/Password).
    /// Nothing happens automatically: Push and Pull are started from Settings, and each shows a preview first.
    /// Conflicts: the newer change wins, by each entry's last-change time.
    /// </summary>
    public class SyncService
    {
        readonly Store store;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        string idToken, refreshToken;
        DateTime idExpires;
        static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("ClipVault sync token");

        public SyncService(Store store)
        {
            this.store = store;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            refreshToken = Unprotect(store.Settings.SyncToken);
        }

        Settings S { get { return store.Settings; } }
        public bool Configured { get { return !string.IsNullOrEmpty(S.SyncApiKey) && !string.IsNullOrEmpty(S.SyncProjectId); } }
        public bool SignedIn { get { return Configured && !string.IsNullOrEmpty(refreshToken) && !string.IsNullOrEmpty(S.SyncUid); } }
        public string Email { get { return S.SyncEmail; } }
        public string ProjectId { get { return S.SyncProjectId; } }

        // ---------------- configuration & account ----------------

        /// <summary>Reads apiKey and projectId from the Firebase config snippet (JS object or JSON).</summary>
        public static bool ParseConfig(string snippet, out string apiKey, out string projectId)
        {
            apiKey = Find(snippet, "apiKey");
            projectId = Find(snippet, "projectId");
            return !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(projectId) && !apiKey.StartsWith("YOUR_");
        }

        static string Find(string s, string key)
        {
            var m = Regex.Match(s ?? "", "[\"']?" + key + "[\"']?\\s*[:=]\\s*[\"']([^\"']+)[\"']");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        public void SetConfig(string apiKey, string projectId)
        {
            if (apiKey != S.SyncApiKey || projectId != S.SyncProjectId) SignOut(); // a different project: the old sign-in doesn't apply
            S.SyncApiKey = apiKey;
            S.SyncProjectId = projectId;
            store.SaveSettings();
        }

        /// <summary>Signs in (or creates the account). Network: call off the UI thread.</summary>
        public void SignIn(string email, string password, bool create)
        {
            if (!Configured) throw new SyncException("Paste your Firebase config first.");
            var r = Post("https://identitytoolkit.googleapis.com/v1/accounts:" + (create ? "signUp" : "signInWithPassword") + "?key=" + S.SyncApiKey,
                json.Serialize(new Dictionary<string, object> { { "email", email }, { "password", password }, { "returnSecureToken", true } }),
                "application/json", false);
            idToken = (string)r["idToken"];
            refreshToken = (string)r["refreshToken"];
            idExpires = DateTime.UtcNow.AddSeconds(int.Parse(r["expiresIn"].ToString()));
            S.SyncUid = (string)r["localId"];
            S.SyncEmail = email;
            S.SyncToken = Protect(refreshToken);
            store.SaveSettings();
        }

        public void SignOut()
        {
            idToken = refreshToken = null;
            S.SyncUid = S.SyncEmail = S.SyncToken = null;
            S.LastPush = S.LastPull = 0;
            store.Index.Synced.Clear();       // a different account starts fresh
            store.Index.Tombstones.Clear();
            store.SaveSettings();
            store.Save();
        }

        /// <summary>Deletes this account's cloud data and the account itself (used for test accounts).</summary>
        public void DeleteAccount()
        {
            EnsureToken();
            var writes = new List<object>();
            foreach (var coll in new[] { "items", "tags", "folders" })
                foreach (var d in List(coll)) writes.Add(new Dictionary<string, object> { { "delete", DocName(d.Path) } });
            Commit(writes);
            Post("https://identitytoolkit.googleapis.com/v1/accounts:delete?key=" + S.SyncApiKey,
                json.Serialize(new Dictionary<string, object> { { "idToken", idToken } }), "application/json", false);
            SignOut();
        }

        // ---------------- push ----------------

        /// <summary>Reads the cloud state. Network: call off the UI thread.</summary>
        public Dictionary<string, RemoteDoc> FetchRemote()
        {
            EnsureToken();
            var all = new Dictionary<string, RemoteDoc>();
            foreach (var coll in new[] { "tags", "folders", "items" })
                foreach (var d in List(coll)) all[d.Path] = d;
            return all;
        }

        /// <summary>Works out what a Push would send (UI thread: reads the store).</summary>
        public SyncPlan PlanPush(Dictionary<string, RemoteDoc> remote)
        {
            var plan = new SyncPlan { IsPush = true };
            foreach (var e in LocalEntities())
            {
                string synced;
                if (store.Index.Synced.TryGetValue(e.Path, out synced) && synced == e.Hash) continue; // unchanged since last sync
                RemoteDoc r;
                remote.TryGetValue(e.Path, out r);
                if (r != null && r.State == "active" && RemoteHash(r) == e.Hash) { plan.RecordSynced[e.Path] = e.Hash; continue; } // already identical
                if (r != null && r.Updated > e.Updated) { plan.SkippedNewer++; continue; } // the cloud's change is newer
                plan.Writes.Add(Update(e.Path, e.Fields(this)));
                plan.RecordSynced[e.Path] = e.Hash;
                if (r != null && r.State == "active") plan.Changed++; else plan.Added++;
            }
            foreach (var t in store.Index.Tombstones)
            {
                RemoteDoc r;
                remote.TryGetValue(t.Path, out r);
                plan.TombstonesDone.Add(t);
                if (r == null || r.State != "active") continue;         // already gone there
                if (r.Updated > t.At) { plan.SkippedNewer++; continue; } // changed in the cloud after it was removed here
                plan.Writes.Add(Update(t.Path, new Dictionary<string, object> { { "state", Str(t.State) }, { "updated", Int(t.At) } }));
                if (t.State == "unpinned") plan.Unpinned++; else plan.Removed++;
            }
            // Removal markers older than 30 days have reached every PC that syncs regularly: clean them up.
            long cutoff = DateTime.UtcNow.AddDays(-30).Ticks;
            foreach (var r in remote.Values.Where(x => x.State != "active" && x.Updated < cutoff))
                plan.Writes.Add(new Dictionary<string, object> { { "delete", DocName(r.Path) } });
            return plan;
        }

        /// <summary>Sends a Push. Network: call off the UI thread.</summary>
        public void ExecutePush(SyncPlan plan) { EnsureToken(); Commit(plan.Writes); }

        /// <summary>Records a finished Push (UI thread).</summary>
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
            // Tags, then folders, then items: items may refer to both.
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
                    if (l != null && l.Hash == rh) { plan.RecordSynced[r.Path] = rh; continue; } // already identical
                    if (l == null && r.Path.StartsWith("items/") && AdoptDuplicate(plan, r, rh)) continue;
                    if (changedHere && l.Updated > r.Updated) { plan.SkippedNewer++; continue; } // this PC's change is newer
                    plan.LocalChanges.Add(() => ApplyActive(doc));
                    plan.RecordSynced[r.Path] = rh;
                    if (l == null && !IsHiddenUnpinnedHere(r)) plan.Added++; else plan.Changed++;
                }
                else
                {
                    if (l == null) { plan.ForgetSynced.Add(r.Path); continue; }               // not here anyway
                    if (changedHere && l.Updated > r.Updated) { plan.SkippedNewer++; continue; } // changed here after the removal
                    plan.LocalChanges.Add(() => ApplyRemoved(doc));
                    plan.ForgetSynced.Add(r.Path);
                    if (r.State == "unpinned") plan.Unpinned++; else plan.Removed++;
                }
            }
            return plan;
        }

        /// <summary>Applies a Pull (UI thread). Saves once at the end.</summary>
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

        /// <summary>The same text was pinned on both PCs separately: give the local item the cloud's id instead of adding a copy.</summary>
        bool AdoptDuplicate(SyncPlan plan, RemoteDoc r, string rh)
        {
            string text = RemoteText(r);
            var twin = store.Index.Items.FirstOrDefault(i => i.Kind == ItemKind.Text && i.Pinned
                && !store.Index.Synced.ContainsKey("items/" + i.Id) && store.GetText(i) == text);
            if (twin == null) return false;
            var doc = r;
            plan.LocalChanges.Add(() => { store.ChangeId(twin, doc.Id); ApplyActive(doc); });
            plan.RecordSynced[r.Path] = rh;
            plan.Changed++;
            return true;
        }

        bool IsHiddenUnpinnedHere(RemoteDoc r)
        {
            return r.Path.StartsWith("items/") && store.Index.Items.Any(i => i.Id == r.Id);
        }

        void ApplyActive(RemoteDoc r)
        {
            var f = r.Fields;
            if (r.Path.StartsWith("tags/"))
            {
                string name = GetStr(f, "name");
                string tag = store.Index.Tags.FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
                if (tag == null) { store.Index.Tags.Add(name); tag = name; }
                string icon = GetStr(f, "icon");
                string localIcon = store.TagIcon(tag);
                // Built-in icons sync; a custom image icon stays as it is on this PC.
                if (icon != null) store.Index.TagIcons[tag] = icon;
                else if (localIcon != null && !localIcon.StartsWith("file:")) store.Index.TagIcons.Remove(tag);
                store.Index.TagUpdated[tag] = r.Updated;
            }
            else if (r.Path.StartsWith("folders/"))
            {
                var folder = store.FindFolder(r.Id);
                if (folder == null) { folder = new Folder { Id = r.Id, Created = GetLong(f, "created") }; store.Index.Folders.Add(folder); }
                folder.Name = GetStr(f, "name") ?? "Folder";
                folder.ParentId = GetStr(f, "parent");
                folder.Tab = GetStr(f, "tab");
                folder.Updated = r.Updated;
            }
            else
            {
                string text = RemoteText(r);
                var item = store.Index.Items.FirstOrDefault(i => i.Id == r.Id);
                if (item == null) item = store.AddFromSync(r.Id, text, GetLong(f, "created"));
                else if (store.GetText(item) != text) store.UpdateText(item, text);
                string tag = GetStr(f, "tag");
                if (tag != null && !store.Index.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))) store.Index.Tags.Add(tag);
                item.Pinned = true;
                item.Tag = tag;
                string folderId = GetStr(f, "folder");
                item.FolderId = folderId != null && store.FindFolder(folderId) != null ? folderId : null;
                item.Updated = r.Updated;
            }
        }

        void ApplyRemoved(RemoteDoc r)
        {
            store.Index.Synced.Remove(r.Path); // so the local change below isn't sent back as a new removal
            if (r.Path.StartsWith("tags/"))
            {
                string tag = store.Index.Tags.FirstOrDefault(t => Store.TagDocId(t) == r.Id);
                if (tag != null) store.DeleteTag(tag);
            }
            else if (r.Path.StartsWith("folders/"))
            {
                // Its synced items arrive with their own removals; anything else in it moves up a level.
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
                if (r.State == "unpinned") store.SetPin(item, false, null); // stays in this PC's history
                else store.Delete(item);
            }
        }

        // ---------------- what syncs ----------------

        class LocalEntity
        {
            public string Path, Hash;
            public long Updated;
            public Func<SyncService, Dictionary<string, object>> Fields;
        }

        IEnumerable<LocalEntity> LocalEntities()
        {
            foreach (var t in store.Index.Tags)
            {
                string tag = t, icon = GlyphOnly(store.TagIcon(t));
                long upd;
                store.Index.TagUpdated.TryGetValue(t, out upd);
                yield return new LocalEntity
                {
                    Path = "tags/" + Store.TagDocId(tag), Hash = Fingerprint("tag", tag, icon), Updated = upd,
                    Fields = s => new Dictionary<string, object> { { "name", Str(tag) }, { "icon", Str(icon) }, { "updated", Int(upd) }, { "state", Str("active") } }
                };
            }
            foreach (var x in store.Index.Folders)
            {
                var f = x;
                yield return new LocalEntity
                {
                    Path = "folders/" + f.Id, Hash = Fingerprint("folder", f.Name, f.ParentId, f.Tab), Updated = f.Updated,
                    Fields = s => new Dictionary<string, object>
                    {
                        { "name", Str(f.Name) }, { "parent", Str(f.ParentId) }, { "tab", Str(f.Tab) },
                        { "created", Int(f.Created) }, { "updated", Int(f.Updated) }, { "state", Str("active") }
                    }
                };
            }
            foreach (var x in store.Index.Items.Where(i => i.Kind == ItemKind.Text && i.Pinned))
            {
                var i = x;
                string text = store.GetText(i) ?? "";
                yield return new LocalEntity
                {
                    Path = "items/" + i.Id, Hash = Fingerprint("item", text, i.Tag, i.FolderId), Updated = i.Updated,
                    Fields = s =>
                    {
                        var d = new Dictionary<string, object>
                        {
                            { "tag", Str(i.Tag) }, { "folder", Str(i.FolderId) },
                            { "created", Int(i.Created) }, { "updated", Int(i.Updated) }, { "state", Str("active") }
                        };
                        byte[] gz = Gzip(text);
                        if (gz != null) { d["text"] = new Dictionary<string, object> { { "bytesValue", Convert.ToBase64String(gz) } }; d["gz"] = Bool(true); }
                        else { d["text"] = Str(text); d["gz"] = Bool(false); }
                        return d;
                    }
                };
            }
        }

        static string GlyphOnly(string icon) { return icon != null && icon.StartsWith("glyph:") ? icon : null; }

        string RemoteHash(RemoteDoc r)
        {
            var f = r.Fields;
            if (r.Path.StartsWith("tags/")) return Fingerprint("tag", GetStr(f, "name"), GetStr(f, "icon"));
            if (r.Path.StartsWith("folders/")) return Fingerprint("folder", GetStr(f, "name"), GetStr(f, "parent"), GetStr(f, "tab"));
            return Fingerprint("item", RemoteText(r), GetStr(f, "tag"), GetStr(f, "folder"));
        }

        static string Fingerprint(params string[] parts)
        {
            using (var sha = SHA1.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\u001F", parts.Select(p => p ?? "\u0000").ToArray()))));
        }

        string RemoteText(RemoteDoc r)
        {
            object v;
            if (!r.Fields.TryGetValue("text", out v)) return "";
            var d = v as Dictionary<string, object>;
            if (d == null) return "";
            if (d.ContainsKey("bytesValue")) return Gunzip(Convert.FromBase64String((string)d["bytesValue"]));
            return d.ContainsKey("stringValue") ? (string)d["stringValue"] : "";
        }

        /// <summary>Gzip-compressed UTF-8, or null when compressing wouldn't make it smaller.</summary>
        static byte[] Gzip(string text)
        {
            byte[] raw = Encoding.UTF8.GetBytes(text);
            if (raw.Length < 200) return null;
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(raw, 0, raw.Length);
                return ms.Length < raw.Length ? ms.ToArray() : null;
            }
        }

        static string Gunzip(byte[] data)
        {
            using (var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress))
            using (var r = new StreamReader(gz, Encoding.UTF8)) return r.ReadToEnd();
        }

        // ---------------- Firestore values ----------------

        static Dictionary<string, object> Str(string s)
        {
            return s == null ? new Dictionary<string, object> { { "nullValue", null } } : new Dictionary<string, object> { { "stringValue", s } };
        }
        static Dictionary<string, object> Int(long v) { return new Dictionary<string, object> { { "integerValue", v.ToString() } }; }
        static Dictionary<string, object> Bool(bool b) { return new Dictionary<string, object> { { "booleanValue", b } }; }

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

        // ---------------- Firebase REST ----------------

        string DocsBase { get { return "https://firestore.googleapis.com/v1/projects/" + S.SyncProjectId + "/databases/(default)/documents"; } }
        string DocName(string path) { return "projects/" + S.SyncProjectId + "/databases/(default)/documents/users/" + S.SyncUid + "/" + path; }

        Dictionary<string, object> Update(string path, Dictionary<string, object> fields)
        {
            return new Dictionary<string, object> { { "update", new Dictionary<string, object> { { "name", DocName(path) }, { "fields", fields } } } };
        }

        List<RemoteDoc> List(string collection)
        {
            var docs = new List<RemoteDoc>();
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
                        string id = name.Substring(name.LastIndexOf('/') + 1);
                        var fields = d.ContainsKey("fields") ? (Dictionary<string, object>)d["fields"] : new Dictionary<string, object>();
                        docs.Add(new RemoteDoc { Path = collection + "/" + id, Id = id, Fields = fields, State = GetStr(fields, "state") ?? "active", Updated = GetLong(fields, "updated") });
                    }
                object next;
                pageToken = r.TryGetValue("nextPageToken", out next) ? (string)next : null;
            } while (pageToken != null);
            return docs;
        }

        void Commit(List<object> writes)
        {
            for (int i = 0; i < writes.Count; i += 400) // Firestore allows up to 500 writes per commit
            {
                var chunk = writes.Skip(i).Take(400).ToList();
                Request("POST", DocsBase + ":commit", json.Serialize(new Dictionary<string, object> { { "writes", chunk } }), "application/json", true);
            }
        }

        void EnsureToken()
        {
            if (!SignedIn) throw new SyncException("Sign in first.");
            if (idToken != null && DateTime.UtcNow < idExpires.AddMinutes(-2)) return;
            var r = Post("https://securetoken.googleapis.com/v1/token?key=" + S.SyncApiKey,
                "grant_type=refresh_token&refresh_token=" + Uri.EscapeDataString(refreshToken), "application/x-www-form-urlencoded", false);
            idToken = (string)r["id_token"];
            refreshToken = (string)r["refresh_token"];
            idExpires = DateTime.UtcNow.AddSeconds(int.Parse(r["expires_in"].ToString()));
            S.SyncToken = Protect(refreshToken);
            store.SaveSettings();
        }

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
                throw new SyncException(Friendly(ex, text));
            }
        }

        /// <summary>Turns Firebase error codes into something a person can act on.</summary>
        string Friendly(WebException ex, string body)
        {
            string code = "";
            try
            {
                var e = (Dictionary<string, object>)json.Deserialize<Dictionary<string, object>>(body)["error"];
                code = (e.ContainsKey("message") ? e["message"] : "") + " " + (e.ContainsKey("status") ? e["status"] : "");
            }
            catch { }
            if (ex.Response == null) return "Couldn't reach Firebase. Check your internet connection.";
            if (code.Contains("INVALID_LOGIN_CREDENTIALS") || code.Contains("INVALID_PASSWORD") || code.Contains("EMAIL_NOT_FOUND")) return "Wrong email or password.";
            if (code.Contains("EMAIL_EXISTS")) return "An account with this email already exists. Use Sign in.";
            if (code.Contains("WEAK_PASSWORD")) return "The password must be at least 6 characters.";
            if (code.Contains("INVALID_EMAIL")) return "That email address isn't valid.";
            if (code.Contains("TOO_MANY_ATTEMPTS")) return "Too many attempts. Wait a few minutes and try again.";
            if (code.Contains("CONFIGURATION_NOT_FOUND") || code.Contains("OPERATION_NOT_ALLOWED")) return "Email/Password sign-in isn't enabled in your Firebase project (Authentication > Sign-in method).";
            if (code.Contains("API key not valid") || code.Contains("API_KEY_INVALID")) return "The Firebase config's apiKey isn't valid.";
            if (code.Contains("TOKEN_EXPIRED") || code.Contains("INVALID_REFRESH_TOKEN") || code.Contains("USER_NOT_FOUND") || code.Contains("USER_DISABLED")) return "Your sign-in expired. Sign in again.";
            if (code.Contains("PERMISSION_DENIED")) return "Firestore refused access. Check your Firestore rules allow users/{uid} for the signed-in user.";
            if (code.Contains("NOT_FOUND")) return "Firestore isn't set up in this Firebase project (Firestore Database > Create database).";
            Log.Write("Sync error: " + ex.Message + " " + body);
            return "Firebase error: " + (code.Trim().Length > 0 ? code.Trim() : ex.Message);
        }

        // ---------------- local secret storage ----------------

        static string Protect(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), TokenEntropy, DataProtectionScope.CurrentUser));
        }

        static string Unprotect(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), TokenEntropy, DataProtectionScope.CurrentUser)); }
            catch { return null; }
        }
    }
}
