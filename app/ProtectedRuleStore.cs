using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PromptFlipper;

namespace PromptNavigator;

internal sealed class ProtectedRuleStore
{
  internal const string LoadError = "The protected secrets could not be loaded. Sanitizing remains disabled.";
  private const uint CryptProtectUiForbidden = 0x1;
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
  };

  public static string DefaultStoragePath { get; } = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "PromptFlipper",
    "sanitizer-rules.dat");

  public string StoragePath { get; }

  public ProtectedRuleStore() : this(DefaultStoragePath) { }

  public ProtectedRuleStore(string storagePath)
  {
    StoragePath = Path.GetFullPath(storagePath);
  }

  public IEnumerable<SanitizerRule> LoadRules()
  {
    var result = Load();
    if (result.Error is not null) throw new CryptographicException(result.Error);
    return result.Rules;
  }

  public RuleLoadResult Load()
  {
    if (Sanitizer.TryLoadRules(LoadProtectedRules, out var loadedRules))
    {
      return new RuleLoadResult(loadedRules.ToList(), null);
    }

    return new RuleLoadResult([], LoadError);
  }

  private IEnumerable<SanitizerRule> LoadProtectedRules()
  {
    byte[]? clearBytes = null;
    try
    {
      var protectedBytes = File.ReadAllBytes(StoragePath);
      clearBytes = Unprotect(protectedBytes);
      var storedRules = JsonSerializer.Deserialize<List<StoredRule?>>(clearBytes, JsonOptions);
      if (storedRules is null || storedRules.Any(rule => rule?.Name is null || rule.Value is null))
      {
        throw new CryptographicException("The protected secret file is invalid.");
      }

      var loadedRules = storedRules
        .Select(rule => new SanitizerRule(rule!.Name!, rule.Value!))
        .ToList();
      return loadedRules;
    }
    catch (FileNotFoundException) { return []; }
    catch (DirectoryNotFoundException) { return []; }
    finally
    {
      if (clearBytes is not null)
      {
        CryptographicOperations.ZeroMemory(clearBytes);
      }
    }
  }

  public void Save(IEnumerable<SanitizerRule> rulesToSave)
  {
    var storedRules = rulesToSave
      .Select(rule => new StoredRule(rule.Name, rule.Value))
      .ToArray();
    var clearBytes = JsonSerializer.SerializeToUtf8Bytes(storedRules, JsonOptions);
    try
    {
      var protectedBytes = Protect(clearBytes);
      var directory = Path.GetDirectoryName(StoragePath)!;
      Directory.CreateDirectory(directory);
      var temporaryPath = Path.Combine(directory, Path.GetRandomFileName());
      try
      {
        File.WriteAllBytes(temporaryPath, protectedBytes);
        File.Move(temporaryPath, StoragePath, overwrite: true);
      }
      finally
      {
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
      }
    }
    finally
    {
      CryptographicOperations.ZeroMemory(clearBytes);
    }
  }

  private static byte[] Protect(byte[] clearBytes)
  {
    var input = AllocateBlob(clearBytes);
    try
    {
      if (!CryptProtectData(
          ref input,
          "Prompt Flipper sanitizer rules",
          IntPtr.Zero,
          IntPtr.Zero,
          IntPtr.Zero,
          CryptProtectUiForbidden,
          out var output))
      {
        throw new Win32Exception(Marshal.GetLastWin32Error());
      }

      return CopyAndFree(output);
    }
    finally
    {
      ClearAndFree(input, clearBytes.Length);
    }
  }

  private static byte[] Unprotect(byte[] protectedBytes)
  {
    var input = AllocateBlob(protectedBytes);
    try
    {
      if (!CryptUnprotectData(
          ref input,
          IntPtr.Zero,
          IntPtr.Zero,
          IntPtr.Zero,
          IntPtr.Zero,
          CryptProtectUiForbidden,
          out var output))
      {
        throw new Win32Exception(Marshal.GetLastWin32Error());
      }

      return CopyAndFree(output);
    }
    finally
    {
      ClearAndFree(input, protectedBytes.Length);
    }
  }

  private static DataBlob AllocateBlob(byte[] bytes)
  {
    var pointer = Marshal.AllocHGlobal(bytes.Length);
    Marshal.Copy(bytes, 0, pointer, bytes.Length);
    return new DataBlob(bytes.Length, pointer);
  }

  private static byte[] CopyAndFree(DataBlob blob)
  {
    try
    {
      var bytes = new byte[blob.Length];
      Marshal.Copy(blob.Data, bytes, 0, blob.Length);
      return bytes;
    }
    finally
    {
      ClearAndFree(blob, blob.Length);
    }
  }

  private static void ClearAndFree(DataBlob blob, int length)
  {
    if (blob.Data == IntPtr.Zero)
    {
      return;
    }

    Marshal.Copy(new byte[length], 0, blob.Data, length);
    Marshal.FreeHGlobal(blob.Data);
  }

  [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CryptProtectData(
    ref DataBlob dataIn,
    string? description,
    IntPtr optionalEntropy,
    IntPtr reserved,
    IntPtr promptStructure,
    uint flags,
    out DataBlob dataOut);

  [DllImport("Crypt32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CryptUnprotectData(
    ref DataBlob dataIn,
    IntPtr description,
    IntPtr optionalEntropy,
    IntPtr reserved,
    IntPtr promptStructure,
    uint flags,
    out DataBlob dataOut);

  [StructLayout(LayoutKind.Sequential)]
  private readonly struct DataBlob(int length, IntPtr data)
  {
    public readonly int Length = length;
    public readonly IntPtr Data = data;
  }

  // A legacy "private" field in older files is ignored on load and dropped on the next save.
  private sealed record StoredRule(string? Name, string? Value);
}


internal sealed record RuleLoadResult(List<SanitizerRule> Rules, string? Error);
