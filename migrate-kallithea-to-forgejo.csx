#r "nuget: KallitheaApiClient, 0.7.0-lib.23.private.1"
#r "nuget: ForgejoApiClient, 9.0.0-rev.2"
#r "nuget: Kokuban, 0.2.0"
#r "nuget: Lestaly, 0.69.0"
#load ".env-helper.csx"
#load ".migrate-helper.csx"
#nullable enable
using System.Text.RegularExpressions;
using KallitheaApiClient.Utils;
using ForgejoApiClient;
using Kokuban;
using Lestaly;
using ForgejoApiClient.Api;
using System.Buffers;
using DocumentFormat.OpenXml.Wordprocessing;
using KallitheaApiClient;
using KallitheaMember = KallitheaApiClient.Member;
using ForgejoErrorResponseException = ForgejoApiClient.ErrorResponseException;

var settings = new
{
    // ドライラン (書き換えしない) モードフラグ
    DryRun = false,

    // Kallithea 関連の情報
    Kallithea = new
    {
        // サービスURL
        ServiceURL = new Uri("http://kallithea.server.home/"),

        // リポジトリ複製の認証情報保存ファイル
        RepoAuthFile = ThisSource.RelativeFile(".auth-kallithea-repo"),

        // APIキー保存ファイル
        ApiTokenFile = ThisSource.RelativeFile(".auth-kallithea-api"),
    },

    // Forgejo 関連の情報
    Forgejo = new
    {
        // サービスURL
        ServiceURL = new Uri("http://localhost:9960"),

        // APIキー保存ファイル
        ApiTokenFile = ThisSource.RelativeFile(".auth-forgejo-api"),
    },

    // リポジトリマイグレーションマッピング
    // 先頭から順に評価。いずれにもマッチしなければマイグレーション対象外。
    RepoMappings = new RepositoryMappings
    {
        new(new(@"^work/server/"), ctx => new(RepoMapType.Org,  "server-manage",   ctx.AfterMatch())),
        new(new(@"^users/(\w+)/"), ctx => new(RepoMapType.User, ctx.MatchGroup(1), ctx.AfterMatch())),
    },

    // メンバーマッピング
    // 先頭から順に評価。いずれにもマッチしなければユーザをユーザに、グループをチームに元の名称で対応付けを試みる。
    MemberMappings = new MemberMappings
    {
        new(new(MemberType.user, "kallithea-name"), new(MapCollaboType.None)),
    },
};


