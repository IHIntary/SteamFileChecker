using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SteamFileChecker.Core;

namespace SteamFileChecker.Desktop;

/// <summary>
/// "Remember login": keeps the account name and Steam refresh token in %LOCALAPPDATA%\SteamFileChecker\login.dat, encrypted
/// with Windows DPAPI for the current Windows user (other users and other computers cannot decrypt it). Only written when the
/// user ticks "记住登录"; deleted on logout, when a login without remembering succeeds, or when Steam rejects the token.
/// No password is ever stored.
/// </summary>
internal sealed class LoginStore(string directory)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SteamFileChecker.login.v1");
    private string FilePath => Path.Combine(directory, "login.dat");

    public bool Exists => File.Exists(FilePath);

    public void Save(SteamSession.SignIn signIn)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(FilePath, Protect(JsonSerializer.SerializeToUtf8Bytes(new Saved(signIn.AccountName, signIn.RefreshToken))));
    }

    public SteamSession.SignIn? Load()
    {
        try
        {
            if (!Exists) return null;
            var saved = JsonSerializer.Deserialize<Saved>(Unprotect(File.ReadAllBytes(FilePath)));
            return saved is { Account.Length: > 0, Token.Length: > 0 } ? new(saved.Account, saved.Token) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicFailure) { Delete(); return null; }
    }

    public void Delete() { try { File.Delete(FilePath); } catch (IOException) { } }

    private sealed record Saved(string Account, string Token);
    private sealed class CryptographicFailure(string message) : Exception(message);

    [StructLayout(LayoutKind.Sequential)] private struct DATA_BLOB { public int cbData; public IntPtr pbData; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB dataIn, string? description, ref DATA_BLOB entropy, IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, ref DATA_BLOB entropy, IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    private static byte[] Protect(byte[] data) => Transform(data, protect: true);
    private static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = Pin(data, out var inputHandle); var entropy = Pin(Entropy, out var entropyHandle); var output = new DATA_BLOB();
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, "Steam File Checker", ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
            if (!ok) throw new CryptographicFailure($"DPAPI error {Marshal.GetLastWin32Error()}");
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            inputHandle.Free(); entropyHandle.Free();
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    private static DATA_BLOB Pin(byte[] bytes, out GCHandle handle)
    {
        handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        return new DATA_BLOB { cbData = bytes.Length, pbData = handle.AddrOfPinnedObject() };
    }
}
