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

[assembly: AssemblyTitle("Paradox games speed-up — installer / Ускорение игр Paradox — установщик")]
[assembly: AssemblyProduct("Paradox games speed-up / Ускорение игр Paradox")]
[assembly: AssemblyCompany("Ostapcev")]
[assembly: AssemblyCopyright("MIT, github.com/Ostapcev/paradox-speedup-launcher")]
[assembly: AssemblyVersion("2.2.0.0")]
[assembly: AssemblyFileVersion("2.2.0.0")]

namespace SpeedLauncher
{
    static class Config
    {
        // Where the launcher looks for catalog.json (the other files are next to it).
        // Plugin releases live in the paradox-speedup repository (catalog.json, <game>_manifest.json, <game>_plugins.zip).
        public const string DefaultSource = "https://github.com/Ostapcev/paradox-speedup/releases/latest/download/catalog.json";
        public const string AppVersion = "2.2.0";
        public const string StateFile = "speedup_launcher_state.txt";
        public const string BackupDir = "speedup_launcher_backup";
    }

    // ================================================================ language
    // Russian or English: --lang, then the saved choice, then Windows' display language.
    static class L
    {
        public static bool En;
        public static string Code { get { return En ? "en" : "ru"; } }
        public static string T(string ru, string en) { return En ? en : ru; }

        public static void Choose(string arg)
        {
            var saved = arg ?? Prefs.GetDir("lang");
            if (saved == "en" || saved == "ru") { En = saved == "en"; return; }
            var ui = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            En = !(ui == "ru" || ui == "be" || ui == "kk");
        }
    }

    // ================================================================ json helpers
    static class J
    {
        // A text field in the chosen language: "key_en" in English when present, else "key".
        public static string T(IDictionary<string, object> o, string key)
        {
            if (SpeedLauncher.L.En) { var en = S(o, key + "_en"); if (en != null) return en; }
            return S(o, key);
        }

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
            var c = new Catalog { Title = J.T(root, "title") };
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
        public List<string> Builds = new List<string>();   // empty: every build in exe_builds
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
        public Dictionary<string, string> ExeBuild = new Dictionary<string, string>();   // exe sha256 -> "Steam" / "GOG"
        public List<Component> Components = new List<Component>();
        public Borderless Borderless;

