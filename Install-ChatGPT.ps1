#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Update', 'Check', 'Schedule', 'Unschedule', 'ReadSchedule', 'Help')]
    [string]$Action = 'Update',

    [switch]$AtLogon,

    [ValidatePattern('^(?:[01][0-9]|2[0-3]):[0-5][0-9]$')]
    [string]$DailyAt,

    [switch]$Pause,

    [Parameter(DontShow)] [switch]$Scheduled,
    [Parameter(DontShow)] [string]$CallerSid,
    [Parameter(DontShow)] [string]$LauncherPath,
    [Parameter(DontShow)] [string]$UiPipe,
    [Parameter(DontShow)] [string]$DownloadFolder,
    [Parameter(DontShow)] [version]$ExpectedVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$packageName = 'OpenAI.Codex'
$packagePublisher = 'CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B'
$uiWriter = $null
$pipe = $null

function Send-UiEvent([hashtable]$Event) {
    $uiWriter.WriteLine((ConvertTo-Json -InputObject $Event -Compress))
}

function Get-PowerShellPath {
    $systemDirectory = if ($env:PROCESSOR_ARCHITEW6432) { 'Sysnative' } else { 'System32' }
    Join-Path $env:SystemRoot "$systemDirectory\WindowsPowerShell\v1.0\powershell.exe"
}

function Get-PackageArchitecture {
    $machineEnvironment = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
    switch ((Get-ItemPropertyValue -LiteralPath $machineEnvironment -Name PROCESSOR_ARCHITECTURE)) {
        'AMD64' { 'x64' }
        'ARM64' { 'arm64' }
        default { throw 'The official ChatGPT MSIX is available for x64 and ARM64 Windows.' }
    }
}

function New-PackageRequest([uri]$Uri) {
    $request = [Net.HttpWebRequest]::CreateHttp($Uri)
    $request.AllowAutoRedirect = $false
    $request.Timeout = 30000
    $request.ReadWriteTimeout = 60000
    $request.CachePolicy = [Net.Cache.RequestCachePolicy]::new([Net.Cache.RequestCacheLevel]::NoCacheNoStore)
    return $request
}

function Get-RemotePackage([string]$Architecture) {
    $uri = "https://persistent.oaistatic.com/codex-app-prod/ChatGPT-$Architecture.msix"
    $request = New-PackageRequest $uri
    $request.AddRange(0, 0)
    $response = $request.GetResponse()
    try {
        if ($response.StatusCode -ne [Net.HttpStatusCode]::PartialContent -or
            $response.ContentLength -ne 1 -or
            $response.Headers['Content-Range'] -notmatch '^bytes 0-0/([1-9][0-9]*)$') {
            throw 'The download server did not return the expected one-byte version response.'
        }
        $size = [long]$Matches[1]
        $tag = $response.Headers['ETag']
        $version = $response.Headers['x-ms-meta-package_version']
        if ($response.Headers['x-ms-meta-package_identity'] -cne $packageName -or
            $response.Headers['x-ms-meta-architecture'] -ne $Architecture -or
            $version -notmatch '^[0-9]+(?:\.[0-9]+){3}$' -or $tag -notmatch '^"[^"\r\n]+"$') {
            throw 'The official download is missing valid package metadata. No MSIX was downloaded.'
        }
        [pscustomobject]@{ Uri = $uri; Size = $size; Version = [version]$version; ETag = $tag }
    } finally {
        $response.Dispose()
    }
}

function Get-InstalledPackage {
    Get-AppxPackage -Name $packageName -PackageTypeFilter Main |
        Where-Object { $_.Publisher -ceq $packagePublisher } |
        Sort-Object { [version]$_.Version } -Descending | Select-Object -First 1
}

function Get-InstalledVersion {
    $package = Get-InstalledPackage
    if ($package) { return [version]$package.Version }
}

function Get-ChatGPTAppId {
    $package = Get-InstalledPackage
    $manifest = Get-AppxPackageManifest -Package $package
    $app = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application' and @Id='App']")
    if (!$app) { throw 'The installed package has no application entry.' }
    return $package.PackageFamilyName + '!' + $app.GetAttribute('Id')
}

