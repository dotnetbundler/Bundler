using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;

#if BUNDLER_UPDATER_LINK
namespace DotNet.Bundler.Updater.Protocol;
#else
namespace DotNet.Bundler.Core.Update;
#endif

/// <summary>
/// ECDSA P-256 更新密钥材料：私钥文件只留发布方（秘密不入库），
/// 公钥以 65 字节未压缩点（0x04|X|Y）的 base64 形式进旁车与清单。
/// </summary>
[DataContract]
public sealed class UpdateKeyMaterial
{
    [DataMember(Name = "kty")] public string Kty { get; set; } = "ec-p256";
    [DataMember(Name = "d")] public string? D { get; set; }
    [DataMember(Name = "x")] public string X { get; set; } = "";
    [DataMember(Name = "y")] public string Y { get; set; } = "";

    public static UpdateKeyMaterial Generate()
    {
        // ECDsa.Create() 的默认曲线是 P-521——P-256 必须显式指定。
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: true);
        return new UpdateKeyMaterial
        {
            D = Convert.ToBase64String(parameters.D!),
            X = Convert.ToBase64String(parameters.Q.X!),
            Y = Convert.ToBase64String(parameters.Q.Y!),
        };
    }

    public static UpdateKeyMaterial Load(string path)
    {
        using var stream = File.OpenRead(path);
        var material = UpdateJson.ReadKeyMaterial(stream);
        static UpdateKeyMaterial Invalid(string path) =>
            throw new InvalidOperationException(
                $"The update signing key '{path}' is not a valid ec-p256 key file.");
        if (material is null || material.Kty != "ec-p256" ||
            string.IsNullOrWhiteSpace(material.X) || string.IsNullOrWhiteSpace(material.Y))
        {
            Invalid(path);
        }
        // 段必须能解码出 32 字节坐标——base64 合法但长度不对的 JSON 同样不是 P-256 公钥。
        byte[] x, y;
        try
        {
            x = Convert.FromBase64String(material!.X!);
            y = Convert.FromBase64String(material.Y!);
        }
        catch (FormatException)
        {
            return Invalid(path);
        }
        return x.Length == 32 && y.Length == 32 ? material : Invalid(path);
    }

    // 旁车/清单里携带的未压缩点（0x04|X|Y）还原出只含公钥的材料。
    public static UpdateKeyMaterial FromPublicPoint(string base64)
    {
        var point = Convert.FromBase64String(base64);
        if (point.Length != 65 || point[0] != 0x04)
        {
            throw new InvalidOperationException(
                "The update public key is not a 65-byte uncompressed P-256 point.");
        }
        return new UpdateKeyMaterial
        {
            X = Convert.ToBase64String(point, 1, 32),
            Y = Convert.ToBase64String(point, 33, 32),
        };
    }

    public void Save(string path)
    {
        SaveCore(path);
#if NET10_0_OR_GREATER
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                // 私钥文件权限收窄到属主，不随 umask。
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception exception)
            {
                FailSecuring(path, exception);
            }
        }
#else
        // netstandard2.0 腿（Core 由老宿主消费时）：同口径 libc chmod 收紧——
        // 私钥落盘权限不能按 TFM 分运；收紧失败则文件保持 umask 权限，
        // 宁可报错删件也不能静默留下 644 私钥。
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
        {
            var secured = false;
            try
            {
                secured = chmod(path, 0x180 /* 0600 */) == 0;
            }
            catch (Exception)
            {
            }
            if (!secured)
            {
                FailSecuring(path, null);
            }
        }
#endif
    }

    private static void FailSecuring(string path, Exception? cause)
    {
        try { File.Delete(path); } catch (Exception) { }
        throw new InvalidOperationException(
            $"Could not restrict the update signing key '{path}' to owner-only permissions — " +
            "the file was removed rather than left with default umask permissions.", cause);
    }

#if !NET10_0_OR_GREATER
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "chmod",
        SetLastError = true)]
    private static extern int chmod(string path, ushort mode);
#endif

    private void SaveCore(string path)
    {
        // 私钥不可静默覆盖：已存在即拒，防误丢既有发布身份。
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        UpdateJson.WriteKeyMaterial(stream, this);
        stream.Write(Encoding.ASCII.GetBytes("\n"), 0, 1);
    }

    public ECParameters ToPrivateParameters()
    {
        if (D is null || D.Length == 0)
        {
            throw new InvalidOperationException("The update key file does not contain a private key.");
        }
        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Convert.FromBase64String(D),
            Q = new ECPoint
            {
                X = Convert.FromBase64String(X),
                Y = Convert.FromBase64String(Y),
            },
        };
    }

    public ECParameters ToPublicParameters() => new()
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint
        {
            X = Convert.FromBase64String(X),
            Y = Convert.FromBase64String(Y),
        },
    };

    public string PublicPointBase64()
    {
        var x = Convert.FromBase64String(X);
        var y = Convert.FromBase64String(Y);
        var point = new byte[1 + x.Length + y.Length];
        point[0] = 0x04;
        Buffer.BlockCopy(x, 0, point, 1, x.Length);
        Buffer.BlockCopy(y, 0, point, 1 + x.Length, y.Length);
        return Convert.ToBase64String(point);
    }
}
