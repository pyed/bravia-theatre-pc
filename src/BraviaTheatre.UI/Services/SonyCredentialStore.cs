using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BraviaTheatre.Core.Auth;

namespace BraviaTheatre.UI.Services;

public enum CredentialLoadStatus
{
    Missing,
    Loaded,
    Invalid,
    Error
}

public sealed record CredentialLoadResult(
    CredentialLoadStatus Status,
    SonyCredentials? Credentials = null,
    string? Message = null);

/// <summary>
/// Stores local-control credentials, optional Sony renewal material, and expiry metadata
/// encrypted for the current Windows user. Short-lived access tokens are not persisted.
/// </summary>
public sealed class SonyCredentialStore
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("BTPC1");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BraviaTheatrePC.Credentials.v1");

    public SonyCredentialStore(string protectedFilePath)
    {
        ProtectedFilePath = Path.GetFullPath(protectedFilePath);
    }

    public string ProtectedFilePath { get; }

    public CredentialLoadResult Load()
    {
        if (!File.Exists(ProtectedFilePath))
            return new CredentialLoadResult(CredentialLoadStatus.Missing);

        try
        {
            var credentials = ReadProtected();
            return credentials.IsValid
                ? new CredentialLoadResult(CredentialLoadStatus.Loaded, credentials)
                : new CredentialLoadResult(CredentialLoadStatus.Invalid, Message: "The protected credential file is incomplete.");
        }
        catch (CryptographicException)
        {
            return new CredentialLoadResult(
                CredentialLoadStatus.Invalid,
                Message: "The protected credentials cannot be decrypted by this Windows account.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return new CredentialLoadResult(CredentialLoadStatus.Error, Message: $"Could not load protected credentials: {ex.Message}");
        }
    }

    public bool TrySave(SonyCredentials credentials, out string? error)
    {
        error = null;
        if (!credentials.IsValid)
        {
            error = "Sony credentials are incomplete and cannot be saved.";
            return false;
        }

        var directory = Path.GetDirectoryName(ProtectedFilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            error = "The credential storage directory is invalid.";
            return false;
        }

        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(directory);
            var clearBytes = JsonSerializer.SerializeToUtf8Bytes(credentials);
            byte[] protectedBytes;
            try
            {
                protectedBytes = ProtectedData.Protect(clearBytes, Entropy, DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clearBytes);
            }
            var payload = new byte[Magic.Length + protectedBytes.Length];
            Buffer.BlockCopy(Magic, 0, payload, 0, Magic.Length);
            Buffer.BlockCopy(protectedBytes, 0, payload, Magic.Length, protectedBytes.Length);

            tempPath = Path.Combine(directory, $".{Path.GetFileName(ProtectedFilePath)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(tempPath, payload);
            File.Move(tempPath, ProtectedFilePath, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            error = $"Could not protect and save Sony credentials: {ex.Message}";
            return false;
        }
        finally
        {
            if (tempPath != null)
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch
                {
                    // Best effort cleanup only.
                }
            }
        }
    }

    private SonyCredentials ReadProtected()
    {
        var payload = File.ReadAllBytes(ProtectedFilePath);
        if (payload.Length <= Magic.Length || !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new FormatException("Unsupported credential file format.");

        var protectedBytes = payload.AsSpan(Magic.Length).ToArray();
        var clearBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var credentials = JsonSerializer.Deserialize<SonyCredentials>(clearBytes);
            return credentials ?? throw new JsonException("The credential payload is empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }
}
