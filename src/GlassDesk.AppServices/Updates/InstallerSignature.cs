using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace GlassDesk.AppServices.Updates;

/// <summary>
/// Optional Authenticode check for downloaded installers via WinVerifyTrust.
/// Disabled by default: GlassDesk installers are unsigned today, so requiring a
/// signature would block every update. Operators who sign their installers set
/// <c>GLASSDESK_SIGNER_SUBJECT</c> and the updater then refuses to launch an
/// installer that is unsigned, fails WinVerifyTrust, or names another publisher.
/// </summary>
public static class InstallerSignature
{
    /// <summary>Environment variable naming the expected signer subject (case-insensitive substring).</summary>
    public const string SubjectVariable = "GLASSDESK_SIGNER_SUBJECT";

    private static readonly bool IsWindows = OperatingSystem.IsWindows();

    /// <summary>True when Authenticode verification is required for this session.</summary>
    public static bool IsRequired => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SubjectVariable));

    /// <summary>
    /// Verifies the installer's Authenticode signature against the configured subject.
    /// Returns silently when verification is not configured; throws when configured
    /// and the file is unsigned, fails WinVerifyTrust, or names another publisher.
    /// </summary>
    public static void VerifyIfRequired(string installerPath)
    {
        var expected = Environment.GetEnvironmentVariable(SubjectVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(expected)) return;
        if (!IsWindows) throw new InvalidOperationException("当前平台不支持 Authenticode 校验。");

        // X509Certificate-constructor-based validation is nonstandard for PE files,
        // so ask Windows for the authoritative Authenticode verdict first.
        var trust = WinVerifyTrust(installerPath);
        if (trust != 0) throw new InvalidDataException($"更新安装包未通过 Windows 数字签名校验（错误码 0x{trust:X8}）。");

        using var signer = X509CertificateLoader.LoadCertificateFromFile(installerPath);
        if (!signer.Subject.Contains(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"更新安装包的签名者（{signer.Subject}）不在允许名单内。");
    }

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern int WinVerifyTrust([In] IntPtr hwnd, [In, MarshalAs(UnmanagedType.LPStruct)] Guid pgActionId, [In] ref WINTRUST_DATA pWVTData);

    private static int WinVerifyTrust(string file)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8222-00AA3AB5F17E"); // WINTRUST_ACTION_GENERIC_VERIFY_V2 covers Authenticode on PE files.
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = file,
        };
        var handle = GCHandle.Alloc(fileInfo, GCHandleType.Pinned);
        try
        {
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,          // WTD_UI_NONE – never show dialogs
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = handle.AddrOfPinnedObject(),
                dwStateAction = 0,       // WTD_STATEACTION_IGNORE
            };
            return WinVerifyTrust(IntPtr.Zero, action, ref data);
        }
        finally
        {
            handle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPCallbackData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