        public static Manifest Parse(string json)
        {
            var root = J.Parse(json);
            var m = new Manifest
            {
                Version = J.S(root, "version"), Title = J.T(root, "title"), GameVersion = J.S(root, "game_version"),
                Package = J.S(root, "package"), PackageSha256 = J.S(root, "package_sha256")
            };
            foreach (var h in J.L(root, "exe_sha256")) m.ExeSha256.Add(Convert.ToString(h).ToLowerInvariant());
            var eb = J.O(root, "exe_builds");
            if (eb != null) foreach (var kv in eb) m.ExeBuild[kv.Key.ToLowerInvariant()] = Convert.ToString(kv.Value);
            var fh = J.O(root, "file_sha256");
            if (fh != null) foreach (var kv in fh) m.FileSha256[kv.Key] = Convert.ToString(kv.Value);
            var bl = J.O(root, "borderless");
            if (bl != null)
                m.Borderless = new Borderless
                {
                    Component = J.S(bl, "component"), File = J.S(bl, "file"), Pattern = J.S(bl, "pattern"),
                    Replace = J.S(bl, "replace"), Label = J.T(bl, "label") ?? L.T("Переключить игру в режим «без рамки»", "Switch the game to borderless fullscreen")
                };
            foreach (IDictionary<string, object> c in J.L(root, "components"))
            {
                var comp = new Component
                {
                    Id = J.S(c, "id"), Title = J.T(c, "title"), Description = J.T(c, "description"), Ini = J.S(c, "ini"),
                    Required = J.B(c, "required", false), Exact = J.B(c, "exact", true), DefaultOn = J.B(c, "default", true)
                };
                foreach (var f in J.L(c, "files")) comp.Files.Add(Convert.ToString(f));
                foreach (var b in J.L(c, "builds")) comp.Builds.Add(Convert.ToString(b));
                foreach (IDictionary<string, object> s in J.L(c, "settings"))
                {
                    var st = new Setting
                    {
                        Section = J.S(s, "section"), Key = J.S(s, "key"), Label = J.T(s, "label"), Default = J.S(s, "default"),
                        ExactValue = J.S(s, "exact_value"), Help = J.T(s, "help") ?? ""
                    };
                    foreach (var ch in J.L(s, "choices"))
                    {
                        var pair = ((IEnumerable)ch).Cast<object>().Select(Convert.ToString).ToArray();
                        st.Choices.Add(new Choice { Value = pair[0], Label = L.En && pair.Length > 2 ? pair[2] : pair.Length > 1 ? pair[1] : pair[0] });
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
            return FindGog(g);
        }

        static string Value(RegistryKey parent, string sub, string name)
        {
            using (var k = parent.OpenSubKey(sub)) return k == null ? null : k.GetValue(name) as string;
        }

        // GOG: GOG Galaxy and the offline installers register the game under GOG.com\Games and in
        // the uninstall list; then the default install folders.
        static string FindGog(GameInfo g)
        {
            var candidates = new List<string>();
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (var key in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" })
                {
                    try
                    {
                        using (var k = hive.OpenSubKey(key))
                        {
                            if (k == null) continue;
                            foreach (var id in k.GetSubKeyNames())
                            {
                                var name = Value(k, id, "gameName");
                                var path = Value(k, id, "path");
                                if (path != null && name != null && name.IndexOf(g.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                                    candidates.Add(path);
                            }
                        }
                    }
                    catch { }
                }
            foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(key))
                    {
                        if (k == null) continue;
                        foreach (var id in k.GetSubKeyNames())
                        {
                            var name = Value(k, id, "DisplayName");
                            var publisher = Value(k, id, "Publisher");
                            var path = Value(k, id, "InstallLocation");
                            if (path != null && name != null && publisher != null &&
                                publisher.IndexOf("GOG", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                name.IndexOf(g.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                                candidates.Add(path);
                        }
                    }
                }
                catch { }
            }
            var install = g.InstallDir ?? g.Name;
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"GOG Galaxy\Games", install));
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                    if (drive.DriveType == DriveType.Fixed) candidates.Add(Path.Combine(drive.Name, "GOG Games", install));
            }
            catch { }
            foreach (var c in candidates)
            {
                try { if (File.Exists(Util.Local(c, g.Exe))) return c.TrimEnd('\\'); }
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
                if (Unsupported) value = false;
                if (isOn == value) return;
                isOn = value;
                On("IsOn");
                if (Changed != null) Changed(this);
            }
        }
        public void SetQuietly(bool value) { isOn = !Unsupported && (C.Required || value); On("IsOn"); }
        // the plugin does not work on the game build in the chosen folder: off and locked
        public bool Unsupported;
        public void SetBuild(string build)
        {
            Unsupported = build != null && C.Builds.Count > 0 &&
                !C.Builds.Any(b => string.Equals(b, build, StringComparison.OrdinalIgnoreCase));
            if (Unsupported) isOn = false;
            foreach (var n in new[] { "IsOn", "CanToggle", "SwitchTip", "BadgeText", "BadgeBg", "BadgeFg" }) On(n);
        }
        public string Title { get { return C.Title; } }
        public string Summary { get { return Util.FirstSentence(C.Description, 90); } }
        public bool CanToggle { get { return !C.Required && !Unsupported; } }
        public string SwitchTip
        {
            get
            {
                return Unsupported ? L.T("Работает только в версии игры из ", "Works only in the game version from ") + string.Join(" / ", C.Builds)
                    : C.Required ? L.T("Нужен для работы остальных плагинов", "Needed by the other plugins") : null;
            }
        }
        public string BadgeText { get { return Unsupported ? L.T("только ", "only ") + string.Join(" / ", C.Builds) : C.Required ? L.T("основа", "base") : C.Exact ? L.T("точный", "exact") : L.T("меняет ход игры", "changes gameplay"); } }
        public Brush BadgeBg { get { return Util.Brush(Unsupported || C.Required ? "#EEF0F4" : C.Exact ? "#E7F6EC" : "#FFF1DD"); } }
        public Brush BadgeFg { get { return Util.Brush(Unsupported || C.Required ? "#556070" : C.Exact ? "#1E7B3C" : "#A35A00"); } }
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
                return L.T("В режиме «Только точные»: ", "In “Exact only” mode: ") + (ch == null ? S.ExactValue : ch.Label) + ".";
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
        string build;   // "Steam" / "GOG" for an accepted eu4.exe, else null
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
            E<TextBlock>("appVersion").Text = L.T("Установщик ", "Installer ") + Config.AppVersion;
            E<Button>("langButton").Content = L.T("English", "Русский");
            E<Button>("langButton").Click += (s, e) => SwitchLanguage();
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
            E<Button>("logToggle").Content = visible ? L.T("Скрыть журнал", "Hide log") : L.T("Показать журнал", "Show log");
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
            if (Program.Auto != null) { Log(L.T("[сообщение] ", "[message] ") + text.Replace('\n', ' ')); return; }
            MessageBox.Show(w, text, w.Title, MessageBoxButton.OK, icon);
        }

        bool Ask(string text)
        {
            if (Program.Auto != null) { Log(L.T("[вопрос, ответ «да»] ", "[question, answered yes] ") + text.Replace('\n', ' ')); return true; }
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
            install.Content = installedVersion == null ? L.T("Установить", "Install") : installedVersion == manifest.Version ? L.T("Применить", "Apply") : L.T("Обновить", "Update");
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
            Status(L.T("Загружаю список игр и плагинов…", "Loading the list of games and plugins…"), "busy");
            Exception failure = null;
            try { catalog = Catalog.Parse(await ReadText(CatalogPath)); }
            catch (Exception ex) { failure = ex; }
            if (failure != null)
            {
                var ex = failure;
                SetBusy(false);
                Status(L.T("Не удалось загрузить список плагинов: ", "Could not load the plugin list: ") + ex.Message, "error");
                ToggleLog(true);
                ShowEmpty("\uE871", L.T("Нет связи с сервером плагинов", "No connection to the plugin server"),
                    L.T("Проверьте подключение к интернету и запустите установщик ещё раз.\n", "Check your internet connection and start the installer again.\n") + ex.Message, null);
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
            if (!vm.Game.Available) { vm.Status = vm.Dir != null ? L.T("игра есть · плагины скоро", "game found · plugins coming") : L.T("плагины скоро", "plugins coming"); vm.StatusBrush = null; return; }
            if (vm.Dir == null) { vm.Status = L.T("игра не найдена", "game not found"); vm.StatusBrush = null; return; }
            var v = InstalledVersion(vm.Dir);
            if (v == null) { vm.Status = L.T("плагины не установлены", "plugins not installed"); vm.StatusBrush = Util.Brush("#8C95A6"); }
            else { vm.Status = L.T("плагины установлены", "plugins installed"); vm.StatusBrush = Util.Brush("#6CCB8A"); }
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
            E<TextBlock>("gameDirText").Text = g.Dir ?? L.T("папка игры не найдена", "game folder not found");
            E<CheckBox>("borderless").Visibility = Visibility.Collapsed;
            SetChip("chipInstalled", null, null);
            if (!gi.Available)
            {
                SetChip("chipVersion", L.T("в разработке", "in development"), "gray");
                ShowEmpty("\uE823", L.T("Плагины для ", "Plugins for ") + gi.Name + L.T(" пока в разработке", " are in development"),
                    L.T("Когда они выйдут, они появятся здесь сами — установщик обновлять не нужно.", "When they are released they will appear here by themselves — no need to update the installer."), null);
                Status(L.T("Для этой игры плагинов пока нет.", "No plugins for this game yet."), "info");
                UpdateButtons();
                return;
            }
            if (!manifests.ContainsKey(gi.Id))
            {
                SetBusy(true);
                Status(L.T("Загружаю описание плагинов для ", "Loading the plugin descriptions for ") + gi.Name + "…", "busy");
                try { manifests[gi.Id] = Manifest.Parse(await ReadText(Sibling(gi.Manifest))); }
                catch (Exception ex)
                {
                    SetBusy(false);
                    Status(L.T("Не удалось загрузить описание плагинов: ", "Could not load the plugin descriptions: ") + ex.Message, "error");
                    ShowEmpty("\uE871", L.T("Не удалось загрузить плагины", "Could not load the plugins"), ex.Message, null);
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
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = L.T("Папка ", "Folder of ") + game.Name + L.T(" (в ней лежит ", " (it contains ") + exe + ")" })
            {
                if (game.Dir != null) dlg.SelectedPath = game.Dir;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                if (!File.Exists(Util.Local(dlg.SelectedPath, game.Game.Exe)))
                {
                    Box(L.T("В этой папке нет ", "This folder has no ") + exe + L.T(". Выберите папку, где установлена игра.", ". Choose the folder where the game is installed."), MessageBoxImage.Warning);
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
                SetChip("chipVersion", L.T("игра не найдена", "game not found"), "warn");
                ShowEmpty("\uE721", L.T("Не нашёл ", "Could not find ") + game.Name + L.T(" в Steam и GOG", " in Steam or GOG"),
                    L.T("Укажите папку, где установлена игра (в ней лежит ", "Choose the folder where the game is installed (it contains ") + game.Game.Exe.Replace('/', '\\') + ").", L.T("Выбрать папку игры…", "Choose the game folder…"));
                Status(L.T("Укажите папку игры.", "Choose the game folder."), "warn");
                UpdateButtons();
                return;
            }
            ShowContent();
            string hash = null;
            try { hash = Util.Sha256File(Util.Local(game.Dir, game.Game.Exe)); }
            catch (Exception ex) { Log(L.T("Не удалось прочитать ", "Could not read ") + game.Game.Exe + ": " + ex.Message); }
            versionOk = hash != null && manifest.ExeSha256.Contains(hash);
            build = null;
            if (versionOk && !manifest.ExeBuild.TryGetValue(hash, out build)) build = null;
            foreach (var p in plugins) p.SetBuild(build);
            if (build != null) Log(L.T("Игра: ", "Game: ") + game.Dir + L.T(" (версия из ", " (version from ") + build + ")");
            if (versionOk) SetChip("chipVersion", L.T("версия ", "version ") + manifest.GameVersion + (build != null ? " " + build : "") + L.T(" — подходит", " — compatible"), "ok");
            else SetChip("chipVersion", L.T("версия игры не подходит (нужна ", "game version does not match (needs ") + manifest.GameVersion + ")", "error");
            ReadInstalledState();
            RefreshGameStatus(game);
            var installed = InstalledVersion();
            if (!versionOk)
                Status(L.T("Плагины сделаны для ", "The plugins are made for ") + game.Game.Short + " " + manifest.GameVersion + L.T(" из Steam или GOG. С другой версией игры они не включатся, поэтому установка выключена.", " from Steam or GOG. They will not turn on with another game version, so installing is disabled."), "error");
            else if (installed == null)
                Status(L.T("Готово к установке. Выберите режим и нажмите «Установить».", "Ready to install. Choose a mode and press “Install”."), "info");
            else if (installed != manifest.Version)
                Status(L.T("Доступна версия плагинов ", "Plugins version available: ") + manifest.Version + L.T(". Нажмите «Обновить» — ваши настройки сохранятся.", ". Press “Update” — your settings are kept."), "warn");
            else
                Status(L.T("Плагины установлены. Изменили настройки — нажмите «Применить».", "Plugins installed. Changed the settings? Press “Apply”."), "ok");
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
            return manual ? L.T("вручную", "manually") : null;
        }

        string InstalledVersion() { return game == null ? null : InstalledVersion(game.Dir); }

        static string Key(Component c, Setting s) { return c.Id + "|" + s.Section + "|" + s.Key; }

        void ReadInstalledState()
        {
            var installed = InstalledVersion();
            if (installed == null) SetChip("chipInstalled", L.T("плагины не установлены", "plugins not installed"), "gray");
            else if (installed == manifest.Version) SetChip("chipInstalled", L.T("установлены плагины версии ", "installed plugins version ") + installed, "blue");
            else if (installed == L.T("вручную", "manually")) SetChip("chipInstalled", L.T("плагины поставлены вручную · доступна версия ", "plugins installed manually · available version ") + manifest.Version, "warn");
            else SetChip("chipInstalled", L.T("установлена версия ", "installed version ") + installed + L.T(" · доступна ", " · available ") + manifest.Version, "warn");
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
                if (p.IsOn != (!p.Unsupported && (c.Required || (all ? c.DefaultOn : c.Exact && c.DefaultOn)))) return false;
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
            E<TextBlock>("pluginCount").Text = L.T("включено ", "enabled ") + on + L.T(" из ", " of ") + plugins.Count;
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
            E<TextBlock>("detBadgeText").Text = c.Required ? L.T("Основа: нужен остальным плагинам", "Base: needed by the other plugins")
                : c.Exact ? L.T("Точный: расчёты игры не меняются", "Exact: the game's calculations do not change") : L.T("Меняет ход игры: в режиме «Только точные» не ставится", "Changes gameplay: not installed in “Exact only” mode");
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
            if (!Ask(L.T("Для записи в папку игры нужны права администратора.\nПерезапустить установщик от имени администратора?", "Writing to the game folder needs administrator rights.\nRestart the installer as administrator?"))) return false;
            try
            {
                var args = "--game " + game.Game.Id + " --dir \"" + game.Dir + "\"" + (source != Config.DefaultSource ? " --source \"" + source + "\"" : "") + " --lang " + L.Code;
                Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, args) { Verb = "runas", UseShellExecute = true });
                w.Close();
            }
            catch (Exception ex) { Status(L.T("Не удалось перезапуститься: ", "Could not restart: ") + ex.Message, "error"); }
            return false;
        }

        // Saves the other language and starts the launcher again with it (same game and folder).
        void SwitchLanguage()
        {
            if (busy) return;
            var lang = L.En ? "ru" : "en";
            Prefs.SetDir("lang", lang);
            var args = "--lang " + lang + (game != null ? " --game " + game.Game.Id : "") +
                (game != null && game.Dir != null ? " --dir \"" + game.Dir + "\"" : "") +
                (source != Config.DefaultSource ? " --source \"" + source + "\"" : "");
            try
            {
                Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, args) { UseShellExecute = true });
                w.Close();
            }
            catch (Exception ex) { Status(ex.Message, "error"); }
        }

