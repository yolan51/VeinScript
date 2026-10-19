using System.Security.Cryptography;
using System.Text;

namespace Vein.Compiler.Ir;

/// Cryptography for VeinScript programs — the built-ins behind `stdlib/Crypto.vein` (Vein.Crypto). One
/// implementation, called by the interpreter directly and by compiled code, so the two runtimes cannot drift.
///
/// NOTHING HERE IS INVENTED. Every primitive is .NET's (System.Security.Cryptography): ECDH and ECDSA on P-256,
/// HKDF-SHA256, AES-256-GCM, PBKDF2-SHA256, SHA-256 and the OS random generator. This file only chooses the
/// standard combination and the text format, because VeinScript values are text:
///
///   keys        vk1:<ECDH private, PKCS#8>:<ECDSA private, PKCS#8>            (secret: never send it)
///   public key  vp1:<ECDH public, SPKI>:<ECDSA public, SPKI>                   (send it to anyone)
///   sealed      vs1:<ephemeral ECDH public>:<nonce>:<ciphertext>:<tag>:<signature>
///   locked      vl1:<salt>:<nonce>:<ciphertext>:<tag>
///
/// each part base64. `Seal` is ECIES: a fresh ephemeral key agrees a secret with the recipient's public key,
/// HKDF turns it into an AES-256-GCM key, and the sender signs everything with its ECDSA key — so only the
/// recipient can read it, and the recipient knows who wrote it. A sealed message carries no plaintext metadata
/// but its length.
///
/// What it is NOT: there is no ratchet, so a stolen private key opens every message ever sealed to it that
/// someone recorded (no forward secrecy). Agreeing that a public key belongs to who it claims is the program's
/// job — `Fingerprint` gives two people a short number to compare.
///
/// Every function is TOTAL, as VeinScript's built-ins are: bad input answers "" rather than throwing, and
/// `Open`/`Unlock` answer "" for anything that is not genuine — tampered, sealed to someone else, signed by
/// someone else, or the wrong password.
public static class VeinCrypto
{
    private static readonly byte[] SealInfo = Encoding.UTF8.GetBytes("vein-seal-v1");
    private const int Pbkdf2Iterations = 210_000;   // OWASP's 2023 figure for PBKDF2-HMAC-SHA256

    /// A new key pair for one person: an agreement key and a signing key, as one secret text.
    public static string NewKeys()
    {
        using var dh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var sig = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return "vk1:" + B64(dh.ExportPkcs8PrivateKey()) + ":" + B64(sig.ExportPkcs8PrivateKey());
    }

    /// The public half of `keys`: safe to send to anyone.
    public static string PublicKey(string keys)
    {
        try
        {
            var p = Parts(keys, "vk1", 2);
            if (p is null) return "";
            using var dh = ECDiffieHellman.Create();
            dh.ImportPkcs8PrivateKey(p[0], out _);
            using var sig = ECDsa.Create();
            sig.ImportPkcs8PrivateKey(p[1], out _);
            return "vp1:" + B64(dh.ExportSubjectPublicKeyInfo()) + ":" + B64(sig.ExportSubjectPublicKeyInfo());
        }
        catch (CryptographicException) { return ""; }
    }