function Save-RemotePackage($Remote, [string]$Path) {
    $request = New-PackageRequest $Remote.Uri
    $request.Headers['If-Match'] = $Remote.ETag
    try {
        $response = $request.GetResponse()
    } catch [Net.WebException] {
        $errorResponse = $_.Exception.Response
        if ($errorResponse) {
            $status = $errorResponse.StatusCode
            $errorResponse.Dispose()
            if ($status -eq [Net.HttpStatusCode]::PreconditionFailed) {
                throw 'The official package changed during the check. Run the updater again.'
            }
        }
        throw
    }

    try {
        if ($response.StatusCode -ne [Net.HttpStatusCode]::OK -or
            $response.Headers['ETag'] -cne $Remote.ETag -or $response.ContentLength -ne $Remote.Size) {
            throw 'The download no longer matches the version check. Run the updater again.'
        }
        $file = [IO.File]::Open($Path, 'CreateNew', 'Write', 'None')
        try {
            $stream = $response.GetResponseStream()
            if ($uiWriter) {
                $buffer = [byte[]]::new(1MB)
                $lastPercent = 0
                while (($count = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $file.Write($buffer, 0, $count)
                    $percent = [int][Math]::Floor(100 * $file.Length / $Remote.Size)
                    if ($percent -ne $lastPercent) {
                        Send-UiEvent @{ Kind = 'Download'; Percent = $percent }
                        $lastPercent = $percent
                    }
                }
            } else {
                $stream.CopyTo($file)
            }
            if ($file.Length -ne $Remote.Size) { throw 'The MSIX download was incomplete.' }
        } finally {
            $file.Dispose()
        }
    } finally {
        $response.Dispose()
    }
}

function Confirm-PackageManifest([string]$Path, [string]$Architecture, [version]$Version) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.GetEntry('AppxManifest.xml')
        if (!$entry) { throw 'The downloaded file has no MSIX manifest.' }
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.CloseInput = $true
        $settings.MaxCharactersInDocument = 1048576
        $reader = [Xml.XmlReader]::Create($entry.Open(), $settings)
        try {
            $manifest = [Xml.XmlDocument]::new()
            $manifest.Load($reader)
            $identity = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
            if (!$identity -or $identity.GetAttribute('Name') -cne $packageName -or
                $identity.GetAttribute('Publisher') -cne $packagePublisher -or
                $identity.GetAttribute('ProcessorArchitecture') -ne $Architecture -or
                [version]$identity.GetAttribute('Version') -ne $Version) {
                throw 'The MSIX manifest does not match the expected ChatGPT package.'
            }
        } finally {
            $reader.Dispose()
        }
    } finally {
        $zip.Dispose()
    }
}

function Write-PackageResult($Installed, $Remote, [bool]$Updated, [string]$Message) {
    if (!$uiWriter) { return $Message }
    $appId = $null
    if ($Updated) {
        try { $appId = Get-ChatGPTAppId } catch { }
    }
    Send-UiEvent @{
        Kind = 'Package'; Installed = [string]$Installed; Available = [string]$Remote.Version
        NeedsUpdate = (!$Installed -or $Installed -lt $Remote.Version); Updated = $Updated; AppId = $appId
    }
}

