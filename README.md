<p align="center"><img src="assets/banner.png" alt="Seek, instant file search for Windows"></p>

## Download

**[Download Seek.exe](https://github.com/MarkDemidovs/Seek/releases/latest/download/Seek.exe)** and run it. That's it. It works on Windows 10 and 11, with nothing else to install.

Windows may say it protected your PC, because the app isn't signed. Click **More info**, then **Run anyway**.

## Using it

Press **Alt+Space** anywhere. A search bar pops up in the middle of your screen. Type, and the 5 newest matching files show up below it. Pick one with the arrow keys and press **Enter** to open it.

If a result is a folder, press **Right arrow** or **Tab** to search only inside that folder. Press **Backspace** or **Left arrow** to go back.

Press **Esc** or click anywhere else to hide it.

## Searching

| Type | Finds |
| --- | --- |
| `.pdf` | every PDF, newest first |
| `thing.pdf` | files named thing.pdf first, then names that contain it |
| `thing*.pdf` | names that start with "thing" and end with ".pdf" |
| `thing*thing2.pdf` | "thing", then anything, then "thing2.pdf" |

`*` means any text and `?` means any one character. Upper and lower case don't matter, and neither do accents.

## How it works

1. Seek goes through every file and folder on your drives and saves a list of them in `%LOCALAPPDATA%\Seek\index.bin`, sorted from newest to oldest. The first time, this takes a moment.
2. When you type, Seek reads that list from the top. The newest files come first, so it can stop as soon as it finds 5 matches. Most searches take a few milliseconds.
3. While it runs, Seek watches your drives, so new, renamed and deleted files show up right away.
4. The list stays on disk instead of in memory, so Seek never uses more than 128 MB of RAM. While the bar is hidden it usually uses around 10 MB.
5. It skips places nobody searches, like the Recycle Bin, Windows system folders, `node_modules` and `.git`.

Seek lives in the tray and starts with Windows. You can turn that off from the tray menu.

## Build

Run `build.cmd`. It uses the C# compiler that already comes with Windows, so there is nothing to install. The app ends up in `bin\Seek.exe`.

The `tools` folder has the test programs used while building Seek.
