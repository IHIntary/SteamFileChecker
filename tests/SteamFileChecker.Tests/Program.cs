using SteamFileChecker.Core;
using SteamKit2;
using System.Security.Cryptography;
using System.Text;

var root = Path.Combine(Path.GetTempPath(), "SteamFileChecker-tests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var passed = 0;
void Check(bool success, string name) { if (!success) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
OfficialFile Official(string path, string content, string source = "base") => new(path, Encoding.UTF8.GetByteCount(content), Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(content))), source);
var game = new Game(1, "Fixture", root, "1", "public", Path.Combine(root, "absent.acf"), "", [], false);
try
{
    await File.WriteAllTextAsync(Path.Combine(root,"base.bin"),"base");
    await File.WriteAllTextAsync(Path.Combine(root,"dlc.bin"),"dlc");
    await File.WriteAllTextAsync(Path.Combine(root,"mod.bin"),"mod");
    var files = new List<OfficialFile> { Official("base.bin","base"), Official("dlc.bin","dlc","DLC"), Official("missing.bin","missing") };
    var full = new Snapshot(files, [], true, []);
    var report = await Verifier.Run(game, full, null, default);
    Check(report.Files.Single(f=>f.Path=="dlc.bin").Status=="match", "installed DLC is not extra");
    Check(report.Files.Single(f=>f.Path=="mod.bin").Status=="extra", "complete scope permits extra");
    Check(report.Files.Single(f=>f.Path=="missing.bin").Status=="missing", "missing official file");
    var partial = await Verifier.Run(game, new([Official("base.bin","base")],[],false,["DLC unavailable"]),null,default);
    Check(partial.Files.Single(f=>f.Path=="dlc.bin").Status=="uncertain", "failed DLC manifest never produces extra");
    var conflict = await Verifier.Run(game, new([..files,Official("base.bin","other","DLC")],[],true,[]),null,default);
    Check(conflict.Files.Single(f=>f.Path=="base.bin").Status=="uncertain", "conflicting depot paths stay uncertain");
    await File.WriteAllTextAsync(Path.Combine(root,"base.bin"),"BASE");
    var changed = await Verifier.Run(game, full,null,default);
    Check(changed.Files.Single(f=>f.Path=="base.bin").Status=="changed", "same-size modifications require hash comparison");
    foreach(var path in new[]{"../escape","C:/escape","/escape","folder/../escape","a:b","a./b"})
    {
        try { Verifier.Normalize(path); throw new Exception("Unsafe path accepted: "+path); } catch(InvalidDataException) { passed++; }
    }
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    try { await Verifier.Run(game,full,null,cancel.Token); throw new Exception("Cancellation ignored"); } catch(OperationCanceledException) { passed++; }
    // Quick mode: names and sizes only. base.bin now holds "BASE" (same size as official "base").
    var quick = await Verifier.Run(game, full, null, default, VerifyMode.Quick);
    Check(quick.Mode=="quick"&&changed.Mode=="full","report records the verification mode");
    Check(quick.Files.Single(f=>f.Path=="base.bin").Status=="match"&&quick.Files.Single(f=>f.Path=="base.bin").LocalHash==null,"quick mode treats same-size files as match without hashing");
    Check(quick.Files.Single(f=>f.Path=="missing.bin").Status=="missing"&&quick.Files.Single(f=>f.Path=="mod.bin").Status=="extra","quick mode still finds missing and extra files");
    await File.WriteAllTextAsync(Path.Combine(root,"dlc.bin"),"dlc-longer");
    Check((await Verifier.Run(game, full, null, default, VerifyMode.Quick)).Files.Single(f=>f.Path=="dlc.bin").Status=="changed","quick mode reports size differences as changed");
    await File.WriteAllTextAsync(Path.Combine(root,"dlc.bin"),"dlc");
    var steam = Path.Combine(root,"steam"); var apps = Path.Combine(steam,"steamapps"); Directory.CreateDirectory(apps);
    var acf = Path.Combine(apps,"appmanifest_1.acf");
    await File.WriteAllTextAsync(acf,"\"AppState\" { \"appid\" \"1\" \"name\" \"Fixture\" \"installdir\" \"Fixture\" \"StateFlags\" \"4\" \"buildid\" \"99\" \"InstalledDepots\" { \"12\" { \"manifest\" \"18446744073709551615\" } \"13\" { \"manifest\" \"5\" \"dlcappid\" \"77\" } } }");
    var found = Library.Discover(steam);
    Check(found.Count==1&&found[0].Depots[0].ManifestId=="18446744073709551615"&&!found[0].Updating,"ACF discovery and depot parsing");
    await File.WriteAllTextAsync(Path.Combine(apps,"libraryfolders.vdf"), $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{steam.Replace('\\','/')}\" }} }}");
    Check(Library.Discover(steam).Count==1,"slash variants of same library are deduplicated");
    Check(found[0].Depots.Single(d=>d.Id==12).DlcAppId==null&&found[0].Depots.Single(d=>d.Id==13).DlcAppId==77u,"ACF dlcappid preserved per depot");
    Check(new DepotOrigin(1,77,false,0,false,0).Candidates().SequenceEqual(new uint[]{77,1}),"unconfirmed DLC depot tries local DLC AppID before base");
    Check(new DepotOrigin(1,77,false,0,true,0).Candidates().SequenceEqual(new uint[]{77,1}),"depot in DLC config uses DLC AppID first");
    Check(new DepotOrigin(1,77,true,0,false,0).Candidates().SequenceEqual(new uint[]{1,77}),"depot in base config uses base AppID first");
    Check(new DepotOrigin(1,0,true,5,false,0).Candidates().SequenceEqual(new uint[]{5,1}),"shared depot uses depotfromapp");
    var notFound = DepotOrigin.DescribeKeyFailure(13,[(77u,EResult.FileNotFound),(1u,EResult.FileNotFound)]);
    Check(notFound.Contains("FileNotFound")&&!notFound.StartsWith("账号无权"),"FileNotFound is not reported as no permission");
    Check(DepotOrigin.DescribeKeyFailure(13,[(77u,EResult.AccessDenied)]).StartsWith("账号无权"),"AccessDenied is reported as no permission");
    // Shared depots (Steamworks Common Redistributables etc.)
    var sharedAcf = Path.Combine(apps,"appmanifest_2.acf");
    await File.WriteAllTextAsync(sharedAcf,"\"AppState\" { \"appid\" \"2\" \"name\" \"Shared\" \"installdir\" \"Shared\" \"StateFlags\" \"4\" \"InstalledDepots\" { \"21\" { \"manifest\" \"9\" } } \"SharedDepots\" { \"228989\" \"228980\" \"228990\" \"228980\" } }");
    var sharedGame = Library.ReadGame(sharedAcf);
    Check(sharedGame.SharedDepots!.Count==2&&sharedGame.SharedDepots[0]==new SharedDepot(228989,228980)&&sharedGame.Depots.Count==1,"ACF SharedDepots parsed separately from installed depots");
    Check(found[0].SharedDepots!.Count==0,"ACF without SharedDepots yields none");
    var note = SteamSession.SharedDepotNote(228980,[228989u,228990u]);
    Check(note.Contains("228989")&&note.Contains("Steamworks Shared")&&note.Contains("不纳入"),"shared depots are described as outside the game folder, not compared");
    // Steam manifests record an all-zero hash for empty files.
    var emptyRoot = Path.Combine(root,"empty-game"); Directory.CreateDirectory(emptyRoot);
    await File.WriteAllTextAsync(Path.Combine(emptyRoot,"keep.txt"),"");
    await File.WriteAllTextAsync(Path.Combine(emptyRoot,"zero.bin"),"x");
    var zero = new string('0',40);
    var emptyReport = await Verifier.Run(game with { Path = emptyRoot },new([new("keep.txt",0,zero,"base"),new("zero.bin",1,zero,"base")],[],true,[]),null,default);
    Check(emptyReport.Files.Single(f=>f.Path=="keep.txt").Status=="match","empty file with all-zero manifest hash matches");
    Check(emptyReport.Files.Single(f=>f.Path=="zero.bin").Status=="changed","non-empty file never matches an all-zero hash");
    // Deleting extra files: only report extras, unchanged, inside the game folder.
    var delRoot = Path.Combine(root,"delete-game"); Directory.CreateDirectory(Path.Combine(delRoot,"Saved"));
    await File.WriteAllTextAsync(Path.Combine(delRoot,"game.bin"),"game");
    await File.WriteAllTextAsync(Path.Combine(delRoot,"Saved","a.log"),"log");
    await File.WriteAllTextAsync(Path.Combine(delRoot,"Saved","b.log"),"log");
    await File.WriteAllTextAsync(Path.Combine(root,"outside.txt"),"out");
    var delGame = game with { Path = delRoot };
    var delReport = await Verifier.Run(delGame,new([Official("game.bin","game")],[],true,[]),null,default);
    var forged = delReport with { Files = [..delReport.Files, new("../outside.txt","extra",3,null,null,null,"未匹配")] };
    await File.WriteAllTextAsync(Path.Combine(delRoot,"Saved","b.log"),"longer log");
    var plan = ExtraFileCleaner.Plan(delGame,forged,["Saved/a.log","Saved/b.log","game.bin","nope.txt","../outside.txt","saved/A.LOG"]);
    Check(plan.Files.Count==1&&plan.Files[0].Relative=="Saved/a.log"&&plan.Files[0].FullPath==Path.Combine(delRoot,"Saved","a.log"),"delete plan accepts only unchanged report extras (case-insensitive dedupe)");
    Check(plan.Skipped.Single(x=>x.Relative=="Saved/b.log").Reason.Contains("大小已变化"),"file changed since scan is not deleted");
    Check(plan.Skipped.Single(x=>x.Relative=="game.bin").Reason.Contains("不是本次报告中的额外文件"),"official (matching) file is never deleted");
    Check(plan.Skipped.Any(x=>x.Relative=="nope.txt")&&plan.Skipped.Any(x=>x.Relative=="../outside.txt"),"unknown and escaping paths are rejected");
    try { ExtraFileCleaner.Plan(delGame,delReport with { ScopeComplete = false },["Saved/a.log"]); throw new Exception("Incomplete scope accepted"); } catch(InvalidOperationException) { passed++; }
    var after = ExtraFileCleaner.WithoutDeleted(delReport,["Saved/a.log","game.bin"]);
    Check(after.Files.All(f=>f.Path!="Saved/a.log")&&after.Files.Any(f=>f.Path=="game.bin"),"report drops only deleted extra rows");
    // Steam library artwork from the local cache.
    var art = Path.Combine(root,"artsteam","appcache","librarycache");
    var hashDir = Path.Combine(art,"2507950","d6f98dfe9bd9d177c67af510e5a360ec002a4bd2"); Directory.CreateDirectory(hashDir);
    await File.WriteAllTextAsync(Path.Combine(hashDir,"library_hero_blur.jpg"),"blur");
    await File.WriteAllTextAsync(Path.Combine(hashDir,"library_hero.jpg"),"hero");
    await File.WriteAllTextAsync(Path.Combine(art,"2507950","52bc6c4bf0c232cf30d66eb3d1638bf099bc6061.jpg"),"icon");
    var steamRootArt = Path.Combine(root,"artsteam");
    Check(Path.GetFileName(SteamArtwork.Find(steamRootArt,2507950,SteamArtwork.Kind.Hero))=="library_hero.jpg","hero art found in hashed subfolder, blurred copy skipped");
    Check(Path.GetFileName(SteamArtwork.Find(steamRootArt,2507950,SteamArtwork.Kind.Icon))=="52bc6c4bf0c232cf30d66eb3d1638bf099bc6061.jpg","library icon is the hash-named jpg in the app folder");
    Check(Path.GetFileName(SteamArtwork.Find(steamRootArt.Replace('\\','/'),2507950,SteamArtwork.Kind.Icon))=="52bc6c4bf0c232cf30d66eb3d1638bf099bc6061.jpg","icon found when Steam root uses forward slashes (registry form)");
    var localized = Path.Combine(art,"431960","x"); Directory.CreateDirectory(localized);
    await File.WriteAllTextAsync(Path.Combine(localized,"library_hero_schinese.jpg"),"hero");
    Check(Path.GetFileName(SteamArtwork.Find(steamRootArt,431960,SteamArtwork.Kind.Hero))=="library_hero_schinese.jpg","localized hero art accepted");
    var headerOnly = Path.Combine(art,"228980"); Directory.CreateDirectory(headerOnly);
    await File.WriteAllTextAsync(Path.Combine(headerOnly,"header.jpg"),"header");
    Check(Path.GetFileName(SteamArtwork.Find(steamRootArt,228980,SteamArtwork.Kind.Hero))=="header.jpg","header used when no hero exists");
    await File.WriteAllTextAsync(Path.Combine(art,"10_library_hero.jpg"),"legacy");
    await File.WriteAllTextAsync(Path.Combine(art,"10_icon.jpg"),"legacy");
    Check(SteamArtwork.Find(steamRootArt,10,SteamArtwork.Kind.Hero)!.EndsWith("10_library_hero.jpg")&&SteamArtwork.Find(steamRootArt,10,SteamArtwork.Kind.Icon)!.EndsWith("10_icon.jpg"),"legacy flat cache layout supported");
    Check(SteamArtwork.Find(steamRootArt,99,SteamArtwork.Kind.Hero)==null&&SteamArtwork.Find(Path.Combine(root,"nosteam"),2507950,SteamArtwork.Kind.Icon)==null,"missing artwork returns null");
    // No-login mode: depot keys from config.vdf, manifests from depotcache.
    var localSteam = Path.Combine(root,"localsteam"); Directory.CreateDirectory(Path.Combine(localSteam,"config"));
    var key = new string('a',64);
    await File.WriteAllTextAsync(Path.Combine(localSteam,"config","config.vdf"),$"\"InstallConfigStore\" {{ \"Software\" {{ \"Valve\" {{ \"Steam\" {{ \"depots\" {{ \"12\" {{ \"DecryptionKey\" \"{key}\" }} \"13\" {{ \"DecryptionKey\" \"bad\" }} }} }} }} }} }}");
    var keys = LocalManifests.ReadDepotKeys(localSteam.Replace('\\','/'));
    Check(keys.Count==1&&keys[12].Length==32,"depot keys read from config.vdf, malformed keys ignored");
    Check(LocalManifests.ReadDepotKeys(Path.Combine(root,"nosteam")).Count==0,"missing config.vdf gives no keys");
    var localGame = found[0] with { Path = Path.Combine(root,"localgame") };
    var local = LocalManifests.Resolve(localGame, localSteam, null, default);
    Check(local.Source=="local"&&!local.Complete&&local.Depots.All(d=>!d.Available&&d.Error!.Contains("depotcache")),"missing local manifest marks the depot unavailable and the scope incomplete");
    Directory.CreateDirectory(localGame.Path);
    Check((await Verifier.Run(localGame with { AcfHash = "" }, local, null, default, VerifyMode.Quick)).Source=="local","report records the manifest source");
    Directory.CreateDirectory(found[0].Path);
    await File.AppendAllTextAsync(acf,"\n");
    try { await Verifier.Run(found[0],new([],[],true,[]),null,default); throw new Exception("ACF mutation ignored"); } catch(IOException) { passed++; }
    Console.WriteLine($"All {passed} checks passed.");
}
finally { Directory.Delete(root,true); }
