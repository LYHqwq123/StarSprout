using System.IO;
using System.IO.Compression;
using System.Windows;
using Newtonsoft.Json;
using StarSprout.Services;

namespace StarSprout;

internal static class BrowserSmoke
{
    internal static async Task Run(Window owner, string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var fixture = Path.Combine(output, "fixture-" + Guid.NewGuid().ToString("N"));
        var site = Path.Combine(fixture, "Site"); var game = Path.Combine(fixture, "Game");
        Directory.CreateDirectory(site); Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "Stardew Valley.exe"), "unexecuted diagnostic fixture");
        File.WriteAllText(Path.Combine(site, "index.html"), "<!doctype html><title>Local browser check</title><a id='download' href='Fixture.zip' download='Fixture.zip'>Download</a><script>window.addEventListener('message',e=>{if(e.origin===location.origin&&e.data==='popup-ready')window.popupReady=true});</script>");
        File.WriteAllText(Path.Combine(site, "popup.html"), "<!doctype html><title>Local login popup</title><script>window.opener.postMessage('popup-ready','https://browser-check.example.com');</script>");
        using (var zip = ZipFile.Open(Path.Combine(site, "Fixture.zip"), ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("BrowserFixture/manifest.json").Open()))
            writer.Write("{\"Name\":\"Browser Fixture\",\"Author\":\"Isolated check\",\"Version\":\"1.0.0\",\"UniqueID\":\"Check.Browser\",\"Description\":\"Local check only\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");
        using (var zip = ZipFile.Open(Path.Combine(site, "FixtureBlob.zip"), ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("BrowserBlobFixture/manifest.json").Open()))
            writer.Write("{\"Name\":\"Browser Blob Fixture\",\"Author\":\"Isolated check\",\"Version\":\"1.0.0\",\"UniqueID\":\"Check.BrowserBlob\",\"Description\":\"Local blob download check\",\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");
        var service = new StardewService(Path.Combine(fixture, "Settings"), Path.Combine(fixture, "Saves"));
        service.SetGameDirectory(game);
        using var content = new ContentService(Path.Combine(fixture, "Content"), service);
        var completed = new TaskCompletionSource<DownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blobCompleted = new TaskCompletionSource<DownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nxmCompleted = new TaskCompletionSource<(string Uri, bool Install, string Directory)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nxmPopupCompleted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var archiveCount = 0; var nxmCount = 0;
        var browser = new CommunityBrowserWindow(Path.Combine(fixture, "Browser"), async (archive, ct) =>
        {
            try
            {
                var result = await content.CacheLocalArchiveAsync(archive.Path, archive.Name, archive.SourceUrl, archive.Install, ct);
                if (++archiveCount == 1) completed.TrySetResult(result); else blobCompleted.TrySetResult(result);
                return "Passed";
            }
            catch (Exception ex) { completed.TrySetException(ex); blobCompleted.TrySetException(ex); throw; }
        }, (uri, install, directory, _) =>
        {
            if (++nxmCount == 1) nxmCompleted.TrySetResult((uri, install, directory)); else nxmPopupCompleted.TrySetResult(uri);
            return Task.FromResult("Isolated NXM interception passed");
        }, () => game, () => true)
        { Owner = owner, ShowInTaskbar = false, Left = -8000, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            await browser.PrepareCheckAsync("browser-check.example.com", site);
            await browser.NavigateAsync("https://browser-check.example.com/index.html", true);
            for (var i = 0; i < 50; i++)
            {
                if (await browser.CheckScriptAsync("Boolean(document.getElementById('download'))") == "true") break;
                await Task.Delay(100);
            }
            await browser.CheckScriptAsync("document.getElementById('download').click()");
            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await browser.CheckScriptAsync("window.checkPopup=window.open('popup.html','login-check')");
            for (var i = 0; i < 100 && await browser.CheckScriptAsync("Boolean(window.popupReady && window.checkPopup && !window.checkPopup.closed)") != "true"; i++) await Task.Delay(100);
            if (await browser.CheckScriptAsync("Boolean(window.popupReady && window.checkPopup && !window.checkPopup.closed)") != "true")
                throw new InvalidOperationException("Native browser login popup lost window.opener");
            await browser.CheckScriptAsync("location.href='nxm://stardewvalley/mods/123/files/45?key=local-fixture&expires=4102444800&user_id=7'");
            var nxm = await nxmCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (!nxm.Uri.Contains("mods/123/files/45") || !nxm.Install || nxm.Directory != game)
                throw new InvalidOperationException("Native NXM navigation was not bound to the selected game");
            await browser.CheckScriptAsync("window.open('nxm://stardewvalley/mods/123/files/46?key=popup-fixture&expires=4102444800&user_id=7')");
            if (!(await nxmPopupCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10))).Contains("files/46"))
                throw new InvalidOperationException("Native NXM popup was not intercepted");
            await browser.CheckScriptAsync("fetch('FixtureBlob.zip').then(r=>r.blob()).then(blob=>{const a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download='FixtureBlob.zip';document.body.append(a);a.click();})");
            var blobResult = await blobCompleted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            CommunityBrowserWindow.ValidateArchiveSource("blob:https://browser-check.example.com/local-blob", "https://browser-check.example.com/index.html");
            try { CommunityBrowserWindow.ValidateArchiveSource("blob:https://other.example.com/foreign-blob", "https://browser-check.example.com/index.html"); throw new InvalidOperationException("Cross-origin blob was accepted"); }
            catch (ArgumentException) { }
            if (browser.HasLauncherBridge || !result.Installed || !blobResult.Installed || service.GetMods().All(m => m.UniqueId != "Check.Browser") || service.GetMods().All(m => m.UniqueId != "Check.BrowserBlob"))
                throw new InvalidOperationException("Local browser ZIP-to-game verification failed");
            await File.WriteAllTextAsync(Path.Combine(output, "browser-result.json"), JsonConvert.SerializeObject(new
            { passed = true, browserBridgeDisabled = true, nativeZipDownload = true, sameOriginBlobZipDownload = true, crossOriginBlobRejected = true,
                nativeNxmNavigation = true, nativeNxmPopup = true, nativeLoginPopupWithOpener = true, cached = true, installedIntoIsolatedGame = true, remoteRequests = 0 }));
        }
        finally { browser.Close(); }
    }
}
