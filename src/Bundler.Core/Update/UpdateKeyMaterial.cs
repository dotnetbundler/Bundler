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
        if (material is null || material.Kty != "ec-p256" ||
            material.X.Length == 0 || material.Y.Length == 0)
        {
            throw new InvalidOperationException(
                $"The update signing key '{path}' is not a valid ec-p256 key file.");
        }
        return material;
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
        using var stream = File.Create(path);
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
