// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using Android.App;
using Android.Security.Keystore;
using CanX.Core.Security;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace CanX.App.Android;

/// <summary>
/// Android credentials are encrypted with an app-local, non-exportable key.
/// The encrypted payload is stored in private app preferences; plaintext never
/// enters the settings or media-source JSON files.
/// </summary>
internal sealed class AndroidCredentialStore : ICredentialStore
{
    private const string PreferenceName = "canx.credentials";
    private const string KeyAlias = "org.canx.app.credentials.v1";

    public ValueTask SetAsync(
        CredentialRef reference,
        string secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(secret);
        var plaintext = Encoding.UTF8.GetBytes(secret);
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")
            ?? throw new CryptographicException("Android AES-GCM cipher is unavailable.");
        using var key = GetOrCreateKey();
        cipher.Init(Javax.Crypto.CipherMode.EncryptMode, key);
        cipher.UpdateAAD(Encoding.UTF8.GetBytes(reference.Id));
        var ciphertext = cipher.DoFinal(plaintext)
            ?? throw new CryptographicException("Android Keystore did not return ciphertext.");
        var nonce = cipher.GetIV()
            ?? throw new CryptographicException("Android Keystore did not return an AES-GCM nonce.");
        var payload = new byte[1 + nonce.Length + ciphertext.Length];
        payload[0] = checked((byte)nonce.Length);
        nonce.CopyTo(payload, 1);
        ciphertext.CopyTo(payload, 1 + nonce.Length);
        if (Preferences().Edit()?.PutString(reference.Id, Convert.ToBase64String(payload))?.Commit() != true)
        {
            throw new IOException("Android credential ciphertext could not be committed.");
        }
        Array.Clear(plaintext);
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetAsync(
        CredentialRef reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reference);
        var encoded = Preferences().GetString(reference.Id, null);
        if (string.IsNullOrEmpty(encoded))
        {
            return ValueTask.FromResult<string?>(null);
        }

        var payload = Convert.FromBase64String(encoded);
        if (payload.Length < 18 || payload[0] is 0 or > 32 || payload.Length <= 1 + payload[0])
        {
            throw new CryptographicException("Stored Android credential is truncated.");
        }
        var nonceLength = payload[0];
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")
            ?? throw new CryptographicException("Android AES-GCM cipher is unavailable.");
        using var key = GetOrCreateKey();
        using var parameters = new GCMParameterSpec(128, payload, 1, nonceLength);
        cipher.Init(Javax.Crypto.CipherMode.DecryptMode, key, parameters);
        cipher.UpdateAAD(Encoding.UTF8.GetBytes(reference.Id));
        var plaintext = cipher.DoFinal(payload, 1 + nonceLength, payload.Length - 1 - nonceLength)
            ?? throw new CryptographicException("Android Keystore did not return plaintext.");
        var secret = Encoding.UTF8.GetString(plaintext);
        Array.Clear(plaintext);
        return ValueTask.FromResult<string?>(secret);
    }

    public ValueTask<bool> DeleteAsync(
        CredentialRef reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reference);
        var existed = Preferences().Contains(reference.Id);
        if (Preferences().Edit()?.Remove(reference.Id)?.Commit() != true)
        {
            throw new IOException("Android credential ciphertext could not be removed.");
        }
        return ValueTask.FromResult(existed);
    }

    private static global::Android.Content.ISharedPreferences Preferences() =>
        global::Android.App.Application.Context.GetSharedPreferences(PreferenceName, global::Android.Content.FileCreationMode.Private)
        ?? throw new InvalidOperationException("Android private preferences are unavailable.");

    private static IKey GetOrCreateKey()
    {
        using var keyStore = KeyStore.GetInstance("AndroidKeyStore")
            ?? throw new CryptographicException("Android Keystore is unavailable.");
        keyStore.Load(null);
        if (!keyStore.ContainsAlias(KeyAlias))
        {
            using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")
                ?? throw new CryptographicException("Android Keystore AES generator is unavailable.");
            using var specification = new KeyGenParameterSpec.Builder(
                    KeyAlias,
                    KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                .SetKeySize(256)
                .Build();
            generator.Init(specification);
            using var generated = generator.GenerateKey()
                ?? throw new CryptographicException("Android Keystore could not generate an AES key.");
        }
        return keyStore.GetKey(KeyAlias, null)
            ?? throw new CryptographicException("Android Keystore key could not be loaded.");
    }
}
