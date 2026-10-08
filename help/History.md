---
title: "urlFido History"
author: "Jamal Mazrui"
---

# urlFido History

## 8 October 2026 -- an audit by another AI

ChatGPT audited urlFido and reported 38 findings. Checked against the code, these held and are fixed:

- **Test fetch never touches your downloads.** It ran in simulate mode but kept the saved Force setting, so a test could delete a real collection; it also wrote its summary over the last real run's. A simulated run now deletes nothing and writes no summary file.
- **Force never loses a collection.** The old folder was deleted before the new run had fetched anything. It is now moved aside, and at the end of the run removed only when the new folder holds files, or else put back.
- **A sign-in page is not saved as a document.** A server answering with a web page where a file was asked for -- a sign-in or error page -- is refused, unless a web page was what was wanted.
- **No half-written files.** A download is written to a .part file and given its name only when complete.
- **Cookies stay with their site.** Redirects are followed by urlFido itself, and each hop carries only the cookies Edge holds for that hop's own address, as Edge would; one site's cookies no longer travel to another.
- **The output folder defaults to Documents again.** A fresh start showed an empty folder, since the dialog's field was filled before the default was set.
- **Kit tools** updated from HomerDev 1.63.3: push stops on a stale whitelist and reports a failed commit as one.

Left for later, as larger changes: an identity for each source beyond its page title, so two pages with one title never share a folder; a record that a download completed; a shared session for the probes of addresses without extensions; and cancel that answers during the longest waits.

## Version 1.2.0 (September 2026)

- **Setup.** The Results box at the end of setup is titled "urlFido Setup Results", and the finish page uses the Homer wording: the verb first, no "recommended", and "Launch urlFido (desktop hotkey ...)".
- **Built with HomerDev 1.43.19.** The build refreshes the kit's tools under their current names, and the ones that call each other now find each other; `scripts\tidy`, `scripts\check` and `scripts\release` carry the day's fixes, among them a release that publishes a draft and confirms it is GitHub's latest.
- The acceptance check that the installer ships the documents searched with a single backslash, which findstr reads as an escape, so it never matched; it is doubled now.
### What's new

- **Built on the Homer Development Kit.** urlFido compiles the kit's shared classes (Elevate, Inix, Lbc, Log, Paths, Say, Util, Web) from `C:\HomerDev\CSharp` instead of carrying its own copies, which had drifted. It follows the Homer layout: the program in `exec`, the documents in `help`, one log per run in `logs`.
- **F11 checks for a newer version.** In the dialog, F11 asks GitHub for the latest release. Yes is the default when a newer version exists and No when this one is current; Yes downloads `urlFido_setup.exe` and starts it. The Help box (F1) ends with the same check.
- **A session log, always.** Every run writes `%LOCALAPPDATA%\urlFido\logs\urlFido-yyyyMMdd-HHmmss.log` with the environment, which screen reader is running, every setting and every step; the 30 newest are kept. An error nothing else caught is recorded with its details, and the console names the log. `-l` (Log session) still also writes `urlFido.log` beside the downloads.
- **Settings file moved.** Use configuration now reads and writes `%LOCALAPPDATA%\urlFido\configs\urlFido.inix`. A `urlFido.inix` from an earlier version, directly under `%LOCALAPPDATA%\urlFido`, is read until the new file exists; the first save copies it, comments and all, and removes it.
- **The progress window's Cancel button is named once.** It no longer sets an accessible name repeating its own caption, which a screen reader could read twice.
- **Documents.** The ReadMe is a quick start; the full guide is `help\urlFido.md` and `.htm`. New Developer, Hotkeys and this History; Announce describes the current release. The key tables in the guide are lists.
- **Version from one place.** The version lives in `version.txt`, which the build steps and writes into the program as `BuildVersion.Version`; nothing else carries a number to keep in step.
- **The desktop hotkey is urlFido's alone.** urlCheck moved to Alt+Control+Shift+U in its 1.12.2, as planned when urlFido first shipped, so Alt+Control+U opens urlFido whichever was installed last.

### Installer