function Invoke-PackageWorkflow([switch]$Install) {
    $architecture = Get-PackageArchitecture
    $installed = Get-InstalledVersion
    $remote = Get-RemotePackage $architecture
    $installedLabel = if ($installed) { $installed } else { 'not installed' }
    $versions = "Installed: $installedLabel | Official $architecture MSIX: $($remote.Version)"

    if ($installed -and $installed -ge $remote.Version) {
        return Write-PackageResult $installed $remote $false "$versions | No update needed."
    }
    if (!$Install) { return Write-PackageResult $installed $remote $false "$versions | Install or update available." }
    if ($ExpectedVersion -and $remote.Version -ne $ExpectedVersion) {
        if ($uiWriter) { Send-UiEvent @{ Kind = 'VersionChanged' }; return }
        throw 'The available version changed. Check again before installing.'
    }

    $folder = if ($DownloadFolder) { $DownloadFolder } else { [IO.Path]::GetTempPath() }
    if (![IO.Directory]::Exists($folder)) { throw "The download folder is unavailable: $folder" }
    if ($uiWriter) {
        Send-UiEvent @{ Kind = 'Download'; Percent = 0 }
    } else {
        Write-Host $versions
        Write-Host ('Downloading {0:N0} MiB from OpenAI...' -f ($remote.Size / 1MB))
    }
    $work = Join-Path $folder ('ChatGPT-Msix-' + [guid]::NewGuid())
    $path = Join-Path $work "ChatGPT-$architecture.msix"
    [void][IO.Directory]::CreateDirectory($work)
    try {
        Save-RemotePackage $remote $path
        if ($uiWriter) { Send-UiEvent @{ Kind = 'Verify' } }
        Confirm-PackageManifest $path $architecture $remote.Version

        # Another updater (such as the Store) may have installed a version during the download.
        $installed = Get-InstalledVersion
        if ($installed -and $installed -ge $remote.Version) {
            return Write-PackageResult $installed $remote $false "ChatGPT $installed is already installed. No update needed."
        }
        if ($uiWriter) {
            Send-UiEvent @{ Kind = 'Install' }
        } else {
            Write-Host 'Installing the MSIX. Windows will close running ChatGPT processes if necessary.'
        }
        Add-AppxPackage -Path $path -ForceTargetApplicationShutdown
        $installed = Get-InstalledVersion
        if (!$installed -or $installed -lt $remote.Version) { throw 'Could not verify the installed version.' }
        return Write-PackageResult $installed $remote $true "ChatGPT $installed is installed."
    } finally {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        try { [IO.Directory]::Delete($work) }
        catch { Write-Warning "Could not remove temporary folder $work." }
    }
}