        async Task<byte[]> GetPackage()
        {
            var id = game.Game.Id + "|" + manifest.Version;
            byte[] data;
            if (packages.TryGetValue(id, out data)) return data;
            Status(L.T("Скачиваю плагины…", "Downloading the plugins…"), "busy");
            data = await Read(Sibling(manifest.Package));
            if (manifest.PackageSha256 != null && !Util.Sha256(data).Equals(manifest.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new Exception(L.T("контрольная сумма архива не совпала — файл повреждён при загрузке. Попробуйте ещё раз.", "the archive checksum does not match — the file was damaged while downloading. Please try again."));
            Log(L.T("Загружено ", "Downloaded ") + (data.Length / 1024) + L.T(" КБ, контрольная сумма совпала.", " KB, checksum matches."));
            packages[id] = data;
            return data;
        }

        async Task Install()
        {
            if (manifest == null || !versionOk || busy) return;
            if (Util.Running(game.Game.Process)) { Box(L.T("Закройте игру перед установкой.", "Close the game before installing."), MessageBoxImage.Warning); return; }
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
                Status(L.T("Устанавливаю…", "Installing…"), "busy");
                await Task.Run(() => job.Run(data));
                SetBusy(false);
                CheckGameDir();
                Status(L.T("Готово: включено плагинов — ", "Done: plugins enabled — ") + job.Selected.Count + L.T(". Запустите игру, потом можно нажать «Проверить работу».", ". Start the game, then you can press “Check”."), "ok");
            }
            catch (Exception ex)
            {
                SetBusy(false);
                Status(L.T("Установка не удалась: ", "Installation failed: ") + ex.Message, "error");
                ToggleLog(true);
            }
            finally { SetProgress(-1); }
        }

        // ------------------------------------------------------------ uninstall
        void Uninstall()
        {
            if (manifest == null || game.Dir == null || busy) return;
            if (Util.Running(game.Game.Process)) { Box(L.T("Закройте игру.", "Close the game."), MessageBoxImage.Warning); return; }
            if (!Ask(L.T("Удалить все плагины этого установщика из папки ", "Remove all plugins of this installer from the folder of ") + game.Name + L.T("?\nСохранения и сама игра не затрагиваются.", "?\nSaves and the game itself are not touched."))) return;
            if (!EnsureWritable()) return;
            try
            {
                int removed = new InstallJob { Dir = game.Dir, M = manifest, Log = Log }.Uninstall();
                CheckGameDir();
                Status(L.T("Удалено файлов: ", "Files removed: ") + removed + L.T(". Игра снова обычная.", ". The game is back to normal."), "ok");
            }
            catch (Exception ex) { Status(L.T("Ошибка удаления: ", "Removal failed: ") + ex.Message, "error"); ToggleLog(true); }
        }

        void Play()
        {
            try
            {
                bool steam = build == "Steam" ||
                    (build == null && game.Dir.IndexOf(@"\steamapps\", StringComparison.OrdinalIgnoreCase) >= 0);
                if (steam) { Process.Start("steam://rungameid/" + game.Game.AppId); Status(L.T("Запускаю ", "Starting ") + game.Name + L.T(" через Steam…", " through Steam…"), "info"); }
                else
                {
                    var exe = Util.Local(game.Dir, game.Game.Exe);
                    Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true });
                    Status(L.T("Запускаю ", "Starting ") + game.Name + "…", "info");
                }
            }
            catch (Exception ex) { Status(L.T("Не удалось запустить: ", "Could not start: ") + ex.Message, "error"); }
        }

        // ------------------------------------------------------------ status check
        void CheckStatus(bool showLog)
        {
            if (manifest == null || game.Dir == null) return;
            Log(L.T("Проверка по журналам последнего запуска игры:", "Check from the logs of the last game start:"));
            int ok = 0, total = 0, notRun = 0, bad = 0;
            foreach (var c in manifest.Components)
            {
                var dll = c.Files.FirstOrDefault(f => f.StartsWith("plugins/") && f.EndsWith(".dll"));
                if (dll == null || !File.Exists(Util.Local(game.Dir, dll))) continue;
                total++;
                var verdict = InstallJob.Verdict(Util.Local(game.Dir, dll.Substring(0, dll.Length - 4) + ".log"));
                if (verdict == "OK") ok++;
                else if (verdict.StartsWith(L.T("не запускался", "not run"))) notRun++;
                else if (verdict.StartsWith(L.T("ОТКАЗ", "REFUSED")) || verdict.StartsWith(L.T("OK, частично", "OK, partly"))) bad++;
                Log("  " + c.Title + ": " + verdict);
            }
            if (notRun == total && total > 0) Status(L.T("Игра ещё не запускалась с плагинами. Запустите её и проверьте снова.", "The game has not run with the plugins yet. Start it and check again."), "info");
            else if (ok == total) Status(L.T("Работают все плагины: ", "All plugins work: ") + ok + L.T(" из ", " of ") + total + ".", "ok");
            else Status(L.T("Работают ", "Working: ") + ok + L.T(" из ", " of ") + total + L.T(" плагинов — подробности в журнале.", " plugins — details in the log."), bad > 0 ? "warn" : "info");
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
            catch (Exception ex) { Log(L.T("ОШИБКА: ", "ERROR: ") + ex); }
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
                            if (entry == null) throw new Exception(L.T("в архиве нет файла ", "the archive has no file ") + rel);
                            byte[] bytes;
                            using (var s = entry.Open())
                            using (var ms = new MemoryStream()) { s.CopyTo(ms); bytes = ms.ToArray(); }
                            string expected;
                            if (M.FileSha256.TryGetValue(rel, out expected) && !Util.Sha256(bytes).Equals(expected, StringComparison.OrdinalIgnoreCase))
                                throw new Exception(L.T("контрольная сумма файла ", "checksum of the file ") + rel + L.T(" не совпала", " does not match"));
                            BackupForeign(rel, path, bytes);
                            Directory.CreateDirectory(Path.GetDirectoryName(path));
                            File.WriteAllBytes(path, bytes);
                            written.Add(rel);
                        }
                        else if (File.Exists(path) && IsOurs(rel, path))
                        {
                            File.Delete(path);
                            Log(L.T("Удалён: ", "Removed: ") + rel);
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
                    if (on) Log(L.T("Установлен: ", "Installed: ") + c.Title);
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
            Log(L.T("Чужой ", "Another ") + rel + L.T(" сохранён в ", " saved to ") + Config.BackupDir + L.T(" (вернётся при удалении).", " (it comes back on removal)."));
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
                    Log(L.T("Возвращён ваш прежний ", "Restored your previous ") + name + ".");
                }
            }
        }

