using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class Preferences
{
    private static readonly string FileName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChatGPTDesktopUpdater", "settings.json");

    internal static readonly string DefaultFolder = Path.GetDirectoryName(PowerShellWorker.Executable);
    public bool Chinese { get; set; }
    public bool AutoInstall { get; set; }
    public bool CloseAfterUpdate { get; set; }
    public bool OpenChatGPT { get; set; }
    public string DownloadFolder { get; set; }

    public Preferences()
    {
        AutoInstall = true;
        CloseAfterUpdate = true;
        OpenChatGPT = true;
    }

    internal string GetDownloadFolder()
    {
        return String.IsNullOrEmpty(DownloadFolder) ? DefaultFolder : DownloadFolder;
    }

    internal static Preferences Load()
    {
        try
        {
            if (File.Exists(FileName))
                return new JavaScriptSerializer().Deserialize<Preferences>(File.ReadAllText(FileName)) ?? new Preferences();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        return new Preferences();
    }

    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileName));
        string temporary = FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(this));
            if (File.Exists(FileName))
                File.Replace(temporary, FileName, null);
            else
                File.Move(temporary, FileName);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

internal sealed class UpdaterForm : Form
{
    private static readonly Font EnglishFont = new Font("Segoe UI", 10);
    private static readonly Font ChineseFont = new Font("Microsoft JhengHei UI", 10);
    private Preferences preferences = Preferences.Load();
    private readonly Button check = NewButton();
    private readonly Button schedule = NewButton();
    private readonly Button settings = NewButton();
    private readonly Label reminder = NewLabel("");
    private readonly Label status = NewLabel("");
    private readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Top, Height = 20 };
    private bool busy;

    internal UpdaterForm()
    {
        ConfigureWindow(this);
        StartPosition = FormStartPosition.CenterScreen;
        var body = NewBody(460);
        body.Controls.Add(check);
        body.Controls.Add(reminder);
        body.Controls.Add(schedule);
        body.Controls.Add(settings);
        body.Controls.Add(status);
        body.Controls.Add(progress);
        Controls.Add(body);
        AcceptButton = check;
        check.Click += async delegate { await RunBusy(CheckAndInstall); };
        schedule.Click += async delegate { await RunBusy(EditSchedule); };
        settings.Click += delegate { EditPreferences(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e)
        {
            if (busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        };
        ApplyLanguage();
        ShowReady();
    }

    private string T(string english, string chinese)
    {
        return preferences.Chinese ? chinese : english;
    }

    private void ApplyLanguage()
    {
        Font = preferences.Chinese ? ChineseFont : EnglishFont;
        Text = T("ChatGPT Desktop Updater", "ChatGPT 桌面版更新工具");
        check.Text = T("Check for updates and install", "檢查更新並安裝");
        reminder.Text = T("Updating closes ChatGPT. Please save your work first.", "更新時會關閉 ChatGPT 程序，請先儲存工作狀態。");
        schedule.Text = T("Set schedule", "設定排程");
        settings.Text = T("Other settings", "其它設定");
    }

    private void ShowReady()
    {
        status.Text = T("Ready", "就緒");
        progress.Style = ProgressBarStyle.Blocks;
        progress.Value = 0;
    }

    private async Task RunBusy(Func<Task<bool>> work)
    {
        busy = true;
        check.Enabled = schedule.Enabled = settings.Enabled = false;
        bool close = false;
        try { close = await work(); }
        catch (Win32Exception error)
        {
            if (error.NativeErrorCode == 1223)
                ShowMessage(T("Update canceled", "已取消更新"), T("Administrator approval was canceled.", "已取消系統管理員權限確認。"));
            else
                ShowError(error);
        }
        catch (Exception error) { ShowError(error); }
        finally
        {
            busy = false;
            check.Enabled = schedule.Enabled = settings.Enabled = true;
            ShowReady();
        }
        if (close) Close();
    }

    private async Task<bool> CheckAndInstall()
    {
        status.Text = T("Checking for updates…", "正在檢查更新…");
        progress.Style = ProgressBarStyle.Marquee;
        WorkerEvent result = await PowerShellWorker.RunAsync(new[] { "-Action", "Check" }, false, null);
        if (!result.NeedsUpdate) return ShowCurrent(result.Installed);
        progress.Style = ProgressBarStyle.Blocks;
        status.Text = T("Update available", "有可用更新");

        string versions = T("Installed version: ", "目前版本：") +
            (String.IsNullOrEmpty(result.Installed) ? T("Not installed", "尚未安裝") : result.Installed)
            + Environment.NewLine + T("Available version: ", "可用版本：") + result.Available;
        if (!preferences.AutoInstall && ShowMessage(T("Update available", "有可用更新"), versions,
            T("Update", "更新"), T("OK", "確定"), DialogResult.Cancel) != DialogResult.OK)
            return false;

        status.Text = T("Waiting for administrator approval…", "正在等候系統管理員權限確認…");
        var events = new Progress<WorkerEvent>(ShowProgress);
        result = await PowerShellWorker.RunAsync(new[] { "-Action", "Update", "-ExpectedVersion", result.Available,
            "-DownloadFolder", preferences.GetDownloadFolder() }, true, events);
        if (result.Kind == "VersionChanged")
        {
            ShowMessage(T("Available version changed", "可用版本已變更"),
                T("Check for updates again to get the current version.", "請重新檢查更新，取得目前的可用版本。"));
            return false;
        }
        if (!result.Updated) return ShowCurrent(result.Installed);

        progress.Style = ProgressBarStyle.Blocks;
        progress.Value = 100;
        status.Text = T("Installed version: ", "已安裝版本：") + result.Installed;
        string launchError = null;
        if (preferences.OpenChatGPT)
        {
            try { OpenChatGPT(result.AppId); }
            catch (Exception error)
            {
                launchError = Environment.NewLine + T("ChatGPT could not be opened. ", "無法開啟 ChatGPT。") + error.Message;
            }
        }
        if (!preferences.CloseAfterUpdate || launchError != null)
            ShowMessage(T("Update complete", "更新完成"), status.Text + launchError);
        return preferences.CloseAfterUpdate;
    }

    private void ShowProgress(WorkerEvent message)
    {
        if (message.Kind == "Download")
        {
            progress.Style = ProgressBarStyle.Blocks;
            progress.Value = Math.Max(0, Math.Min(100, message.Percent));
            status.Text = T("Downloading the MSIX… ", "正在下載 MSIX… ") + progress.Value + "%";
        }
        else
        {
            progress.Style = ProgressBarStyle.Marquee;
            status.Text = message.Kind == "Verify"
                ? T("Verifying the downloaded package…", "正在驗證下載的套件…")
                : T("Installing ChatGPT…", "正在安裝 ChatGPT…");
        }
    }

    private bool ShowCurrent(string version)
    {
        progress.Style = ProgressBarStyle.Blocks;
        status.Text = T("You're up to date", "已是最新版本");
        return ShowMessage(status.Text,
            T("Installed version: ", "目前版本：") + version,
            T("OK", "確定"), T("Exit", "結束"), DialogResult.No) == DialogResult.No;
    }

    private void OpenChatGPT(string appId)
    {
        if (String.IsNullOrEmpty(appId))
            throw new InvalidOperationException(T("The app entry was not found.", "找不到應用程式的啟動項目。"));
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        using (Process.Start(new ProcessStartInfo(explorer, @"shell:AppsFolder\" + appId) { UseShellExecute = false })) { }
    }

    private async Task<bool> EditSchedule()
    {
        status.Text = T("Reading the current schedule…", "正在讀取目前排程…");
        WorkerEvent state = await PowerShellWorker.RunAsync(new[] { "-Action", "ReadSchedule" }, false, null);
        status.Text = schedule.Text;
        using (var dialog = CreateDialog(T("Set schedule", "設定排程")))
        {
            var body = NewBody(390);
            var logon = NewCheck(T("Check when I sign in to this account", "登入目前帳號時檢查"), state.AtLogon);
            var daily = NewCheck(T("Check daily at", "每天在指定時間檢查"), !String.IsNullOrEmpty(state.DailyAt));
            var time = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm",
                ShowUpDown = true, Width = 100, Enabled = daily.Checked };
            time.Value = DateTime.ParseExact(daily.Checked ? state.DailyAt : "09:00", "HH:mm", CultureInfo.InvariantCulture);
            daily.CheckedChanged += delegate { time.Enabled = daily.Checked; };
            body.Controls.Add(logon);
            body.Controls.Add(NewRow(daily, time));
            body.Controls.Add(NewLabel(T("Checks are recorded in checks.log and run while you're signed in.",
                "登入後才會執行檢查，結果會寫入 checks.log。")));
            Button save = AddDialogButtons(dialog, body, T("Save", "儲存"), T("Cancel", "取消"), DialogResult.Cancel);
            save.DialogResult = DialogResult.OK;
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;

            status.Text = T("Saving the schedule…", "正在儲存排程…");
            var args = new List<string> { "-Action", "Unschedule" };
            if (logon.Checked || daily.Checked)
            {
                args[1] = "Schedule";
                if (logon.Checked) args.Add("-AtLogon");
                if (daily.Checked) args.AddRange(new[] { "-DailyAt", time.Value.ToString("HH:mm", CultureInfo.InvariantCulture) });
            }
            await PowerShellWorker.RunAsync(args.ToArray(), false, null);
            ShowMessage(T("Schedule saved", "已儲存排程"), logon.Checked || daily.Checked
                ? T("Scheduled checks are enabled.", "已啟用排程檢查。")
                : T("Scheduled checks are disabled.", "已停用排程檢查。"));
        }
        return false;
    }

    private void EditPreferences()
    {
        using (var dialog = CreateDialog(T("Other settings", "其它設定")))
        {
            var body = NewBody(460);
            var english = new RadioButton { Text = "English", AutoSize = true, Checked = !preferences.Chinese };
            var chinese = new RadioButton { Text = "繁體中文", AutoSize = true, Checked = preferences.Chinese };
            body.Controls.Add(NewRow(NewLabel(T("Language", "語言")), english, chinese));
            var auto = NewCheck(T("Install automatically after checking", "檢查後自動安裝"), preferences.AutoInstall);
            var close = NewCheck(T("Close this updater after installing", "更新完成後自動關閉本程式"), preferences.CloseAfterUpdate);
            var open = NewCheck(T("Open ChatGPT after installing", "更新完成後自動開啟 ChatGPT 桌面版"), preferences.OpenChatGPT);
            body.Controls.Add(auto);
            body.Controls.Add(close);
            body.Controls.Add(open);
            body.Controls.Add(NewLabel(T("Download folder", "下載資料夾")));
            var folder = new TextBox { Text = preferences.GetDownloadFolder(), Width = 345 };
            var browse = NewButton(T("Browse…", "瀏覽…"));
            browse.Click += delegate
            {
                using (var picker = new FolderBrowserDialog { SelectedPath = folder.Text, ShowNewFolderButton = true })
                    if (picker.ShowDialog(dialog) == DialogResult.OK) folder.Text = picker.SelectedPath;
            };
            body.Controls.Add(NewRow(folder, browse));
            Button save = AddDialogButtons(dialog, body, T("Save", "儲存"), T("Cancel", "取消"), DialogResult.Cancel);
            save.Click += delegate
            {
                try
                {
                    string path = Path.GetFullPath(folder.Text.Trim());
                    if (!Directory.Exists(path))
                        throw new DirectoryNotFoundException(T("Choose an existing download folder.", "請選擇已存在的下載資料夾。"));
                    var updated = new Preferences
                    {
                        Chinese = chinese.Checked, AutoInstall = auto.Checked,
                        CloseAfterUpdate = close.Checked, OpenChatGPT = open.Checked,
                        DownloadFolder = String.Equals(path, Preferences.DefaultFolder, StringComparison.OrdinalIgnoreCase)
                            ? null : path
                    };
                    updated.Save();
                    preferences = updated;
                    dialog.DialogResult = DialogResult.OK;
                }
                catch (Exception error)
                {
                    ShowMessage(T("Settings could not be saved", "無法儲存設定"), error.Message, owner: dialog);
                }
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                ApplyLanguage();
                ShowReady();
            }
        }
    }

    private void ShowError(Exception error)
    {
        var worker = error as WorkerException;
        string detail = worker == null || !String.IsNullOrWhiteSpace(worker.Detail)
            ? error.Message
            : T("The Windows PowerShell process exited with code ", "Windows PowerShell 結束代碼為 ") + worker.ExitCode;
        ShowMessage(T("Operation failed", "操作失敗"), detail);
    }

    private DialogResult ShowMessage(string title, string message, string primary = null,
        string secondary = null, DialogResult secondaryResult = DialogResult.Cancel, Form owner = null)
    {
        using (var dialog = CreateDialog(title))
        {
            var body = NewBody(350);
            body.Controls.Add(NewLabel(message));
            Button accept = AddDialogButtons(dialog, body, primary ?? T("OK", "確定"), secondary, secondaryResult);
            accept.DialogResult = DialogResult.OK;
            return dialog.ShowDialog(owner ?? this);
        }
    }

    private Form CreateDialog(string title)
    {
        var form = new Form { Text = title, Font = Font, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
        ConfigureWindow(form);
        return form;
    }

    private static void ConfigureWindow(Form form)
    {
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false;
        form.MinimizeBox = false;
        form.AutoSize = true;
        form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        form.AutoScaleMode = AutoScaleMode.Font;
        form.Padding = new Padding(16);
    }

    private static TableLayoutPanel NewBody(int width)
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
            ColumnCount = 1, MinimumSize = new Size(width, 0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return panel;
    }

    private static FlowLayoutPanel NewRow(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Button AddDialogButtons(Form dialog, TableLayoutPanel body, string primary,
        string secondary, DialogResult secondaryResult)
    {
        var accept = NewButton(primary);
        var row = NewRow(accept);
        if (secondary != null)
        {
            var cancel = NewButton(secondary);
            cancel.DialogResult = secondaryResult;
            if (secondaryResult == DialogResult.Cancel) dialog.CancelButton = cancel;
            row.Controls.Add(cancel);
        }
        body.Controls.Add(row);
        dialog.AcceptButton = accept;
        dialog.Controls.Add(body);
        return accept;
    }

    private static Button NewButton(string text = "")
    {
        return new Button { Text = text, AutoSize = true, Dock = DockStyle.Top, MinimumSize = new Size(90, 32) };
    }

    private static CheckBox NewCheck(string text, bool isChecked)
    {
        return new CheckBox { Text = text, AutoSize = true, Checked = isChecked };
    }

    private static Label NewLabel(string text)
    {
        return new Label { Text = text, AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(3, 7, 3, 7) };
    }
}
