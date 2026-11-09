# ChatGPT Desktop MSIX Updater

[English](README.md) | **繁體中文**

這是獨立開發的 Windows 工具，可在 x64 或 ARM64 上直接從 OpenAI 下載官方 MSIX，透過 Windows PowerShell 為目前登入的帳號安裝或更新 ChatGPT。適用於 Microsoft Store 無法使用，但仍允許 MSIX 安裝的環境。[OpenAI Windows 部署文件](https://learn.chatgpt.com/docs/enterprise/windows-deployment)

## 一鍵安裝或更新

1. 從 GitHub 下載 `ChatGPT-Desktop-Updater.exe`，存到固定位置後雙擊。[GitHub Releases](https://github.com/Lecanorchis/ChatGPT-desktop-updater/releases/latest)
2. 在主畫面選擇 **Check for updates and install**（**檢查更新並安裝**）。需要更新或首次安裝時才下載完整 MSIX，視窗會顯示下載百分比。
3. 在 UAC 視窗用**同一個 Windows 帳號**提升權限。這個帳號須能提升為系統管理員，因為 `Add-AppxPackage` 會安裝到執行指令的帳號。下載與驗證完成後，Windows 安裝程序會關閉仍在執行的 ChatGPT。請先儲存工作。

## 介面設定

- **設定排程**可選擇登入目前帳號時檢查、每日指定時間檢查，或兩者同時啟用。首次使用時兩者皆未勾選。清除兩個勾選並儲存即可停用。
- **其它設定**可切換英語或繁體中文。預設使用英語，並啟用檢查後自動安裝、更新完成後關閉工具、更新完成後開啟 ChatGPT。取消自動安裝後，找到更新時會先顯示新舊版本供確認。
- 下載資料夾預設為 EXE 所在資料夾。MSIX 暫存在所選資料夾的獨立子資料夾，流程結束後清除。介面設定存放在 `%LOCALAPPDATA%\ChatGPTDesktopUpdater\settings.json`。

## 套件驗證

工具透過官方 HTTPS 端點的 1 byte 部分內容回應讀取套件版本、架構和 ETag。下載時固定使用該 ETag，並驗證檔案長度及 MSIX manifest 內的版本、架構與發行者。Windows 在部署時驗證套件簽章。官方端點若缺少版本資料或在檢查與下載之間更換檔案，工具會停止，可稍後重試。

工具和腳本目前皆未簽章。執行須符合裝置上的程式與 PowerShell 政策，安裝也須符合組織的 MSIX 及相依套件政策。

## 命令列與腳本

在工具所在資料夾開啟終端機執行以下指令。`Check` 只顯示版本與結果，不要求 UAC，也不下載完整套件。

```powershell
.\ChatGPT-Desktop-Updater.exe -Action Check
.\ChatGPT-Desktop-Updater.exe -Action Schedule -AtLogon -DailyAt 09:00
.\ChatGPT-Desktop-Updater.exe -Action Unschedule
```

使用腳本時，在相同指令中把 `.\ChatGPT-Desktop-Updater.exe` 換成 `powershell.exe -NoProfile -File .\Install-ChatGPT.ps1`。
若要直接使用一鍵安裝腳本，把 `Install-ChatGPT.cmd` 和 `Install-ChatGPT.ps1` 放在一起，雙擊 `.cmd`。

- `-AtLogon` 在開機後登入目前帳號時檢查。`-DailyAt` 使用 Windows 本機時間，可單獨使用任一選項或同時啟用。
- 排程只有在該帳號已登入時執行，結果寫入 `%LOCALAPPDATA%\ChatGPTDesktopUpdater\checks.log`。發現更新時，可雙擊工具安裝。
- 重新設定會替換同一帳號的舊排程。搬移工具後，請重新設定排程以更新路徑。

## 建置 EXE

```powershell
powershell.exe -NoProfile -File .\Build.ps1
```

使用 Windows 內的 .NET Framework C# 編譯器產生 `dist\ChatGPT-Desktop-Updater.exe`，無須安裝 .NET SDK。EXE 內嵌同一支 PowerShell 腳本，執行時由系統內建的 Windows PowerShell 5.1 執行。
