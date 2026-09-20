Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class WinUtil {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
"@

$procs = Get-Process -Name 'IptvPlayer.App', 'dotnet' -ErrorAction SilentlyContinue
if (-not $procs) {
    Write-Output "No IptvPlayer.App or dotnet process running. Launching via shell:AppsFolder..."
    Start-Process "shell:AppsFolder\WHOSEIPTV.WhoseIPTV_jhywfcyt2h7f8!App"
    Start-Sleep -Seconds 2
    $procs = Get-Process -Name 'IptvPlayer.App', 'dotnet' -ErrorAction SilentlyContinue
}

foreach ($p in $procs) {
    $targetPid = $p.Id
    Write-Output "Checking windows for PID: $targetPid"

    [WinUtil]::EnumWindows({
        param($hwnd, $lparam)
        $procId = [uint32]0
        [void][WinUtil]::GetWindowThreadProcessId($hwnd, [ref]$procId)
        if ($procId -eq $targetPid) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][WinUtil]::GetWindowText($hwnd, $sb, 256)
            $title = $sb.ToString()
            $vis = [WinUtil]::IsWindowVisible($hwnd)
            Write-Output "Found HWND: $hwnd, Visible: $vis, Title: '$title'"
            [void][WinUtil]::ShowWindow($hwnd, 3) # SW_MAXIMIZE
            [void][WinUtil]::SetForegroundWindow($hwnd)
        }
        return $true
    }, [IntPtr]::Zero)
}
