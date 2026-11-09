# ChatGPT Desktop MSIX Updater

**English** | [繁體中文](README.zh-TW.md)

An independent Windows utility that downloads the official ChatGPT MSIX directly from OpenAI and uses Windows PowerShell to install or update it for the current user. It supports x64 and ARM64 systems where Microsoft Store is unavailable and MSIX deployment is permitted. [OpenAI Windows deployment documentation](https://learn.chatgpt.com/docs/enterprise/windows-deployment)

## Install or update

1. Download `ChatGPT-Desktop-Updater.exe`, save it to a permanent location, and double-click it. [GitHub Releases](https://github.com/Lecanorchis/ChatGPT-desktop-updater/releases/latest)
2. Select **Check for updates and install**. The updater downloads the full MSIX for a first-time install or when a newer version is available. The window shows the download percentage.
3. When Windows prompts for administrator approval, elevate using **the same Windows account**. `Add-AppxPackage` installs the app for the account running the command. Save your work before updating. Windows closes any running ChatGPT processes when it installs the verified package.

## Interface settings

- **Set schedule** offers checks at sign-in, daily at a chosen time, or both. Both options start unchecked. Clear both and save to disable scheduled checks.
- **Other settings** lets you switch between English and Traditional Chinese. The updater defaults to English, automatically installs available updates, and closes itself and opens ChatGPT after a successful installation. Turn off automatic installation to compare the installed and available versions before choosing whether to update.
- The download folder defaults to the folder containing the EXE. The updater stores the MSIX in a temporary subfolder of the selected folder and cleans it up afterward. Preferences are saved to `%LOCALAPPDATA%\ChatGPTDesktopUpdater\settings.json`.

## Package verification

The updater requests one byte from the official HTTPS endpoint to read the package version, architecture, and ETag. It uses that ETag for the full download and verifies the file size and the version, architecture, and publisher in the MSIX manifest. Windows validates the package signature during deployment. If the endpoint omits the version metadata or changes the package between the check and download, the updater stops. Run the check again to retry.

The updater EXE and scripts are unsigned. Running them requires compliance with the device's application and PowerShell policies. Installation is subject to the organization's MSIX and dependency policies.

## Command line and scripts

Run these commands from the folder containing the updater. `Check` reports the installed and available versions without requesting administrator approval or downloading the full package.

```powershell
.\ChatGPT-Desktop-Updater.exe -Action Check
.\ChatGPT-Desktop-Updater.exe -Action Schedule -AtLogon -DailyAt 09:00
.\ChatGPT-Desktop-Updater.exe -Action Unschedule
```

To use the script, replace `.\ChatGPT-Desktop-Updater.exe` in those commands with `powershell.exe -NoProfile -File .\Install-ChatGPT.ps1`. For one-click installation from source, keep `Install-ChatGPT.cmd` beside `Install-ChatGPT.ps1` and double-click the CMD file.

- `-AtLogon` checks when the current account signs in. `-DailyAt` uses the Windows local clock. You can use either option or both.
- Scheduled runs check for updates while the account is signed in and write results to `%LOCALAPPDATA%\ChatGPTDesktopUpdater\checks.log`. To install an available update, open the updater.
- Saving a schedule replaces the existing schedule for the current account. If you move the updater, save the schedule again to update its path.

## Build the EXE

```powershell
powershell.exe -NoProfile -File .\Build.ps1
```

The build uses the Windows .NET Framework C# compiler to produce `dist\ChatGPT-Desktop-Updater.exe`. The EXE embeds the PowerShell script and runs it using Windows PowerShell 5.1.
