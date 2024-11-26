#r "nuget: LibGit2Sharp, 0.30.0"
#r "nuget: Kokuban, 0.2.0"
#r "nuget: Lestaly, 0.69.0"
#nullable enable
using System.Text.RegularExpressions;
using Kokuban;
using Lestaly;
using LibGit2Sharp;

var settings = new
{
    // ドライラン (書き換えしない) モードフラグ
    DryRun = true,

    // 書き換えマッピング
    Mapping = new RemoteRewrit[]
    {
        new(new(@"^https?://kallithea.myserver.home/(.*)$"),             m => $"https://kallithea.myserver.home/{m.Groups[1].Value}"),
        new(new(@"^ssh://kallithea@kallithea.myserver.home:2222/(.*)$"), m => $"ssh://git@forgejo.myserver.home:2022/{m.Groups[1].Value}"),
    },
};

/// <summary>リモートURL書き換え情報</summary>
/// <param name="Pattern"></param>
/// <param name="Replacer"></param>
record RemoteRewrit(Regex Pattern, Func<Match, string> Replacer);

return await Paved.RunAsync(config: o => o.AnyPause(), action: async () =>
{
    await Task.CompletedTask;

    using var signal = new SignalCancellationPeriod();

    Write("Gitリモートアドレスの書き換え");
    if (settings.DryRun) Write(" (dry-run)");
    WriteLine();
    WriteLine();

    WriteLine("リポジトリ検索ディレクトリの入力"); Write(">");
    var input = ReadLine().CancelIfWhite();
    var directory = CurrentDir.RelativeDirectory(input);

    // ディレクトリ検索動作オプション
    var options = new SelectFilesOptions(
        Recurse: true,
        Handling: SelectFilesHandling.OnlyDirectory,
        Sort: false,
        Buffered: false,
        SkipInaccessible: true
    );

    // Gitディレクトリを検索
    var modeMerker = settings.DryRun ? "(dry-run) " : "";
    directory.DoFiles(options: options, processor: walker =>
    {
        // リポジトリでないディレクトリをスキップ
        if (!Repository.IsValid(walker.Directory.FullName)) return;

        // リポジトリを見つけたらそれ以上は辿らない。サブリポジトリ？しりません。
        walker.Break = true;

        // リポジトリパス表示
        WriteLine($"{walker.Directory.FullName}");

        // リポジトリの処理
        var repo = new Repository(walker.Directory.FullName);
        var rewrite = false;
        foreach (var remote in repo.Network.Remotes)
        {
            // fetch の置き換えURLを取得
            var newFetch = replaceUrl(remote.Url);
            if (newFetch != null)
            {
                rewrite = true;
                WriteLine($"  Url:");
                WriteLine($"    Original: {Chalk.Blue[remote.Url]}");
                WriteLine($"    Rewrite : -->{modeMerker}{Chalk.Blue[newFetch]}");
            }

            // push の置き換えURLを取得
            var newPush = remote.Url == remote.PushUrl ? default : replaceUrl(remote.PushUrl);
            if (newPush != null)
            {
                rewrite = true;
                WriteLine($"  PushUrl:");
                WriteLine($"    Original: {Chalk.Blue[remote.PushUrl]}");
                WriteLine($"    Rewrite : -->{modeMerker}{Chalk.Blue[newPush]}");
            }

            // ドライランでない場合、必要であれば実際の置き換えを行う。
            if (!settings.DryRun && (newFetch != null || newPush != null))
            {
                repo.Network.Remotes.Update(remote.Name, updater =>
                {
                    if (newFetch != null) updater.Url = newFetch;
                    if (newPush != null) updater.PushUrl = newPush;
                });
            }
        }

        // 置き換えが無い場合はその旨を表示
        if (!rewrite)
        {
            WriteLine(Chalk.Gray["  Not match"]);
        }
    });

});

string? replaceUrl(string url)
{
    if (!string.IsNullOrWhiteSpace(url))
    {
        foreach (var map in settings.Mapping)
        {
            var match = map.Pattern.Match(url);
            if (!match.Success) continue;
            return map.Replacer(match);
        }
    }
    return default;
}