- The program installs to `exec`, the documents other than ReadMe and License to `help`, and `urlFido.cmd` at the top runs the program from a command prompt there.
- The finish page offers Launch (checked) and Open the user guide (unchecked). A results box says what was installed and where the logs are, and urlFido starts only after that box is closed, with its dialog and saved settings as the desktop shortcut starts it.
- A reinstall no longer asks for the folder; it goes where the last one went.
- The uninstaller removes the logs and the NVDA library urlFido may have written. Your saved settings stay, as before.

### For developers

- `buildUrlFido.cmd` is the kit's C# build template: kit version check, `version.txt` seeded and stepped, Roslyn found with vswhere or installed as the Build Tools, NVDA's controller client fetched from NV Access and embedded, the program built into `exec`, the kit's tools refreshed into `scripts`, documents converted, the Homer encoding applied, and every `help` file checked against the installer. It logs every command and exit code to `logs`.
- `nvdaControllerClient.dll` is fetched by the build; nobody has to put it beside the sources.
- `RepoFiles.txt` and `LocalFiles.txt` decide what git carries; built programs are no longer in the repository.

## Version 1.1.0

This release is about knowing what a page really offers, and about the dialog behaving the way Windows dialogs behave.

- **Link analysis is automatic.** urlFido works out what every link would actually deliver, including addresses like `/download?id=42` that reveal nothing until the server is asked. There is no setting to switch it on. It is kept quick instead: links that could not be files are ruled out by inspection, the rest are asked about in parallel with headers-only requests that stop as soon as a server says "web page", and answers are cached.
- **A folder per page.** The output directory is a parent; each source gets its own subfolder named after the page title, so a run over several sites produces output you can tell apart at a glance. A page already downloaded is skipped, preserving what you have, unless Force overwrite is set. A page with nothing to download leaves no folder at all.
- **Fido barks when he is ready.** The dialog announces itself with a single short woof instead of a spoken "ready" — quicker to recognize, and it does not talk over your screen reader. The sound is original to urlFido and embedded in the program.
- **Every kind of file, not just links.** Ask for `jpg`, `png`, `js`, or `css` and urlFido finds them, because it now collects every address a page names — images and their `srcset` and lazy-loading attributes, scripts, stylesheets, fonts, video and audio, background images, and urls inside stylesheet rules — rather than only the links. Whether a picture arrives through an `img` tag or a CSS rule is not something anyone should have to think about.
- **Downloads that look like clicks.** Each file is fetched with the browser's cookies and identity, a Referer naming the page it came from, and the Origin and Sec-Fetch headers a browser sends — so files that servers only release to a genuine click now arrive instead of a 403.
- **Test fetch.** A button beside the extensions field reports what a run would download, without downloading it. The report ends with the addresses themselves, so Control+C on the message box gives you a block ready to paste into a url list.
- **Nothing left open.** The browser is closed gracefully and, if anything survives, its whole process tree is ended, so a leftover window never leaves you wondering whether urlFido has finished. Your own browser under Main profile is deliberately left alone, and the summary says so.
- **No stray console window.** Started from the hotkey or a shortcut, urlFido now hides the console Windows gives it. Run from a command prompt, your shell is left alone.
- **A shorter summary.** The results report the number of links, the number of matches, and the names of what was downloaded. The rest went to the log.
- **Paced requests.** urlFido spreads its requests out so sites do not throttle or block it.
- **Progress you can hear.** The status line reports each step with a count and percentage, using the status-bar role that the JAWS read-status-bar command reads on demand.
- **Settings that stick.** Ticking Use configuration is now enough; the dialog reopens the way you left it, with no command-line switch required. On the command line `-u` is still needed, so scripts pick up no state they did not ask for.
- **Access keys throughout.** Every field and check box has one, and each matches its command-line switch. OK and Cancel deliberately have none: Cancel is Escape, OK is Enter or Control+Enter.
- **Only the page you asked for opens.** Downloads are retrieved directly, replaying the browser's cookies and identity, so no extra tab appears per file.
- **A quieter browser.** The temporary profile launches with extensions, component updates, sign-in, and sync all switched off.
- **Sensible defaults.** Sources start at the major blindness organizations; the output directory starts at Documents rather than wherever the program happened to be launched from.

