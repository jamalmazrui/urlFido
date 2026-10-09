---
title: "urlFido ReadMe"
author: "Jamal Mazrui"
---

# urlFido ReadMe

urlFido downloads files of the types you choose from web pages. You give it one or more pages and a list of extensions, such as `pdf docx`; for each page it finds every linked file that matches and saves it in a folder named after the page. It drives your installed Microsoft Edge, so pages that need scripts or a sign-in work as they do for you.

This is the quick start. The full guide is `help\urlFido.htm`.

## Install

1. Download `urlFido_setup.exe` from the [urlFido releases page](https://github.com/JamalMazrui/urlFido/releases).
2. Run it. It asks for administrator rights, because it installs for everyone on the computer.
3. On the last page, leave **Launch urlFido now** checked and press Finish. Read the results box, then close it; urlFido opens.

You need Windows 10 or 11, 64-bit. Edge is already part of Windows, and nothing else needs installing.

## Download from a page

1. Press **Alt+Control+U** from anywhere in Windows. The urlFido dialog opens with focus in **Source urls**, and Fido gives a short bark.
2. Type a web address, such as `https://www.itic.org/policy/accessibility/vpat`.
3. In **File extensions**, type what you want, such as `pdf docx`.
4. Press Enter.

A small window reports progress, and a results box says what was downloaded. The files are in a folder named after the page, inside the output directory (your Documents folder unless you choose another).

## From the command line

In a Command Prompt in the program folder:

```cmd
urlFido -e pdf https://www.itic.org/policy/accessibility/vpat
urlFido -e "pdf docx" -o "C:\Downloads" --view-output urls.txt
urlFido -h
```

## Keys in the dialog

- **Alt** with an underlined letter moves to that control.
- **Enter** or **Control+Enter** runs; **Escape** closes the dialog.
- **F1** shows Help.
- **F11** checks the web for a newer version of urlFido and offers to install it.

All the keys are listed in `help\Hotkeys.htm`.

## Learn by listening

Ten short spoken walks teach urlFido, each a few minutes long, in two voices: a host, and a screen reader saying what you would hear. They are installed in the `help\tutorials` folder as mp3 files, with a playlist, and as text in `Tutorials.htm`. Start with walk 0, the overview.

## When something goes wrong

Every run keeps a log in `%LOCALAPPDATA%\urlFido\logs`, one file per run. Zip that folder and send it with a description of what happened.

## License

urlFido is free and open source under the MIT License. See `License.htm`.
