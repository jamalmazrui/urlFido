// urlFido.cs — Download files from web pages by extension, driving real Microsoft Edge
// Copyright (c) 2026 Jamal Mazrui — MIT License — https://github.com/JamalMazrui/urlFido
// Compile: buildUrlFido.cmd (Roslyn csc, /platform:x64, .NET Framework 4.8)
// Usage:   urlFido [options] <url, local html file, or url-list text file> [...]
//          urlFido -g          (GUI parameter dialog)
//
// urlFido fetches every file matching the requested patterns (default:
// docx pdf zip) that is
// linked from each source url, saving the files directly into the output
// directory. It drives the system-installed Microsoft Edge over the Chrome
// DevTools Protocol (CDP) — the same wire protocol Playwright uses — so pages
// behave as they do for a signed-in human: real browser, real cookies, real
// JavaScript execution. No Playwright driver, no Node.js, no external DLLs;
// the only references are .NET Framework 4.8 assemblies, so urlFido.exe is a
// true single-file executable like 2htm.exe and extCheck.exe.
//
// Browser-driving mechanisms (ported from urlCheck):
//   - System Edge only (never a bundled Chromium), located at the standard
//     install paths.
//   - Launch flags: --mute-audio --no-default-browser-check --no-first-run
//     --window-size, plus --remote-debugging-port=0 and a temp
//     --user-data-dir (Edge/Chrome 136+ refuse remote debugging against the
//     default profile directory). The chosen port is read from the
//     DevToolsActivePort file.
//   - -i / --invisible maps to headless; overridden to visible (and logged)
//     when -a is set, because the user must be able to interact with the
//     window to authenticate.
//   - -a / --authenticate pauses once per registrable domain so the user can
//     sign in, accept cookies, complete 2FA, etc. Without -m, urlFido severs
//     its CDP connections during the pause and reconnects after — the
//     disconnect pattern that improves success against sites that block
//     automated browsers. (We own the Edge process, so it survives the
//     disconnect.)
//   - -m / --main-profile launches Edge against the user's real profile so
//     saved logins and cookies apply. Requires that no Edge process is
//     already running (checked up front with a friendly message). NOTE:
//     recent Edge versions may refuse remote debugging against the default
//     profile directory even when it is named explicitly; urlFido detects
//     that refusal (DevToolsActivePort never appears) and reports it with a
//     suggestion to use -a instead.
//   - navigator.webdriver override injected on every new document.
//   - Wait strategy: load event, then a network-quiet window (no requests in
//     flight for a quiet interval, capped by a timeout), then a settle delay.
//
// Download strategy (in order):
//   1. Browser-managed download: Browser.setDownloadBehavior allowAndName
//      into the output directory, then open the file url in a throwaway tab.
//      Completion is tracked via Browser.downloadProgress events; the
//      guid-named file is renamed to the final name. The temp profile is
//      seeded with plugins.always_open_pdf_externally so Edge downloads PDFs
//      instead of rendering them in the viewer.
//   2. HTTP fallback: a direct HttpWebRequest that replays the browser's
//      cookies (Network.getCookies) and user-agent, streaming to disk. Still
//      benefits from any authenticated session established in the browser.
//
// File naming (behavior documented in the FileDir user guide, Web Download
// command): the name comes from the url's final path segment when that is a
// valid file name; otherwise a name is synthesized from other characters of
// the url. Name collisions get a numeric suffix (name_001.pdf) unless
// -f / --force overwrites.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Homer;

// ===========================================================================
// program — constants, run-state, dispatcher
// ===========================================================================
static class program {
    // Constants (Camel Type: Hungarian prefix, grouped by type)
    public const bool bDefaultForce = false;
    public const bool bDefaultInvisible = false;

    public const int iCdpResponseTimeoutMs = 30000;
    public const int iDefaultNavTimeoutMs = 60000;
    public const int iDefaultPostLoadDelayMs = 1500;
    public const int iDefaultViewportHeight = 1440;
    public const int iDefaultViewportWidth = 1600;
    public const int iDevToolsPortTimeoutSec = 30;
    public const int iDownloadTimeoutMs = 300000;
    public const int iNetworkIdleTimeoutMs = 8000;
    public const int iNetworkQuietWindowMs = 500;
    public const int iAuthPostConfirmSettleDelayMs = 4000;

    public const string sConfigDirName = "urlFido";
    public const string sConfigFileName = "urlFido.inix";
    public const string sDefaultExtensions = "docx pdf zip";
    public const string sLogFileName = "urlFido.log";
    public const string sProgramName = "urlFido";
    // Keep in step with sAppVersion in urlFido_setup.iss. tagRelease tags
    // the version stamped into urlFido_setup.exe, so a mismatch here would
    // make -v report something the release does not.
    public const string sProgramVersion = "1.0.0";
    public const string sReadmeUrl = "https://github.com/JamalMazrui/urlFido#readme";
    public const string sUsage =
        "Usage: urlFido [options] <url, local html file, or url-list text file> [...]";
    public const string sUserAgentSuffix = " urlFido/1.0.0";
    public const string sWebdriverOverrideScript =
        "Object.defineProperty(navigator, 'webdriver', { get: () => undefined });";

    // Run state (resolved from CLI, config, and/or GUI dialog)
    public static bool bAuthenticate = false;
    public static bool bAuthenticateFromCli = false;
    public static bool bForce = bDefaultForce;
    public static bool bForceFromCli = false;
    public static bool bGuiMode = false;
    public static bool bInvisible = bDefaultInvisible;
    public static bool bInvisibleFromCli = false;
    public static bool bLog = false;
    public static bool bLogFromCli = false;
    public static bool bMainProfile = false;
    public static bool bMainProfileFromCli = false;
    public static bool bOutputDirFromCli = false;
    public static bool bSourceFromCli = false;
    public static bool bExtensionsFromCli = false;
    public static bool bUseConfig = false;
    public static bool bViewOutput = false;
    public static bool bViewOutputFromCli = false;

    public static string sExtensions = sDefaultExtensions;
    public static string sOutputDir = "";

    public static List<string> lSources = new List<string>();

    public static int run(string[] aArgs) {
        var lFileArgs = new List<string>();
        int iParse = parseArgs(aArgs, lFileArgs);
        if (iParse >= 0) return iParse;  // -h or -v handled, or a parse error

        if (bUseConfig) configManager.loadInto(lFileArgs);

        if (bGuiMode) {
            string sSource = string.Join(" ", lFileArgs.Select(quoteIfNeeded));
            string sExt = sExtensions;
            string sOut = sOutputDir;
            bool bF = bForce, bV = bViewOutput, bL = bLog;
            bool bU = bUseConfig, bI = bInvisible, bA = bAuthenticate, bM = bMainProfile;
            if (!guiDialog.show(ref sSource, ref sExt, ref sOut,
                    ref bA, ref bM, ref bI, ref bF, ref bV, ref bL, ref bU)) {
                return 0;  // Cancel
            }
            lFileArgs = splitSourceField(sSource).ToList();
            sExtensions = sExt;
            sOutputDir = sOut;
            bForce = bF; bViewOutput = bV; bLog = bL; bUseConfig = bU;
            bInvisible = bI; bAuthenticate = bA; bMainProfile = bM;
            if (bUseConfig) {
                configManager.save(sSource, sExtensions, sOutputDir,
                    bAuthenticate, bMainProfile, bInvisible, bForce, bViewOutput, bLog);
            }
        }

        if (lFileArgs.Count == 0) {
            notify(sUsage);
            notify("Run urlFido -h for the full option list, or urlFido -g for the dialog.");
            return 2;
        }

        // -a needs a visible window; override -i with a note (urlCheck behavior).
        if (bAuthenticate && bInvisible) {
            notify("--authenticate overrides --invisible; Edge will be visible.");
            bInvisible = false;
        }

        // Resolve output directory.
        if (string.IsNullOrWhiteSpace(sOutputDir)) sOutputDir = Directory.GetCurrentDirectory();
        try {
            sOutputDir = Path.GetFullPath(sOutputDir);
            if (!Directory.Exists(sOutputDir)) Directory.CreateDirectory(sOutputDir);
        } catch (Exception ex) {
            notify("Could not create output directory '" + sOutputDir + "': " + ex.Message);
            return finishGui(2, sProgramName + " — Output directory error");
        }

        if (bLog) {
            logger.open(sOutputDir);
            logger.header(sProgramName, sProgramVersion, buildParamList(lFileArgs));
        }

        // Resolve the extension field into wildcard patterns.
        List<string> lPatterns = patternParser.parse(sExtensions);
        if (lPatterns.Count == 0) {
            notify("No valid file extensions were specified. Examples: -e \"pdf, docx\" " +
                "or -e \"*newsletter*.pdf\"");
            logger.close();
            return finishGui(2, sProgramName + " — No extensions");
        }
        notify("Matching: " + string.Join(" ", lPatterns));

        // Expand url-list files into individual sources; normalize.
        lSources = urlHelper.expandSources(lFileArgs);
        if (lSources.Count == 0) {
            notify("No usable source urls were found.");
            logger.close();
            return finishGui(2, sProgramName + " — No sources");
        }

        // Main-profile pre-flight: Edge cannot share its real profile
        // across two processes, so it must not already be running.
        if (bMainProfile && edgeLauncher.isEdgeRunning()) {
            notify("Microsoft Edge is currently running. The --main-profile option " +
                "requires that Edge be fully closed first, because Edge cannot share " +
                "your real profile across two processes. Close all Edge windows " +
                "(check the system tray too) and run urlFido again.");
            logger.close();
            return finishGui(2, sProgramName + " — Edge is running");
        }

        int iExit = downloadEngine.runAll(lSources, lPatterns);

        if (bViewOutput) {
            try { Process.Start("explorer.exe", "\"" + sOutputDir + "\""); }
            catch (Exception ex) { logger.warn("Could not open output directory: " + ex.Message); }
        }
        logger.close();
        return finishGui(iExit, sProgramName + " — Results");
    }

