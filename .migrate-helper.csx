#r "nuget: Lestaly, 0.69.0"
#r "nuget: KallitheaApiClient, 0.7.0-lib.23.private.1"
#nullable enable
using System.Buffers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using KallitheaApiClient;
using Lestaly;

using KallitheaMember = KallitheaApiClient.Member;
using KallitheaMemberType = KallitheaApiClient.MemberType;

/// <summary>マッピング先リポジトリ種別</summary>
public enum RepoMapType { User, Org, }

/// <summary>リポジトリマッピング仲介</summary>
public record RepoMapAdapter(string Path, Match Match)
{
    public string AfterMatch() => this.Path[(this.Match.Index + this.Match.Length)..];
    public string BeforeMatch() => this.Path[..this.Match.Index];
    public string MatchGroup(int pos) => this.Match.Groups[pos].Value;
    public string MatchGroup(string name) => this.Match.Groups[name].Value;
}
/// <summary>マッピング先リポジトリ種別</summary>
public record RepoMapTarget(RepoMapType Type, string Owner, string Name);

/// <summary>リポジトリマッピング定義</summary>
public record RepoMapDefine(Regex Pattern, Func<RepoMapAdapter, RepoMapTarget> Mapper);

/// <summary>リポジトリ</summary>
public class RepositoryMappings : List<RepoMapDefine>
{
    /// <summary>マイグレーション先リポジトリ情報を取得する</summary>
    /// <param name="orgPath">Kallitheaリポジトリパス</param>
    /// <returns>マイグレーション先リポジトリ情報。対象でない場合は null を返す。</returns>
    public RepoMapTarget? GetMigrateRepo(string orgPath)
    {
        foreach (var define in this)
        {
            // パターンにマッチするかチェック。マッチしなければ次へ。
            var path = orgPath.TrimStart('/');
            var match = define.Pattern.Match(path);
            if (!match.Success) continue;

            // マッピング先を取得。空の結果を得たらマッピング対象外とする。
            var mapAdapter = new RepoMapAdapter(path, match);
            var mapRepo = define.Mapper(mapAdapter);
            if (string.IsNullOrWhiteSpace(mapRepo.Owner) || string.IsNullOrWhiteSpace(mapRepo.Name)) return default;

            // リポジトリパスをforgejoで使える名称に変換
            var validName = GetAlphaDashDot(mapRepo.Name);
            return new(mapRepo.Type, mapRepo.Owner, validName);
        }
        return default;
    }

    /// <summary>リポジトリパスをForgejoで有効なリポジトリ名に変換する</summary>
    /// <param name="path">リポジトリパス</param>
    /// <returns>forgejoで有効なリポジトリ名。(ただし長さの制限を満たさない可能性はある)</returns>
    public string GetAlphaDashDot(string path)
    {
        var buffer = new ArrayBufferWriter<char>();
        foreach (var rune in path.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune) || rune.Value == '.' || rune.Value == '_' || rune.Value == '-')
            {
                var span = buffer.GetSpan();
                var written = rune.EncodeToUtf16(span);
                buffer.Advance(written);
            }
            else if (rune.Value == '/')
            {
                buffer.Write(".");
            }
            else
            {
                buffer.Write($"u{rune.Value:X4}");
            }
        }
        return new string(buffer.WrittenSpan);
    }
}

/// <summary>Forgejo共同作業者種別</summary>
public enum MapCollaboType { None, User, Team, }

/// <summary>Forgejo共同作業者許可種別</summary>
public enum MapCollaboPerm { Auto, Read, Write, Admin, }

/// <summary>Kallitheaメンバー情報(マッピング元)</summary>
public record MemberKallithea(KallitheaMemberType Type, string Name);

/// <summary>Forgejo共同作業者情報(マッピング先)</summary>
public record MemberForgejo(MapCollaboType Type, string Name = "", MapCollaboPerm Permission = MapCollaboPerm.Auto);

/// <summary>リポジトリアクセス許可マッピング定義</summary>
public record MemberMapDefine(MemberKallithea Kallithea, MemberForgejo Forgejo);

/// <summary>リポジトリアクセス許可マッピング</summary>
public class MemberMappings : List<MemberMapDefine>
{
    /// <summary>KallitehaのリポジトリメンバをForgejo共同作業者にマッピングする</summary>
    /// <param name="member">Kallitehaリポジトリメンバ</param>
    /// <returns>Forgejoのマップ先共同作業者</returns>
    public MemberForgejo GetMapMember(KallitheaMember member)
    {
        // 元メンバに許可なしの場合はマッピングなし
        if (member.permission == "none") return new(MapCollaboType.Team);

        // とりあえず、マッピング定義を取得。定義があればそれを有効とする。
        var map = this.FindMapMember(member);
        if (map == null)
        {
            // 定義がない場合、リポジトリメンバの種別に応じて自動で決める。
            if (member.type == KallitheaMemberType.user)
            {
                // ユーザはユーザにマップ。
                // ただし default に対する権限は特別扱い。定義がない場合はマップ先無しにする。
                map = member.name == "default" ? new(MapCollaboType.None) : new(MapCollaboType.User);
            }
            else if (member.type == KallitheaMemberType.user_group)
            {
                // ユーザグループはチームにマップ。
                map = new(MapCollaboType.Team);
            }
            else
            {
                // それ以外の場合はないはずだが、マッピング無しに落としておく。
                map = new(MapCollaboType.None);
            }
        }

        // マッピング無しの場合は他の情報は不要なのでそのまま返却。
        if (map.Type == MapCollaboType.None) return map;

        // 元から引きつぐ設定の場合はそれを解決する
        var collaborator = new MemberForgejo(
            map.Type,
            string.IsNullOrWhiteSpace(map.Name) ? member.name : map.Name,
            map.Permission == MapCollaboPerm.Auto ? ConvertPermission(member) : map.Permission
        );

        return collaborator;
    }

    public MemberForgejo? FindMapMember(KallitheaMember member)
        => this.FirstOrDefault(m => m.Kallithea.Type == member.type && m.Kallithea.Name == member.name)?.Forgejo;

    public MapCollaboPerm ConvertPermission(KallitheaMember member)
    {
        if (member.type == KallitheaMemberType.user_group)
        {
            return member.permission switch
            {
                nameof(RepoGroupPerm.admin) => MapCollaboPerm.Admin,
                nameof(RepoGroupPerm.write) => MapCollaboPerm.Write,
                nameof(RepoGroupPerm.read) => MapCollaboPerm.Read,
                _ => MapCollaboPerm.Auto,
            };
        }
        return member.permission switch
        {
            nameof(RepoPerm.admin) => MapCollaboPerm.Admin,
            nameof(RepoPerm.write) => MapCollaboPerm.Write,
            nameof(RepoPerm.read) => MapCollaboPerm.Read,
            _ => MapCollaboPerm.Auto,
        };
    }


}