        void SetBorderless()
        {
            try
            {
                var b = M.Borderless;
                var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), b.File.Replace('/', '\\'));
                if (!File.Exists(file)) { Log(L.T("Файл настроек игры не найден — режим экрана поменяйте в настройках игры.", "The game's settings file was not found — change the screen mode in the game's settings.")); return; }
                var text = File.ReadAllText(file);
                var updated = Regex.Replace(text, b.Pattern, b.Replace);
                if (updated == text) { Log(L.T("Режим экрана уже «без рамки».", "The screen mode is already borderless.")); return; }
                File.Copy(file, file + ".bak_launcher", true);
                File.WriteAllText(file, updated);
                Log(L.T("Режим экрана переключён на «без рамки» (копия настроек: ", "Screen mode switched to borderless (settings backup: ") + Path.GetFileName(file) + ".bak_launcher).");
            }
            catch (Exception ex) { Log(L.T("Не удалось поменять режим экрана: ", "Could not change the screen mode: ") + ex.Message); }
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
            Log(L.T("Удалено файлов: ", "Files removed: ") + removed + ".");
            return removed;
        }

        public static string Verdict(string logPath)
        {
            if (!File.Exists(logPath)) return L.T("не запускался (игру ещё не запускали после установки)", "not run (the game has not been started since installing)");
            string[] lines;
            try
            {
                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    lines = sr.ReadToEnd().Split('\n');
            }
            catch (Exception ex) { return L.T("не удалось прочитать журнал: ", "could not read the log: ") + ex.Message; }
            int init = -1;
            for (int i = lines.Length - 1; i >= 0; i--)
                if (lines[i].Contains("initialized") || lines[i].Contains(" installed") || lines[i].Contains("disabled by configuration")) { init = i; break; }
            if (init < 0) return lines.Any(l => l.Contains("REFUSED")) ? L.T("ОТКАЗ (не та версия игры?)", "REFUSED (wrong game version?)") : L.T("нет данных", "no data");
            var pid = Regex.Match(lines[init], @"^\[(\d+):").Groups[1].Value;
            Func<string, bool> same = l => pid != "" && l.StartsWith("[" + pid + ":");
            bool active = lines.Any(l => same(l) && (l.Contains("initialized") || l.Contains(" installed")));
            if (!active) return L.T("выключен в настройках", "turned off in the settings");
            if (lines.Any(l => same(l) && l.Contains("REFUSED"))) return L.T("OK, частично (часть функций отключилась, см. ", "OK, partly (some features turned off, see ") + Path.GetFileName(logPath) + ")";
            return "OK";
        }
    }

    static class Program
    {
        public static string Auto;

        // The window's texts in English (MainWindow.xaml is written in Russian).
        static readonly Dictionary<string, string> XamlEnglish = new Dictionary<string, string>
        {
            { "Ускорение игр Paradox", "Paradox games speed-up" },
            { "Ускорение", "Speed-up" },
            { "плагины для игр Paradox", "plugins for Paradox games" },
            { "ИГРЫ", "GAMES" },
            { "Изменить…", "Change…" },
            { "Открыть папку", "Open folder" },
            { "Все плагины", "All plugins" },
            { "Максимальная скорость. Партия может идти немного иначе, чем в обычной игре.", "Maximum speed. A campaign may go slightly differently than in the normal game." },
            { "Только точные", "Exact only" },
            { "Расчёты игры такие же, как без плагинов. Ускорение чуть меньше.", "The game calculates exactly as without plugins. A bit less speed-up." },
            { "Выборочно", "Custom" },
            { "Включить нужные плагины и поменять их настройки самому.", "Choose the plugins and change their settings yourself." },
            { "Плагины", "Plugins" },
            { "НАСТРОЙКИ", "SETTINGS" },
            { "У этого плагина нет настроек.", "This plugin has no settings." },
            { "Показать журнал", "Show log" },
            { "Удалить", "Remove" },
            { "Проверить работу", "Check" },
            { "Играть", "Play" },
            { "Установить", "Install" },
        };

        [STAThread]
        static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // TLS 1.2 + 1.3
            string source = Config.DefaultSource, dir = null, gameId = null, lang = null;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--source") source = args[++i];
                else if (args[i] == "--dir") dir = args[++i];
                else if (args[i] == "--game") gameId = args[++i];
                else if (args[i] == "--auto") Auto = args[++i];
                else if (args[i] == "--lang") lang = args[++i];
            }
            L.Choose(lang);
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            Window window;
            string xaml;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
            using (var r = new StreamReader(s, Encoding.UTF8))
                xaml = r.ReadToEnd();
            if (L.En)
                foreach (var kv in XamlEnglish)
                {
                    var from = "\"" + kv.Key + "\"";
                    if (!xaml.Contains(from)) throw new Exception("MainWindow.xaml has no " + from);
                    xaml = xaml.Replace(from, "\"" + kv.Value + "\"");
                }
            window = (Window)XamlReader.Parse(xaml);
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                if (s != null) window.Icon = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            new Launcher(window, source, gameId, dir);
            app.Run(window);
        }
    }
}
