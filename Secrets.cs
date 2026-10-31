using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace DocsSync;

// API key encrypted with Windows DPAPI: only this Windows user on this PC can read it.
public static class Secrets
{
    static string KeyPath => Path.Combine(Scanner.AppDir, "apikey.bin");

    public static string? LoadApiKey()
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), null, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is IOException or CryptographicException) { return null; }
    }

    public static void SaveApiKey(string? key)
    {
        Directory.CreateDirectory(Scanner.AppDir);
        if (string.IsNullOrWhiteSpace(key)) { File.Delete(KeyPath); return; }
        File.WriteAllBytes(KeyPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
    }

    public static bool HasKey => LoadApiKey() != null || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
}

public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "DocsSync";

    public static bool Enabled
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(Name) != null; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue(Name, false);
        }
    }
}