    // In GUI mode, show the captured console text in a final message box so
    // screen reader users get the whole session summary in one reviewable
    // dialog (urlCheck behavior).
    static int finishGui(int iExit, string sTitle) {
        if (!bGuiMode) return iExit;
        try {
            MessageBox.Show(results.capturedText(), sTitle,
                MessageBoxButtons.OK,
                iExit == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        } catch { }
        return iExit;
    }

    // Write a line to the console and to the GUI capture buffer and log.
    public static void notify(string sMsg) {
        Console.WriteLine(sMsg);
        results.capture(sMsg);
        logger.info(sMsg);
    }

    public static string quoteIfNeeded(string s) {
        return s.Contains(" ") ? "\"" + s + "\"" : s;
    }

    // Split a source field the way extCheck does: whitespace-separated,
    // honoring double quotes around items containing spaces.
    public static IEnumerable<string> splitSourceField(string sField) {
        var l = new List<string>();
        if (string.IsNullOrWhiteSpace(sField)) return l;
        var m = Regex.Matches(sField, "\"([^\"]*)\"|(\\S+)");
        foreach (Match o in m) {
            string s = o.Groups[1].Success ? o.Groups[1].Value : o.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(s)) l.Add(s);
        }
        return l;
    }

    static List<KeyValuePair<string, string>> buildParamList(List<string> lFileArgs) {
        var l = new List<KeyValuePair<string, string>>();
        l.Add(new KeyValuePair<string, string>("Sources", string.Join(" ", lFileArgs)));
        l.Add(new KeyValuePair<string, string>("Extensions", sExtensions));
        l.Add(new KeyValuePair<string, string>("Output directory", sOutputDir));
        l.Add(new KeyValuePair<string, string>("Authenticate", bAuthenticate ? "yes" : "no"));
        l.Add(new KeyValuePair<string, string>("Main profile", bMainProfile ? "yes" : "no"));
        l.Add(new KeyValuePair<string, string>("Invisible", bInvisible ? "yes" : "no"));
        l.Add(new KeyValuePair<string, string>("Force", bForce ? "yes" : "no"));
        l.Add(new KeyValuePair<string, string>("View output", bViewOutput ? "yes" : "no"));
        return l;
    }

    // Returns -1 to continue the run, or an exit code when the program
    // should stop (help/version shown, or a bad option).
    static int parseArgs(string[] aArgs, List<string> lFileArgs) {
        for (int i = 0; i < aArgs.Length; i++) {
            string sArg = aArgs[i];
            switch (sArg) {
                case "-h": case "--help":
                    printHelp();
                    return 0;
                case "-v": case "--version":
                    Console.WriteLine(sProgramName + " " + sProgramVersion);
                    return 0;
                case "-g": case "--gui-mode":
                    bGuiMode = true; break;
                case "-e": case "--extensions":
                    if (i + 1 >= aArgs.Length) { Console.Error.WriteLine("Missing value after " + sArg); return 2; }
                    sExtensions = aArgs[++i]; bExtensionsFromCli = true; break;
                case "-o": case "--output-folder":
                    if (i + 1 >= aArgs.Length) { Console.Error.WriteLine("Missing value after " + sArg); return 2; }
                    sOutputDir = aArgs[++i]; bOutputDirFromCli = true; break;
                case "-f": case "--force":
                    bForce = true; bForceFromCli = true; break;
                case "-i": case "--invisible":
                    bInvisible = true; bInvisibleFromCli = true; break;
                case "-a": case "--authenticate":
                    bAuthenticate = true; bAuthenticateFromCli = true; break;
                case "-m": case "--main-profile":
                    bMainProfile = true; bMainProfileFromCli = true; break;
                case "-u": case "--use-configuration":
                    bUseConfig = true; break;
                case "-l": case "--log":
                    bLog = true; bLogFromCli = true; break;
                case "--view-output":
                    bViewOutput = true; bViewOutputFromCli = true; break;
                default:
                    if (sArg.StartsWith("-")) {
                        Console.Error.WriteLine("Unknown option: " + sArg);
                        Console.Error.WriteLine(sUsage);
                        return 2;
                    }
                    lFileArgs.Add(sArg);
                    bSourceFromCli = true;
                    break;
            }
        }
        return -1;
    }

    static void printHelp() {
        Console.WriteLine(sProgramName + " " + sProgramVersion +
            " — download files from web pages by extension, using real Microsoft Edge.");
        Console.WriteLine(sUsage);
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -e, --extensions <list>    File extensions to download, separated by");
        Console.WriteLine("                             commas or spaces. A leading dot is optional:");
        Console.WriteLine("                             \"pdf\", \".pdf\", and \"*.pdf\" all work.");
        Console.WriteLine("                             Default: " + sDefaultExtensions);
        Console.WriteLine("  -o, --output-folder <dir>  Directory that receives the downloaded files.");
        Console.WriteLine("                             Default: the current directory.");
        Console.WriteLine("  -g, --gui-mode             Show the parameter dialog instead of using");
        Console.WriteLine("                             command-line values alone.");
        Console.WriteLine("  -f, --force                Overwrite existing files instead of creating");
        Console.WriteLine("                             uniquely numbered names.");
        Console.WriteLine("  -i, --invisible            Run Edge with no visible window (headless).");
        Console.WriteLine("  -a, --authenticate         Pause at the first url of each domain so you");
        Console.WriteLine("                             can sign in, accept cookies, or complete 2FA");
        Console.WriteLine("                             in the visible Edge window, then continue.");
        Console.WriteLine("                             Overrides --invisible.");
        Console.WriteLine("  -m, --main-profile         Use your real Edge profile so saved logins and");
        Console.WriteLine("                             cookies apply. Requires Edge to be fully closed.");
        Console.WriteLine("  -u, --use-configuration    Load saved settings from " + sConfigFileName + ".");
        Console.WriteLine("  -l, --log                  Write " + sLogFileName + " to the output directory.");
        Console.WriteLine("  --view-output              Open the output directory when done.");
        Console.WriteLine("  -h, --help                 Show this help. -v, --version shows the version.");
        Console.WriteLine();
        Console.WriteLine("Each source may be a url, a local .htm/.html file, or a text file listing");
        Console.WriteLine("one url per line. For each source, urlFido downloads every linked file");
        Console.WriteLine("whose extension matches the -e list, directly into the output directory.");
    }
}