## Version 1.0.0

The first release of urlFido, a 64-bit GUI/CLI hybrid tool that downloads files from web pages by extension.

### What it does

- **Download by extension, or by wildcard pattern.** Give urlFido one or more source pages and a list of what to fetch (default: `docx pdf zip`). Short forms escalate: `pdf` is short for `.pdf`, which is short for `*.pdf`. Because each entry is a cmd.exe-style pattern, you can also write `*newsletter*.pdf` to take only the newsletters, or `report?.xlsx` for a single-character match. Matching ignores case. Unix glob syntax is deliberately not supported, keeping the field predictable.
- **Real browser retrieval.** urlFido drives your installed Microsoft Edge to open each page, so scripts run, cookies apply, and JavaScript-generated links are found — files are far more likely to be reachable than with a browserless download. PDFs are saved to disk rather than opened in Edge's viewer.
- **Authenticate mode (`-a`).** Pause at the first page of each site so you can sign in, accept cookies, or complete two-factor in the visible Edge window, then continue; the session is reused for the rest of that site during the run. urlFido detaches and reattaches its automation channel around the pause to improve success on sites that resist automation.
- **Main-profile mode (`-m`).** Use your real Edge profile so existing logins apply. Requires Edge to be fully closed; urlFido checks and reports clearly if it is running.
- **Single executable.** urlFido.exe is one 64-bit file. It speaks the Chrome DevTools Protocol to Edge using only .NET Framework classes, so there is no Node.js driver and no bundled browser to ship. The shared Homer modules — Lbc.cs, Say.cs, Inix.cs, Util.cs, and Web.cs — are compiled into the same assembly rather than referenced as libraries. NVDA support is included too, with nothing to install alongside: because NVDA does not ship its controller client in the end-user installation, that native library is embedded in the executable as a resource and extracted to `%LOCALAPPDATA%\urlFido` on first use — and then only when NVDA is actually running, so users of JAWS, Narrator, or SAPI never see the file written and urlFido leaves no footprint of its own.
- **Dialog built on shared Lbc primitives.** The parameter dialog is constructed from the Homer Layout-by-Code classes, so it inherits the whole shared convenience set rather than reimplementing any of it: Control+Enter clicks OK from any control; Control+C copies the selection or, with nothing selected, the current line; Alt+C and Alt+X append to the clipboard; Control+D deletes a line; Alt+F8 reads all; Alt+Y gives line and character counts; and Shift+F1 speaks a focus tip written for every field. The Help button is generated from those same labels and tips. Improvements made to Lbc in DbDo, EdSharp, or FileDir reach urlFido by replacing one file.
- **CLI parallel to urlCheck.** The command-line switches and dialog controls mirror urlCheck as closely as the different purpose allows, so the two tools feel the same to use.
- **Structured results summary.** The end-of-run summary follows the 2htm and extCheck convention: up to three sections — Downloaded, Failed to download, Skipped — each shown only when its count is non-zero, singular or plural to match, failures given as `name: reason`, and the output directory on a closing line. GUI mode puts it in the final message box with the names listed; command-line mode prints the headings without repeating names that already scrolled by. A short spoken line gives the outcome at a glance.
- **Smart file naming, shared with FileDir.** urlFido calls the same Web helper FileDir's Web Download command uses. Names come from the url's final path segment; when the url carries no usable name, the server is asked, so links like `example.com/download?id=42` still get a proper name and extension from Content-Disposition or the MIME type. Ordinary links cost no extra request.
- **Configuration and logging.** With Use configuration (`-u`), settings persist at `%LOCALAPPDATA%\urlFido\urlFido.inix`, written through the shared Inix codec so the file round-trips in order and keeps any comments or extra sections you add by hand. Without the option, urlFido leaves nothing on disk. With Log session (`-l`), a fresh urlFido.log is written to the output directory. The build script now logs the full compiler output to `buildUrlFido.log` as well.
- **Desktop hotkey Alt+Control+U.** The installer adds a desktop shortcut on Alt+Control+U that opens the dialog from anywhere in Windows.
- **Camel Type coding standard.** All identifiers follow the project's Camel Type style, and shared-concept names match across the companion tools urlCheck, extCheck, and 2htm.