    /// `text` sealed for the holder of `toPublic` and signed with `fromKeys`.
    public static string Seal(string text, string toPublic, string fromKeys)
    {
        try
        {
            var to = Parts(toPublic, "vp1", 2);
            var from = Parts(fromKeys, "vk1", 2);
            if (to is null || from is null) return "";

            using var recipient = ECDiffieHellman.Create();
            recipient.ImportSubjectPublicKeyInfo(to[0], out _);
            using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            byte[] ephemeralPublic = ephemeral.ExportSubjectPublicKeyInfo();
            byte[] key = DeriveKey(ephemeral.DeriveRawSecretAgreement(recipient.PublicKey), ephemeralPublic, to[0]);

            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] plain = Encoding.UTF8.GetBytes(text);
            byte[] cipher = new byte[plain.Length];
            byte[] tag = new byte[16];
            using (var aes = new AesGcm(key, 16))
                aes.Encrypt(nonce, plain, cipher, tag, associatedData: to[0]);

            using var signer = ECDsa.Create();
            signer.ImportPkcs8PrivateKey(from[1], out _);
            byte[] signature = signer.SignData(Signed(ephemeralPublic, nonce, cipher, tag, to[0]), HashAlgorithmName.SHA256);

            return string.Join(":", "vs1", B64(ephemeralPublic), B64(nonce), B64(cipher), B64(tag), B64(signature));
        }
        catch (CryptographicException) { return ""; }
    }

    /// The text of `sealedText` if it was sealed for `keys` and signed by `fromPublic`; "" otherwise.
    public static string Open(string sealedText, string keys, string fromPublic)
    {
        try
        {
            var s = Parts(sealedText, "vs1", 5);
            var mine = Parts(keys, "vk1", 2);
            var from = Parts(fromPublic, "vp1", 2);
            if (s is null || mine is null || from is null) return "";
            byte[] ephemeralPublic = s[0], nonce = s[1], cipher = s[2], tag = s[3], signature = s[4];
            if (nonce.Length != 12 || tag.Length != 16) return "";

            using var me = ECDiffieHellman.Create();
            me.ImportPkcs8PrivateKey(mine[0], out _);
            byte[] myPublic = me.ExportSubjectPublicKeyInfo();

            // Who wrote it first: a message not signed by `fromPublic` is not opened at all.
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(from[1], out _);
            if (!verifier.VerifyData(Signed(ephemeralPublic, nonce, cipher, tag, myPublic), signature, HashAlgorithmName.SHA256))
                return "";

            using var ephemeral = ECDiffieHellman.Create();
            ephemeral.ImportSubjectPublicKeyInfo(ephemeralPublic, out _);
            byte[] key = DeriveKey(me.DeriveRawSecretAgreement(ephemeral.PublicKey), ephemeralPublic, myPublic);

            byte[] plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(nonce, cipher, tag, plain, associatedData: myPublic);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { return ""; }
        catch (FormatException) { return ""; }
    }

    /// A short number two people compare — read aloud, in person — to know a public key is really the
    /// other's: 30 digits in six groups, from SHA-256 of the key (Signal's way of turning a hash into digits).
    public static string Fingerprint(string publicKey)
    {
        if (Parts(publicKey, "vp1", 2) is null) return "";
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(publicKey.Trim()));
        var groups = new string[6];
        for (int g = 0; g < 6; g++)
        {
            long n = 0;
            for (int i = 0; i < 5; i++) n = (n << 8) | h[g * 5 + i];
            groups[g] = (n % 100_000).ToString("D5");
        }
        return string.Join(" ", groups);
    }

    /// `text` locked with a password: for keeping a secret (keys, above all) on disk. PBKDF2 makes guessing
    /// the password slow; AES-GCM makes the result unreadable and tamper-evident.
    public static string Lock(string text, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plain = Encoding.UTF8.GetBytes(text);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        return string.Join(":", "vl1", B64(salt), B64(nonce), B64(cipher), B64(tag));
    }

    /// The text `Lock` locked, with the same password; "" for any other password, or a changed text.
    public static string Unlock(string locked, string password)
    {
        try
        {
            var p = Parts(locked, "vl1", 4);
            if (p is null || p[0].Length != 16 || p[1].Length != 12 || p[3].Length != 16) return "";
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), p[0], Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
            byte[] plain = new byte[p[2].Length];
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(p[1], p[2], p[3], plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { return ""; }
    }

    /// SHA-256 of `text`, as 64 lowercase hex digits.
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// `bytes` random bytes from the operating system's generator, base64url — unguessable ids and secrets.
    /// Between 1 and 1024; anything else answers "".
    public static string Token(long bytes) =>
        bytes is < 1 or > 1024 ? "" : Convert.ToBase64String(RandomNumberGenerator.GetBytes((int)bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---- the format --------------------------------------------------------------------------------

    private static string B64(byte[] b) => Convert.ToBase64String(b);

    /// `version:part:part…` → the parts decoded, or null when the version, the count or the base64 is wrong.
    private static byte[][]? Parts(string text, string version, int count)
    {
        var p = (text ?? "").Trim().Split(':');
        if (p.Length != count + 1 || p[0] != version) return null;
        try { return p.Skip(1).Select(Convert.FromBase64String).ToArray(); }
        catch (FormatException) { return null; }
    }

    private static byte[] DeriveKey(byte[] shared, byte[] ephemeralPublic, byte[] recipientPublic) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt: ephemeralPublic.Concat(recipientPublic).ToArray(), info: SealInfo);

    /// What the sender signs: everything the recipient will rely on, recipient included, so a signed message
    /// cannot be re-addressed to someone else.
    private static byte[] Signed(byte[] ephemeralPublic, byte[] nonce, byte[] cipher, byte[] tag, byte[] recipientPublic) =>
        ephemeralPublic.Concat(nonce).Concat(cipher).Concat(tag).Concat(recipientPublic).ToArray();
}
