// Speed-up plugins for Paradox games: installer and settings launcher (WPF, no XAML compiler needed:
// MainWindow.xaml is embedded as a resource and loaded at run time).
// Builds with the C# compiler of .NET Framework 4.x (C# 5), see tools/build.py.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

[assembly: AssemblyTitle("Ускорение игр Paradox — установщик")]
[assembly: AssemblyVersion("2.0.0.0")]

namespace SpeedLauncher
{
    static class Config
    {
        // Where the launcher looks for catalog.json (the other files are next to it).
        // Replace OWNER/REPO with the GitHub repository that holds the releases.
        public const string DefaultSource = "https://github.com/OWNER/REPO/releases/latest/download/catalog.json";
        public const string AppVersion = "2.0";
        public const string StateFile = "speedup_launcher_state.txt";
        public const string BackupDir = "speedup_launcher_backup";
    }

    // ================================================================ json helpers
    static class J
    {
        public static string S(IDictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : null;
        }
        public static bool B(IDictionary<string, object> d, string k, bool def)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) && v is bool ? (bool)v : def;
        }
        public static IEnumerable<object> L(IDictionary<string, object> d, string k)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v is IEnumerable && !(v is string))
                foreach (var x in (IEnumerable)v) yield return x;
        }
        public static IDictionary<string, object> O(IDictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v as IDictionary<string, object> : null;
        }
        public static IDictionary<string, object> Parse(string json)
        {
            var js = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
            return (IDictionary<string, object>)js.DeserializeObject(json);
        }
    }

    // ================================================================ catalog / manifest model
    class GameInfo
    {
        public string Id, Name, Short, AppId, Exe, Process, InstallDir, Accent, Manifest, Status, PluginsVersion, GameVersion;
        public bool Available { get { return Status == "available" && Manifest != null; } }
    }

    class Catalog
    {
        public string Title;
        public List<GameInfo> Games = new List<GameInfo>();

        public static Catalog Parse(string json)
        {
            var root = J.Parse(json);
            var c = new Catalog { Title = J.S(root, "title") };
            foreach (IDictionary<string, object> g in J.L(root, "games"))
                c.Games.Add(new GameInfo
                {
                    Id = J.S(g, "id"), Name = J.S(g, "name"), Short = J.S(g, "short") ?? J.S(g, "id"),
                    AppId = J.S(g, "steam_appid"), Exe = J.S(g, "exe"), Process = J.S(g, "process"),
                    InstallDir = J.S(g, "install_dir"), Accent = J.S(g, "accent") ?? "#475467",
                    Manifest = J.S(g, "manifest"), Status = J.S(g, "status") ?? "planned",
                    PluginsVersion = J.S(g, "plugins_version"), GameVersion = J.S(g, "game_version")
                });
            return c;
        }
    }

    class Choice { public string Value; public string Label; }

    class Setting
    {
        public string Section, Key, Label, Default, ExactValue, Help;
        public List<Choice> Choices = new List<Choice>();
    }

    class Component
    {
        public string Id, Title, Description, Ini;
        public bool Required, Exact, DefaultOn = true;
        public List<string> Files = new List<string>();
        public List<Setting> Settings = new List<Setting>();
        public List<string[]> Fixed = new List<string[]>();
        public IEnumerable<string> AllFiles()
        {
            foreach (var f in Files) yield return f;
            if (Ini != null) yield return Ini;
        }
    }

    class Borderless { public string Component, File, Pattern, Replace, Label; }

    class Manifest
    {
        public string Version, Title, GameVersion, Package, PackageSha256;
        public List<string> ExeSha256 = new List<string>();
        public Dictionary<string, string> FileSha256 = new Dictionary<string, string>();
        public List<Component> Components = new List<Component>();
        public Borderless Borderless;

        public static Manifest Parse(string json)
        {
            var root = J.Parse(json);
            var m = new Manifest
            {
                Version = J.S(root, "version"), Title = J.S(root, "title"), GameVersion = J.S(root, "game_version"),
                Package = J.S(root, "package"), PackageSha256 = J.S(root, "package_sha256")
            };
            foreach (var h in J.L(root, "exe_sha256")) m.ExeSha256.Add(Convert.ToString(h).ToLowerInvariant());
            var fh = J.O(root, "file_sha256");
            if (fh != null) foreach (var kv in fh) m.FileSha256[kv.Key] = Convert.ToString(kv.Value);
            var bl = J.O(root, "borderless");
            if (bl != null)
                m.Borderless = new Borderless
                {
                    Component = J.S(bl, "component"), File = J.S(bl, "file"), Pattern = J.S(bl, "pattern"),
                    Replace = J.S(bl, "replace"), Label = J.S(bl, "label") ?? "Переключить игру в режим «без рамки»"
                };
            foreach (IDictionary<string, object> c in J.L(root, "components"))
            {
                var comp = new Component
                {
                    Id = J.S(c, "id"), Title = J.S(c, "title"), Description = J.S(c, "description"), Ini = J.S(c, "ini"),
                    Required = J.B(c, "required", false), Exact = J.B(c, "exact", true), DefaultOn = J.B(c, "default", true)
                };
                foreach (var f in J.L(c, "files")) comp.Files.Add(Convert.ToString(f));
                foreach (IDictionary<string, object> s in J.L(c, "settings"))
                {
                    var st = new Setting
                    {
                        Section = J.S(s, "section"), Key = J.S(s, "key"), Label = J.S(s, "label"), Default = J.S(s, "default"),
                        ExactValue = J.S(s, "exact_value"), Help = J.S(s, "help") ?? ""
                    };
                    foreach (var ch in J.L(s, "choices"))
                    {
                        var pair = ((IEnumerable)ch).Cast<object>().Select(Convert.ToString).ToArray();
                        st.Choices.Add(new Choice { Value = pair[0], Label = pair.Length > 1 ? pair[1] : pair[0] });
                    }
                    comp.Settings.Add(st);
                }
                foreach (var fx in J.L(c, "fixed"))
                    comp.Fixed.Add(((IEnumerable)fx).Cast<object>().Select(Convert.ToString).ToArray());
                m.Components.Add(comp);
            }
            return m;
        }
    }

    // ================================================================ helpers
    static class Util
    {
        public static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        public static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }

        public static string Local(string dir, string rel) { return Path.Combine(dir, rel.Replace('/', '\\')); }

        public static bool Running(string process)
        {
            return !string.IsNullOrEmpty(process) && System.Diagnostics.Process.GetProcessesByName(process).Length > 0;
        }

        public static bool CanWrite(string dir)
        {
            try
            {
                var probe = Path.Combine(dir, "speedup_launcher_probe.tmp");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
        }

        public static Brush Brush(string hex)
        {
            try
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                b.Freeze();
                return b;
            }
            catch { return Brushes.Gray; }
        }

        public static string FirstSentence(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var t = text.Replace("\r", " ").Replace("\n", " ");
            int dot = t.IndexOf(". ");
            if (dot > 0 && dot < max) return t.Substring(0, dot + 1);
            return t.Length <= max ? t : t.Substring(0, max).TrimEnd() + "…";
        }
    }

    // Small persistent store: chosen game folders.
    static class Prefs
    {
        static string FilePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ParadoxSpeedup", "folders.txt");
            }
        }

        public static string GetDir(string game)
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    if (line.StartsWith(game + "=")) return line.Substring(game.Length + 1);
            }
            catch { }
            return null;
        }

        public static void SetDir(string game, string dir)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath, Encoding.UTF8).Where(l => !l.StartsWith(game + "=")).ToList() : new List<string>();
                lines.Add(game + "=" + dir);
                File.WriteAllLines(FilePath, lines, Encoding.UTF8);
            }
            catch { }
        }
    }

    // INI editor that keeps comments and UTF-8 text intact.
    class IniFile
    {
        readonly List<string> lines;
        readonly string path;

        public IniFile(string path)
        {
            this.path = path;
            lines = File.Exists(path)
                ? File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Split('\n').ToList()
                : new List<string>();
            if (lines.Count > 0 && lines[lines.Count - 1] == "") lines.RemoveAt(lines.Count - 1);
        }

        int SectionStart(string section)
        {
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Trim().Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        int SectionEnd(int start)
        {
            int i = start + 1;
            while (i < lines.Count && !lines[i].TrimStart().StartsWith("[")) i++;
            return i;
        }

        int KeyLine(string section, string key)
        {
            int s = SectionStart(section);
            if (s < 0) return -1;
            int e = SectionEnd(s);
            for (int i = s + 1; i < e; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith(";") || t.StartsWith("#")) continue;
                int eq = t.IndexOf('=');
                if (eq > 0 && t.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        public string Get(string section, string key)
        {
            int i = KeyLine(section, key);
            if (i < 0) return null;
            var t = lines[i];
            return t.Substring(t.IndexOf('=') + 1).Trim();
        }

        public void Set(string section, string key, string value)
        {
            int i = KeyLine(section, key);
            if (i >= 0) { lines[i] = key + "=" + value; return; }
            int s = SectionStart(section);
            if (s < 0)
            {
                lines.Add("[" + section + "]");
                lines.Add(key + "=" + value);
                return;
            }
            int e = SectionEnd(s);
            while (e > s + 1 && lines[e - 1].Trim() == "") e--;
            lines.Insert(e, key + "=" + value);
        }

        public void Save()
        {
            File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
        }
    }

    static class GameLocator
    {
        static List<string> libraries;

        static List<string> Libraries()
        {
            if (libraries != null) return libraries;
            var steamRoots = new List<string>();
            foreach (var probe in new[] {
                new { Hive = Registry.CurrentUser, Key = @"Software\Valve\Steam", Value = "SteamPath" },
                new { Hive = Registry.LocalMachine, Key = @"SOFTWARE\WOW6432Node\Valve\Steam", Value = "InstallPath" },
                new { Hive = Registry.LocalMachine, Key = @"SOFTWARE\Valve\Steam", Value = "InstallPath" } })
            {
                try
                {
                    using (var k = probe.Hive.OpenSubKey(probe.Key))
                    {
                        var v = k == null ? null : k.GetValue(probe.Value) as string;
                        if (!string.IsNullOrEmpty(v)) steamRoots.Add(v.Replace('/', '\\'));
                    }
                }
                catch { }
            }
            var libs = new List<string>();
            foreach (var root in steamRoots)
            {
                libs.Add(root);
                var vdf = Path.Combine(root, @"steamapps\libraryfolders.vdf");
                try
                {
                    if (!File.Exists(vdf)) continue;
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
                catch { }
            }
            libraries = libs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return libraries;
        }

        public static string Find(GameInfo g)
        {
            foreach (var lib in Libraries())
            {
                try
                {
                    var acf = Path.Combine(lib, @"steamapps\appmanifest_" + g.AppId + ".acf");
                    string install = g.InstallDir ?? g.Name;
                    if (File.Exists(acf))
                    {
                        var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s*\"([^\"]+)\"");
                        if (m.Success) install = m.Groups[1].Value;
                    }
                    var dir = Path.Combine(lib, @"steamapps\common", install);
                    if (File.Exists(Util.Local(dir, g.Exe))) return dir;
                }
                catch { }
            }
            return null;
        }
    }

    // ================================================================ view models
    class Notifier : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void On(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    class GameVM : Notifier
    {
        public GameInfo Game;
        public string Dir;
        string status;
        Brush statusBrush;
        public string Name { get { return Game.Name; } }
        public string Short { get { return Game.Short; } }
        public Brush LogoBrush { get { return Util.Brush(Game.Accent); } }
        public double Opacity { get { return Game.Available ? 1.0 : 0.55; } }
        public string Status { get { return status; } set { status = value; On("Status"); } }
        public Brush StatusBrush { get { return statusBrush ?? Util.Brush("#8C95A6"); } set { statusBrush = value; On("StatusBrush"); } }
    }

    class PluginVM : Notifier
    {
        public Component C;
        public Action<PluginVM> Changed;
        bool isOn;
        public bool IsOn
        {
            get { return isOn; }
            set
            {
                if (C.Required) value = true;
                if (isOn == value) return;
                isOn = value;
                On("IsOn");
                if (Changed != null) Changed(this);
            }
        }
        public void SetQuietly(bool value) { isOn = C.Required || value; On("IsOn"); }
        public string Title { get { return C.Title; } }
        public string Summary { get { return Util.FirstSentence(C.Description, 90); } }
        public bool CanToggle { get { return !C.Required; } }
        public string SwitchTip { get { return C.Required ? "Нужен для работы остальных плагинов" : null; } }
        public string BadgeText { get { return C.Required ? "основа" : C.Exact ? "точный" : "меняет ход игры"; } }
        public Brush BadgeBg { get { return Util.Brush(C.Required ? "#EEF0F4" : C.Exact ? "#E7F6EC" : "#FFF1DD"); } }
        public Brush BadgeFg { get { return Util.Brush(C.Required ? "#556070" : C.Exact ? "#1E7B3C" : "#A35A00"); } }
    }

    class SettingVM : Notifier
    {
        public Setting S;
        public Action<SettingVM, string> Changed;
        int index;
        public string Label { get { return S.Label; } }
        public List<string> Choices { get { return S.Choices.Select(c => c.Label).ToList(); } }
        public string Help { get { return S.Help; } }
        public Visibility HelpVisibility { get { return string.IsNullOrEmpty(S.Help) ? Visibility.Collapsed : Visibility.Visible; } }
        public string ExactNote
        {
            get
            {
                if (S.ExactValue == null || S.ExactValue == S.Default) return null;
                var ch = S.Choices.FirstOrDefault(x => x.Value == S.ExactValue);
                return "В режиме «Только точные»: " + (ch == null ? S.ExactValue : ch.Label) + ".";
            }
        }
        public Visibility ExactVisibility { get { return ExactNote == null ? Visibility.Collapsed : Visibility.Visible; } }
        public int SelectedIndex
        {
            get { return index; }
            set
            {
                if (value < 0 || value == index) return;
                index = value;
                On("SelectedIndex");
                if (Changed != null) Changed(this, S.Choices[index].Value);
            }
        }
        public SettingVM(Setting s, string value)
        {
            S = s;
            index = Math.Max(0, s.Choices.FindIndex(c => c.Value == value));
        }
    }

    // ================================================================ main window logic
    class Launcher
    {
        readonly Window w;
        readonly string source;
        readonly string presetGame, presetDir;
        Catalog catalog;
        readonly Dictionary<string, Manifest> manifests = new Dictionary<string, Manifest>();
        readonly Dictionary<string, byte[]> packages = new Dictionary<string, byte[]>();

        GameVM game;          // selected game
        Manifest manifest;    // its manifest (null while loading or for planned games)
        bool versionOk;
        readonly Dictionary<string, string> values = new Dictionary<string, string>();
        readonly ObservableCollection<GameVM> games = new ObservableCollection<GameVM>();
        readonly ObservableCollection<PluginVM> plugins = new ObservableCollection<PluginVM>();
        bool suppress;
        bool busy;

        T E<T>(string name) where T : class { return (T)w.FindName(name); }
        ListBox GameList { get { return E<ListBox>("gameList"); } }
        ListBox PluginList { get { return E<ListBox>("pluginList"); } }
        RadioButton ModeAll { get { return E<RadioButton>("modeAll"); } }
        RadioButton ModeExact { get { return E<RadioButton>("modeExact"); } }
        RadioButton ModeCustom { get { return E<RadioButton>("modeCustom"); } }
        CheckBox BorderlessBox { get { return E<CheckBox>("borderless"); } }
        TextBox LogBox { get { return E<TextBox>("logBox"); } }

        public Launcher(Window window, string source, string presetGame, string presetDir)
        {
            w = window;
            this.source = source;
            this.presetGame = presetGame;
            this.presetDir = presetDir;
            E<TextBlock>("appVersion").Text = "Установщик " + Config.AppVersion;
            GameList.ItemsSource = games;
            PluginList.ItemsSource = plugins;
            GameList.SelectionChanged += (s, e) => { var g = GameList.SelectedItem as GameVM; if (g != null) SelectGame(g); };
            PluginList.SelectionChanged += (s, e) => ShowDetails();
            ModeAll.Checked += (s, e) => OnMode(true);
            ModeExact.Checked += (s, e) => OnMode(false);
            E<Button>("changeDirButton").Click += (s, e) => BrowseDir();
            E<Button>("openDirButton").Click += (s, e) => { if (game != null && Directory.Exists(game.Dir ?? "")) Process.Start("explorer.exe", "\"" + game.Dir + "\""); };
            E<Button>("installButton").Click += async (s, e) => await Install();
            E<Button>("removeButton").Click += (s, e) => Uninstall();
            E<Button>("checkButton").Click += (s, e) => CheckStatus(true);
            E<Button>("playButton").Click += (s, e) => Play();
            E<Button>("emptyButton").Click += (s, e) => BrowseDir();
            E<Button>("logToggle").Click += (s, e) => ToggleLog(null);
            w.Loaded += async (s, e) => await Startup();
        }

        // ------------------------------------------------------------ log & status
        void Log(string text)
        {
            if (!w.Dispatcher.CheckAccess()) { w.Dispatcher.BeginInvoke(new Action<string>(Log), text); return; }
            LogBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + "\r\n");
            LogBox.ScrollToEnd();
        }

        // kind: ok, warn, error, info, busy
        void Status(string text, string kind)
        {
            if (!w.Dispatcher.CheckAccess()) { w.Dispatcher.BeginInvoke(new Action<string, string>(Status), text, kind); return; }
            var icon = E<TextBlock>("statusIcon");
            icon.Text = kind == "ok" ? "\uE73E" : kind == "warn" ? "\uE7BA" : kind == "error" ? "\uEA39" : kind == "busy" ? "\uE895" : "\uE946";
            icon.Foreground = Util.Brush(kind == "ok" ? "#1E7B3C" : kind == "warn" ? "#A35A00" : kind == "error" ? "#B42318" : "#667085");
            E<TextBlock>("statusText").Text = text;
            E<TextBlock>("statusText").ToolTip = text;
            Log(text);
        }

        void ToggleLog(bool? show)
        {
            var panel = E<Border>("logPanel");
            bool visible = show ?? panel.Visibility != Visibility.Visible;
            panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            E<Button>("logToggle").Content = visible ? "Скрыть журнал" : "Показать журнал";
        }

        void SetProgress(double percent)
        {
            if (!w.Dispatcher.CheckAccess()) { w.Dispatcher.BeginInvoke(new Action<double>(SetProgress), percent); return; }
            var p = E<ProgressBar>("progress");
            p.Visibility = percent < 0 ? Visibility.Hidden : Visibility.Visible;
            p.Value = Math.Max(0, percent);
        }

        void Box(string text, MessageBoxImage icon)
        {
            if (Program.Auto != null) { Log("[сообщение] " + text.Replace('\n', ' ')); return; }
            MessageBox.Show(w, text, w.Title, MessageBoxButton.OK, icon);
        }

        bool Ask(string text)
        {
            if (Program.Auto != null) { Log("[вопрос, ответ «да»] " + text.Replace('\n', ' ')); return true; }
            return MessageBox.Show(w, text, w.Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        void SetBusy(bool value)
        {
            busy = value;
            w.Cursor = value ? System.Windows.Input.Cursors.Wait : null;
            UpdateButtons();
        }

        void UpdateButtons()
        {
            bool ready = game != null && manifest != null && Directory.Exists(game.Dir ?? "");
            var installedVersion = ready ? InstalledVersion() : null;
            var install = E<Button>("installButton");
            install.IsEnabled = !busy && ready && versionOk;
            install.Content = installedVersion == null ? "Установить" : installedVersion == manifest.Version ? "Применить" : "Обновить";
            E<Button>("removeButton").IsEnabled = !busy && installedVersion != null;
            E<Button>("checkButton").IsEnabled = !busy && installedVersion != null;
            E<Button>("playButton").IsEnabled = !busy && game != null && game.Dir != null;
            E<Button>("changeDirButton").IsEnabled = !busy && game != null && game.Game.Available;
            var avail = game != null && game.Game.Available ? Visibility.Visible : Visibility.Collapsed;
            foreach (var n in new[] { "installButton", "removeButton", "checkButton", "changeDirButton" }) E<Button>(n).Visibility = avail;
            foreach (var rb in new[] { ModeAll, ModeExact, ModeCustom }) rb.IsEnabled = !busy;
            PluginList.IsEnabled = !busy;
            GameList.IsEnabled = !busy;
        }

        // ------------------------------------------------------------ sources
        static bool IsUrl(string s) { return s.StartsWith("http://") || s.StartsWith("https://"); }

        string CatalogPath { get { return IsUrl(source) || File.Exists(source) ? source : Path.Combine(source, "catalog.json"); } }

        string Sibling(string name)
        {
            var cat = CatalogPath;
            if (IsUrl(cat)) return cat.Substring(0, cat.LastIndexOf('/') + 1) + name;
            return Path.Combine(Path.GetDirectoryName(cat), name);
        }

        async Task<byte[]> Read(string src)
        {
            if (!IsUrl(src)) return File.ReadAllBytes(src);
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "paradox-speedup-launcher";
                wc.DownloadProgressChanged += (s, e) => { if (e.TotalBytesToReceive > 0) SetProgress(e.ProgressPercentage); };
                return await wc.DownloadDataTaskAsync(new Uri(src));
            }
        }

        async Task<string> ReadText(string src)
        {
            return Encoding.UTF8.GetString(await Read(src)).TrimStart('\uFEFF');
        }

        // ------------------------------------------------------------ startup
        async Task Startup()
        {
            SetBusy(true);
            Status("Загружаю список игр и плагинов…", "busy");
            Exception failure = null;
            try { catalog = Catalog.Parse(await ReadText(CatalogPath)); }
            catch (Exception ex) { failure = ex; }
            if (failure != null)
            {
                var ex = failure;
                SetBusy(false);
                Status("Не удалось загрузить список плагинов: " + ex.Message, "error");
                ToggleLog(true);
                ShowEmpty("\uE871", "Нет связи с сервером плагинов",
                    "Проверьте подключение к интернету и запустите установщик ещё раз.\n" + ex.Message, null);
                if (Program.Auto != null) await RunAuto();
                return;
            }
            foreach (var g in catalog.Games)
            {
                var vm = new GameVM { Game = g };
                vm.Dir = g.Id == presetGame && presetDir != null ? presetDir : Prefs.GetDir(g.Id);
                if (vm.Dir != null && !File.Exists(Util.Local(vm.Dir, g.Exe))) vm.Dir = null;
                if (vm.Dir == null) vm.Dir = GameLocator.Find(g);
                RefreshGameStatus(vm);
                games.Add(vm);
            }
            SetBusy(false);
            var first = games.FirstOrDefault(g => g.Game.Id == presetGame)
                ?? games.FirstOrDefault(g => g.Game.Available && g.Dir != null)
                ?? games.FirstOrDefault(g => g.Game.Available) ?? games.FirstOrDefault();
            if (first != null)
            {
                game = first;   // so that SelectionChanged does not start a second load
                GameList.SelectedItem = first;
                game = null;
                await SelectGameAsync(first);
            }
            if (Program.Auto != null) await RunAuto();
        }

        void RefreshGameStatus(GameVM vm)
        {
            if (!vm.Game.Available) { vm.Status = vm.Dir != null ? "игра есть · плагины скоро" : "плагины скоро"; vm.StatusBrush = null; return; }
            if (vm.Dir == null) { vm.Status = "игра не найдена"; vm.StatusBrush = null; return; }
            var v = InstalledVersion(vm.Dir);
            if (v == null) { vm.Status = "плагины не установлены"; vm.StatusBrush = Util.Brush("#8C95A6"); }
            else { vm.Status = "плагины установлены"; vm.StatusBrush = Util.Brush("#6CCB8A"); }
        }

        // ------------------------------------------------------------ game selection
        async void SelectGame(GameVM g)
        {
            if (g == game) return;
            await SelectGameAsync(g);
        }

        async Task SelectGameAsync(GameVM g)
        {
            game = g;
            manifest = null;
            versionOk = false;
            plugins.Clear();
            var gi = g.Game;
            E<Border>("headLogo").Background = Util.Brush(gi.Accent);
            E<TextBlock>("headShort").Text = gi.Short;
            E<TextBlock>("headName").Text = gi.Name;
            E<TextBlock>("gameDirText").Text = g.Dir ?? "папка игры не найдена";
            E<CheckBox>("borderless").Visibility = Visibility.Collapsed;
            SetChip("chipInstalled", null, null);
            if (!gi.Available)
            {
                SetChip("chipVersion", "в разработке", "gray");
                ShowEmpty("\uE823", "Плагины для " + gi.Name + " пока в разработке",
                    "Когда они выйдут, они появятся здесь сами — установщик обновлять не нужно.", null);
                Status("Для этой игры плагинов пока нет.", "info");
                UpdateButtons();
                return;
            }
            if (!manifests.ContainsKey(gi.Id))
            {
                SetBusy(true);
                Status("Загружаю описание плагинов для " + gi.Name + "…", "busy");
                try { manifests[gi.Id] = Manifest.Parse(await ReadText(Sibling(gi.Manifest))); }
                catch (Exception ex)
                {
                    SetBusy(false);
                    Status("Не удалось загрузить описание плагинов: " + ex.Message, "error");
                    ShowEmpty("\uE871", "Не удалось загрузить плагины", ex.Message, null);
                    return;
                }
                SetBusy(false);
                if (game != g) return;
            }
            manifest = manifests[gi.Id];
            foreach (var c in manifest.Components)
                plugins.Add(new PluginVM { C = c, Changed = OnPluginToggled });
            var bl = manifest.Borderless;
            BorderlessBox.Visibility = bl == null ? Visibility.Collapsed : Visibility.Visible;
            if (bl != null) { BorderlessBox.Content = bl.Label; BorderlessBox.IsChecked = Program.Auto == null; }
            CheckGameDir();
        }

        void SetChip(string name, string text, string kind)
        {
            var chip = E<Border>(name);
            chip.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            if (text == null) return;
            var label = E<TextBlock>(name + "Text");
            label.Text = text;
            chip.Background = Util.Brush(kind == "ok" ? "#E7F6EC" : kind == "warn" ? "#FFF1DD" : kind == "error" ? "#FDE8E8" : kind == "blue" ? "#E8EFFE" : "#EEF0F4");
            label.Foreground = Util.Brush(kind == "ok" ? "#1E7B3C" : kind == "warn" ? "#A35A00" : kind == "error" ? "#B42318" : kind == "blue" ? "#1D4ED8" : "#556070");
        }

        void ShowEmpty(string icon, string title, string text, string button)
        {
            E<Grid>("content").Visibility = Visibility.Collapsed;
            E<Border>("emptyState").Visibility = Visibility.Visible;
            E<TextBlock>("emptyIcon").Text = icon;
            E<TextBlock>("emptyTitle").Text = title;
            E<TextBlock>("emptyText").Text = text;
            var b = E<Button>("emptyButton");
            b.Visibility = button == null ? Visibility.Collapsed : Visibility.Visible;
            b.Content = button;
        }

        void ShowContent()
        {
            E<Grid>("content").Visibility = Visibility.Visible;
            E<Border>("emptyState").Visibility = Visibility.Collapsed;
        }

        void BrowseDir()
        {
            if (game == null) return;
            var exe = game.Game.Exe.Replace('/', '\\');
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Папка " + game.Name + " (в ней лежит " + exe + ")" })
            {
                if (game.Dir != null) dlg.SelectedPath = game.Dir;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                if (!File.Exists(Util.Local(dlg.SelectedPath, game.Game.Exe)))
                {
                    Box("В этой папке нет " + exe + ". Выберите папку, где установлена игра.", MessageBoxImage.Warning);
                    return;
                }
                game.Dir = dlg.SelectedPath;
                Prefs.SetDir(game.Game.Id, game.Dir);
                E<TextBlock>("gameDirText").Text = game.Dir;
                CheckGameDir();
            }
        }

        void CheckGameDir()
        {
            versionOk = false;
            if (manifest == null) return;
            if (game.Dir == null)
            {
                SetChip("chipVersion", "игра не найдена", "warn");
                ShowEmpty("\uE721", "Не нашёл " + game.Name + " в Steam",
                    "Укажите папку, где установлена игра (в ней лежит " + game.Game.Exe.Replace('/', '\\') + ").", "Выбрать папку игры…");
                Status("Укажите папку игры.", "warn");
                UpdateButtons();
                return;
            }
            ShowContent();
            string hash = null;
            try { hash = Util.Sha256File(Util.Local(game.Dir, game.Game.Exe)); }
            catch (Exception ex) { Log("Не удалось прочитать " + game.Game.Exe + ": " + ex.Message); }
            versionOk = hash != null && manifest.ExeSha256.Contains(hash);
            if (versionOk) SetChip("chipVersion", "версия " + manifest.GameVersion + " — подходит", "ok");
            else SetChip("chipVersion", "версия игры не подходит (нужна " + manifest.GameVersion + ")", "error");
            ReadInstalledState();
            RefreshGameStatus(game);
            var installed = InstalledVersion();
            if (!versionOk)
                Status("Плагины сделаны для " + game.Game.Short + " " + manifest.GameVersion + " из Steam. С другой версией игры они не включатся, поэтому установка выключена.", "error");
            else if (installed == null)
                Status("Готово к установке. Выберите режим и нажмите «Установить».", "info");
            else if (installed != manifest.Version)
                Status("Доступна версия плагинов " + manifest.Version + ". Нажмите «Обновить» — ваши настройки сохранятся.", "warn");
            else
                Status("Плагины установлены. Изменили настройки — нажмите «Применить».", "ok");
            UpdateButtons();
        }

        static string InstalledVersion(string dir)
        {
            if (dir == null) return null;
            var state = Path.Combine(dir, Config.StateFile);
            if (File.Exists(state))
            {
                foreach (var line in File.ReadAllLines(state))
                    if (line.StartsWith("version=")) return line.Substring(8);
                return "?";
            }
            var pluginsDir = Path.Combine(dir, "plugins");
            bool manual = Directory.Exists(pluginsDir) && Directory.GetFiles(pluginsDir, "*.dll").Length > 0 &&
                File.Exists(Path.Combine(dir, "VERSION.dll"));
            return manual ? "вручную" : null;
        }

        string InstalledVersion() { return game == null ? null : InstalledVersion(game.Dir); }

        static string Key(Component c, Setting s) { return c.Id + "|" + s.Section + "|" + s.Key; }

        void ReadInstalledState()
        {
            var installed = InstalledVersion();
            if (installed == null) SetChip("chipInstalled", "плагины не установлены", "gray");
            else if (installed == manifest.Version) SetChip("chipInstalled", "установлены плагины версии " + installed, "blue");
            else if (installed == "вручную") SetChip("chipInstalled", "плагины поставлены вручную · доступна версия " + manifest.Version, "warn");
            else SetChip("chipInstalled", "установлена версия " + installed + " · доступна " + manifest.Version, "warn");
            values.Clear();
            foreach (var c in manifest.Components)
            {
                IniFile ini = null;
                if (installed != null && c.Ini != null && File.Exists(Util.Local(game.Dir, c.Ini))) ini = new IniFile(Util.Local(game.Dir, c.Ini));
                foreach (var s in c.Settings)
                {
                    string v = ini == null ? null : ini.Get(s.Section, s.Key);
                    values[Key(c, s)] = v != null && s.Choices.Any(ch => ch.Value == v) ? v : s.Default;
                }
            }
            suppress = true;
            if (installed == null)
            {
                ModeAll.IsChecked = true;
                ApplyMode(true);
            }
            else
            {
                foreach (var p in plugins)
                    p.SetQuietly(p.C.Files.Count > 0 && File.Exists(Util.Local(game.Dir, p.C.Files[0])));
                if (MatchesMode(true)) ModeAll.IsChecked = true;
                else if (MatchesMode(false)) ModeExact.IsChecked = true;
                else ModeCustom.IsChecked = true;
            }
            suppress = false;
            if (PluginList.SelectedIndex < 0 && plugins.Count > 0) PluginList.SelectedIndex = 0;
            ShowDetails();
            UpdateCount();
        }

        // ------------------------------------------------------------ modes
        void OnMode(bool all)
        {
            if (suppress || manifest == null) return;
            ApplyMode(all);
            ShowDetails();
        }

        void ApplyMode(bool all)
        {
            bool old = suppress;
            suppress = true;
            foreach (var p in plugins)
            {
                var c = p.C;
                p.SetQuietly(c.Required || (all ? c.DefaultOn : c.Exact && c.DefaultOn));
                foreach (var s in c.Settings)
                    values[Key(c, s)] = !all && s.ExactValue != null ? s.ExactValue : s.Default;
            }
            suppress = old;
            UpdateCount();
        }

        bool MatchesMode(bool all)
        {
            foreach (var p in plugins)
            {
                var c = p.C;
                if (p.IsOn != (c.Required || (all ? c.DefaultOn : c.Exact && c.DefaultOn))) return false;
                foreach (var s in c.Settings)
                    if (values[Key(c, s)] != (!all && s.ExactValue != null ? s.ExactValue : s.Default)) return false;
            }
            return true;
        }

        void OnPluginToggled(PluginVM p)
        {
            UpdateCount();
            if (!suppress) SwitchToCustom();
        }

        void SwitchToCustom()
        {
            if (suppress) return;
            suppress = true;
            ModeCustom.IsChecked = true;
            suppress = false;
        }

        void UpdateCount()
        {
            int on = plugins.Count(p => p.IsOn);
            E<TextBlock>("pluginCount").Text = "включено " + on + " из " + plugins.Count;
        }

        // ------------------------------------------------------------ details
        void ShowDetails()
        {
            var p = PluginList.SelectedItem as PluginVM;
            var list = E<ItemsControl>("settingsList");
            if (p == null)
            {
                E<TextBlock>("detTitle").Text = "";
                E<TextBlock>("detText").Text = "";
                list.ItemsSource = null;
                return;
            }
            var c = p.C;
            E<TextBlock>("detTitle").Text = c.Title;
            E<TextBlock>("detBadgeText").Text = c.Required ? "Основа: нужен остальным плагинам"
                : c.Exact ? "Точный: расчёты игры не меняются" : "Меняет ход игры: в режиме «Только точные» не ставится";
            E<Border>("detBadge").Background = p.BadgeBg;
            E<TextBlock>("detBadgeText").Foreground = p.BadgeFg;
            E<TextBlock>("detText").Text = c.Description ?? "";
            var items = c.Settings.Select(s => new SettingVM(s, values[Key(c, s)])
            {
                Changed = (vm, v) => { values[Key(c, vm.S)] = v; SwitchToCustom(); }
            }).ToList();
            list.ItemsSource = items;
            E<TextBlock>("noSettings").Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ------------------------------------------------------------ install
        bool EnsureWritable()
        {
            if (Util.CanWrite(game.Dir)) return true;
            if (!Ask("Для записи в папку игры нужны права администратора.\nПерезапустить установщик от имени администратора?")) return false;
            try
            {
                var args = "--game " + game.Game.Id + " --dir \"" + game.Dir + "\"" + (source != Config.DefaultSource ? " --source \"" + source + "\"" : "");
                Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, args) { Verb = "runas", UseShellExecute = true });
                w.Close();
            }
            catch (Exception ex) { Status("Не удалось перезапуститься: " + ex.Message, "error"); }
            return false;
        }

        async Task<byte[]> GetPackage()
        {
            var id = game.Game.Id + "|" + manifest.Version;
            byte[] data;
            if (packages.TryGetValue(id, out data)) return data;
            Status("Скачиваю плагины…", "busy");
            data = await Read(Sibling(manifest.Package));
            if (manifest.PackageSha256 != null && !Util.Sha256(data).Equals(manifest.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new Exception("контрольная сумма архива не совпала — файл повреждён при загрузке. Попробуйте ещё раз.");
            Log("Загружено " + (data.Length / 1024) + " КБ, контрольная сумма совпала.");
            packages[id] = data;
            return data;
        }

        async Task Install()
        {
            if (manifest == null || !versionOk || busy) return;
            if (Util.Running(game.Game.Process)) { Box("Закройте игру перед установкой.", MessageBoxImage.Warning); return; }
            if (!EnsureWritable()) return;
            SetBusy(true);
            SetProgress(0);
            try
            {
                var data = await GetPackage();
                var job = new InstallJob
                {
                    Dir = game.Dir, M = manifest, Values = new Dictionary<string, string>(values),
                    Selected = new HashSet<string>(plugins.Where(p => p.IsOn).Select(p => p.C.Id)),
                    Borderless = BorderlessBox.Visibility == Visibility.Visible && BorderlessBox.IsChecked == true,
                    Log = Log, Progress = SetProgress
                };
                Status("Устанавливаю…", "busy");
                await Task.Run(() => job.Run(data));
                SetBusy(false);
                CheckGameDir();
                Status("Готово: включено плагинов — " + job.Selected.Count + ". Запустите игру, потом можно нажать «Проверить работу».", "ok");
            }
            catch (Exception ex)
            {
                SetBusy(false);
                Status("Установка не удалась: " + ex.Message, "error");
                ToggleLog(true);
            }
            finally { SetProgress(-1); }
        }

        // ------------------------------------------------------------ uninstall
        void Uninstall()
        {
            if (manifest == null || game.Dir == null || busy) return;
            if (Util.Running(game.Game.Process)) { Box("Закройте игру.", MessageBoxImage.Warning); return; }
            if (!Ask("Удалить все плагины этого установщика из папки " + game.Name + "?\nСохранения и сама игра не затрагиваются.")) return;
            if (!EnsureWritable()) return;
            try
            {
                int removed = new InstallJob { Dir = game.Dir, M = manifest, Log = Log }.Uninstall();
                CheckGameDir();
                Status("Удалено файлов: " + removed + ". Игра снова обычная.", "ok");
            }
            catch (Exception ex) { Status("Ошибка удаления: " + ex.Message, "error"); ToggleLog(true); }
        }

        void Play()
        {
            try { Process.Start("steam://rungameid/" + game.Game.AppId); Status("Запускаю " + game.Name + " через Steam…", "info"); }
            catch (Exception ex) { Status("Не удалось запустить: " + ex.Message, "error"); }
        }

        // ------------------------------------------------------------ status check
        void CheckStatus(bool showLog)
        {
            if (manifest == null || game.Dir == null) return;
            Log("Проверка по журналам последнего запуска игры:");
            int ok = 0, total = 0, notRun = 0, bad = 0;
            foreach (var c in manifest.Components)
            {
                var dll = c.Files.FirstOrDefault(f => f.StartsWith("plugins/") && f.EndsWith(".dll"));
                if (dll == null || !File.Exists(Util.Local(game.Dir, dll))) continue;
                total++;
                var verdict = InstallJob.Verdict(Util.Local(game.Dir, dll.Substring(0, dll.Length - 4) + ".log"));
                if (verdict == "OK") ok++;
                else if (verdict.StartsWith("не запускался")) notRun++;
                else if (verdict.StartsWith("ОТКАЗ") || verdict.StartsWith("OK, частично")) bad++;
                Log("  " + c.Title + ": " + verdict);
            }
            if (notRun == total && total > 0) Status("Игра ещё не запускалась с плагинами. Запустите её и проверьте снова.", "info");
            else if (ok == total) Status("Работают все плагины: " + ok + " из " + total + ".", "ok");
            else Status("Работают " + ok + " из " + total + " плагинов — подробности в журнале.", bad > 0 ? "warn" : "info");
            if (showLog && ok != total) ToggleLog(true);
        }

        // ------------------------------------------------------------ headless mode
        // --auto all|exact|uninstall|check|shot[:plugin[:log]] : one action without user input; log written next to the exe.
        async Task RunAuto()
        {
            var outDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            try
            {
                var a = Program.Auto;
                if (a == "all") { ModeAll.IsChecked = true; await Install(); }
                else if (a == "exact") { ModeExact.IsChecked = true; await Install(); }
                else if (a == "uninstall") Uninstall();
                else if (a == "check") CheckStatus(false);
                else if (a.StartsWith("shot"))
                {
                    var parts = a.Split(':');
                    if (parts.Length > 1 && parts[1] != "")
                    {
                        var p = plugins.FirstOrDefault(x => x.C.Id == parts[1]);
                        if (p != null) PluginList.SelectedItem = p;
                    }
                    if (parts.Length > 2 && parts[2] == "log") ToggleLog(true);
                    await Task.Delay(500);
                    var root = (FrameworkElement)w.Content;
                    var dpi = VisualTreeHelper.GetDpi(root);
                    var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                        dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    bmp.Render(root);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    var name = "launcher_" + (game == null ? "none" : game.Game.Id) + (parts.Length > 1 && parts[1] != "" ? "_" + parts[1] : "") + ".png";
                    using (var f = File.Create(Path.Combine(outDir, name))) enc.Save(f);
                }
            }
            catch (Exception ex) { Log("ОШИБКА: " + ex); }
            try { File.WriteAllText(Path.Combine(outDir, "launcher_auto.log"), LogBox.Text, Encoding.UTF8); } catch { }
            w.Close();
        }
    }

    // ================================================================ file work (runs off the UI thread)
    class InstallJob
    {
        public string Dir;
        public Manifest M;
        public Dictionary<string, string> Values;
        public HashSet<string> Selected;
        public bool Borderless;
        public Action<string> Log;
        public Action<double> Progress;

        static string Key(Component c, Setting s) { return c.Id + "|" + s.Section + "|" + s.Key; }

        string State { get { return Path.Combine(Dir, Config.StateFile); } }

        bool InState(string rel) { return File.Exists(State) && File.ReadAllLines(State).Contains("file=" + rel); }

        bool IsOurs(string rel, string path)
        {
            if (rel.StartsWith("plugins/")) return true;
            string expected;
            if (M.FileSha256.TryGetValue(rel, out expected) && Util.Sha256File(path) == expected) return true;
            return InState(rel);
        }

        public void Run(byte[] data)
        {
            Directory.CreateDirectory(Path.Combine(Dir, "plugins"));
            var written = new List<string>();
            using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
            {
                int done = 0;
                foreach (var c in M.Components)
                {
                    bool on = c.Required || Selected.Contains(c.Id);
                    foreach (var rel in c.AllFiles())
                    {
                        var path = Util.Local(Dir, rel);
                        if (on)
                        {
                            var entry = zip.GetEntry(rel);
                            if (entry == null) throw new Exception("в архиве нет файла " + rel);
                            byte[] bytes;
                            using (var s = entry.Open())
                            using (var ms = new MemoryStream()) { s.CopyTo(ms); bytes = ms.ToArray(); }
                            string expected;
                            if (M.FileSha256.TryGetValue(rel, out expected) && !Util.Sha256(bytes).Equals(expected, StringComparison.OrdinalIgnoreCase))
                                throw new Exception("контрольная сумма файла " + rel + " не совпала");
                            BackupForeign(rel, path, bytes);
                            Directory.CreateDirectory(Path.GetDirectoryName(path));
                            File.WriteAllBytes(path, bytes);
                            written.Add(rel);
                        }
                        else if (File.Exists(path) && IsOurs(rel, path))
                        {
                            File.Delete(path);
                            Log("Удалён: " + rel);
                        }
                    }
                    if (on && c.Ini != null)
                    {
                        // a fresh ini from the package (new comments and keys), then every managed value
                        var ini = new IniFile(Util.Local(Dir, c.Ini));
                        foreach (var fx in c.Fixed) ini.Set(fx[0], fx[1], fx[2]);
                        foreach (var s in c.Settings) ini.Set(s.Section, s.Key, Values[Key(c, s)]);
                        ini.Save();
                    }
                    if (on) Log("Установлен: " + c.Title);
                    Progress(++done * 100.0 / M.Components.Count);
                }
            }
            RestoreBackups();
            var state = new StringBuilder();
            state.Append("version=" + M.Version + "\r\n");
            foreach (var f in written) state.Append("file=" + f + "\r\n");
            File.WriteAllText(State, state.ToString(), new UTF8Encoding(false));
            if (Borderless && M.Borderless != null && Selected.Contains(M.Borderless.Component)) SetBorderless();
        }

        // A top-level DLL (VERSION.dll, d3d9.dll…) that is not ours is saved once and returned on removal.
        void BackupForeign(string rel, string path, byte[] ours)
        {
            if (rel.Contains("/") || !rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return;
            var current = Util.Sha256File(path);
            if (current == Util.Sha256(ours) || InState(rel)) return;
            var backup = Path.Combine(Dir, Config.BackupDir);
            Directory.CreateDirectory(backup);
            var target = Path.Combine(backup, Path.GetFileName(path));
            if (File.Exists(target)) target = Path.Combine(backup, Path.GetFileNameWithoutExtension(path) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".dll");
            File.Copy(path, target);
            Log("Чужой " + rel + " сохранён в " + Config.BackupDir + " (вернётся при удалении).");
        }

        void RestoreBackups()
        {
            var backup = Path.Combine(Dir, Config.BackupDir);
            if (!Directory.Exists(backup)) return;
            foreach (var saved in Directory.GetFiles(backup, "*.dll"))
            {
                var name = Path.GetFileName(saved);
                var live = Path.Combine(Dir, name);
                if (!File.Exists(live) && !Regex.IsMatch(name, @"_\d{8}_\d{6}\.dll$"))
                {
                    File.Move(saved, live);
                    Log("Возвращён ваш прежний " + name + ".");
                }
            }
        }

        void SetBorderless()
        {
            try
            {
                var b = M.Borderless;
                var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), b.File.Replace('/', '\\'));
                if (!File.Exists(file)) { Log("Файл настроек игры не найден — режим экрана поменяйте в настройках игры."); return; }
                var text = File.ReadAllText(file);
                var updated = Regex.Replace(text, b.Pattern, b.Replace);
                if (updated == text) { Log("Режим экрана уже «без рамки»."); return; }
                File.Copy(file, file + ".bak_launcher", true);
                File.WriteAllText(file, updated);
                Log("Режим экрана переключён на «без рамки» (копия настроек: " + Path.GetFileName(file) + ".bak_launcher).");
            }
            catch (Exception ex) { Log("Не удалось поменять режим экрана: " + ex.Message); }
        }

        public int Uninstall()
        {
            int removed = 0;
            foreach (var c in M.Components)
                foreach (var rel in c.AllFiles())
                {
                    var path = Util.Local(Dir, rel);
                    if (File.Exists(path) && IsOurs(rel, path)) { File.Delete(path); removed++; }
                }
            var plugins = Path.Combine(Dir, "plugins");
            if (Directory.Exists(plugins))
            {
                foreach (var c in M.Components)
                    foreach (var dll in c.Files.Where(f => f.StartsWith("plugins/") && f.EndsWith(".dll")))
                    {
                        var stem = dll.Substring(0, dll.Length - 4);
                        var log = Util.Local(Dir, stem + ".log");
                        if (File.Exists(log)) File.Delete(log);
                        var data = Util.Local(Dir, stem + "_data");
                        if (Directory.Exists(data)) Directory.Delete(data, true);
                    }
                if (Directory.GetFileSystemEntries(plugins).Length == 0) Directory.Delete(plugins);
            }
            foreach (var extra in new[] { "eu4.dxvk-cache", "eu4_d3d9.log", Config.StateFile })
            {
                var p = Path.Combine(Dir, extra);
                if (File.Exists(p)) File.Delete(p);
            }
            RestoreBackups();
            var backup = Path.Combine(Dir, Config.BackupDir);
            if (Directory.Exists(backup) && Directory.GetFileSystemEntries(backup).Length == 0) Directory.Delete(backup);
            Log("Удалено файлов: " + removed + ".");
            return removed;
        }

        public static string Verdict(string logPath)
        {
            if (!File.Exists(logPath)) return "не запускался (игру ещё не запускали после установки)";
            string[] lines;
            try
            {
                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    lines = sr.ReadToEnd().Split('\n');
            }
            catch (Exception ex) { return "не удалось прочитать журнал: " + ex.Message; }
            int init = -1;
            for (int i = lines.Length - 1; i >= 0; i--)
                if (lines[i].Contains("initialized") || lines[i].Contains(" installed") || lines[i].Contains("disabled by configuration")) { init = i; break; }
            if (init < 0) return lines.Any(l => l.Contains("REFUSED")) ? "ОТКАЗ (не та версия игры?)" : "нет данных";
            var pid = Regex.Match(lines[init], @"^\[(\d+):").Groups[1].Value;
            Func<string, bool> same = l => pid != "" && l.StartsWith("[" + pid + ":");
            bool active = lines.Any(l => same(l) && (l.Contains("initialized") || l.Contains(" installed")));
            if (!active) return "выключен в настройках";
            if (lines.Any(l => same(l) && l.Contains("REFUSED"))) return "OK, частично (часть функций отключилась, см. " + Path.GetFileName(logPath) + ")";
            return "OK";
        }
    }

    static class Program
    {
        public static string Auto;

        [STAThread]
        static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // TLS 1.2 + 1.3
            string source = Config.DefaultSource, dir = null, gameId = null;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--source") source = args[++i];
                else if (args[i] == "--dir") dir = args[++i];
                else if (args[i] == "--game") gameId = args[++i];
                else if (args[i] == "--auto") Auto = args[++i];
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            Window window;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                window = (Window)XamlReader.Load(s);
            new Launcher(window, source, gameId, dir);
            app.Run(window);
        }
    }
}
