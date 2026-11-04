using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RoleImport;

/// <summary>
/// The auto-login's credential file (ro3/autologin/salvar-credencial.ps1): the account password protected
/// with DPAPI under the current Windows user and the 'kga-ro3-autologin' entropy, at
/// %LOCALAPPDATA%\kga-ro3\cred\&lt;conta&gt;.bin. P/Invoked instead of referencing the ProtectedData package so
/// this tool keeps the repository's dependency set: crypt32's own CryptProtectData/CryptUnprotectData.
/// </summary>
public static class Dpapi
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("kga-ro3-autologin");
    private const int UiForbidden = 1;

    public static string CredentialPath(string account) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kga-ro3", "cred", account + ".bin");

    /// <summary>The saved password of an account; the plain text never leaves this process.</summary>
    public static string Read(string account)
    {
        string path = CredentialPath(account);
        if (!File.Exists(path)) throw new FileNotFoundException($"No saved credential for '{account}'.", path);
        return Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(path)))!;
    }

    /// <summary>Saves (or replaces) the DPAPI credential of an account, as salvar-credencial.ps1 does.</summary>
    public static string Save(string account, string password)
    {
        string path = CredentialPath(account);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Protect(Encoding.UTF8.GetBytes(password)));
        return path;
    }

    /// <summary>A password the ordinary login can type: no characters that the game or PowerShell could eat.</summary>
    public static string NewPassword(int length = 24)
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var text = new StringBuilder(length);
        foreach (byte value in bytes) text.Append(alphabet[value % alphabet.Length]);
        return text.ToString();
    }

    public static byte[] Protect(byte[] plain) => Crypt(plain, protect: true);

    public static byte[] Unprotect(byte[] protectedBytes) => Crypt(protectedBytes, protect: false);

    private static byte[] Crypt(byte[] data, bool protect)
    {
        var input = new Blob(data);
        var entropy = new Blob(Entropy);
        try
        {
            bool ok = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "DPAPI call failed.");
            try { return output.ToArray(); }
            finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
        }
        finally
        {
            input.Free();
            entropy.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;

        public Blob(byte[] bytes)
        {
            Size = bytes.Length;
            Data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
            Marshal.Copy(bytes, 0, Data, bytes.Length);
        }

        public byte[] ToArray()
        {
            var bytes = new byte[Size];
            Marshal.Copy(Data, bytes, 0, Size);
            return bytes;
        }

        public void Free() => Data = IntPtr.Zero;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob dataIn, string? description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
