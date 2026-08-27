using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace VisionStudio.Ai;

public interface ISecretProtector { string Protect(string plaintext); string Unprotect(string protectedValue); }

public static class SecretProtector {
  public static ISecretProtector CreateDefault() {
    return OperatingSystem.IsWindows()
      ? new WindowsDpapiSecretProtector()
      : new AesFileSecretProtector(Path.Combine(AiSoftwarePaths.ConfigDirectory, ".ai-secrets.key"));
  }
}

internal sealed class WindowsDpapiSecretProtector : ISecretProtector {
  private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VisionStudio.RemoteAiModels.v1");
  public string Protect(string plaintext) {
    var input = new DataBlob(Encoding.UTF8.GetBytes(plaintext)); var entropy = new DataBlob(Entropy);
    try {
      if (!CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 0, out var output)) {
        throw new CryptographicException(Marshal.GetLastWin32Error());
      }

      try { return "dpapi:" + Convert.ToBase64String(output.ToArray()); } finally { output.Free(); }
    } finally { input.Free(); entropy.Free(); }
  }
  public string Unprotect(string protectedValue) {
    if (!protectedValue.StartsWith("dpapi:", StringComparison.Ordinal)) {
      throw new CryptographicException("Unsupported encrypted AI secret format.");
    }

    var input = new DataBlob(Convert.FromBase64String(protectedValue[6..])); var entropy = new DataBlob(Entropy);
    try {
      if (!CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 0, out var output)) {
        throw new CryptographicException(Marshal.GetLastWin32Error());
      }

      try { return Encoding.UTF8.GetString(output.ToArray()); } finally { output.Free(); }
    } finally { input.Free(); entropy.Free(); }
  }
  [StructLayout(LayoutKind.Sequential)]
  private struct DataBlob {
    public int Size; public IntPtr Data;
    public DataBlob(byte[] bytes) { Size = bytes.Length; Data = Marshal.AllocHGlobal(Size); Marshal.Copy(bytes, 0, Data, Size); }
    public readonly byte[] ToArray() { byte[] result = new byte[Size]; Marshal.Copy(Data, result, 0, Size); return result; }
    public void Free() { if (Data == IntPtr.Zero) { return; } Marshal.FreeHGlobal(Data); Data = IntPtr.Zero; Size = 0; }
  }
  [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);
  [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);
}

internal sealed class AesFileSecretProtector(string keyPath) : ISecretProtector {
  public string Protect(string plaintext) {
    byte[] nonce = RandomNumberGenerator.GetBytes(12); byte[] data = Encoding.UTF8.GetBytes(plaintext);
    byte[] cipher = new byte[data.Length]; byte[] tag = new byte[16];
    using var aes = new AesGcm(GetOrCreateKey(), 16); aes.Encrypt(nonce, data, cipher, tag);
    return "aesgcm:" + Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
  }
  public string Unprotect(string protectedValue) {
    if (!protectedValue.StartsWith("aesgcm:", StringComparison.Ordinal)) {
      throw new CryptographicException("Unsupported encrypted AI secret format.");
    }

    byte[] payload = Convert.FromBase64String(protectedValue[7..]);
    if (payload.Length < 28) {
      throw new CryptographicException("Encrypted AI secret is invalid.");
    }

    byte[] plain = new byte[payload.Length - 28];
    using var aes = new AesGcm(GetOrCreateKey(), 16); aes.Decrypt(payload[..12], payload[28..], payload[12..28], plain);
    return Encoding.UTF8.GetString(plain);
  }
  private byte[] GetOrCreateKey() {
    if (File.Exists(keyPath)) {
      return Convert.FromBase64String(File.ReadAllText(keyPath));
    }

    Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!); byte[] key = RandomNumberGenerator.GetBytes(32);
    File.WriteAllText(keyPath, Convert.ToBase64String(key));
    if (!OperatingSystem.IsWindows()) {
      File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    return key;
  }
}
