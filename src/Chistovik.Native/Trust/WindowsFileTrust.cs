using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Chistovik.Core.Ports;
using Microsoft.Win32.SafeHandles;

namespace Chistovik.Native.Trust;

/// <summary>
/// Проверка подписи файла через WinVerifyTrust: сначала встроенная подпись, затем подпись через каталог безопасности
/// (так подписано большинство файлов Windows). Проверка отзыва отключена и сетевые запросы не делаются (только кеш),
/// поэтому результат детерминирован офлайн.
/// MicrosoftSigned: подпись валидна, организация (O) листового сертификата подписанта — «Microsoft Corporation»,
/// а корень цепочки — «Microsoft Root ...». Это сырой факт; доверие решает Core.
/// </summary>
public sealed class WindowsFileTrust : IFileTrust
{
    private const uint WtdUiNone = 2;
    private const uint WtdChoiceFile = 1;
    private const uint WtdChoiceCatalog = 2;
    private const uint WtdStateVerify = 1;
    private const uint WtdStateClose = 2;
    private const uint WtdRevocationCheckNone = 0x10;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustESubjectNotTrusted = unchecked((int)0x800B0004);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public SignatureFacts GetSignature(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) return new SignatureFacts(false, false);

        var embedded = Verify(path, catalog: null);
        if (embedded.Valid || !IsNoSignature(embedded.Status)) return embedded.ToFacts();

