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
    public const int iProbeTimeoutMs = 8000;
    // Politeness. Servers throttle or block clients that arrive in a burst,
    // and a tool that gets urlFido blacklisted from a site is worse than a
    // slightly slower one. Concurrency is modest and each worker pauses
    // between requests, so a site sees a steady trickle rather than a flood.
    public const int iProbeConcurrency = 4;
    public const int iRequestGapMs = 250;
    public const int iDownloadGapMs = 400;
    // The longest single name a Windows path component may hold. Nothing
    // shorter is imposed: a page title is trimmed only when the file system
    // would actually refuse it.
    public const int iMaxPathComponent = 255;

    // Room kept free inside a page folder for the file names that will go in
    // it, so a long title cannot leave a folder whose contents are
    // uncreatable. Windows paths are capped at 259 characters unless long
    // path support is switched on.
    public const int iPathBudget = 259;
    public const int iFileNameAllowance = 64;
    public const string sFallbackTitle = "untitled-page";
    public const int iAuthPostConfirmSettleDelayMs = 4000;

    public const string sConfigDirName = "urlFido";
    public const string sConfigFileName = "urlFido.inix";
    public const string sDefaultExtensions = "docx pdf zip";

    // Sources the dialog offers when it has nothing else to show: the major
    // organizations of and for blind people. They give a new user something
    // real to run against immediately, and each publishes documents worth
    // having. Note wbu.ngo, not wbu.org — the World Blind Union is on the
    // .ngo top-level domain.
    public const string sDefaultSources =
        "acb.org afb.org nfb.org wbu.ngo";
    public const string sLogFileName = "urlFido.log";
    public const string sProgramName = "urlFido";
    // Keep in step with sAppVersion in urlFido_setup.iss. tagRelease tags
    // the version stamped into urlFido_setup.exe, so a mismatch here would
    // make -v report something the release does not.
    public const string sProgramVersion = "1.1.0";
    public const string sReadmeUrl = "https://github.com/JamalMazrui/urlFido#readme";
    public const string sUsage =
        "Usage: urlFido [options] <url, local html file, or url-list text file> [...]";
    public const string sUserAgentSuffix = " urlFido/1.1.0";
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

    // Where the CURRENT source's files are being written: a subdirectory of
    // sOutputDir named after the page title. sOutputDir is the parent.
    public static string sTargetDir = "";

    // The page currently being harvested. Sent as the Referer when files from
    // it are downloaded: many sites serve a document only to a request that
    // appears to come from the page linking it, and refuse or redirect
    // anything that arrives cold.
    public static string sCurrentPageUrl = "";

    // Test Fetch: do everything except write anything. The page is loaded and
    // examined exactly as for a real run, so the report reflects what would
    // actually happen rather than a guess, but nothing is downloaded and no
    // folder is created.
    public static bool bSimulate = false;

    // The output directory the dialog offers when none has been chosen.
    // 2htm and extCheck both settle on Documents for this, and it is the
    // right answer here too: downloads should not land in whatever folder
    // the program happened to start in, which is how a run launched from
    // the source tree drops files among the sources.
    public static string defaultOutputDirForGui() {
        try {
            string sDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(sDocs) && Directory.Exists(sDocs)) return sDocs;
        } catch { }
        return Directory.GetCurrentDirectory();
    }

    public static List<string> lSources = new List<string>();

    public static int run(string[] aArgs) {
        var lFileArgs = new List<string>();
        int iParse = parseArgs(aArgs, lFileArgs);
        if (iParse >= 0) return iParse;  // -h or -v handled, or a parse error

        // Configuration is opt-in for the command line (-u is required, so a
        // scripted run never picks up state the script did not ask for) but
        // IMPLICIT in GUI mode whenever a config file already exists. That
        // asymmetry is the convention established by 2htm and followed by
        // extCheck and urlCheck, and it is what makes the dialog checkbox
        // mean what a user expects: tick "Use configuration" once, and the
        // next time the dialog opens it comes back the way you left it.
        //
        // Without this, checking the box saved settings that were never read
        // back, because loading depended on a switch the user never typed.
        if (bGuiMode && !bUseConfig && configManager.configExists()) {
            bUseConfig = true;
            logger.info("GUI mode: an existing configuration was found and loaded.");
        }
        if (bUseConfig) configManager.loadInto(lFileArgs);

        if (bGuiMode) {
            // Started from Explorer, a shortcut, or the hotkey, Windows gives
            // urlFido a console of its own that serves no purpose once the
            // dialog is up. Hide it -- but only when it is ours alone; run
            // from an existing cmd.exe, that window is the user's shell.
            if (consoleWindow.launchedFromGui()) {
                consoleWindow.hide();
                logger.info("Console window hidden: launched from the GUI.");
            }

            string sSource = string.Join(" ", lFileArgs.Select(quoteIfNeeded));
            string sExt = sExtensions;
            string sOut = sOutputDir;
            bool bF = bForce, bV = bViewOutput, bL = bLog;
            // Seed empty fields with the defaults. Only the DIALOG gets these:
            // on the command line, the working directory stays the default, so
            // a scripted run behaves the way every other command-line tool does.
            if (sSource.Trim().Length == 0) sSource = sDefaultSources;
            if (sOutputDir.Trim().Length == 0) sOutputDir = defaultOutputDirForGui();

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
            if (!bUseConfig) {
                // Because GUI mode now auto-loads an existing config, leaving
                // the file behind after the box is cleared would make the
                // setting impossible to switch off: the next run would find
                // the file and load it again. Clearing the box therefore
                // removes the stored settings, as it does in urlCheck.
                configManager.eraseAll();
            } else {
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
            logger.info("Mode: " + (bGuiMode ? "GUI" : "command line"));
            logger.info("Working directory: " + Directory.GetCurrentDirectory());
            logger.info("Executable: " + Application.ExecutablePath);
            logger.info("OS: " + Environment.OSVersion + ", 64-bit process: " + Environment.Is64BitProcess);
            logger.info("NVDA client loaded: " + (nvdaLoader.bLoaded ? "yes" : "no"));
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
        l.Add(new KeyValuePair<string, string>("Use configuration", bUseConfig ? "yes" : "no"));
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
                case "-e": case "--file-extensions": case "--extensions":
                    if (i + 1 >= aArgs.Length) { Console.Error.WriteLine("Missing value after " + sArg); return 2; }
                    sExtensions = aArgs[++i]; bExtensionsFromCli = true; break;
                case "-o": case "--output-dir": case "--output-folder":
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
        Console.WriteLine("  -e, --file-extensions <list>  File extensions to download, separated by");
        Console.WriteLine("                             commas or spaces. A leading dot is optional:");
        Console.WriteLine("                             \"pdf\", \".pdf\", and \"*.pdf\" all work.");
        Console.WriteLine("                             Default: " + sDefaultExtensions);
        Console.WriteLine("  -o, --output-dir <dir>     Directory that receives the downloaded files.");
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

    // Expand each source argument into one or more urls. A source may be:
    //
    //   * a url, with or without a scheme       https://example.com/docs
    //   * a local web page                      C:\Saved\page.htm
    //   * a TEXT FILE LISTING ONE URL PER LINE  C:\lists\sites.txt
    //
    // The third form is the urlCheck convention and is what makes a repeated
    // job convenient: keep the pages you harvest from in a text file and hand
    // urlFido the file. Blank lines are ignored, and a line beginning with #
    // or ; is a comment, so a list can be annotated and entries can be
    // commented out without deleting them. Each line goes through the same
    // normalization as a typed url, so bare domains work there too, and a
    // line naming a local .htm file is accepted as well.
    //
    // Any existing file that is not a web page is read as a list. That is
    // deliberate: it means .txt, .lst, .urls, or no extension at all behave
    // the same, and the user never has to remember a required extension.
    public static List<string> expandSources(List<string> lArgs) {
        var l = new List<string>();
        foreach (string sArgRaw in lArgs) {
            string sArg = (sArgRaw ?? "").Trim().Trim('"');
            if (sArg.Length == 0) continue;

            bool bLooksLikePath = sArg.IndexOf(":\\") > 0 || sArg.StartsWith("\\\\") ||
                sArg.StartsWith(".") || sArg.IndexOf(Path.DirectorySeparatorChar) >= 0;

            if (File.Exists(sArg)) {
                string sExt = Path.GetExtension(sArg).ToLowerInvariant();
                if (sExt == ".htm" || sExt == ".html" || sExt == ".xhtml") {
                    string sFileUrl = new Uri(Path.GetFullPath(sArg)).AbsoluteUri;
                    logger.info("Source is a local page: " + sArg);
                    l.Add(sFileUrl);
                    continue;
                }
                l.AddRange(readUrlList(sArg));
                continue;
            }

            if (bLooksLikePath) {
                // Named something path-shaped that is not there. Say so
                // plainly rather than trying to normalize it into a url and
                // reporting a confusing "unrecognized source".
                program.notify("File not found: " + sArg);
                continue;
            }

            string sUrl = normalize(sArg);
            if (sUrl != "") l.Add(sUrl);
            else program.notify("Skipping unrecognized source: " + sArg);
        }

        var lUnique = l.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (lUnique.Count < l.Count)
            logger.info("Removed " + (l.Count - lUnique.Count) + " duplicate source url(s).");
        return lUnique;
    }

    // Read one url per line, skipping blanks and # or ; comments. Reports
    // what it found, so a mistyped list does not fail silently.
    static List<string> readUrlList(string sPath) {
        var l = new List<string>();
        int iLine = 0, iBad = 0;
        try {
            foreach (string sLineRaw in File.ReadAllLines(sPath, new UTF8Encoding(true))) {
                iLine++;
                string sLine = (sLineRaw ?? "").Trim();
                if (sLine.Length == 0) continue;
                if (sLine.StartsWith("#") || sLine.StartsWith(";")) continue;

                if (File.Exists(sLine)) {
                    string sExt = Path.GetExtension(sLine).ToLowerInvariant();
                    if (sExt == ".htm" || sExt == ".html" || sExt == ".xhtml") {
                        l.Add(new Uri(Path.GetFullPath(sLine)).AbsoluteUri);
                        continue;
                    }
                }
                string sNorm = normalize(sLine);
                if (sNorm != "") {
                    l.Add(sNorm);
                } else {
                    iBad++;
                    logger.warn("Line " + iLine + " of " + sPath + " is not a usable url: " + sLine);
                }
            }
        } catch (Exception ex) {
            program.notify("Could not read the url list '" + sPath + "': " + ex.Message);
            return l;
        }
        program.notify("Read " + Util.stringPlural("url", l.Count) + " from " +
            Path.GetFileName(sPath) +
            (iBad > 0 ? " (" + iBad + " unusable line(s) skipped; see the log)" : "") + ".");
        return l;
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

    // The name this url would be saved as on disk, worked out WITHOUT any
    // network traffic: the last path segment, percent-decoded. Returns ""
    // when the address carries no usable file name.
    public static string quickNameForUrl(string sUrl) {
        try { return Web.nameFromUrl(sUrl); }
        catch { return ""; }
    }

    // Does the address itself carry a usable extension? Pure string work.
    public static bool hasExtensionInPath(string sUrl) {
        string sName = quickNameForUrl(sUrl);
        return sName.Length > 0 && Path.GetExtension(sName).Length > 1;
    }

    // Everything after '#' identifies a place within a page, never a
    // different resource, so two links differing only by fragment are one
    // address as far as downloading is concerned.
    public static string stripFragment(string sUrl) {
        int i = sUrl.IndexOf('#');
        return i >= 0 ? sUrl.Substring(0, i) : sUrl;
    }

    // Is this address worth asking a server about? Answering no here is what
    // keeps automatic analysis quick: the great majority of links on a page
    // can be ruled out by inspection, for free.
    public static bool isWorthAsking(string sUrl) {
        if (string.IsNullOrEmpty(sUrl)) return false;

        // Only the web schemes can yield a download. mailto:, tel:,
        // javascript: and friends never can.
        if (!sUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !sUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;

        string sBare = stripFragment(sUrl);

        // A bare origin, or a link that is only a fragment of the current
        // page, is the page itself.
        try {
            var oUri = new Uri(sBare);
            string sPath = oUri.AbsolutePath;
            if (sPath.Length <= 1) return false;

            // Paths that end in a slash name a directory index — a page.
            if (sPath.EndsWith("/")) return false;

            // A last segment that is plainly a page extension is a page.
            string sExt = Path.GetExtension(sPath).ToLowerInvariant();
            if (sExt == ".htm" || sExt == ".html" || sExt == ".xhtml" ||
                sExt == ".asp" || sExt == ".aspx" || sExt == ".php" ||
                sExt == ".jsp" || sExt == ".cfm") return false;
        } catch {
            return false;
        }
        return true;
    }

    // Answers already obtained in this run. A page often links the same
    // address several times, and asking once is enough.
    static readonly Dictionary<string, string> dProbeCache =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Ask a server what it would actually send. Returns the file name it
    // would arrive as, or "" if this address does not yield a file.
    //
    // The decisive heuristic is the content type: a server answering
    // text/html is serving a page, and no page is a download, so the answer
    // is settled by the response headers alone without transferring a byte
    // of the body. That is what makes asking about every unknown link
    // affordable.
    public static string probeName(string sUrl) {
        string sKey = stripFragment(sUrl);
        lock (dProbeCache) {
            string sHit;
            if (dProbeCache.TryGetValue(sKey, out sHit)) return sHit;
        }

        string sResult = "";
        try {
            Web.configure();
            var oReq = (HttpWebRequest)WebRequest.Create(sKey);
            oReq.Method = "HEAD";
            if (program.sCurrentPageUrl != "") {
                try { oReq.Referer = program.sCurrentPageUrl; } catch { }
            }
            oReq.UserAgent = Web.userAgent();
            oReq.Accept = "*/*";
            oReq.AllowAutoRedirect = true;
            oReq.Timeout = program.iProbeTimeoutMs;
            oReq.ReadWriteTimeout = program.iProbeTimeoutMs;
            using (var oResp = (HttpWebResponse)oReq.GetResponse()) {
                string sType = (oResp.ContentType ?? "").ToLowerInvariant();

                // A page, not a file. Settled without reading the body.
                if (sType.StartsWith("text/html") ||
                    sType.StartsWith("application/xhtml")) {
                    sResult = "";
                } else {
                    string sFromHeader = Web.fileFromDisposition(oResp.Headers["Content-Disposition"]);
                    if (sFromHeader.Length > 0) {
                        sResult = Web.sanitizeName(sFromHeader);
                    } else {
                        string sName = "";
                        try { sName = Path.GetFileName(oResp.ResponseUri.LocalPath); } catch { }
                        if (sName.Length == 0) sName = "download";
                        if (Path.GetExtension(sName).Length <= 1) {
                            string sMimeExt = Web.mimeToExt(oResp.ContentType);
                            if (sMimeExt.Length > 0) sName = sName + "." + sMimeExt;
                        }
                        sResult = Web.sanitizeName(sName);
                    }
                }
            }
        } catch {
            sResult = "";
        }

        lock (dProbeCache) { dProbeCache[sKey] = sResult; }
        return sResult;
    }

    // The name to save under, for a link already known to match.
    public static string fileNameForUrl(string sUrl) {
        string sName = quickNameForUrl(sUrl);
        if (sName.Length > 0 && Path.GetExtension(sName).Length > 1) return Web.sanitizeName(sName);
        string sProbed = probeName(sUrl);
        if (sProbed.Length > 0) return sProbed;
        return sName.Length > 0 ? Web.sanitizeName(sName) : "download";
    }

    // Turn a page title into a folder name, following urlCheck's rules.
    //
    // The name is meant to be read by a person browsing the output in File
    // Explorer, so original capitalization and the spaces between words are
    // preserved rather than being flattened into dashes: "Home | American
    // Foundation for the Blind" reads better than "home-american-foundation".
    // Only what Windows actually forbids is removed.
    public static string folderNameFromTitle(string sTitle) { return folderNameFromTitle(sTitle, null); }

    public static string folderNameFromTitle(string sTitle, string sParent) {
        string sBase = (sTitle ?? "").Trim();
        if (sBase.Length == 0) sBase = program.sFallbackTitle;

        // Characters Windows forbids in a folder name, plus controls.
        string sName = Regex.Replace(sBase, "[<>:\"/\\\\|?*\\x00-\\x1f]", "");
        // Collapse internal whitespace runs so a title containing newlines or
        // tabs does not leave multi-space gaps.
        sName = Regex.Replace(sName, "\\s+", " ").Trim();
        // Trailing dots and spaces are illegal at the end of a folder name.
        while (sName.EndsWith(".") || sName.EndsWith(" ")) sName = sName.Substring(0, sName.Length - 1);

        // Reserved device names would make an uncreatable folder.
        string sCheck = sName.ToUpperInvariant().Split('.')[0];
        var oReserved = new HashSet<string>(new string[] {
            "CON","PRN","AUX","NUL",
            "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
            "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9" });
        if (oReserved.Contains(sCheck)) sName = "_" + sName;

        if (sName.Length == 0) sName = program.sFallbackTitle;

        // Trim only as much as the file system requires. A component cannot
        // exceed 255 characters, and the whole path -- folder plus the files
        // that will sit inside it -- has to remain creatable.
        int iLimit = program.iMaxPathComponent;
        if (!string.IsNullOrEmpty(sParent)) {
            int iRoom = program.iPathBudget - sParent.Length - 1 - program.iFileNameAllowance;
            if (iRoom < iLimit) iLimit = iRoom;
            if (iLimit < 8) iLimit = 8;   // never trim away to nothing
        }
        if (sName.Length > iLimit) {
            logger.info("Folder name trimmed to " + iLimit + " characters to fit the path limit.");
            sName = sName.Substring(0, iLimit);
        }

        while (sName.EndsWith(".") || sName.EndsWith(" ")) sName = sName.Substring(0, sName.Length - 1);
        if (sName.Length == 0) sName = program.sFallbackTitle;
        return sName;
    }

    // Decide the per-page folder under the output directory. Returns "" to
    // mean "skip this source": the folder is already there from an earlier
    // run and --force was not given, so previous downloads are preserved.
    // With --force the folder is emptied and reused.
    public static string chooseTargetDir(string sParent, string sTitle, bool bForce) {
        string sDir = Path.Combine(sParent, folderNameFromTitle(sTitle, sParent));
        if (Directory.Exists(sDir)) {
            if (!bForce) return "";
            // Remove it outright rather than emptying it. If this run then
            // finds nothing to download, no hollow folder is left behind.
            try {
                Directory.Delete(sDir, true);
                logger.info("Removed existing folder (--force): " + sDir);
            } catch (Exception ex) {
                logger.warn("Could not remove '" + sDir + "': " + ex.Message);
            }
        }
        // Deliberately NOT created here. A page with nothing to download
        // should leave nothing behind; an empty folder is just noise for
        // someone browsing the results. See ensureDir.
        return sDir;
    }

    // Create the page folder, at the last possible moment: the first time a
    // file is actually about to be written into it.
    public static bool ensureDir(string sDir) {
        if (string.IsNullOrEmpty(sDir)) return false;
        try {
            if (!Directory.Exists(sDir)) {
                Directory.CreateDirectory(sDir);
                logger.info("Created folder: " + sDir);
            }
            return true;
        } catch (Exception ex) {
            logger.error("Could not create '" + sDir + "': " + ex.Message);
            program.notify("Could not create the folder '" + sDir + "': " + ex.Message);
            return false;
        }
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
            // Edge signs a brand-new profile into the Windows account by
            // default and immediately begins syncing. That is reasonable for
            // a browser the user is adopting; it is entirely wrong for a
            // throwaway profile that exists to fetch a file, and it produced
            // a sync notification on every run. These switches shut down
            // sign-in, sync, and the background services that drive them.
            //
            // The --disable-features list is best-effort: the Chromium
            // entries are long-standing, while Edge's implicit sign-in
            // feature has been named differently across versions, so several
            // spellings are passed. An unrecognized feature name is ignored
            // rather than rejected, so listing extras is safe.
            "--disable-sync",
            "--disable-background-networking",
            "--disable-client-side-phishing-detection",
            "--disable-default-apps",
            "--no-service-autorun",
            "--metrics-recording-only",
            "--disable-features=msImplicitSignin,msEdgeImplicitSignin," +
                "EdgeAutoSignIn,SyncPromo,SigninPromo,PrivacySandboxSettings4," +
                "SearchEngineChoiceScreen",
            "--mute-audio",
            "--no-default-browser-check",
            "--no-first-run",
            "--window-size=" + program.iDefaultViewportWidth + "," + program.iDefaultViewportHeight,
            "--remote-debugging-port=0",
            "--user-data-dir=" + quote(sUserDataDir),
            "about:blank"
        };
        // A fresh temporary profile should behave like a clean browser. Edge
        // will still load and UPDATE any extension that policy or a bundled
        // installer drops into a new profile, which is slow, produces windows
        // the user has to dismiss, and has nothing to do with downloading a
        // file. Suppress it — but only for the ephemeral profile, since -m is
        // meant to reproduce the user's real browsing environment faithfully.
        if (!bMainProfile) {
            lArgs.Insert(0, "--disable-extensions");
            lArgs.Insert(0, "--disable-component-update");
        }
        if (bHeadless) lArgs.Insert(0, "--headless=new");

        logger.info("Edge executable: " + sEdgeExe);
        logger.info("Profile mode: " + (bMainProfile ? "main profile" : "temporary profile"));
        logger.info("User data dir: " + sUserDataDir);
        logger.info("Headless: " + (bHeadless ? "yes" : "no"));
        logger.info("Edge command line: " + string.Join(" ", lArgs));
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
                        logger.info("DevTools ready after " +
                            oWatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) +
                            " s; port " + iDebugPort + ", browser ws " + sWsBrowserUrl);
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

    // Terminate a whole process tree. Edge is not one process: the one we
    // start spawns renderer, GPU, and utility children, and killing only the
    // parent can leave windows on screen. Worse, the process we start often
    // exits almost at once after handing work to another, so HasExited says
    // "gone" while a window is still sitting there -- which is exactly the
    // state that leaves a user unsure whether urlFido has finished.
    static void killTree(int iPid) {
        try {
            var oInfo = new ProcessStartInfo("taskkill", "/PID " + iPid + " /T /F");
            oInfo.CreateNoWindow = true;
            oInfo.UseShellExecute = false;
            var oKill = Process.Start(oInfo);
            if (oKill != null) oKill.WaitForExit(5000);
            logger.info("Terminated Edge process tree " + iPid + ".");
        } catch (Exception ex) {
            logger.warn("Could not terminate process tree " + iPid + ": " + ex.Message);
        }
    }

    public static void shutdown() {
        // Under --main-profile the browser is the user's own. Closing it
        // would take away their tabs and their session, so it is left alone
        // and the summary says so rather than leaving them to wonder.
        if (program.bMainProfile) {
            logger.info("Main profile in use: Edge left running deliberately.");
            oEdgeProcess = null;
            return;
        }
        try {
            if (oEdgeProcess != null) {
                int iPid = -1;
                try { iPid = oEdgeProcess.Id; } catch { }
                // Browser.close was already sent, so give it a moment to go
                // quietly before anything is forced.
                if (!oEdgeProcess.HasExited) oEdgeProcess.WaitForExit(3000);
                if (iPid > 0) killTree(iPid);
            }
        } catch (Exception ex) {
            logger.warn("Edge shutdown: " + ex.Message);
        }
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
        // Belt and braces alongside the command-line switches: writing these
        // preferences before first launch means Edge never reaches the state
        // where it would offer to sign in or sync in the first place.
        var d = new Dictionary<string, object> {
            { "plugins", new Dictionary<string, object> {
                { "always_open_pdf_externally", true } } },
            { "download", new Dictionary<string, object> {
                { "prompt_for_download", false },
                { "default_directory", sDownloadDir } } },
            { "signin", new Dictionary<string, object> {
                { "allowed", false },
                { "allowed_on_next_startup", false } } },
            { "sync", new Dictionary<string, object> {
                { "requested", false },
                { "has_setup_completed", false } } },
            { "credentials_enable_service", false },
            { "browser", new Dictionary<string, object> {
                { "has_seen_welcome_page", true } } },
            { "profile", new Dictionary<string, object> {
                { "password_manager_enabled", false },
                { "exit_type", "Normal" },
                { "exited_cleanly", true } } }
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
        var oNav = Stopwatch.StartNew();
        var dLoad = oPage.waitForEvent("Page.loadEventFired", null, program.iDefaultNavTimeoutMs);
        logger.info("Load event after " +
            (oNav.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) +
            " s for " + sUrl + (dLoad == null ? " (TIMED OUT)" : ""));
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
        // A person typing "jpg" or "js" means "the pictures and scripts on
        // this page". They neither know nor should have to know that a
        // picture arrives through img src, or srcset, or a lazy-loading
        // data-src, or a CSS background rule, while a script arrives through
        // script src. So every way a page can name a file is harvested, not
        // just the anchors a link checker would care about.
        const string sScript =
            "(function(){" +
            "var s=new Set();" +
            "function add(u){try{if(!u)return;u=String(u).trim();" +
            "if(!u||u.charAt(0)==='#')return;" +
            "if(/^(javascript|mailto|tel|data|blob|about):/i.test(u))return;" +
            "s.add(new URL(u,document.baseURI).href);}catch(e){}}" +
            // srcset and imagesrcset are comma-separated candidate lists,
            // each entry a url followed by an optional descriptor.
            "function addSet(v){if(!v)return;String(v).split(',').forEach(function(p){" +
            "add(p.trim().split(/\\s+/)[0]);});}" +
            // Anything a url can hang off, including the data-* attributes
            // that lazy-loading libraries use in place of the real ones.
            "var aAttr=['href','src','data','poster','data-src','data-href'," +
            "'data-original','data-lazy','data-lazy-src','data-url','data-file','content'];" +
            "Array.prototype.forEach.call(document.querySelectorAll('*'),function(e){" +
            "aAttr.forEach(function(k){if(e.hasAttribute&&e.hasAttribute(k)){" +
            "var v=e.getAttribute(k);" +
            // meta content is only a url when the tag is one that carries one.
            "if(k==='content'){var n=(e.getAttribute('property')||e.getAttribute('name')||'');" +
            "if(!/image|url|audio|video/i.test(n))return;}" +
            "add(v);}});" +
            "addSet(e.getAttribute&&e.getAttribute('srcset'));" +
            "addSet(e.getAttribute&&e.getAttribute('data-srcset'));" +
            "addSet(e.getAttribute&&e.getAttribute('imagesrcset'));" +
            // currentSrc resolves what the browser actually chose.
            "if(e.currentSrc)add(e.currentSrc);" +
            // Background images set inline or by a class.
            "try{var bg=window.getComputedStyle(e).backgroundImage;" +
            "if(bg&&bg!=='none'){var m=bg.match(/url\\((.*?)\\)/g);" +
            "if(m)m.forEach(function(u){add(u.slice(4,-1).replace(/[\"']/g,''));});}}catch(e2){}" +
            "});" +
            // Rules inside same-origin stylesheets: fonts, sprites, icons.
            "try{Array.prototype.forEach.call(document.styleSheets,function(ss){" +
            "try{Array.prototype.forEach.call(ss.cssRules,function(r){" +
            "var t=r.cssText||'';var m=t.match(/url\\((.*?)\\)/g);" +
            "if(m)m.forEach(function(u){add(u.slice(4,-1).replace(/[\"']/g,''));});" +
            "});}catch(e3){}});}catch(e4){}" +
            "return JSON.stringify(Array.from(s));})()";
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
    static bool bDownloadedAny = false;

    // Point browser-managed downloads at a directory. Re-issued for every
    // source, because each source writes into its own folder.
    public static void setDownloadDir(string sDir) {
        if (oBrowser == null) return;
        oBrowser.send("Browser.setDownloadBehavior", new Dictionary<string, object> {
            { "behavior", "allowAndName" },
            { "downloadPath", sDir },
            { "eventsEnabled", true }
        });
        logger.info("Browser downloads directed to " + sDir);
    }
    static cdpClient oPage = null;
    static readonly HashSet<string> setSeenDomains =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static int runAll(List<string> lSources, List<string> lPatterns) {
        var lRegexes = patternParser.compile(lPatterns);
        guiProgress.open();
        guiProgress.status("Launching Edge...");
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
            // Order matters: Browser.close travels over the CDP socket, so it
            // has to go before disconnect(). A graceful close lets Edge write
            // out its profile and shut its windows properly; the forced
            // termination in shutdown() is only the backstop.
            closeBrowser();
            disconnect();
            guiProgress.status("Closing Edge...");
            edgeLauncher.shutdown();
            guiProgress.close();
            if (program.bMainProfile) {
                program.notify("Edge was left open because your main profile was used.");
            }
        }
    }

    // Ask Edge to close every window of its own accord.
    static void closeBrowser() {
        if (program.bMainProfile) return;   // not ours to close
        try {
            if (oBrowser != null) {
                oBrowser.send("Browser.close", new Dictionary<string, object>());
                logger.info("Sent Browser.close.");
                Thread.Sleep(300);          // let the windows actually go
            }
        } catch (Exception ex) {
            logger.debug("Browser.close did not succeed: " + ex.Message);
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
            logger.info("Browser CDP connected: " + edgeLauncher.sWsBrowserUrl);
            logger.info("Page CDP endpoint: " + sPageWs);
            oPage = new cdpClient();
            oPage.connect(sPageWs);
            pageDriver.preparePage(oPage);
            // Route downloads into the output directory, named by guid so we
            // control the final names; enable progress events.
            setDownloadDir(program.sOutputDir);
            logger.info("Download behavior set to allowAndName into " + program.sOutputDir);
            return true;
        } catch (Exception ex) {
            logger.error("CDP connect failed: " + ex.ToString());
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
        guiProgress.status("Opening " + sUrl + "...");

        if (!pageDriver.navigate(oPage, sUrl)) {
            results.addSourceFailure(sUrl);
            return;
        }

        pauseForAuthenticationIfNeeded(sUrl);

        string sTitle = pageDriver.getTitle(oPage);
        program.sCurrentPageUrl = sUrl;
        if (sTitle != "") program.notify("Page title: " + sTitle);

        // Each source gets its own folder under the output directory, named
        // after the page title, so several sources in one run produce output
        // a person can tell apart at a glance. This happens BEFORE the
        // examination work, so a source that is going to be skipped costs
        // nothing beyond loading the page.
        program.sTargetDir = urlHelper.chooseTargetDir(program.sOutputDir, sTitle, program.bForce);
        if (program.sTargetDir.Length == 0) {
            program.notify("Already downloaded to \"" +
                urlHelper.folderNameFromTitle(sTitle) +
                "\"; skipping. Check \"Force overwrite\" to replace it.");
            results.addSkipped(urlHelper.folderNameFromTitle(sTitle));
            return;
        }
        // Browser-managed downloads land wherever the browser was last told,
        // so the destination has to be re-stated for each source.
        try {
            downloadEngine.setDownloadDir(program.sTargetDir);
        } catch (Exception ex) {
            logger.warn("Could not retarget browser downloads: " + ex.Message);
        }

        // Working out what a page offers happens in two passes.
        //
        // The first is free: where the address itself carries a file name,
        // that name is matched directly, with no network traffic at all.
        //
        // The second asks the server about the links the address could not
        // settle — the /download?id=42 case, where only the server knows a
        // PDF is coming. Knowing what a page really offers is the whole point
        // of the program, so this is not optional and there is no switch for
        // it. It is made affordable instead:
        //
        //   * Links that cannot yield a file are ruled out by inspection
        //     first — non-web schemes, bare origins, directory paths, page
        //     extensions, and duplicates that differ only by #fragment.
        //   * The remainder are asked in parallel rather than one at a time,
        //     which is the difference between seconds and minutes.
        //   * Each question is a HEAD request, so a server answering
        //     text/html settles the matter from its headers without sending
        //     a page body.
        //   * Answers are cached, so an address linked several times on one
        //     page is asked about once.
        //
        // Progress is reported throughout, because the honest answer to "how
        // long will this take" is "it depends on the site", and a user
        // should never be left wondering whether the program has stopped.
        var lLinks = pageDriver.harvestLinks(oPage);
        logger.info("Harvested " + lLinks.Count + " links from " + sUrl);
        guiProgress.status(Util.stringPlural("link", lLinks.Count) + " found; examining...");

        var lMatches = new List<string>();
        var lAsk = new List<string>();
        var oSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int iAskedCount = 0, iFileCount = 0;

        if (patternParser.isMatch(lRegexes, urlHelper.quickNameForUrl(sUrl))) lMatches.Add(sUrl);

        foreach (string sLink in lLinks) {
            string sBare = urlHelper.stripFragment(sLink);
            if (!oSeen.Add(sBare)) continue;                 // same address already considered

            if (urlHelper.hasExtensionInPath(sLink)) {
                string sCandidate = urlHelper.quickNameForUrl(sLink);
                if (patternParser.isMatch(lRegexes, sCandidate)) {
                    logger.info("MATCH   " + sCandidate + "  <-  " + sLink);
                    lMatches.Add(sLink);
                } else {
                    logger.debug("no match " + sCandidate + "  <-  " + sLink);
                }
            } else if (urlHelper.isWorthAsking(sLink)) {
                lAsk.Add(sBare);
            } else {
                logger.debug("cannot yield a file: " + sLink);
            }
        }

        if (lAsk.Count > 0) {
            logger.info("Asking " + lAsk.Count + " server(s) what " +
                (lAsk.Count == 1 ? "this address" : "these addresses") + " would send.");
            var aFound = new string[lAsk.Count];
            int iDone = 0;
            var oClock = Stopwatch.StartNew();

            // The requests run on worker threads; progress is reported from
            // this thread, because guiProgress pumps the message queue and
            // that must not happen anywhere but the thread owning the window.
            var oWork = Task.Factory.StartNew(() =>
                Parallel.For(0, lAsk.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = program.iProbeConcurrency },
                    i => {
                        aFound[i] = urlHelper.probeName(lAsk[i]);
                        Interlocked.Increment(ref iDone);
                        // Pace the workers. With four of them pausing a
                        // quarter second each, a server sees roughly sixteen
                        // requests a second at most, which no ordinary site
                        // treats as abuse.
                        try { Thread.Sleep(program.iRequestGapMs); } catch { }
                    }));

            while (!oWork.Wait(150)) {
                guiProgress.update("Examining links", Thread.VolatileRead(ref iDone) + 1, lAsk.Count);
            }
            guiProgress.update("Examining links", lAsk.Count + 1, lAsk.Count);

            int iFiles = 0;
            for (int i = 0; i < lAsk.Count; i++) {
                if (aFound[i].Length == 0) { logger.debug("not a file: " + lAsk[i]); continue; }
                iFiles++;
                if (patternParser.isMatch(lRegexes, aFound[i])) {
                    logger.info("MATCH   " + aFound[i] + "  <-  " + lAsk[i]);
                    lMatches.Add(lAsk[i]);
                } else {
                    logger.debug("is " + aFound[i] + " (no match)  <-  " + lAsk[i]);
                }
            }
            iAskedCount = lAsk.Count; iFileCount = iFiles;
            logger.info("Examined " + lAsk.Count + " address(es) in " +
                (oClock.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) +
                " s; " + iFiles + " turned out to be files.");
        }

        if (guiProgress.bCancelled) return;

        // The reader wants the shape of the result, not a description of how
        // it was reached: how many links, how many matched. The mechanics of
        // which links had to be asked about belong in the log.
        logger.info("Examined " + lLinks.Count + " link(s); asked about " +
            iAskedCount + "; " + iFileCount + " turned out to be files.");
        string sAccount = Util.stringPlural("link", lLinks.Count) + ", " +
            Util.stringPlural("match", lMatches.Count) + ".";
        program.notify(sAccount);
        if (lMatches.Count == 0) return;
        program.notify("Folder: " + Path.GetFileName(program.sTargetDir));

        int iN = 0;
        foreach (string sFileUrl in lMatches) {
            if (guiProgress.bCancelled) { program.notify("Cancelled."); return; }
            iN++;
            guiProgress.update(Path.GetFileName(urlHelper.quickNameForUrl(sFileUrl)),
                iN, lMatches.Count);
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
        if (program.bSimulate) {
            // Deliberately before ensureDir: a simulation must leave no trace.
            results.addWould(urlHelper.fileNameForUrl(sFileUrl), sFileUrl);
            return;
        }
        if (!urlHelper.ensureDir(program.sTargetDir)) return;
        // A short pause between files, for the same reason as between probes.
        if (bDownloadedAny) { try { Thread.Sleep(program.iDownloadGapMs); } catch { } }
        bDownloadedAny = true;
        string sName = urlHelper.fileNameForUrl(sFileUrl);
        var oWatch = Stopwatch.StartNew();

        // Without Force, an existing file of the same name is reported as
        // skipped rather than silently renamed, matching the extCheck and
        // 2htm convention for the Skipped section of the summary.
        if (!program.bForce) {
            try {
                if (File.Exists(Path.Combine(program.sTargetDir, sName))) {
                    results.addSkipped(sName);
                    program.notify("  Skipped (already present): " + sName);
                    return;
                }
            } catch { }
        }

        // Retrieval order matters to what the user SEES. Asking the browser
        // to download opens a throwaway tab per file, which flickers past and
        // leaves Edge showing pages the user never asked for — the only page
        // Edge should show is the one being scanned. A direct request opens
        // nothing, and because it replays the live session's cookies and
        // user agent it is just as authorized as the browser is. So the
        // direct request goes first, and the browser is the fallback for the
        // cases it cannot handle.
        logger.info("Download start: " + sFileUrl + " -> " + sName);
        long iBytes = httpFallback.download(oPage, sFileUrl, ref sName);
        if (iBytes < 0) {
            logger.info("Direct request failed for " + sFileUrl +
                "; falling back to a browser download.");
            iBytes = downloadViaBrowser(sFileUrl, ref sName);
            if (iBytes >= 0) logger.info("Browser download succeeded for " + sFileUrl);
        } else {
            logger.info("Direct request succeeded for " + sFileUrl);
        }
        logger.info("Download end: " + sName + ", " + iBytes + " bytes, " +
            (oWatch.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s");

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
                    string sTempPath = Path.Combine(program.sTargetDir, sGuid);
                    string sFinalPath = urlHelper.uniquePath(
                        program.sTargetDir, sName, program.bForce);
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

            // Present the request the way the browser would have presented it
            // had the user clicked the link. Sites commonly gate downloads on
            // the Referer, and some inspect the Sec-Fetch-* set that every
            // modern browser sends; a request missing them looks like a
            // scraper and gets a 403 or a login page instead of the file.
            if (program.sCurrentPageUrl != "") {
                try {
                    oReq.Referer = program.sCurrentPageUrl;
                    var oPageUri = new Uri(program.sCurrentPageUrl);
                    var oFileUri = new Uri(sFileUrl);
                    string sOrigin = oPageUri.Scheme + "://" + oPageUri.Authority;
                    bool bSameSite = string.Equals(oPageUri.Host, oFileUri.Host,
                        StringComparison.OrdinalIgnoreCase);
                    oReq.Headers["Sec-Fetch-Site"] = bSameSite ? "same-origin" : "cross-site";
                    if (!bSameSite) oReq.Headers["Origin"] = sOrigin;
                } catch { }
            }
            oReq.Headers["Sec-Fetch-Dest"] = "document";
            oReq.Headers["Sec-Fetch-Mode"] = "navigate";
            oReq.Headers["Sec-Fetch-User"] = "?1";
            oReq.Headers["Upgrade-Insecure-Requests"] = "1";
            oReq.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9," +
                "image/avif,image/webp,*/*;q=0.8";
            oReq.Headers[HttpRequestHeader.AcceptLanguage] = "en-US,en;q=0.9";
            try { oReq.AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate; } catch { }

            using (var oResp = (HttpWebResponse)oReq.GetResponse()) {
                logger.info("HTTP " + (int)oResp.StatusCode + " " + oResp.StatusCode +
                    ", type " + (oResp.ContentType ?? "(none)") +
                    ", length " + oResp.ContentLength + " for " + sFileUrl);
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
                    program.sTargetDir, sName, program.bForce);
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
// guiProgress — the modeless status form shown during a run in GUI mode.
//
// This reproduces the technique already proven in 2htm and extCheck, and the
// detail that makes it work for screen reader users is one line:
//
//     lblStatus.AccessibleRole = AccessibleRole.StatusBar;
//
// A plain Label carrying the StatusBar accessible role is what the JAWS
// read-status-bar command (Insert+PageDown) targets. Screen readers also
// announce changes to it as they happen, so progress is spoken without the
// user having to go looking. Application.DoEvents() pumps the message queue
// so the new text actually paints between units of work.
//
// The same three-method API as 2htm and extCheck — open / update / close —
// plus status() for the phases that have no meaningful denominator
// (launching Edge, loading a page). As in those programs, the displayed
// count reflects work ALREADY COMPLETED, not the item being started, so the
// user never sees 100% while the last file is still downloading.
//
// In CLI mode every method is a no-op for the window but STILL logs, so a
// -l log is equally detailed either way.
// ===========================================================================
static class guiProgress {
    static Form frm = null;
    static Label lblStatus = null;
    static string sLastLogged = "";
    public static volatile bool bCancelled = false;

    public static void open() {
        bCancelled = false;
        logger.info("Run started.");
        if (!program.bGuiMode) return;
        try {
            frm = new Form();
            frm.Text = program.sProgramName + " — working";
            frm.FormBorderStyle = FormBorderStyle.FixedDialog;
            frm.StartPosition = FormStartPosition.CenterScreen;
            frm.MaximizeBox = false;
            frm.MinimizeBox = false;
            frm.ControlBox = false;
            frm.ShowInTaskbar = true;
            frm.ClientSize = new System.Drawing.Size(480, 128);
            frm.Font = System.Drawing.SystemFonts.MessageBoxFont;

            var lblIntro = new Label();
            lblIntro.Text = "Downloading files. Please wait...";
            lblIntro.AutoSize = false;
            lblIntro.Location = new System.Drawing.Point(14, 14);
            lblIntro.Size = new System.Drawing.Size(452, 22);
            lblIntro.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            frm.Controls.Add(lblIntro);

            lblStatus = new Label();
            lblStatus.Text = "Starting...";
            lblStatus.AutoSize = false;
            lblStatus.Location = new System.Drawing.Point(14, 42);
            lblStatus.Size = new System.Drawing.Size(452, 22);
            lblStatus.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            lblStatus.AccessibleName = "Download status";
            lblStatus.AccessibleRole = AccessibleRole.StatusBar;
            frm.Controls.Add(lblStatus);

            // urlFido runs are network-bound and can be far longer than a
            // local file conversion, so unlike 2htm and extCheck this form
            // offers a way out. The button does not disturb the status
            // label or its accessible role.
            var btnCancel = new Button();
            btnCancel.Text = "&Cancel";
            btnCancel.AccessibleName = "Cancel";
            btnCancel.Location = new System.Drawing.Point(356, 76);
            btnCancel.Size = new System.Drawing.Size(110, 28);
            btnCancel.TabIndex = 0;
            btnCancel.UseVisualStyleBackColor = true;
            btnCancel.Click += (o, e) => {
                bCancelled = true;
                status("Cancelling — finishing the current file...");
            };
            frm.Controls.Add(btnCancel);

            frm.Show();
            Application.DoEvents();
        } catch (Exception ex) {
            logger.warn("Could not open the progress window: " + ex.Message);
            frm = null; lblStatus = null;
        }
    }

    // A phase with no denominator.
    public static void status(string sText) {
        if (string.IsNullOrEmpty(sText)) return;
        if (sText != sLastLogged) { logger.info("Status: " + sText); sLastLogged = sText; }
        if (frm == null || lblStatus == null) return;
        try { lblStatus.Text = sText; Application.DoEvents(); } catch { }
    }

    // A phase with a known total. iIndex is 1-based; the count and percent
    // show work DONE, matching 2htm and extCheck.
    public static void update(string sBase, int iIndex, int iTotal) {
        int iCompleted = iIndex - 1;
        int iPercent = iTotal > 0 ? (iCompleted * 100 / iTotal) : 0;
        string sText = sBase + " \u2014 " + iCompleted + " of " + iTotal + ", " + iPercent + "%";
        if (sText != sLastLogged) { logger.info("Status: " + sText); sLastLogged = sText; }
        if (frm == null || lblStatus == null) return;
        try { lblStatus.Text = sText; Application.DoEvents(); } catch { }
    }

    public static void close() {
        logger.info("Run finished.");
        if (frm == null) return;
        try { frm.Close(); frm.Dispose(); } catch { }
        frm = null; lblStatus = null;
        try { Application.DoEvents(); } catch { }
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

    // Test Fetch collects these instead of downloading. lsMatchedUrls is
    // listed last and in full, so Control+C on the message box yields a block
    // of addresses that can be pasted straight into a url list file.
    public static readonly List<string> lsWould = new List<string>();
    public static readonly List<string> lsMatchedUrls = new List<string>();
    public static void addWould(string sName, string sUrl) {
        lsWould.Add(sName);
        if (!lsMatchedUrls.Contains(sUrl)) lsMatchedUrls.Add(sUrl);
    }
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

    // Clear everything between runs. Test fetch can be pressed repeatedly,
    // and the results of one attempt must not bleed into the next.
    public static void reset() {
        oCapture.Length = 0;
        lsDownloaded.Clear();
        lsFailed.Clear();
        lsSkipped.Clear();
        lsWould.Clear();
        lsMatchedUrls.Clear();
        iTotalBytes = 0;
        iSourceFailures = 0;
    }
    public static string capturedText() { return oCapture.ToString(); }

    // Write the structured summary to the console (and therefore into the
    // GUI capture buffer and the log, via program.notify).
    // What a real run would do, and nothing more. Ends with the addresses
    // themselves so the whole box can be copied and the tail pasted into a
    // list file.
    public static void writeSimulation() {
        program.notify("");
        program.notify("SIMULATION -- nothing was downloaded and no folder was created.");
        program.notify("");
        if (lsWould.Count == 0) {
            program.notify("Nothing would be downloaded.");
        } else {
            program.notify("Would download " + Util.stringPlural("file", lsWould.Count) + ":");
            foreach (string sName in lsWould) program.notify("  " + sName);
        }
        if (iSourceFailures > 0) {
            program.notify("");
            program.notify("Could not load " + Util.stringPlural("source page", iSourceFailures) + ".");
        }
        if (lsMatchedUrls.Count > 0) {
            program.notify("");
            program.notify("Addresses (" + lsMatchedUrls.Count + "):");
            foreach (string sUrl in lsMatchedUrls) program.notify(sUrl);
        }
    }

    public static void writeSummary(string sOutputDir) {
        if (program.bSimulate) { writeSimulation(); return; }
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
// bark — urlFido's audio icon.
//
// Played once when the dialog is ready for input, in place of a spoken
// "ready" announcement. A short sound identifies the program faster than a
// sentence does, and it does not collide with whatever the screen reader is
// saying about the newly focused field.
//
// The audio is embedded in the executable, so the single-file property is
// preserved; SoundPlayer can play straight from the resource stream without
// anything being written to disk. Playback is asynchronous and every failure
// is swallowed: a machine with no sound device, or a build without the
// resource, must still show the dialog normally.
// ===========================================================================
static class bark {
    const string sResourceName = "urlFido.wav";
    static bool bPlayed = false;

    public static void ready() {
        if (bPlayed) return;      // once per run, not once per dialog loop
        bPlayed = true;
        play();
    }

    public static void play() {
        try {
            var oAsm = Assembly.GetExecutingAssembly();
            using (Stream oStream = oAsm.GetManifestResourceStream(sResourceName)) {
                if (oStream == null) {
                    logger.debug("No embedded " + sResourceName + "; audio icon skipped.");
                    return;
                }
                var oPlayer = new System.Media.SoundPlayer(oStream);
                oPlayer.Play();
                logger.debug("Audio icon played.");
            }
        } catch (Exception ex) {
            logger.debug("Audio icon unavailable: " + ex.Message);
        }
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
                // Three bands, each a label, its edit box, and -- where one
                // applies -- the button that fills it in, sharing a row. This
                // is LbC's band concept: related controls belong together on
                // one horizontal run, so Tab moves from a field straight to
                // the way of changing it rather than past everything else.
                dlg.addBand();
                TextBox tbSource = dlg.addInputBox("&Source urls:", sSource,
                    "One or more urls, local web pages, or text files listing one url per line, " +
                    "separated by spaces. Put double quotes around any item containing a space.");
                Button btnBrowse = dlg.addButton("&Browse source...",
                    "Choose a local web page or a text file of urls.");

                dlg.addBand();
                TextBox tbExt = dlg.addInputBox("File &extensions:", sExtensions,
                    "What to download, separated by spaces or commas. Bare extensions and " +
                    "cmd.exe wildcard patterns both work, for example pdf, *.pdf, or *newsletter*.pdf.");

                Button btnTest = dlg.addButton("&Test fetch...",
                    "Report what would be downloaded, without downloading anything.");

                dlg.addBand();
                TextBox tbOut = dlg.addInputBox("&Output directory:", sOutputDir,
                    "The parent directory. Each source gets its own folder inside it, " +
                    "named after the page title.");
                Button btnChoose = dlg.addButton("&Choose output...",
                    "Choose the directory to download into.");
                dlg.endBand();

                bark.ready();

                btnBrowse.Click += (o, e) => {
                    string sPicked = tbSource.Text;
                    if (browseSourceInto(ref sPicked)) {
                        tbSource.Text = sPicked;
                        tbSource.Focus();
                    }
                };
                btnTest.Click += (o, e) => {
                    // Run the real pipeline against the values on screen, but
                    // with bSimulate set so nothing is written. The dialog
                    // stays open afterwards, so the user can adjust and try
                    // again without starting over.
                    string sSaveExt = program.sExtensions;
                    string sSaveOut = program.sOutputDir;
                    bool bSaveInv = program.bInvisible;
                    try {
                        program.sExtensions = tbExt.Text.Trim();
                        program.sOutputDir = tbOut.Text.Trim();
                        program.bSimulate = true;
                        results.reset();
                        var lTest = urlHelper.expandSources(
                                program.splitSourceField(tbSource.Text).ToList());
                        if (lTest.Count == 0) {
                            MessageBox.Show(dlg.form, "No sources to test.",
                                program.sProgramName + " - Test fetch",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        } else {
                            downloadEngine.runAll(lTest,
                                patternParser.parse(program.sExtensions));
                            results.writeSummary(program.sOutputDir);
                            MessageBox.Show(dlg.form, results.capturedText(),
                                program.sProgramName + " - Test fetch",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    } catch (Exception ex) {
                        MessageBox.Show(dlg.form, "Test fetch failed: " + ex.Message,
                            program.sProgramName + " - Test fetch",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    } finally {
                        program.bSimulate = false;
                        program.sExtensions = sSaveExt;
                        program.sOutputDir = sSaveOut;
                        program.bInvisible = bSaveInv;
                        results.reset();
                        try { tbExt.Focus(); } catch { }
                    }
                };

                btnChoose.Click += (o, e) => {
                    using (var dlgFolder = new FolderBrowserDialog()) {
                        dlgFolder.Description = "Choose the output directory";
                        dlgFolder.ShowNewFolderButton = true;
                        try { dlgFolder.SelectedPath = getInitialBrowseDir(tbOut.Text); } catch { }
                        if (dlgFolder.ShowDialog(dlg.form) == DialogResult.OK) {
                            tbOut.Text = dlgFolder.SelectedPath;
                            tbOut.Focus();
                            Say.say("Output directory set");
                        }
                    }
                };

                dlg.addSeparator();
                CheckBox cbAuth = dlg.addCheckBox("&Authenticate credentials", bAuth,
                    "Pause at the first page of each site so you can sign in, accept cookies, " +
                    "or complete two-factor in the Edge window, then continue. Overrides Invisible.");
                CheckBox cbMain = dlg.addCheckBox("&Main profile", bMain,
                    "Use your real Edge profile so existing logins apply. Edge must be fully closed.");
                CheckBox cbInvisible = dlg.addCheckBox("&Invisible browser", bInvisible,
                    "Run Edge with no visible window. Ignored when Authenticate is checked.");
                CheckBox cbForce = dlg.addCheckBox("&Force overwrite", bForce,
                    "Overwrite existing files instead of adding a numeric suffix such as _001.");
                CheckBox cbView = dlg.addCheckBox("&View output", bView,
                    "Open the output directory in File Explorer when the run finishes.");
                CheckBox cbLog = dlg.addCheckBox("&Log session", bLog,
                    "Write a fresh " + program.sLogFileName + " to the output directory.");
                CheckBox cbUseCfg = dlg.addCheckBox("&Use configuration", bUseCfg,
                    "Load settings at startup and save them on OK, in " + program.sConfigFileName + ".");

                sButton = dlg.runWithButtons(new string[] {
                    "OK", "Default settings", "Cancel" });

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

            if (string.Equals(sButton, "Default settings", StringComparison.OrdinalIgnoreCase)) {
                // Restore the defaults, rather than emptying the fields and
                // relying on something else to fill them in later: the seeding
                // happens once before the dialog opens, so a blanked field
                // would simply stay blank for the rest of this dialog session.
                sSource = program.sDefaultSources;
                sExtensions = program.sDefaultExtensions;
                sOutputDir = program.defaultOutputDirForGui();
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
    // Fill the source field from a file picker. Called from the Browse source
    // button on the first band, so it works against the live text box rather
    // than closing and reopening the dialog.
    static bool browseSourceInto(ref string sSource) {
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
            Say.say("Source set");
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
            // Appends across runs so a sequence of attempts stays in one
            // place, which is what you want when chasing an intermittent
            // failure. --force replaces it instead, matching urlCheck.
            bool bAppend = !program.bForce && File.Exists(sPath);
            writer = new StreamWriter(sPath, bAppend, new UTF8Encoding(true));
            writer.AutoFlush = true;
            foreach (string sHeld in lPending) writer.WriteLine(sHeld);
            lPending.Clear();
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
    public static void debug(string sMsg) { write("DEBUG", sMsg); }
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

    // Messages logged before the output directory is known (configuration
    // load and save both happen before the dialog resolves it) are held here
    // and flushed when the file opens, so nothing is silently lost.
    static readonly List<string> lPending = new List<string>();

    static void write(string sLevel, string sMsg) {
        if (writer == null) {
            if (program.bLog && lPending.Count < 200)
                lPending.Add(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff") +
                    " [" + sLevel + "] " + sMsg);
            return;
        }
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
// ===========================================================================
// consoleWindow -- hide the console when urlFido was started from Explorer,
// a shortcut, or the Alt+Control+U hotkey.
//
// The test is GetConsoleProcessList, following extCheck and 2htm: if exactly
// ONE process is attached to the console, Windows created that console for
// urlFido alone, so hiding it removes a window nobody asked for. If two or
// more are attached, urlFido was run from an existing cmd.exe and that
// console belongs to the user -- hiding it would take away their shell.
// ===========================================================================
static class consoleWindow {
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] aiProcessIds, uint iCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    const int iSwHide = 0;

    public static bool launchedFromGui() {
        try {
            var aiList = new uint[16];
            uint iCount = GetConsoleProcessList(aiList, (uint) aiList.Length);
            return iCount == 1;
        } catch {
            return false;
        }
    }

    public static void hide() {
        try {
            IntPtr hwnd = GetConsoleWindow();
            if (hwnd != IntPtr.Zero) ShowWindow(hwnd, iSwHide);
        } catch { }
    }
}

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