- **New icon.** urlFido now carries a dog-fetching-a-document mark: a dog's head in profile with a white page, folded corner and all, carried in its mouth. It is original artwork drawn as vector-style shapes and rendered to every icon size from 16 to 256 pixels, so nothing is licensed from a third party. The small sizes drop the pupil and the page's text lines, leaving the two shapes that still read at 16 pixels: the dark head and the white page.

- **A folder per page.** The output directory is a parent; each source gets its own subfolder named after the page title, so a run over several sites produces output you can tell apart at a glance. A page already downloaded is skipped, preserving what you have, unless Force overwrite is set. A page with nothing to download leaves no folder at all.
- **Fido barks when he is ready.** The dialog announces itself with a single short woof instead of a spoken "ready" — quicker to recognize, and it does not talk over your screen reader. The sound is original to urlFido and embedded in the program.
- **Every kind of file, not just links.** Ask for `jpg`, `png`, `js`, or `css` and urlFido finds them, because it now collects every address a page names — images and their `srcset` and lazy-loading attributes, scripts, stylesheets, fonts, video and audio, background images, and urls inside stylesheet rules — rather than only the links. Whether a picture arrives through an `img` tag or a CSS rule is not something anyone should have to think about.
- **Downloads that look like clicks.** Each file is fetched with the browser's cookies and identity, a Referer naming the page it came from, and the Origin and Sec-Fetch headers a browser sends — so files that servers only release to a genuine click now arrive instead of a 403.
- **Test fetch.** A button beside the extensions field reports what a run would download, without downloading it. The report ends with the addresses themselves, so Control+C on the message box gives you a block ready to paste into a url list.
- **Nothing left open.** The browser is closed gracefully and, if anything survives, its whole process tree is ended, so a leftover window never leaves you wondering whether urlFido has finished. Your own browser under Main profile is deliberately left alone, and the summary says so.
- **No stray console window.** Started from the hotkey or a shortcut, urlFido now hides the console Windows gives it. Run from a command prompt, your shell is left alone.
- **A shorter summary.** The results report the number of links, the number of matches, and the names of what was downloaded. The rest went to the log.
- **Paced requests.** urlFido spreads its requests out so sites do not throttle or block it.
- **Progress you can hear.** A run shows a status window whose status line names each step and counts files as they arrive. The line carries the status-bar accessible role — the same technique used in 2htm and extCheck — so screen readers announce changes automatically and the JAWS read-status-bar command reads it on demand. A Cancel button stops after the current file.
- **A log worth sending.** With `-l`, urlFido records the Edge command line, the profile in use, every link and whether it matched, each download attempt and which method served it, HTTP statuses, byte counts, and timings.
- **It works out what a link really is.** Where the address names a file, that is enough. Where it does not — `/download?id=42` and its kind — urlFido asks the server, always, with no switch to remember. Links that could not be files are ruled out first, the rest are asked in parallel with headers-only requests, and answers are cached, so knowing what a page truly offers costs seconds rather than minutes.
- **A clean browser every time.** The temporary profile launches with extensions, component updates, background networking, sign-in, and sync all switched off, so nothing from your everyday browser loads, updates, or interrupts — and Edge does not sign the throwaway profile into your account.
- **Only the page you asked for.** Files are retrieved with a direct request that replays the browser's cookies and identity, so no extra tab opens for each download. The browser download path remains as a fallback.
- **Source lists.** A source can be a url, a local web page, or a text file listing one url per line, with `#` or `;` comments — keep the pages you harvest from in a file and hand urlFido the file.

- **Settings that stick.** Tick Use configuration and the dialog reopens the way you left it — no command-line switch needed. On the command line `-u` is still required, so a script picks up no state it did not ask for.
