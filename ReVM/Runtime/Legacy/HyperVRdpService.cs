using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace ReVM;

///<summary>
///METHOD 2: RDP ActiveX Control embedding.
///
///Hyper-V Enhanced Session Mode uses RDP under the hood.
///We can host the Microsoft RDP ActiveX control directly in WPF
///via WindowsFormsHost, bypassing vmconnect.exe entirely.
///
///This is the MORE RELIABLE approach because:
///  - No window reparenting hacks (SetParent can break D3D rendering)
///  - Proper DPI scaling
///  - Keyboard/mouse input works correctly
///  - No chrome-stripping artifacts
///
///Requirements:
///  - Hyper-V Enhanced Session Mode enabled on the VM
///    (Set-VM -VMName "ReVM-Base" -EnhancedSessionTransportType HvSocket)
///  - RDP ActiveX control registered (comes with Windows, no extra install)
///  - WindowsFormsHost in WPF (add System.Windows.Forms and WindowsFormsIntegration refs)
///
///The ActiveX control CLSID/progID:
///  - MsRdpClient9NotSafeForScripting  (Windows 10/11)
///  - MsRdpClient8NotSafeForScripting  (Windows 8.1)
///  - MsRdpClient7NotSafeForScripting  (Windows 7)
///  - MSTSCLib.MsRdpClient9            (interop assembly)
///
///Connection string for Hyper-V Enhanced Session:
///  The VM GUID is used as the RDP server address via HvSocket:
///  server = VM GUID (e.g., "12345678-1234-1234-1234-123456789abc")
///  The RDP client connects via the Hyper-V socket transport.
///
///Alternative: Use the full MSTSCLib interop for programmatic control.
///</summary>
public class HyperVRdpService
{
    ///<summary>
    ///Create an RDP ActiveX control configured for Hyper-V Enhanced Session.
    ///
    ///Usage in WPF:
    ///  var host = new WindowsFormsHost();
    ///  var rdp = HyperVRdpService.CreateRdpControl(vmGuid, username, password);
    ///  host.Child = rdp;
    ///  DisplayHost.Child = host;  // DisplayHost is the WPF Border
    ///
    ///The RDP control handles its own rendering — no SetParent needed.
    ///</summary>
    public static dynamic CreateRdpControl(
        string vmGuid,
        string? username = null,
        string? password = null)
    {
        // Create the RDP ActiveX control via COM
        // This requires the MSTSCLib interop assembly or late-bound COM
        Type rdpType = Type.GetTypeFromProgID("MsRdpClient9NotSafeForScripting")
            ?? Type.GetTypeFromProgID("MsRdpClient8NotSafeForScripting")
            ?? Type.GetTypeFromProgID("MsTscAxNotSafeForScripting")
            ?? throw new InvalidOperationException(
                "RDP ActiveX control not found. Is Remote Desktop installed?");

        dynamic rdp = Activator.CreateInstance(rdpType)!;

        // Configure for Hyper-V Enhanced Session
        rdp.Server = vmGuid;  // VM GUID as server address
        rdp.Domain = "";
        rdp.UserName = username ?? "";
        rdp.AdvancedSettings9.EnableCredSspSupport = true;
        rdp.AdvancedSettings9.NegotiateSecurityLayer = true;

        // Display settings
        rdp.DesktopWidth = 1920;
        rdp.DesktopHeight = 1080;
        rdp.ColorDepth = 32;
        rdp.AdvancedSettings9.SmartSizing = true;

        // Disable unwanted features
        rdp.AdvancedSettings9.DisplayConnectionBar = false;
        rdp.AdvancedSettings9.EnableWindowsKey = true;

        if (!string.IsNullOrEmpty(password))
        {
            rdp.AdvancedSettings9.ClearTextPassword = password;
        }

        return rdp;
    }

    ///<summary>
    ///Get the GUID of a Hyper-V VM by name.
    ///</summary>
    public static string? GetVmGuid(string vmName)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -Command \"(Get-VM -Name '{vmName}').Id.Guid\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd()?.Trim() ?? "";
            p?.WaitForExit(10000);
            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    ///<summary>
    ///Enable Enhanced Session Mode on a Hyper-V VM.
    ///Required for RDP-based display embedding.
    ///</summary>
    public static bool EnableEnhancedSession(string vmName)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -Command \"Set-VM -Name '{vmName}' " +
                "-EnhancedSessionTransportType HvSocket; 'OK'\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd()?.Trim() ?? "";
            p?.WaitForExit(15000);
            return output.Contains("OK");
        }
        catch
        {
            return false;
        }
    }
}