        // Встроенной подписи нет: ищем каталог безопасности по хешу файла.
        var viaCatalog = VerifyViaCatalog(path);
        return (viaCatalog ?? embedded).ToFacts();
    }

    private static bool IsNoSignature(int status) =>
        status is TrustENoSignature or TrustEProviderUnknown or TrustESubjectFormUnknown or TrustESubjectNotTrusted;

    private readonly record struct Outcome(int Status, bool Microsoft)
    {
        public bool Valid => Status == 0;
        public SignatureFacts ToFacts() => new(Valid, Valid && Microsoft);
    }

    private sealed record CatalogRef(string CatalogFile, string MemberTag, byte[] Hash, IntPtr Admin);

    private static Outcome Verify(string file, CatalogRef? catalog)
    {
        var allocs = new List<IntPtr>();
        try
        {
            IntPtr info;
            uint choice;
            if (catalog is null)
            {
                var fi = new WintrustFileInfo
                {
                    cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                    pcwszFilePath = Str(file, allocs),
                };
                info = Struct(fi, allocs);
                choice = WtdChoiceFile;
            }
            else
            {
                var ci = new WintrustCatalogInfo
                {
                    cbStruct = (uint)Marshal.SizeOf<WintrustCatalogInfo>(),
                    pcwszCatalogFilePath = Str(catalog.CatalogFile, allocs),
                    pcwszMemberTag = Str(catalog.MemberTag, allocs),
                    pcwszMemberFilePath = Str(file, allocs),
                    pbCalculatedFileHash = Bytes(catalog.Hash, allocs),
                    cbCalculatedFileHash = (uint)catalog.Hash.Length,
                    hCatAdmin = catalog.Admin,
                };
                info = Struct(ci, allocs);
                choice = WtdChoiceCatalog;
            }

            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = WtdUiNone,
                dwUnionChoice = choice,
                pUnion = info,
                dwStateAction = WtdStateVerify,
                dwProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval,
            };

            var invalidHwnd = new IntPtr(-1);
            var action = GenericVerifyV2;
            var status = WinVerifyTrust(invalidHwnd, ref action, ref data);
            try
            {
                var microsoft = status == 0 && IsMicrosoftChain(data.hWVTStateData);
                return new Outcome(status, microsoft);
            }
            finally
            {
                data.dwStateAction = WtdStateClose;
                WinVerifyTrust(invalidHwnd, ref action, ref data);
            }
        }
        finally { foreach (var p in allocs) Marshal.FreeHGlobal(p); }
    }

    private static Outcome? VerifyViaCatalog(string file)
    {
        foreach (var algorithm in new[] { "SHA256", "SHA1" })
        {
            if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0)) continue;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                uint hashLen = 0;
                CryptCATAdminCalcHashFromFileHandle2(admin, fs.SafeFileHandle, ref hashLen, null, 0);
                if (hashLen == 0) continue;
                var hash = new byte[hashLen];
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, fs.SafeFileHandle, ref hashLen, hash, 0)) continue;

                var prev = IntPtr.Zero;
                var cat = CryptCATAdminEnumCatalogFromHash(admin, hash, hashLen, 0, ref prev);
                while (cat != IntPtr.Zero)
                {
                    var ci = new CatalogInfo { cbStruct = (uint)Marshal.SizeOf<CatalogInfo>(), wszCatalogFile = new string('\0', 260) };
                    if (CryptCATCatalogInfoFromContext(cat, ref ci, 0))
                    {
                        var tag = Convert.ToHexString(hash, 0, (int)hashLen);
                        var outcome = Verify(file, new CatalogRef(ci.wszCatalogFile.TrimEnd('\0'), tag, hash[..(int)hashLen], admin));
                        if (outcome.Valid)
                        {
                            CryptCATAdminReleaseCatalogContext(admin, cat, 0);
                            return outcome;
                        }
                    }
                    prev = cat;
                    cat = CryptCATAdminEnumCatalogFromHash(admin, hash, hashLen, 0, ref prev);
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            finally { CryptCATAdminReleaseContext(admin, 0); }
        }
        return null;
    }

    /// <summary>Цепочка подписанта из состояния WinVerifyTrust: лист — «Microsoft Corporation», корень — «Microsoft Root».</summary>
    private static bool IsMicrosoftChain(IntPtr state)
    {
        if (state == IntPtr.Zero) return false;
        var prov = WTHelperProvDataFromStateData(state);
        if (prov == IntPtr.Zero) return false;
        var sgnr = WTHelperGetProvSignerFromChain(prov, 0, false, 0);
        if (sgnr == IntPtr.Zero) return false;

        var signer = Marshal.PtrToStructure<ProviderSigner>(sgnr);
        if (signer.csCertChain == 0 || signer.pasCertChain == IntPtr.Zero) return false;

        var stride = Marshal.SizeOf<ProviderCert>();
        X509Certificate2? leaf = null, root = null;
        try
        {
            var first = Marshal.PtrToStructure<ProviderCert>(signer.pasCertChain);
            var last = Marshal.PtrToStructure<ProviderCert>(signer.pasCertChain + (int)(signer.csCertChain - 1) * stride);
            if (first.pCert == IntPtr.Zero || last.pCert == IntPtr.Zero) return false;
            leaf = new X509Certificate2(first.pCert);
            root = new X509Certificate2(last.pCert);
            return OrganizationOf(leaf) == "Microsoft Corporation"
                   && CommonNameOf(root) is { } cn && cn.StartsWith("Microsoft Root", StringComparison.Ordinal);
        }
        finally
        {
            leaf?.Dispose();
            root?.Dispose();
        }
    }

    private static string? OrganizationOf(X509Certificate2 cert) => Rdn(cert, "2.5.4.10");
    private static string? CommonNameOf(X509Certificate2 cert) => Rdn(cert, "2.5.4.3");

    private static string? Rdn(X509Certificate2 cert, string oid)
    {
        foreach (var rdn in cert.SubjectName.EnumerateRelativeDistinguishedNames())
            if (rdn.GetSingleElementType().Value == oid) return rdn.GetSingleElementValue();
        return null;
    }

    private static IntPtr Str(string s, List<IntPtr> allocs)
    {
        var p = Marshal.StringToHGlobalUni(s);
        allocs.Add(p);
        return p;
    }

    private static IntPtr Bytes(byte[] b, List<IntPtr> allocs)
    {
        var p = Marshal.AllocHGlobal(b.Length);
        allocs.Add(p);
        Marshal.Copy(b, 0, p, b.Length);
        return p;
    }

    private static IntPtr Struct<T>(T value, List<IntPtr> allocs) where T : struct
    {
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        allocs.Add(p);
        Marshal.StructureToPtr(value, p, false);
        return p;
    }

    // ---------- P/Invoke ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath, hFile, pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustCatalogInfo
    {
        public uint cbStruct, dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath, pcwszMemberTag, pcwszMemberFilePath, hMemberFile, pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext, hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pUnion;
        public uint dwStateAction;
        public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile;
    }

    // CRYPT_PROVIDER_SGNR (wintrust.h)
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderSigner
    {
        public uint cbStruct, verifyLow, verifyHigh, csCertChain;
        public IntPtr pasCertChain;
        public uint dwSignerType;
        public IntPtr psSigner;
        public uint dwError, csCounterSigners;
        public IntPtr pasCounterSigners, pChainContext;
    }

    // CRYPT_PROVIDER_CERT (wintrust.h)
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCert
    {
        public uint cbStruct;
        public IntPtr pCert;
        public int fCommercial, fTrustedRoot, fSelfSigned, fTestCert;
        public uint dwRevokedReason, dwConfidence, dwError;
        public IntPtr pTrustListContext;
        public int fTrustListSignerCert;
        public IntPtr pCtlContext;
        public uint dwCtlError;
        public int fIsCyclic;
        public IntPtr pChainElement;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, uint signerIdx,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIdx);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr subsystem, string algorithm, IntPtr policy, uint flags);

    [DllImport("wintrust.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, SafeFileHandle file, ref uint hashLen, byte[]? hash, uint flags);

    [DllImport("wintrust.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte[] hash, uint hashLen, uint flags, ref IntPtr prev);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr catalog, ref CatalogInfo info, uint flags);

    [DllImport("wintrust.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catalog, uint flags);

    [DllImport("wintrust.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);
}
