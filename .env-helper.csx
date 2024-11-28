#r "nuget: Lestaly, 0.69.0"
#nullable enable
using System.Buffers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Lestaly;

public record ServiceCredential(Uri Service, string Username, string Password);
public record ServiceToken(Uri Service, string Token);

/// ファイルと認証情報(ユーザ名/パスワード)を紐づける。
public static async ValueTask<ServiceCredential> BindCredentialAsync(this FileInfo self, string name, Uri service, CancellationToken cancelToken = default)
{
    var scrambler = self.CreateScrambler(context: self.FullName);
    var auth = await scrambler.DescrambleObjectAsync<ServiceCredential>(cancelToken);
    if (auth == null || auth.Service.AbsoluteUri != service.AbsoluteUri)
    {
        WriteLine($"{name} を入力");
        Write("username>"); var user = ReadLine() ?? "";
        Write("password>"); var pass = ReadLine() ?? "";
        auth = new(service, user, pass);
        await scrambler.ScrambleObjectAsync(auth, cancelToken: cancelToken);
    }
    return auth;
}

/// ファイルに認証トークンを保存する。
public static FileRoughScrambler ScriptScrambler(this FileInfo self)
    => self.CreateScrambler(context: self.FullName);

/// ファイルに認証トークンを保存する。
public static async ValueTask<ServiceToken> SaveTokenAsync(this FileRoughScrambler self, Uri service, string token, CancellationToken cancelToken = default)
{
    var auth = new ServiceToken(service, token.Trim());
    await self.ScrambleObjectAsync(auth, cancelToken: cancelToken);
    return auth;
}

/// ファイルに認証トークンを保存する。
public static ValueTask<ServiceToken?> LoadTokenAsync(this FileRoughScrambler self, CancellationToken cancelToken = default)
    => self.DescrambleObjectAsync<ServiceToken>(cancelToken);

/// ファイルと認証トークンを紐づける。
public static async ValueTask<ServiceToken> BindTokenAsync(this FileInfo self, string name, Uri service, CancellationToken cancelToken = default)
{
    var scrambler = self.ScriptScrambler();
    var auth = await scrambler.LoadTokenAsync(cancelToken);
    if (auth == null || auth.Service.AbsoluteUri != service.AbsoluteUri)
    {
        WriteLine($"{name} を入力");
        Write(">"); var token = ReadLine().CancelIfWhite();
        auth = await scrambler.SaveTokenAsync(service, token, cancelToken);
    }
    return auth;
}