function Get-CheckTask([string]$TaskName) {
    try { Get-ScheduledTask -TaskName $TaskName -TaskPath '\' -ErrorAction Stop }
    catch { if ($_.CategoryInfo.Category -ne 'ObjectNotFound') { throw } }
}

function Set-CheckSchedule($Identity, [string]$ScriptPath) {
    $taskName = "ChatGPT Desktop MSIX Check ($($Identity.User.Value))"
    if ($Action -eq 'ReadSchedule') {
        $state = @{ Kind = 'Schedule'; AtLogon = $false; DailyAt = '' }
        $task = Get-CheckTask $taskName
        if ($task) {
            foreach ($trigger in $task.Triggers) {
                switch ($trigger.CimClass.CimClassName) {
                    'MSFT_TaskLogonTrigger' { $state.AtLogon = $true }
                    'MSFT_TaskDailyTrigger' {
                        $start = [datetime]::Parse($trigger.StartBoundary, [Globalization.CultureInfo]::InvariantCulture)
                        $state.DailyAt = $start.ToString('HH:mm', [Globalization.CultureInfo]::InvariantCulture)
                    }
                }
            }
        }
        Send-UiEvent $state
        return
    }
    if ($Action -eq 'Unschedule') {
        if (Get-CheckTask $taskName) {
            Unregister-ScheduledTask -TaskName $taskName -TaskPath '\' -Confirm:$false
        }
        if ($uiWriter) { Send-UiEvent @{ Kind = 'Saved' }; return }
        return 'Scheduled checks are disabled.'
    }

    $triggers = @()
    if ($AtLogon) { $triggers += New-ScheduledTaskTrigger -AtLogOn -User $Identity.Name }
    if ($DailyAt) {
        $time = [datetime]::ParseExact($DailyAt, 'HH:mm', [Globalization.CultureInfo]::InvariantCulture)
        $daily = New-ScheduledTaskTrigger -Daily -At $time
        # A boundary without a UTC offset follows the computer's local clock.
        $daily.StartBoundary = $time.ToString('s', [Globalization.CultureInfo]::InvariantCulture)
        $triggers += $daily
    }
    if ($LauncherPath) {
        $taskAction = New-ScheduledTaskAction -Execute $LauncherPath -Argument '-Action Check -Scheduled'
    } else {
        $arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -File "{0}" -Action Check -Scheduled' -f $ScriptPath
        $powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $taskAction = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments
    }
    $principal = New-ScheduledTaskPrincipal -UserId $Identity.Name -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $taskName -TaskPath '\' -Trigger $triggers -Action $taskAction `
        -Principal $principal -Settings $settings -Description 'Checks the official ChatGPT MSIX version.' -Force | Out-Null
    if ($uiWriter) { Send-UiEvent @{ Kind = 'Saved' }; return }
    return "Scheduled checks are enabled. Log: $env:LOCALAPPDATA\ChatGPTDesktopUpdater\checks.log"
}

function Write-CheckLog([string]$Message) {
    $folder = Join-Path $env:LOCALAPPDATA 'ChatGPTDesktopUpdater'
    [void][IO.Directory]::CreateDirectory($folder)
    Add-Content -LiteralPath (Join-Path $folder 'checks.log') -Encoding UTF8 `
        -Value ('{0:yyyy-MM-dd HH:mm:ss zzz}  {1}' -f [datetimeoffset]::Now, $Message)
}

$exitCode = 0
try {
    if ($UiPipe) {
        # The GUI receives progress only and cannot impersonate the elevated worker.
        $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $UiPipe, 'Out', 'None', 'Anonymous')
        $pipe.Connect(5000)
        $uiWriter = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false))
        $uiWriter.AutoFlush = $true
    }
    if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use Windows PowerShell 5.1 (powershell.exe).' }
    if (!$uiWriter -and ($Action -eq 'ReadSchedule' -or $DownloadFolder -or $ExpectedVersion)) {
        throw 'These options require the graphical updater.'
    }
    if ($Scheduled -and $Action -ne 'Check') { throw 'Scheduled runs support Check only.' }
    if ($Action -eq 'Schedule' -and !$AtLogon -and !$DailyAt) { throw 'Specify -AtLogon or -DailyAt HH:mm.' }
    if ($Action -ne 'Schedule' -and ($AtLogon -or $DailyAt)) { throw 'Use scheduling options with -Action Schedule.' }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $sid = $identity.User.Value
    if ($CallerSid -and $sid -ne $CallerSid) {
        throw 'Use the same Windows account at the UAC prompt. Add-AppxPackage installs for the running account.'
    }
    switch ($Action) {
        'Help' {
            Write-Output @'
-Action Update      Install or update ChatGPT (default, requests UAC).
-Action Check       Compare versions without downloading the full MSIX.
-Action Schedule    Set -AtLogon, -DailyAt HH:mm, or both to check for updates.
-Action Unschedule  Remove scheduled checks for the current Windows user.
'@
        }
        'Update' {
            $principal = [Security.Principal.WindowsPrincipal]::new($identity)
            if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                if ($uiWriter) { throw 'The graphical updater needs administrator rights to install.' }
                $arguments = '-NoProfile -File "{0}" -Action Update -CallerSid {1}' -f $PSCommandPath, $sid
                if ($Pause) { $arguments += ' -Pause' }
                $child = Start-Process -FilePath (Get-PowerShellPath) -ArgumentList $arguments -Verb RunAs -Wait -PassThru
                $Pause = $false
                $exitCode = $child.ExitCode
                if ($exitCode -ne 0) { Write-Host "The elevated updater exited with code $exitCode." }
            } else {
                $mutex = [Threading.Mutex]::new($false, "Global\ChatGPTDesktopUpdater-$sid")
                try {
                    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
                    if (!$locked) { throw 'Another ChatGPT update is running.' }
                    try { Write-Output (Invoke-PackageWorkflow -Install) } finally { $mutex.ReleaseMutex() }
                } finally {
                    $mutex.Dispose()
                }
            }
        }
        'Check' {
            $result = Invoke-PackageWorkflow
            if ($Scheduled) { Write-CheckLog $result }
            Write-Output $result
        }
        default { Write-Output (Set-CheckSchedule $identity $PSCommandPath) }
    }
} catch {
    $exitCode = 1
    if ($uiWriter) {
        try { Send-UiEvent @{ Kind = 'Error'; Message = $_.Exception.Message } } catch { }
    } else {
        [Console]::Error.WriteLine($_.Exception.Message)
    }
    if ($Scheduled) {
        try { Write-CheckLog "ERROR: $($_.Exception.Message)" }
        catch { [Console]::Error.WriteLine($_.Exception.Message) }
    }
} finally {
    if ($uiWriter) { $uiWriter.Dispose() }
    if ($pipe) { $pipe.Dispose() }
    if ($Pause -and !$Scheduled -and ![Console]::IsInputRedirected) {
        [void](Read-Host 'Press Enter to close')
    }
}
exit $exitCode
