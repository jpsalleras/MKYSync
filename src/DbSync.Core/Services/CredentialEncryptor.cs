using System.Security.Cryptography;
using System.Text;

namespace DbSync.Core.Services;

/// <summary>
/// Encripta y desencripta credenciales usando AES-256-CBC.
/// La clave se configura en appsettings.json ("Encryption:Key").
/// Prefijo "AES:" indica encriptación AES (actual).
/// Prefijo "ENC:" indica encriptación DPAPI legacy (se migra automáticamente al re-guardar).
/// Sin prefijo = texto plano (backward compatible, se migra al iniciar).
/// </summary>
public class CredentialEncryptor
{
    private const string AesPrefix = "AES:";
    private const string DpapiPrefix = "ENC:";

    private readonly byte[] _key;

    public CredentialEncryptor(string encryptionKey)
    {
        if (string.IsNullOrWhiteSpace(encryptionKey))
            throw new ArgumentException("Encryption:Key es requerido en appsettings.json");

        // Derivar clave AES-256 (32 bytes) desde la clave configurada
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(encryptionKey));
    }

    /// <summary>
    /// Encripta un texto plano con AES-256-CBC. Si ya está encriptado con AES, lo retorna tal cual.
    /// </summary>
    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;
        if (plainText.StartsWith(AesPrefix)) return plainText;

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Guardar IV (16 bytes) + datos encriptados
        var combined = new byte[aes.IV.Length + encryptedBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, combined, 0, aes.IV.Length);
        Buffer.BlockCopy(encryptedBytes, 0, combined, aes.IV.Length, encryptedBytes.Length);

        return AesPrefix + Convert.ToBase64String(combined);
    }

    /// <summary>
    /// Desencripta un texto. Soporta AES (actual) y DPAPI legacy.
    /// Sin prefijo = texto plano, se retorna tal cual.
    /// </summary>
    public string Decrypt(string encryptedText)
    {
        if (string.IsNullOrEmpty(encryptedText)) return encryptedText;

        if (encryptedText.StartsWith(AesPrefix))
            return DecryptAes(encryptedText);

        if (encryptedText.StartsWith(DpapiPrefix))
            return DecryptDpapiLegacy(encryptedText);

        // Texto plano (sin prefijo) — backward compatible
        return encryptedText;
    }

    /// <summary>
    /// Verifica si un texto ya está encriptado (AES o DPAPI legacy).
    /// </summary>
    public bool IsEncrypted(string text)
    {
        return !string.IsNullOrEmpty(text)
            && (text.StartsWith(AesPrefix) || text.StartsWith(DpapiPrefix));
    }

    /// <summary>
    /// Verifica si un texto usa encriptación DPAPI legacy que necesita migración a AES.
    /// </summary>
    public bool NeedsMigration(string text)
    {
        return !string.IsNullOrEmpty(text) && text.StartsWith(DpapiPrefix);
    }

    private string DecryptAes(string encryptedText)
    {
        var base64 = encryptedText[AesPrefix.Length..];
        var combined = Convert.FromBase64String(base64);

        // Primeros 16 bytes = IV, resto = datos encriptados
        var iv = new byte[16];
        var encrypted = new byte[combined.Length - 16];
        Buffer.BlockCopy(combined, 0, iv, 0, 16);
        Buffer.BlockCopy(combined, 16, encrypted, 0, encrypted.Length);

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private static string DecryptDpapiLegacy(string encryptedText)
    {
        try
        {
            var base64 = encryptedText[DpapiPrefix.Length..];
            var encryptedBytes = Convert.FromBase64String(base64);
            var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException(
                "No se puede desencriptar la contraseña (DPAPI legacy). " +
                "Reingrese la contraseña en la configuración del cliente para migrarla a AES.");
        }
    }
}