// ===========================================================================
// patternParser — turn the File Extensions field into a list of cmd.exe-style
// wildcard patterns, matched case-insensitively against the file name a url
// would be saved as.
//
// Escalation rule (each form is shorthand for the next):
//   pdf              ->  .pdf   ->  *.pdf
//   *newsletter*.pdf ->  used as written
// Only the two cmd.exe wildcards are supported -- '*' for any run of
// characters and '?' for exactly one. Unix glob syntax (character classes,
// brace expansion) is deliberately NOT supported.
// ===========================================================================
static class patternParser {
    // Split the field on commas, semicolons, and whitespace, then normalize
    // each token to a full wildcard pattern.
    public static List<string> parse(string sInput) {
        var l = new List<string>();
        if (string.IsNullOrWhiteSpace(sInput)) return l;
        foreach (string sRaw in sInput.Split(new[] { ',', ';', ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries)) {
            string s = normalize(sRaw);
            if (s == "") continue;
            if (!l.Contains(s, StringComparer.OrdinalIgnoreCase)) l.Add(s);
        }
        return l;
    }

    // Apply the escalation rule. A bare extension becomes "*.ext"; a
    // dot-prefixed extension becomes "*.ext"; anything already containing a
    // wildcard is taken as written.
    public static string normalize(string sRaw) {
        string s = (sRaw ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return "";
        if (s.IndexOf('*') < 0 && s.IndexOf('?') < 0) {
            // No wildcard: it is an extension, with or without the dot.
            if (s.StartsWith(".")) s = s.Substring(1);
            if (s.Length == 0) return "";
            return "*." + s;
        }
        if (s.StartsWith(".")) return "*" + s;   // ".pdf" with a wildcard elsewhere
        return s;
    }

    // Translate a cmd.exe wildcard pattern into an anchored regex. Every
    // character is escaped except '*' and '?', which become ".*" and ".".
    public static Regex toRegex(string sPattern) {
        var sb = new StringBuilder("^");
        foreach (char c in sPattern) {
            if (c == '*') sb.Append(".*");
            else if (c == '?') sb.Append('.');
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append("$");
        return new Regex(sb.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // Compile a pattern list once, for reuse across every link on every page.
    public static List<Regex> compile(List<string> lPatterns) {
        var l = new List<Regex>();
        foreach (string s in lPatterns) {
            try { l.Add(toRegex(s)); }
            catch (Exception ex) { program.notify("Ignoring bad pattern '" + s + "': " + ex.Message); }
        }
        return l;
    }

    public static bool isMatch(List<Regex> lRegexes, string sFileName) {
        if (string.IsNullOrEmpty(sFileName)) return false;
        foreach (Regex o in lRegexes) if (o.IsMatch(sFileName)) return true;
        return false;
    }
}

// ===========================================================================
// urlHelper — source expansion, url normalization, registrable domains,
// and file-name derivation (the FileDir Web Download behavior).
// ===========================================================================
static class urlHelper {
    // Domains whose second level is generic (co.uk pattern), so the
    // registrable domain takes three labels rather than two.
    static readonly string[] aGenericSecondLevels = {
        "ac", "co", "com", "edu", "gov", "net", "org"
    };

    public static List<string> expandSources(List<string> lArgs) {
        var l = new List<string>();
        foreach (string sArg in lArgs) {
            if (File.Exists(sArg)) {
                string sExt = Path.GetExtension(sArg).ToLowerInvariant();
                if (sExt == ".htm" || sExt == ".html" || sExt == ".xhtml") {
                    l.Add(new Uri(Path.GetFullPath(sArg)).AbsoluteUri);
                    continue;
                }
                // Treat any other existing file as a url-list text file.
                try {
                    foreach (string sLineRaw in File.ReadAllLines(sArg)) {
                        string sLine = sLineRaw.Trim();
                        if (sLine.Length == 0 || sLine.StartsWith("#") || sLine.StartsWith(";"))
                            continue;
                        string sNorm = normalize(sLine);
                        if (sNorm != "") l.Add(sNorm);
                    }
                } catch (Exception ex) {
                    program.notify("Could not read url list '" + sArg + "': " + ex.Message);
                }
                continue;
            }
            string sUrl = normalize(sArg);
            if (sUrl != "") l.Add(sUrl);
            else program.notify("Skipping unrecognized source: " + sArg);
        }
        return l.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Accept bare domains ("example.com") by assuming https.
    public static string normalize(string sInput) {
        string s = (sInput ?? "").Trim();
        if (s.Length == 0) return "";
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) {
            return s;
        }
        if (Regex.IsMatch(s, @"^([a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,}(:[0-9]+)?(/\S*)?$") ||
            Regex.IsMatch(s, @"^(\d{1,3}\.){3}\d{1,3}(:\d+)?(/\S*)?$")) {
            return "https://" + s;
        }
        return "";
    }

    // Approximate registrable domain: last two labels, or three when the
    // second level looks generic before a two-letter country code.
    public static string registrableDomain(string sUrl) {
        try {
            string sHost = new Uri(sUrl).Host.ToLowerInvariant();
            string[] a = sHost.Split('.');
            if (a.Length <= 2) return sHost;
            string sTld = a[a.Length - 1];
            string sSecond = a[a.Length - 2];
            if (sTld.Length == 2 && aGenericSecondLevels.Contains(sSecond) && a.Length >= 3) {
                return a[a.Length - 3] + "." + sSecond + "." + sTld;
            }
            return sSecond + "." + sTld;
        } catch {
            return sUrl;
        }
    }

    // The name this url would be saved as on disk, and its extension.
    // Both delegate to Homer.Web, which is the same logic FileDir's Web
    // Download command uses: the last path segment when it carries a real
    // file name, otherwise a HEAD request whose Content-Disposition or
    // MIME type supplies the name and extension, otherwise a sanitized
    // fallback. Ordinary links that already end in a file name cost no
    // network call.
    public static string fileNameForUrl(string sUrl) {
        try { return Web.suggestedName(sUrl); }
        catch { return "download"; }
    }

    public static string extensionOfUrl(string sUrl) {
        try { return Web.extensionOf(sUrl); }
        catch { return ""; }
    }

    // Collision-free path in the output directory. With bForce the plain
    // path is returned (overwrite); otherwise Homer.Web.uniquePath applies
    // the numeric-suffix rule shared with FileDir.
    public static string uniquePath(string sDir, string sName, bool bForce) {
        string sPath = Path.Combine(sDir, sName);
        if (bForce) return sPath;
        try { return Web.uniquePath(sPath); }
        catch { return sPath; }
    }
}

// ===========================================================================
// jsonHelper — thin, tolerant wrapper over JavaScriptSerializer
// (System.Web.Extensions, part of .NET Framework 4.8 — no NuGet needed).
// ===========================================================================
static class jsonHelper {
    static readonly JavaScriptSerializer oSerializer =
        new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    public static string write(object v) { return oSerializer.Serialize(v); }

    public static Dictionary<string, object> parse(string sJson) {
        return oSerializer.DeserializeObject(sJson) as Dictionary<string, object>
            ?? new Dictionary<string, object>();
    }

    public static Dictionary<string, object> getDict(object v) {
        return v as Dictionary<string, object> ?? new Dictionary<string, object>();
    }

    public static List<object> getList(object v) {
        var l = new List<object>();
        var e = v as IEnumerable;
        if (e != null && !(v is string)) foreach (object o in e) l.Add(o);
        return l;
    }

    public static string getString(Dictionary<string, object> d, string sKey) {
        object v;
        return d.TryGetValue(sKey, out v) && v != null ? v.ToString() : "";
    }

    public static long getLong(Dictionary<string, object> d, string sKey) {
        object v;
        if (!d.TryGetValue(sKey, out v) || v == null) return 0;
        try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    public static bool getBool(Dictionary<string, object> d, string sKey) {
        object v;
        if (!d.TryGetValue(sKey, out v) || v == null) return false;
        return v is bool && (bool)v;
    }

    public static Dictionary<string, object> getDictAt(
            Dictionary<string, object> d, string sKey) {
        object v;
        return d.TryGetValue(sKey, out v)
            ? getDict(v) : new Dictionary<string, object>();
    }
}

// ===========================================================================
// edgeLauncher — locate msedge.exe, prepare the profile, launch with remote
// debugging, and read the DevToolsActivePort handshake (from urlCheck).
// ===========================================================================
static class edgeLauncher {
    public static Process oEdgeProcess = null;
    public static string sUserDataDir = "";
    public static string sWsBrowserUrl = "";
    public static int iDebugPort = 0;

    public static string getEdgeExecutablePath() {
        var lCandidates = new List<string>();
        string sProgFiles86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        string sProgFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        string sLocalAppData = Environment.GetEnvironmentVariable("LocalAppData");
        if (!string.IsNullOrEmpty(sProgFiles86))
            lCandidates.Add(Path.Combine(sProgFiles86, "Microsoft", "Edge", "Application", "msedge.exe"));
        if (!string.IsNullOrEmpty(sProgFiles))
            lCandidates.Add(Path.Combine(sProgFiles, "Microsoft", "Edge", "Application", "msedge.exe"));
        if (!string.IsNullOrEmpty(sLocalAppData))
            lCandidates.Add(Path.Combine(sLocalAppData, "Microsoft", "Edge", "Application", "msedge.exe"));
        foreach (string s in lCandidates) if (File.Exists(s)) return s;
        return null;
    }

    public static bool isEdgeRunning() {
        try { return Process.GetProcessesByName("msedge").Length > 0; }
        catch { return false; }
    }

    public static string getMainProfileUserDataDir() {
        string sLocalAppData = Environment.GetEnvironmentVariable("LocalAppData") ?? "";
        return Path.Combine(sLocalAppData, "Microsoft", "Edge", "User Data");
    }

    // Launch Edge with remote debugging. For the ephemeral (non -m) mode, a
    // temp profile directory is created and seeded so PDFs download instead
    // of opening in the viewer. Returns true on success; on failure a
    // friendly message has already been notified.
    public static bool launch(bool bMainProfile, bool bHeadless, string sDownloadDir) {
        string sEdgeExe = getEdgeExecutablePath();
        if (sEdgeExe == null) {
            program.notify("Could not find msedge.exe at the standard Microsoft Edge " +
                "install locations. urlFido requires Edge to be installed.");
            return false;
        }

        if (bMainProfile) {
            sUserDataDir = getMainProfileUserDataDir();
            if (!Directory.Exists(sUserDataDir)) {
                program.notify("Could not find your Edge profile directory at " +
                    sUserDataDir + ". Run Edge once normally, then try again.");
                return false;
            }
        } else {
            try {
                sUserDataDir = Path.Combine(Path.GetTempPath(),
                    "urlFido-tmp-profile-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(sUserDataDir);
                seedPreferences(sUserDataDir, sDownloadDir);
            } catch (Exception ex) {
                program.notify("Could not create a temporary profile folder: " + ex.Message);
                return false;
            }
        }

        var lArgs = new List<string> {
            "--mute-audio",
            "--no-default-browser-check",
            "--no-first-run",
            "--window-size=" + program.iDefaultViewportWidth + "," + program.iDefaultViewportHeight,
            "--remote-debugging-port=0",
            "--user-data-dir=" + quote(sUserDataDir),
            "about:blank"
        };
        if (bHeadless) lArgs.Insert(0, "--headless=new");

        logger.info("Launching msedge.exe with --remote-debugging-port=0 and --user-data-dir=" + sUserDataDir);
        try {
            var oInfo = new ProcessStartInfo {
                FileName = sEdgeExe,
                Arguments = string.Join(" ", lArgs),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            oEdgeProcess = Process.Start(oInfo);
        } catch (Exception ex) {
            program.notify("Could not launch Microsoft Edge: " + ex.Message);
            return false;
        }

        // Wait for DevToolsActivePort: line 1 is the port, line 2 is the
        // browser websocket path (/devtools/browser/<guid>).
        string sPortFile = Path.Combine(sUserDataDir, "DevToolsActivePort");
        var oWatch = Stopwatch.StartNew();
        while (oWatch.Elapsed.TotalSeconds < program.iDevToolsPortTimeoutSec) {
            try {
                if (File.Exists(sPortFile)) {
                    string[] aLines = File.ReadAllLines(sPortFile);
                    if (aLines.Length >= 2 && int.TryParse(aLines[0].Trim(), out iDebugPort) && iDebugPort > 0) {
                        sWsBrowserUrl = "ws://127.0.0.1:" + iDebugPort + aLines[1].Trim();
                        logger.info("DevTools port " + iDebugPort + " ready.");
                        return true;
                    }
                }
            } catch { }
            Thread.Sleep(250);
        }

        if (bMainProfile) {
            program.notify("Edge started, but its remote-debugging channel never became " +
                "available against your main profile. Recent Edge versions refuse " +
                "remote debugging on the default profile directory for security. " +
                "Please use -a (authenticate) instead, which signs you in within a " +
                "clean temporary profile.");
        } else {
            program.notify("Edge started, but its remote-debugging channel never became " +
                "available (timed out after " + program.iDevToolsPortTimeoutSec + " seconds).");
        }
        shutdown();
        return false;
    }

    public static void shutdown() {
        try {
            if (oEdgeProcess != null && !oEdgeProcess.HasExited) {
                oEdgeProcess.Kill();
                oEdgeProcess.WaitForExit(5000);
            }
        } catch { }
        oEdgeProcess = null;
        // Best-effort temp profile cleanup (never the main profile).
        try {
            if (!program.bMainProfile && sUserDataDir != "" &&
                    sUserDataDir.Contains("urlFido-tmp-profile-") &&
                    Directory.Exists(sUserDataDir)) {
                Directory.Delete(sUserDataDir, true);
            }
        } catch { }
    }

    // Seed Default\Preferences so Edge downloads PDFs rather than opening
    // its built-in viewer, and points downloads at the output directory.
    static void seedPreferences(string sProfileDir, string sDownloadDir) {
        var d = new Dictionary<string, object> {
            { "plugins", new Dictionary<string, object> {
                { "always_open_pdf_externally", true } } },
            { "download", new Dictionary<string, object> {
                { "prompt_for_download", false },
                { "default_directory", sDownloadDir } } }
        };
        string sDefaultDir = Path.Combine(sProfileDir, "Default");
        Directory.CreateDirectory(sDefaultDir);
        File.WriteAllText(Path.Combine(sDefaultDir, "Preferences"),
            jsonHelper.write(d), new UTF8Encoding(false));
    }

    static string quote(string s) { return s.Contains(" ") ? "\"" + s + "\"" : s; }
}

// ===========================================================================
// cdpClient — one websocket connection speaking the Chrome DevTools
// Protocol: request/response correlation by id, plus an event buffer that
// callers can wait on. Used twice: a browser-level connection (Browser.* and
// Target.* methods, download events) and a page-level connection (Page.*,
// Runtime.*, Network.*).
// ===========================================================================
class cdpClient : IDisposable {
    ClientWebSocket oSocket = null;
    Task oReceiveTask = null;
    CancellationTokenSource oCancel = null;
    int iNextId = 0;
    readonly object oLock = new object();
    readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> dPending =
        new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
    readonly List<Dictionary<string, object>> lEvents = new List<Dictionary<string, object>>();
    public volatile bool bClosed = false;

    public void connect(string sWsUrl) {
        oSocket = new ClientWebSocket();
        oSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        oCancel = new CancellationTokenSource();
        oSocket.ConnectAsync(new Uri(sWsUrl), oCancel.Token).Wait();
        oReceiveTask = Task.Run(() => receiveLoop());
    }

    public Dictionary<string, object> send(string sMethod,
            Dictionary<string, object> dParams = null,
            int iTimeoutMs = program.iCdpResponseTimeoutMs) {
        int iId;
        var oTcs = new TaskCompletionSource<Dictionary<string, object>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (oLock) {
            iId = ++iNextId;
            dPending[iId] = oTcs;
        }
        var d = new Dictionary<string, object> { { "id", iId }, { "method", sMethod } };
        if (dParams != null) d["params"] = dParams;
        byte[] aBytes = Encoding.UTF8.GetBytes(jsonHelper.write(d));
        lock (oSocket) {
            oSocket.SendAsync(new ArraySegment<byte>(aBytes),
                WebSocketMessageType.Text, true, oCancel.Token).Wait();
        }
        if (!oTcs.Task.Wait(iTimeoutMs)) {
            lock (oLock) { dPending.Remove(iId); }
            throw new TimeoutException("CDP method " + sMethod + " timed out after " +
                iTimeoutMs + " ms.");
        }
        var dReply = oTcs.Task.Result;
        if (dReply.ContainsKey("error")) {
            var dErr = jsonHelper.getDictAt(dReply, "error");
            throw new InvalidOperationException("CDP " + sMethod + " failed: " +
                jsonHelper.getString(dErr, "message"));
        }
        return jsonHelper.getDictAt(dReply, "result");
    }

    // Wait until an event with the given method (and passing the optional
    // predicate) has been received, consuming it. Also scans events that
    // arrived before the call. Returns null on timeout.
    public Dictionary<string, object> waitForEvent(string sMethod,
            Func<Dictionary<string, object>, bool> fnMatch, int iTimeoutMs) {
        var oWatch = Stopwatch.StartNew();
        while (oWatch.ElapsedMilliseconds < iTimeoutMs && !bClosed) {
            lock (oLock) {
                for (int i = 0; i < lEvents.Count; i++) {
                    var d = lEvents[i];
                    if (jsonHelper.getString(d, "method") != sMethod) continue;
                    var dParams = jsonHelper.getDictAt(d, "params");
                    if (fnMatch == null || fnMatch(dParams)) {
                        lEvents.RemoveAt(i);
                        return dParams;
                    }
                }
            }
            Thread.Sleep(50);
        }
        return null;
    }

    // Count matching buffered events without consuming them.
    public int countEvents(string sMethod) {
        lock (oLock) {
            return lEvents.Count(d => jsonHelper.getString(d, "method") == sMethod);
        }
    }

    // Consume and return all buffered events of a method.
    public List<Dictionary<string, object>> drainEvents(string sMethod) {
        var l = new List<Dictionary<string, object>>();
        lock (oLock) {
            for (int i = lEvents.Count - 1; i >= 0; i--) {
                if (jsonHelper.getString(lEvents[i], "method") == sMethod) {
                    l.Insert(0, jsonHelper.getDictAt(lEvents[i], "params"));
                    lEvents.RemoveAt(i);
                }
            }
        }
        return l;
    }

    void receiveLoop() {
        var aBuffer = new byte[65536];
        var oMessage = new MemoryStream();
        try {
            while (oSocket.State == WebSocketState.Open && !oCancel.IsCancellationRequested) {
                oMessage.SetLength(0);
                WebSocketReceiveResult oResult;
                do {
                    oResult = oSocket.ReceiveAsync(
                        new ArraySegment<byte>(aBuffer), oCancel.Token).Result;
                    if (oResult.MessageType == WebSocketMessageType.Close) { bClosed = true; return; }
                    oMessage.Write(aBuffer, 0, oResult.Count);
                } while (!oResult.EndOfMessage);
                string sJson = Encoding.UTF8.GetString(oMessage.ToArray());
                var d = jsonHelper.parse(sJson);
                if (d.ContainsKey("id")) {
                    int iId = (int)jsonHelper.getLong(d, "id");
                    TaskCompletionSource<Dictionary<string, object>> oTcs = null;
                    lock (oLock) {
                        if (dPending.TryGetValue(iId, out oTcs)) dPending.Remove(iId);
                    }
                    if (oTcs != null) oTcs.TrySetResult(d);
                } else if (d.ContainsKey("method")) {
                    lock (oLock) {
                        lEvents.Add(d);
                        if (lEvents.Count > 5000) lEvents.RemoveAt(0);  // bound memory
                    }
                }
            }
        } catch {
            // Socket torn down (authenticate disconnect, shutdown, or Edge
            // exit). Pending calls fail via timeout.
        } finally {
            bClosed = true;
        }
    }

    public void Dispose() {
        bClosed = true;
        try { oCancel?.Cancel(); } catch { }
        try {
            if (oSocket != null && oSocket.State == WebSocketState.Open) {
                oSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "",
                    CancellationToken.None).Wait(2000);
            }
        } catch { }
        try { oSocket?.Dispose(); } catch { }
    }
}

// ===========================================================================
// pageDriver — page-level operations: find/attach the page target, inject
// the webdriver override, navigate with load + network-quiet + settle
// waits, and harvest matching links.
// ===========================================================================
static class pageDriver {
    // Fetch the target list over the DevTools HTTP endpoint and return the
    // websocket url of the first "page" target (created as about:blank at
    // launch).
    public static string findPageWsUrl(int iPort) {
        string sJson = httpFallback.getString("http://127.0.0.1:" + iPort + "/json/list");
        object v = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(sJson);
        foreach (object o in jsonHelper.getList(v)) {
            var d = jsonHelper.getDict(o);
            if (jsonHelper.getString(d, "type") == "page") {
                string sWs = jsonHelper.getString(d, "webSocketDebuggerUrl");
                if (sWs != "") return sWs;
            }
        }
        return null;
    }

    public static void preparePage(cdpClient oPage) {
        oPage.send("Page.enable");
        oPage.send("Network.enable");
        oPage.send("Runtime.enable");
        oPage.send("Page.addScriptToEvaluateOnNewDocument",
            new Dictionary<string, object> { { "source", program.sWebdriverOverrideScript } });
    }

    // Navigate and wait: load event, then network-quiet window, then the
    // settle delay (urlCheck's wait strategy).
    public static bool navigate(cdpClient oPage, string sUrl) {
        oPage.drainEvents("Page.loadEventFired");
        try {
            oPage.send("Page.navigate", new Dictionary<string, object> { { "url", sUrl } },
                program.iDefaultNavTimeoutMs);
        } catch (Exception ex) {
            program.notify("Navigation failed for " + sUrl + ": " + ex.Message);
            return false;
        }
        var dLoad = oPage.waitForEvent("Page.loadEventFired", null, program.iDefaultNavTimeoutMs);
        if (dLoad == null) {
            program.notify("Timed out waiting for " + sUrl + " to load; continuing with " +
                "whatever content is present.");
        }
        waitForNetworkQuiet(oPage);
        Thread.Sleep(program.iDefaultPostLoadDelayMs);
        return true;
    }

    // Approximate networkidle: consider the page quiet when no
    // requestWillBeSent events have arrived for a quiet window, capped by
    // the overall idle timeout.
    static void waitForNetworkQuiet(cdpClient oPage) {
        var oWatch = Stopwatch.StartNew();
        int iLastCount = -1;
        var oQuiet = Stopwatch.StartNew();
        while (oWatch.ElapsedMilliseconds < program.iNetworkIdleTimeoutMs) {
            int iCount = oPage.countEvents("Network.requestWillBeSent");
            if (iCount != iLastCount) {
                iLastCount = iCount;
                oQuiet.Restart();
            } else if (oQuiet.ElapsedMilliseconds >= program.iNetworkQuietWindowMs) {
                return;
            }
            Thread.Sleep(100);
        }
    }

    // Return the page title, for progress messages.
    public static string getTitle(cdpClient oPage) {
        try {
            var d = oPage.send("Runtime.evaluate", new Dictionary<string, object> {
                { "expression", "document.title" }, { "returnByValue", true } });
            return jsonHelper.getString(jsonHelper.getDictAt(d, "result"), "value");
        } catch {
            return "";
        }
    }

    // Harvest absolute urls of links and embedded resources on the page.
    public static List<string> harvestLinks(cdpClient oPage) {
        const string sScript =
            "(function(){var l=[];" +
            "document.querySelectorAll('a[href],area[href]').forEach(function(e){l.push(e.href);});" +
            "document.querySelectorAll('iframe[src],embed[src],object[data]').forEach(function(e){" +
            "l.push(e.src||e.data);});" +
            "return JSON.stringify(l);})()";
        var l = new List<string>();
        try {
            var d = oPage.send("Runtime.evaluate", new Dictionary<string, object> {
                { "expression", sScript }, { "returnByValue", true } });
            string sJson = jsonHelper.getString(jsonHelper.getDictAt(d, "result"), "value");
            object v = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(sJson);
            foreach (object o in jsonHelper.getList(v)) {
                string s = (o ?? "").ToString().Trim();
                if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
                    l.Add(s);
                }
            }
        } catch (Exception ex) {
            program.notify("Could not read links from the page: " + ex.Message);
        }
        return l.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

// ===========================================================================
// downloadEngine — the orchestrator: launch Edge, connect CDP, iterate the
// sources, harvest and filter links, and download each match with the
// browser (falling back to cookie-replaying HTTP).
// ===========================================================================
static class downloadEngine {
    static cdpClient oBrowser = null;
    static cdpClient oPage = null;
    static readonly HashSet<string> setSeenDomains =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static int runAll(List<string> lSources, List<string> lPatterns) {
        var lRegexes = patternParser.compile(lPatterns);
        Console.WriteLine("Launching Edge...");
        if (!edgeLauncher.launch(program.bMainProfile, program.bInvisible, program.sOutputDir)) {
            return 1;
        }
        try {
            if (!connect()) return 1;

            foreach (string sUrl in lSources) {
                processSource(sUrl, lRegexes);
            }

            results.writeSummary(program.sOutputDir);
            try { Say.say(results.spokenSummary()); } catch { }
            return results.iFailed > 0 && results.iDownloaded == 0 ? 1 : 0;
        } catch (Exception ex) {
            program.notify("Unexpected error: " + ex.Message);
            logger.error(ex.ToString());
            return 1;
        } finally {
            disconnect();
            edgeLauncher.shutdown();
        }
    }

    static bool connect() {
        try {
            oBrowser = new cdpClient();
            oBrowser.connect(edgeLauncher.sWsBrowserUrl);
            string sPageWs = pageDriver.findPageWsUrl(edgeLauncher.iDebugPort);
            if (sPageWs == null) {
                program.notify("Could not find a browser page to drive.");
                return false;
            }
            oPage = new cdpClient();
            oPage.connect(sPageWs);
            pageDriver.preparePage(oPage);
            // Route downloads into the output directory, named by guid so we
            // control the final names; enable progress events.
            oBrowser.send("Browser.setDownloadBehavior", new Dictionary<string, object> {
                { "behavior", "allowAndName" },
                { "downloadPath", program.sOutputDir },
                { "eventsEnabled", true }
            });
            return true;
        } catch (Exception ex) {
            program.notify("Could not connect to Edge's automation channel: " + ex.Message);
            return false;
        }
    }

    static void disconnect() {
        try { oPage?.Dispose(); } catch { }
        try { oBrowser?.Dispose(); } catch { }
        oPage = null;
        oBrowser = null;
    }

    static void processSource(string sUrl, List<Regex> lRegexes) {
        program.notify("");
        program.notify("Source: " + sUrl);

        if (!pageDriver.navigate(oPage, sUrl)) {
            results.addSourceFailure(sUrl);
            return;
        }

        pauseForAuthenticationIfNeeded(sUrl);

        string sTitle = pageDriver.getTitle(oPage);
        if (sTitle != "") program.notify("Page title: " + sTitle);

        // Patterns are matched against the file name each url would be
        // saved as -- not the raw url -- so *newsletter*.pdf behaves the way
        // it would at a command prompt against the resulting files.
        var lMatches = new List<string>();
        if (patternParser.isMatch(lRegexes, urlHelper.fileNameForUrl(sUrl))) lMatches.Add(sUrl);

        foreach (string sLink in pageDriver.harvestLinks(oPage)) {
            if (lMatches.Contains(sLink, StringComparer.OrdinalIgnoreCase)) continue;
            if (patternParser.isMatch(lRegexes, urlHelper.fileNameForUrl(sLink))) lMatches.Add(sLink);
        }

        if (lMatches.Count == 0) {
            program.notify("No links matching the requested extensions were found on this page.");
            return;
        }
        program.notify(Util.stringPlural("matching link", lMatches.Count) + " found.");

        foreach (string sFileUrl in lMatches) {
            downloadOne(sFileUrl);
        }
    }

    // --authenticate: prompt once per registrable domain. Without -m, sever
    // the CDP connections during the pause and reconnect after (the
    // disconnect pattern that defeats many anti-automation checks); with
    // -m the disconnect pattern is skipped, matching urlCheck.
    static void pauseForAuthenticationIfNeeded(string sUrl) {
        if (!program.bAuthenticate) return;
        string sDomain = urlHelper.registrableDomain(sUrl);
        if (setSeenDomains.Contains(sDomain)) return;
        setSeenDomains.Add(sDomain);

        bool bDisconnectPattern = !program.bMainProfile;
        if (bDisconnectPattern) {
            logger.info("Authenticate pause: disconnecting automation channel for " + sDomain);
            disconnect();
        }

        string sPrompt = "The page from " + sDomain + " is open in the Edge window. " +
            "Switch to Edge (Alt+Tab), sign in, accept cookies, or dismiss popups " +
            "as needed. When you are ready to continue, " +
            (program.bGuiMode ? "click OK in this dialog." : "return here and press Enter.");
        if (program.bGuiMode) {
            showTopmostPrompt(sPrompt, program.sProgramName + " — Authenticate: " + sDomain);
        } else {
            Console.WriteLine(sPrompt);
            Console.ReadLine();
        }

        if (bDisconnectPattern) {
            logger.info("Authenticate pause over: reconnecting automation channel.");
            if (!connect()) {
                throw new InvalidOperationException(
                    "Could not reconnect to Edge after authentication.");
            }
        }
        Thread.Sleep(program.iAuthPostConfirmSettleDelayMs);
    }

    // Owner form is created TopMost so the prompt appears in front of Edge
    // rather than behind it (urlCheck lesson: without an owner, the
    // MessageBox belongs to whatever window has focus — usually Edge).
    static void showTopmostPrompt(string sMsg, string sTitle) {
        try {
            using (var oOwner = new Form()) {
                oOwner.TopMost = true;
                oOwner.ShowInTaskbar = false;
                oOwner.FormBorderStyle = FormBorderStyle.None;
                oOwner.StartPosition = FormStartPosition.CenterScreen;
                oOwner.Size = new System.Drawing.Size(1, 1);
                oOwner.Show();
                MessageBox.Show(oOwner, sMsg, sTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        } catch {
            Console.WriteLine(sMsg);
            Console.ReadLine();
        }
    }

    static void downloadOne(string sFileUrl) {
        string sName = urlHelper.fileNameForUrl(sFileUrl);
        var oWatch = Stopwatch.StartNew();

        // Without Force, an existing file of the same name is reported as
        // skipped rather than silently renamed, matching the extCheck and
        // 2htm convention for the Skipped section of the summary.
        if (!program.bForce) {
            try {
                if (File.Exists(Path.Combine(program.sOutputDir, sName))) {
                    results.addSkipped(sName);
                    program.notify("  Skipped (already present): " + sName);
                    return;
                }
            } catch { }
        }

        long iBytes = downloadViaBrowser(sFileUrl, ref sName);
        if (iBytes < 0) {
            logger.info("Browser download failed for " + sFileUrl + "; trying HTTP fallback.");
            iBytes = httpFallback.download(oPage, sFileUrl, ref sName);
        }

        if (iBytes >= 0) {
            results.addSuccess(sName, iBytes);
            program.notify("  Downloaded " + sName + " (" + Util.formatBytes(iBytes) +
                ", " + (oWatch.ElapsedMilliseconds / 1000.0).ToString("0.0",
                CultureInfo.InvariantCulture) + " s)");
        } else {
            results.addFailure(sName, "could not be retrieved");
            program.notify("  FAILED: " + sName);
        }
    }

    // Browser-managed download: open the url in a throwaway tab; Edge
    // downloads it (allowAndName wrote it under a guid name), and we track
    // Browser.downloadWillBegin / downloadProgress to rename on completion.
    // Returns byte count, or -1 on failure. sName may be refined from the
    // browser's suggestedFilename.
    static long downloadViaBrowser(string sFileUrl, ref string sName) {
        string sTargetId = "";
        try {
            oBrowser.drainEvents("Browser.downloadWillBegin");
            oBrowser.drainEvents("Browser.downloadProgress");

            var dCreate = oBrowser.send("Target.createTarget", new Dictionary<string, object> {
                { "url", sFileUrl }
            });
            sTargetId = jsonHelper.getString(dCreate, "targetId");

            var dBegin = oBrowser.waitForEvent("Browser.downloadWillBegin", null, 20000);
            if (dBegin == null) {
                logger.info("No download began for " + sFileUrl +
                    " (the server may have rendered it instead).");
                return -1;
            }
            string sGuid = jsonHelper.getString(dBegin, "guid");
            string sSuggested = jsonHelper.getString(dBegin, "suggestedFilename");
            if (sSuggested != "" &&
                    sSuggested.ToLowerInvariant().EndsWith(
                        Path.GetExtension(sName).ToLowerInvariant())) {
                sName = sSuggested;
            }

            var oWatch = Stopwatch.StartNew();
            while (oWatch.ElapsedMilliseconds < program.iDownloadTimeoutMs) {
                var dProg = oBrowser.waitForEvent("Browser.downloadProgress",
                    d => jsonHelper.getString(d, "guid") == sGuid, 1000);
                if (dProg == null) continue;
                string sState = jsonHelper.getString(dProg, "state");
                if (sState == "completed") {
                    string sTempPath = Path.Combine(program.sOutputDir, sGuid);
                    string sFinalPath = urlHelper.uniquePath(
                        program.sOutputDir, sName, program.bForce);
                    if (File.Exists(sFinalPath) && program.bForce) File.Delete(sFinalPath);
                    File.Move(sTempPath, sFinalPath);
                    sName = Path.GetFileName(sFinalPath);
                    return new FileInfo(sFinalPath).Length;
                }
                if (sState == "canceled") return -1;
            }
            logger.info("Download timed out for " + sFileUrl);
            return -1;
        } catch (Exception ex) {
            logger.info("Browser download error for " + sFileUrl + ": " + ex.Message);
            return -1;
        } finally {
            try {
                if (sTargetId != "") {
                    oBrowser.send("Target.closeTarget",
                        new Dictionary<string, object> { { "targetId", sTargetId } });
                }
            } catch { }
        }
    }
}

// ===========================================================================
// httpFallback — direct HTTP with the browser's cookies and user agent, so
// even the fallback benefits from any session established in Edge.
// ===========================================================================
static class httpFallback {
    public static string getString(string sUrl) {
        var oReq = (HttpWebRequest)WebRequest.Create(sUrl);
        oReq.Timeout = 15000;
        using (var oResp = (HttpWebResponse)oReq.GetResponse())
        using (var oReader = new StreamReader(oResp.GetResponseStream(), Encoding.UTF8)) {
            return oReader.ReadToEnd();
        }
    }

    // Returns byte count, or -1 on failure. sName may be refined from the
    // Content-Disposition header.
    public static long download(cdpClient oPage, string sFileUrl, ref string sName) {
        try {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
        } catch {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }
        try {
            var oReq = (HttpWebRequest)WebRequest.Create(sFileUrl);
            oReq.Timeout = program.iDownloadTimeoutMs;
            oReq.AllowAutoRedirect = true;
            oReq.UserAgent = getBrowserUserAgent(oPage);
            string sCookies = getBrowserCookies(oPage, sFileUrl);
            if (sCookies != "") oReq.Headers[HttpRequestHeader.Cookie] = sCookies;

            using (var oResp = (HttpWebResponse)oReq.GetResponse()) {
                string sDisp = oResp.Headers["Content-Disposition"] ?? "";
                var m = Regex.Match(sDisp, "filename\\*?=\"?([^\";]+)\"?",
                    RegexOptions.IgnoreCase);
                if (m.Success) {
                    string sFromHeader = m.Groups[1].Value.Trim();
                    if (sFromHeader.StartsWith("UTF-8''", StringComparison.OrdinalIgnoreCase))
                        sFromHeader = Uri.UnescapeDataString(sFromHeader.Substring(7));
                    if (sFromHeader.ToLowerInvariant().EndsWith(
                            Path.GetExtension(sName).ToLowerInvariant())) {
                        sName = Path.GetFileName(sFromHeader);
                    }
                }
                string sFinalPath = urlHelper.uniquePath(
                    program.sOutputDir, sName, program.bForce);
                using (var oIn = oResp.GetResponseStream())
                using (var oOut = File.Create(sFinalPath)) {
                    oIn.CopyTo(oOut, 81920);
                }
                sName = Path.GetFileName(sFinalPath);
                return new FileInfo(sFinalPath).Length;
            }
        } catch (Exception ex) {
            logger.info("HTTP fallback failed for " + sFileUrl + ": " + ex.Message);
            return -1;
        }
    }

    static string getBrowserUserAgent(cdpClient oPage) {
        try {
            var d = oPage.send("Runtime.evaluate", new Dictionary<string, object> {
                { "expression", "navigator.userAgent" }, { "returnByValue", true } });
            string s = jsonHelper.getString(jsonHelper.getDictAt(d, "result"), "value");
            if (s != "") return s;
        } catch { }
        return "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" + program.sUserAgentSuffix;
    }

    static string getBrowserCookies(cdpClient oPage, string sUrl) {
        try {
            var d = oPage.send("Network.getCookies", new Dictionary<string, object> {
                { "urls", new[] { sUrl } } });
            var lParts = new List<string>();
            foreach (object o in jsonHelper.getList(
                    d.ContainsKey("cookies") ? d["cookies"] : null)) {
                var dC = jsonHelper.getDict(o);
                string sK = jsonHelper.getString(dC, "name");
                string sV = jsonHelper.getString(dC, "value");
                if (sK != "") lParts.Add(sK + "=" + sV);
            }
            return string.Join("; ", lParts);
        } catch {
            return "";
        }
    }
}

// ===========================================================================
// results — per-file records, the GUI capture buffer, and the final
// structured summary.
//
// The summary follows the convention established in 2htm and extCheck:
// three sections -- Downloaded, Failed to download, Skipped -- each printed
// ONLY when its count is non-zero, with singular "file" at a count of one
// and "files" otherwise. In GUI mode the captured stdout becomes the final
// MessageBox, so the per-name lists are included there; in CLI mode the
// names have already scrolled by inline during the run, so the section
// header is printed but the list beneath it is suppressed rather than
// repeated. Failures read "name: reason", or just the name when no reason
// is available. A closing line gives the output directory.
// ===========================================================================
static class results {
    public class failure {
        public string sName;
        public string sReason;
    }

    public static readonly List<string> lsDownloaded = new List<string>();
    public static readonly List<failure> lsFailed = new List<failure>();
    public static readonly List<string> lsSkipped = new List<string>();
    public static int iSourceFailures = 0;
    public static long iTotalBytes = 0;
    static readonly StringBuilder oCapture = new StringBuilder();

    public static int iDownloaded { get { return lsDownloaded.Count; } }
    public static int iFailed     { get { return lsFailed.Count; } }

    public static void addSuccess(string sName, long iBytes) {
        lsDownloaded.Add(sName);
        iTotalBytes += iBytes;
    }

    public static void addFailure(string sName, string sReason) {
        lsFailed.Add(new failure { sName = sName, sReason = sReason ?? "" });
    }

    public static void addSkipped(string sName) { lsSkipped.Add(sName); }
    public static void addSourceFailure(string sUrl) { iSourceFailures++; }

    public static void capture(string sMsg) { oCapture.AppendLine(sMsg); }
    public static string capturedText() { return oCapture.ToString(); }

    // Write the structured summary to the console (and therefore into the
    // GUI capture buffer and the log, via program.notify).
    public static void writeSummary(string sOutputDir) {
        bool bGui = program.bGuiMode;
        int iDown = lsDownloaded.Count;
        int iFail = lsFailed.Count;
        int iSkip = lsSkipped.Count;

        if (iDown > 0) {
            program.notify("");
            program.notify("Downloaded " + Util.stringPlural("file", iDown) +
                " (" + Util.formatBytes(iTotalBytes) + "):");
            if (bGui) foreach (string sName in lsDownloaded) program.notify(sName);
        }
        if (iFail > 0) {
            program.notify("");
            program.notify("Failed to download " + Util.stringPlural("file", iFail) + ":");
            if (bGui) {
                foreach (failure o in lsFailed) {
                    program.notify(string.IsNullOrEmpty(o.sReason)
                        ? o.sName : o.sName + ": " + o.sReason);
                }
            }
        }
        if (iSkip > 0) {
            program.notify("");
            program.notify("Skipped " + Util.stringPlural("file", iSkip) +
                ". Check \"Force overwrite\" to replace existing files.");
            if (bGui) foreach (string sName in lsSkipped) program.notify(sName);
        }
        if (iSourceFailures > 0) {
            program.notify("");
            program.notify("Could not load " + Util.stringPlural("source page", iSourceFailures) + ".");
        }
        if (iDown == 0 && iFail == 0 && iSkip == 0) {
            program.notify("");
            program.notify("No matching files were found.");
        }
        program.notify("");
        program.notify("Output directory: " + sOutputDir);
    }

    // One short spoken line for screen reader users, so the outcome is
    // audible without reading the whole MessageBox.
    public static string spokenSummary() {
        int iDown = lsDownloaded.Count;
        int iFail = lsFailed.Count;
        if (iDown == 0 && iFail == 0) return "No matching files found";
        string s = Util.stringPlural("file", iDown) + " downloaded";
        if (iFail > 0) s += ", " + iFail + " failed";
        return s;
    }
}

// ===========================================================================
// guiDialog — the parameter dialog, built from Homer Lbc primitives so it
// inherits the whole shared convenience set rather than reimplementing any
// of it. Every text field is an LbcTextBox and so gets, for free:
//
//   Control+C        Copy: the selection, or the CURRENT LINE when nothing
//                    is selected -- the behavior asked for specifically
//   Alt+C            Copy Append (clipboard + selection-or-line)
//   Control+X / Alt+X  Cut and Cut Append, same selection-or-line rule
//   Control+D        Delete Line
//   Control+A / Control+Shift+A   Select All / Unselect All
//   F8 / Shift+F8    Start / Complete Selection
//   Control+F8       Copy All          Alt+F8   Read All
//   Alt+Y            Say Yield (line and character counts)
//   Alt+Apostrophe   Say Clipboard
//   Shift+F1         Focus Tip -- speaks the tip passed to each adder below
//
// The dialog Form is an LbcForm, so Control+Enter clicks OK from ANY
// control, and LbcDialog itself supplies the Help button (Alt+H / F1) whose
// text is generated from the field labels and their tips.
//
// LbcDialog lays out one control per row in a vertical stack, so the
// browse buttons cannot sit beside their text fields as they did in the
// hand-built version. They become dialog buttons instead: pressing one
// closes the dialog, runs the picker, and reopens the dialog with the
// chosen value filled in and every other field preserved. run() loops
// until OK or Cancel.
// ===========================================================================
public static class guiDialog {
    public static bool show(ref string sSource, ref string sExtensions, ref string sOutputDir,
            ref bool bAuth, ref bool bMain, ref bool bInvisible,
            ref bool bForce, ref bool bView, ref bool bLog, ref bool bUseCfg) {
        while (true) {
            string sButton;
            using (var dlg = new LbcDialog(program.sProgramName, null)) {
                TextBox tbSource = dlg.addInputBox("Source urls:", sSource,
                    "One or more urls, local HTML files, or text files listing one url per line. " +
                    "Separate items with spaces; quote any single item containing a space.");
                TextBox tbExt = dlg.addInputBox("File extensions:",
                    string.IsNullOrWhiteSpace(sExtensions) ? program.sDefaultExtensions : sExtensions,
                    "What to download, separated by commas or spaces. A bare extension like pdf " +
                    "means .pdf, which means *.pdf. Wildcards * and ? work as in the command " +
                    "prompt, so *newsletter*.pdf matches only newsletter PDFs. Matching ignores case.");
                TextBox tbOut = dlg.addInputBox("Output directory:", sOutputDir,
                    "Where downloaded files are saved. Leave empty for the current directory.");
                dlg.addSeparator();
                CheckBox cbAuth = dlg.addCheckBox("Authenticate credentials", bAuth,
                    "Pause at the first page of each site so you can sign in, accept cookies, " +
                    "or complete two-factor in the Edge window, then continue. Overrides Invisible.");
                CheckBox cbMain = dlg.addCheckBox("Main profile", bMain,
                    "Use your real Edge profile so existing logins apply. Edge must be fully closed.");
                CheckBox cbInvisible = dlg.addCheckBox("Invisible browser", bInvisible,
                    "Run Edge with no visible window. Ignored when Authenticate is checked.");
                CheckBox cbForce = dlg.addCheckBox("Force overwrite", bForce,
                    "Overwrite existing files instead of adding a numeric suffix such as _001.");
                CheckBox cbView = dlg.addCheckBox("View output", bView,
                    "Open the output directory in File Explorer when the run finishes.");
                CheckBox cbLog = dlg.addCheckBox("Log session", bLog,
                    "Write a fresh " + program.sLogFileName + " to the output directory.");
                CheckBox cbUseCfg = dlg.addCheckBox("Use configuration", bUseCfg,
                    "Load settings at startup and save them on OK, in " + program.sConfigFileName + ".");

                sButton = dlg.runWithButtons(new string[] {
                    "OK", "Browse source", "Choose output", "Default settings", "Cancel" });

                // Harvest every field before the dialog is disposed, so a
                // browse round trip preserves whatever else was typed.
                sSource      = (tbSource.Text ?? "").Trim();
                sExtensions  = (tbExt.Text ?? "").Trim();
                sOutputDir   = (tbOut.Text ?? "").Trim();
                bAuth        = cbAuth.Checked;
                bMain        = cbMain.Checked;
                bInvisible   = cbInvisible.Checked;
                bForce       = cbForce.Checked;
                bView        = cbView.Checked;
                bLog         = cbLog.Checked;
                bUseCfg      = cbUseCfg.Checked;
            }

            if (string.Equals(sButton, "Cancel", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(sButton)) {
                return false;
            }

            if (string.Equals(sButton, "Browse source", StringComparison.OrdinalIgnoreCase)) {
                browseSource(ref sSource, ref sOutputDir);
                continue;
            }

            if (string.Equals(sButton, "Choose output", StringComparison.OrdinalIgnoreCase)) {
                chooseOutput(ref sOutputDir);
                continue;
            }

            if (string.Equals(sButton, "Default settings", StringComparison.OrdinalIgnoreCase)) {
                sSource = ""; sExtensions = program.sDefaultExtensions; sOutputDir = "";
                bAuth = false; bMain = false; bInvisible = false;
                bForce = false; bView = false; bLog = false; bUseCfg = false;
                configManager.eraseAll();
                Say.say("Default settings restored");
                continue;
            }

            // OK: validate the output directory, offering to create it.
            if (!confirmOutputDir(sOutputDir)) continue;
            return true;
        }
    }

    // Pick a local HTML page or a url-list text file.
    static bool browseSource(ref string sSource, ref string sOutputDir) {
        using (var dialog = new OpenFileDialog()) {
            dialog.Title = "Choose a local HTML page or a url-list text file";
            dialog.Filter =
                "Web pages and url lists (*.htm;*.html;*.txt)|*.htm;*.html;*.txt|" +
                "HTML pages (*.htm;*.html)|*.htm;*.html|" +
                "Text files (*.txt)|*.txt|All files (*.*)|*.*";
            dialog.CheckFileExists = true;
            dialog.RestoreDirectory = true;
            try { dialog.InitialDirectory = getInitialBrowseDir(sSource); } catch { }
            if (dialog.ShowDialog() != DialogResult.OK) return false;
            string sPicked = dialog.FileName;
            sSource = sPicked.Contains(" ") ? "\"" + sPicked + "\"" : sPicked;
            if (string.IsNullOrWhiteSpace(sOutputDir)) {
                try { sOutputDir = Path.GetDirectoryName(dialog.FileName); } catch { }
            }
            Say.say("Source set");
            return true;
        }
    }

    static bool chooseOutput(ref string sOutputDir) {
        using (var dialog = new FolderBrowserDialog()) {
            dialog.Description = "Choose the output directory";
            dialog.ShowNewFolderButton = true;
            try { dialog.SelectedPath = getInitialBrowseDir(sOutputDir); } catch { }
            if (dialog.ShowDialog() != DialogResult.OK) return false;
            sOutputDir = dialog.SelectedPath;
            Say.say("Output directory set");
            return true;
        }
    }

    // Offer to create a non-existent output directory. Returns false to send
    // the user back to the dialog.
    static bool confirmOutputDir(string sOutputDir) {
        string s = (sOutputDir ?? "").Trim();
        if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
            s = s.Substring(1, s.Length - 2).Trim();
        if (s.Length == 0) return true;
        try { if (Directory.Exists(s)) return true; } catch { return true; }
        DialogResult dr = MessageBox.Show("Create " + s + "?", program.sProgramName,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
        if (dr != DialogResult.Yes) return false;
        try { Directory.CreateDirectory(s); return true; }
        catch (Exception ex) {
            MessageBox.Show("Could not create directory:\r\n" + s + "\r\n\r\n" + ex.Message,
                program.sProgramName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    static string getInitialBrowseDir(string sFieldText) {
        try {
            string s = (sFieldText ?? "").Trim().Trim('"');
            if (s.Length > 0) {
                string sDir = Directory.Exists(s) ? s : Path.GetDirectoryName(s);
                if (!string.IsNullOrEmpty(sDir) && Directory.Exists(sDir)) return sDir;
            }
        } catch { }
        try { return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
        catch { return Directory.GetCurrentDirectory(); }
    }
}

// ===========================================================================
// logger — ISO-8601 timestamped UTF-8 log, flushed per write, no-op when
// closed (from extCheck). Writes urlFido.log to the output directory.
// ===========================================================================
public static class logger {
    static StreamWriter writer = null;

    public static void open(string sDir = "") {
        if (writer != null) return;
        string sLogDir;
        try {
            sLogDir = (!string.IsNullOrWhiteSpace(sDir) && Directory.Exists(sDir))
                ? Path.GetFullPath(sDir) : Directory.GetCurrentDirectory();
        } catch { sLogDir = Directory.GetCurrentDirectory(); }
        string sPath = Path.Combine(sLogDir, program.sLogFileName);
        try {
            writer = new StreamWriter(sPath, false, new UTF8Encoding(true));
            writer.AutoFlush = true;
        } catch (Exception ex) {
            Console.Error.WriteLine("Could not open log file '" + sPath + "': " + ex.Message);
            writer = null;
        }
    }

    public static void close() {
        if (writer == null) return;
        try { writer.WriteLine(stamp("INFO") + " Log closed."); writer.Flush(); writer.Close(); }
        catch { }
        writer = null;
    }

    public static void info(string sMsg) { write("INFO", sMsg); }
    public static void warn(string sMsg) { write("WARN", sMsg); }
    public static void error(string sMsg) { write("ERROR", sMsg); }

    public static void header(string sName, string sVersion,
            List<KeyValuePair<string, string>> lParams) {
        if (writer == null) return;
        try {
            writer.WriteLine("=== " + sName + " " + sVersion + " ===");
            writer.WriteLine("Run on " + friendlyTime(DateTime.Now));
            if (lParams != null && lParams.Count > 0) {
                writer.WriteLine("Parameters:");
                int iPad = 0;
                foreach (var o in lParams) if (o.Key.Length > iPad) iPad = o.Key.Length;
                foreach (var o in lParams) writer.WriteLine("  " + o.Key.PadRight(iPad) + " : " + o.Value);
            }
            writer.WriteLine("===");
        } catch { }
    }

    public static string friendlyTime(DateTime dt) {
        return dt.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture) + " at " +
            dt.ToString("h:mm tt", CultureInfo.InvariantCulture);
    }

    static void write(string sLevel, string sMsg) {
        if (writer == null) return;
        try { writer.WriteLine(stamp(sLevel) + " " + sMsg); } catch { }
    }

    static string stamp(string sLevel) {
        return DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff") + " [" + sLevel + "]";
    }
}

// ===========================================================================
// configManager — settings persistence at
// %LOCALAPPDATA%\urlFido\urlFido.inix, opt-in via -u / Use configuration.
//
// .inix is the Homer superset of classic .ini (order-preserving round-trip,
// implicit [Global], verbatim multi-line values). urlFido uses the plain
// section/key subset, so the file is readable and hand-editable as ordinary
// .ini. Booleans are stored as y/n, matching the FileDir convention, and
// read tolerantly (y/yes/1/true all count as true).
// ===========================================================================
public static class configManager {
    const string sSettingsSection = "Settings";

    public static string getConfigDir() {
        string sAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(sAppData, program.sConfigDirName);
    }

    public static string getConfigPath() {
        return Path.Combine(getConfigDir(), program.sConfigFileName);
    }

    public static bool configExists() {
        try { return File.Exists(getConfigPath()); } catch { return false; }
    }

    public static void eraseAll() {
        string sDir = getConfigDir();
        string sPath = getConfigPath();
        try { if (File.Exists(sPath)) { File.Delete(sPath); logger.info("Deleted configuration file: " + sPath); } }
        catch (Exception ex) { logger.info("Could not delete configuration file " + sPath + ": " + ex.Message); }
        try {
            if (Directory.Exists(sDir)) {
                bool bEmpty = !Directory.EnumerateFileSystemEntries(sDir).GetEnumerator().MoveNext();
                if (bEmpty) { Directory.Delete(sDir); logger.info("Removed empty configuration directory: " + sDir); }
            }
        } catch (Exception ex) { logger.info("Could not remove configuration directory " + sDir + ": " + ex.Message); }
    }

    // Read the [Settings] section into a plain dictionary. Returns an empty
    // dictionary when the file is absent or unreadable.
    static Dictionary<string, string> readSettings() {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string sPath = getConfigPath();
        if (!File.Exists(sPath)) return d;
        try {
            foreach (InixCodec.Section oSec in InixCodec.read(sPath)) {
                if (!string.Equals(oSec.Name, sSettingsSection, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(oSec.Name)) continue;
                foreach (string sKey in oSec.keys()) d[sKey] = oSec.get(sKey) ?? "";
            }
        } catch (Exception ex) {
            string sMsg = "Could not read configuration from:\r\n" + sPath + "\r\n\r\n" + ex.Message;
            Console.Error.WriteLine(sMsg);
            logger.warn("Could not read configuration: " + ex.Message);
            if (program.bGuiMode) {
                try { MessageBox.Show(sMsg, program.sProgramName + " — Configuration not loaded",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
            }
        }
        return d;
    }

    public static void loadInto(List<string> lFileArgs) {
        var d = readSettings();
        if (d.Count == 0) return;
        if (!program.bSourceFromCli) {
            string sSaved = getOrEmpty(d, "SourceUrls");
            if (!string.IsNullOrWhiteSpace(sSaved))
                foreach (var s in program.splitSourceField(sSaved)) lFileArgs.Add(s);
        }
        if (!program.bExtensionsFromCli) {
            string s = getOrEmpty(d, "Extensions");
            if (!string.IsNullOrWhiteSpace(s)) program.sExtensions = s;
        }
        if (!program.bOutputDirFromCli)   program.sOutputDir   = getOrEmpty(d, "OutputDirectory");
        if (!program.bAuthenticateFromCli) program.bAuthenticate = getBool(d, "Authenticate");
        if (!program.bMainProfileFromCli)  program.bMainProfile  = getBool(d, "MainProfile");
        if (!program.bInvisibleFromCli)    program.bInvisible    = getBool(d, "Invisible");
        if (!program.bForceFromCli)        program.bForce        = getBool(d, "Force");
        if (!program.bViewOutputFromCli)   program.bViewOutput   = getBool(d, "ViewOutput");
        if (!program.bLogFromCli)          program.bLog          = getBool(d, "LogSession");
    }

    public static void save(string sSource, string sExtensions, string sOutputDir,
            bool bAuth, bool bMain, bool bInvisible, bool bForce, bool bView, bool bLog) {
        string sDir = getConfigDir();
        string sPath = getConfigPath();
        try {
            if (!Directory.Exists(sDir)) Directory.CreateDirectory(sDir);
            // InixCodec.writeValue preserves any other sections, keys, and
            // comments the user may have added by hand.
            InixCodec.writeValue(sPath, sSettingsSection, "SourceUrls",      sSource ?? "");
            InixCodec.writeValue(sPath, sSettingsSection, "Extensions",      sExtensions ?? "");
            InixCodec.writeValue(sPath, sSettingsSection, "OutputDirectory", sOutputDir ?? "");
            InixCodec.writeValue(sPath, sSettingsSection, "Authenticate",    yn(bAuth));
            InixCodec.writeValue(sPath, sSettingsSection, "MainProfile",     yn(bMain));
            InixCodec.writeValue(sPath, sSettingsSection, "Invisible",       yn(bInvisible));
            InixCodec.writeValue(sPath, sSettingsSection, "Force",           yn(bForce));
            InixCodec.writeValue(sPath, sSettingsSection, "ViewOutput",      yn(bView));
            InixCodec.writeValue(sPath, sSettingsSection, "LogSession",      yn(bLog));
            logger.info("Saved configuration to " + sPath);
        } catch (Exception ex) {
            string sMsg = "Could not save configuration to:\r\n" + sPath + "\r\n\r\n" + ex.Message;
            Console.Error.WriteLine(sMsg);
            logger.warn("Could not save configuration: " + ex.Message);
            if (program.bGuiMode) {
                try { MessageBox.Show(sMsg, program.sProgramName + " — Configuration not saved",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
            }
        }
    }

    static string yn(bool b) { return b ? "y" : "n"; }

    static bool getBool(Dictionary<string, string> d, string sKey) {
        string s;
        if (!d.TryGetValue(sKey, out s)) return false;
        s = (s ?? "").Trim().ToLowerInvariant();
        return s == "y" || s == "yes" || s == "1" || s == "true";
    }

    static string getOrEmpty(Dictionary<string, string> d, string sKey) {
        string s;
        return d.TryGetValue(sKey, out s) ? (s ?? "") : "";
    }
}

// ===========================================================================
// nvdaLoader — NVDA speech from a single executable.
//
// Say.cs reaches NVDA through P/Invoke into nvdaControllerClient.dll, a
// NATIVE Win32 DLL. NVDA does not ship that DLL in its end-user
// installation (it comes with the developer package), so it cannot be
// borrowed from the user's NVDA folder — it has to travel with us.
//
// Windows has no load-a-DLL-from-memory entry point: LoadLibrary takes a
// path. So the DLL is embedded as a managed resource (csc /resource:) and,
// when it is actually needed, written once to %LOCALAPPDATA%\urlFido and
// loaded from there. After LoadLibrary succeeds, Windows registers the
// module under its base name, so the plain [DllImport("nvdaControllerClient
// .dll")] declarations in Say.cs bind to the already-loaded module. Say.cs
// therefore needs no modification and stays byte-identical to the copy in
// DbDo, EdSharp, and FileDir.
//
// Nothing happens unless NVDA is actually running. That keeps the promise
// that urlFido leaves no filesystem footprint of its own: a JAWS, Narrator,
// or SAPI user never causes the file to be written at all.
//
// Every failure path is silent by design. If the resource is absent (built
// without the DLL beside the sources), if the directory cannot be written,
// or if LoadLibrary fails — for instance because a 32-bit DLL was embedded
// into this 64-bit build — the DllImport simply throws
// DllNotFoundException on first use, Say.cs catches it, and speech falls
// back to JAWS, Narrator, or SAPI exactly as before.
// ===========================================================================
static class nvdaLoader {
    // Must match the resource identifier passed to csc in buildUrlFido.cmd.
    const string sNvdaResourceName = "nvdaControllerClient.dll";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr loadLibraryW(string sPath);

    public static bool bLoaded = false;

    public static void preload() {
        try {
            // No NVDA, no extraction, no footprint.
            if (Process.GetProcessesByName("nvda").Length == 0) return;

            byte[] aBytes;
            using (Stream oIn = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(sNvdaResourceName)) {
                if (oIn == null) return;          // built without the DLL present
                aBytes = new byte[oIn.Length];
                int iRead = 0;
                while (iRead < aBytes.Length) {
                    int i = oIn.Read(aBytes, iRead, aBytes.Length - iRead);
                    if (i <= 0) break;
                    iRead += i;
                }
            }

            string sDir = configManager.getConfigDir();
            if (!Directory.Exists(sDir)) Directory.CreateDirectory(sDir);
            string sPath = Path.Combine(sDir, sNvdaResourceName);

            // Rewrite only when the extracted copy differs in size from the
            // embedded one, so a newer build refreshes a stale extraction
            // without paying the write cost on every run.
            bool bWrite = true;
            try {
                if (File.Exists(sPath) && new FileInfo(sPath).Length == aBytes.Length)
                    bWrite = false;
            } catch { }
            if (bWrite) {
                try { File.WriteAllBytes(sPath, aBytes); }
                catch {
                    // Usually means another urlFido process has the file
                    // loaded, which locks it. An existing copy is then the
                    // right one to use anyway.
                    if (!File.Exists(sPath)) return;
                }
            }

            bLoaded = loadLibraryW(sPath) != IntPtr.Zero;
        } catch {
            // Deliberately silent: NVDA speech is a convenience, never a
            // precondition for urlFido doing its job.
        }
    }
}

// ===========================================================================
// Entry point. [STAThread] is REQUIRED for the WinForms common dialogs
// (OpenFileDialog, FolderBrowserDialog) and MessageBox owner handling.
// ===========================================================================
class urlFido {
    [STAThread]
    static int Main(string[] aArgs) {
        // Must run before the first Say call, so the module is already in
        // the process when Say.cs's DllImport resolves by base name.
        nvdaLoader.preload();
        return program.run(aArgs);
    }
}