return await Paved.RunAsync(config: o => o.AnyPause(), action: async () =>
{
    using var signal = new SignalCancellationPeriod();
    using var outenc = ConsoleWig.OutputEncodingPeriod(Encoding.UTF8);

    void WriteScriptTitle()
    {
        const string ScriptTitle = "KallitheaからForgejoへのリポジトリマイグレーション";
        WriteLine(ScriptTitle);
        WriteLine($"  Kalliteha : {settings.Kallithea.ServiceURL}");
        WriteLine($"  Forgejo   : {settings.Forgejo.ServiceURL}");
        WriteLine();
    }

    WriteScriptTitle();
    WriteLine("サービス認証情報の準備");
    var kallitheaCred = await settings.Kallithea.RepoAuthFile.BindCredentialAsync("Kallithea リポジトリ認証情報", settings.Kallithea.ServiceURL, signal.Token);
    var kallitheaToken = await settings.Kallithea.ApiTokenFile.BindTokenAsync("Kallithea APIトークン", settings.Kallithea.ServiceURL, signal.Token);
    var forgejoToken = await settings.Forgejo.ApiTokenFile.BindTokenAsync("Forgejo APIトークン", settings.Forgejo.ServiceURL, signal.Token);
    Clear();
    WriteScriptTitle();

    WriteLine("Kallithea クライアントの生成 ...");
    using var kallithea = new SimpleKallitheaClient(new Uri(kallitheaToken.Service, "/_admin/api"), kallitheaToken.Token);
    var kallitheaUser = await kallithea.GetUserAsync(cancelToken: signal.Token);

    WriteLine("Forgejo クライアントの生成 ...");
    using var forgejo = new ForgejoClient(forgejoToken.Service, forgejoToken.Token);
    var forgejoUser = await forgejo.User.GetMeAsync(cancelToken: signal.Token);

    WriteLine("Kallithea リポジトリ一覧の取得 ...");
    var kallitheaRepos = await kallithea.GetReposAsync(cancelToken: signal.Token);

    WriteLine("リポジトリマイグレーション");
    foreach (var repo in kallitheaRepos)
    {
        WriteLine(Chalk.Blue[repo.repo_name]);
        try
        {
            // リポジトリ詳細を取得
            WriteLine(Chalk.Gray["  .. リポジトリ詳細取得"]);
            var details = await kallithea.GetRepoAsync(new(repo.repo_name), cancelToken: signal.Token);

            // マイグレーションマッピング評価
            var mapRepo = settings.RepoMappings.GetMigrateRepo(details.repo.repo_name);
            if (mapRepo == null)
            {
                // マイグレーション対象でない。
                WriteLine("  .. マイグレーション対象外");
                continue;
            }
            else
            {
                WriteLine($"  .. マイグレーション先：{mapRepo.Owner}/{mapRepo.Name}");
            }

            // クローンURL
            var cloneUrl = $"{settings.Kallithea.ServiceURL.AbsoluteUri.TrimEnd('/')}/{details.repo.repo_name}";

            // 非公開リポジトリとするか
            var isPrivate = details.repo.@private || details.members.FirstOrDefault(m => m.type == MemberType.user && m.name == "default")?.permission == nameof(RepoPerm.none);

            // マイグレーションオプションを準備
            var migrateOptions = new MigrateRepoOptions(
                clone_addr: cloneUrl,
                auth_username: kallitheaCred.Username,
                auth_password: kallitheaCred.Password,
                repo_owner: mapRepo.Owner,
                repo_name: mapRepo.Name,
                description: details.repo.description,
                @private: isPrivate
            );

            WriteLine("  .. マイグレーション実行");
            var migrateRepo = await forgejo.Repository.MigrateAsync(migrateOptions, cancelToken: signal.Token);

            WriteLine("  .. 権限のマッピング");
            foreach (var member in details.members)
            {
                // メンバーマッピングを取得
                Write($"  .. .. {member.type}: {member.name}");
                var memberMap = settings.MemberMappings.GetMapMember(member);
                if (memberMap == null || memberMap.Type == MapCollaboType.None || memberMap.Permission == MapCollaboPerm.Auto)
                {
                    WriteLine("マッピング対象外");
                    continue;
                }

                // メンバー権限の反映
                try
                {
                    // メンバー種別に応じたマッピング
                    if (memberMap.Type == MapCollaboType.Team)
                    {
                        // グループを組織のチームへのマッピング
                        if (mapRepo.Type == RepoMapType.User) throw new PavedMessageException("組織リポジトリではない");
                        var teams = await forgejo.Organization.SearchTeamsAsync(mapRepo.Owner);
                        var team = teams.data.FirstOrDefault(t => t.name == memberMap.Name) ?? throw new PavedMessageException($"組織のチーム '{memberMap.Name}' が存在しない");
                        var permission = memberMap.Permission switch
                        {
                            MapCollaboPerm.Admin => AddCollaboratorOptionPermission.Admin,
                            MapCollaboPerm.Write => AddCollaboratorOptionPermission.Write,
                            _ => AddCollaboratorOptionPermission.Read,
                        };
                        await forgejo.Organization.AddTeamRepositoryAsync(team.id!.Value, mapRepo.Owner, mapRepo.Name);
                    }
                    else
                    {
                        // ユーザをリポジトリ共同作業者へのマッピング
                        var permission = memberMap.Permission switch
                        {
                            MapCollaboPerm.Admin => AddCollaboratorOptionPermission.Admin,
                            MapCollaboPerm.Write => AddCollaboratorOptionPermission.Write,
                            _ => AddCollaboratorOptionPermission.Read,
                        };
                        await forgejo.Repository.AddCollaboratorAsync(mapRepo.Owner, mapRepo.Name, memberMap.Name, new(permission: permission));
                    }
                }
                catch (PavedMessageException ex)
                {
                    var color = ex.Kind switch { PavedMessageKind.Warning => Chalk.Yellow, PavedMessageKind.Information => Chalk.Gray, _ => Chalk.Red, };
                    WriteLine(color[ex.Message]);
                }
                catch (Exception ex)
                {
                    WriteLine(Chalk.Red[ex.Message]);
                }
            }
        }
        catch (Exception ex)
        {
            WriteLine(Chalk.Red[$"  .. {ex.Message}"]);
        }
    }
});
