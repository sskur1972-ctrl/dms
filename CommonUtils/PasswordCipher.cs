using System.Security.Cryptography;
using System.Text;
using PTPLCRYPTORENGINE;

namespace DMS.Web.CommonUtils;

public sealed class PasswordCipher(IConfiguration configuration, ILogger<PasswordCipher> log)
{
    private string Key => !string.IsNullOrWhiteSpace(configuration["Cipherkey"])
        ? configuration["Cipherkey"]!
        : throw new InvalidOperationException("Cipherkey is not configured.");

    public string GetEncryptedPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return string.Empty;
        try { return AESCryptor.Encrypt(password, Key) ?? string.Empty; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "A password could not be encrypted; it was not saved.");
            return string.Empty;
        }
    }

    public string EncryptForStorage(string password)
    {
        var encrypted = GetEncryptedPassword(password);
        if (string.IsNullOrEmpty(encrypted))
            throw new InvalidOperationException("Password encryption failed. Check Cipherkey configuration.");
        return encrypted;
    }

    public string? Unprotect(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return null;
        try { return AESCryptor.Decrypt(encrypted, Key); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "A stored secret could not be decrypted (was Cipherkey changed?)");
            return null;
        }
    }

    public bool Verify(string? password, string? stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored)) return false;
        var decrypted = Unprotect(stored);
        return decrypted is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(decrypted));
    }
}
