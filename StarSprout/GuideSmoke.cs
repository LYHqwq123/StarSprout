using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StarSprout;

/// <summary>Opt-in, isolated real WebView/API check. Never reads or launches an installed game.</summary>
internal static class GuideSmoke
{
    internal static async Task Run(WebView2 browser, string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var results = new List<string>();
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("攻略检查失败：" + label);
            results.Add(label);
        }
        await browser.CoreWebView2.ExecuteScriptAsync("navigate('guides')");
        var initial = await Read(browser, "({sidebar:!!document.querySelector('[data-page=guides]'),ready:document.getElementById('content').textContent.includes('攻略百科'),emptyGame:!state.installation.hasGame})");
        Check(initial.Value<bool>("sidebar") && initial.Value<bool>("ready"), "sidebar and guide landing page render");
        Check(initial.Value<bool>("emptyGame"), "GUI test uses an isolated empty game profile");
        await Capture(browser, output, "guide-home");

        await RunTask(browser, "guideQuery='钓鱼';await searchGuides(1);if(!guideResults?.items?.length)throw Error('API search returned no results');");
        Check((await Read(browser, "({found:guideResults.items.some(item=>item.title.includes('钓鱼'))})")).Value<bool>("found"), "real anonymous Chinese search returns fishing entries");
        await Capture(browser, output, "guide-search");
        await RunTask(browser, "await loadGuideArticle('农场');if(guideArticle?.title!=='农场')throw Error('Farm article did not open');window.__originalGuide=guideArticle;");
        var article = await Read(browser, "({length:document.getElementById('guideArticleBody').textContent.length,images:document.querySelectorAll('.guide-body img').length,toc:guideArticle.sections.length,proxies:[...document.querySelectorAll('.guide-body img')].every(img=>img.src.startsWith('https://guides.starsprout.local/image?url=')),attribution:document.querySelector('.guide-attribution').textContent.includes('CC BY-NC-SA')})");
        Check(article.Value<int>("length") > 1000 && article.Value<int>("toc") > 0, "farm article has formatted body and table of contents");
        Check(article.Value<int>("images") > 0 && article.Value<bool>("proxies"), "article images use the bounded native proxy");
        Check(article.Value<bool>("attribution"), "content attribution and license are visible");
        await RunTask(browser, "const img=document.querySelector('.guide-body img');await img.decode();if(!img.naturalWidth)throw Error('Image decode failed');window.__guideImageUrl=img.src;");
        Check(true, "actual BWIKI PNG loads and decodes through the local image cache");
        await Capture(browser, output, "guide-article");
        await RunTask(browser, "state.guides=await rpc('guideFavorite',{title:guideArticle.title,favorite:true});render();");
        Check((await Read(browser, "({favorite:state.guides.favorites.some(item=>item.title==='农场'),recent:state.guides.recent.some(item=>item.title==='农场')})")).Value<bool>("favorite"), "favorite is persisted through the native host");
        await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('.guide-body img').click()");
        await RunTask(browser, "const img=document.querySelector('#guideImageModal img');if(!img)throw Error('Zoom modal missing');await img.decode();");
        Check(true, "clicking an article image opens a working zoom viewer");
        await Capture(browser, output, "guide-image");
        await browser.CoreWebView2.ExecuteScriptAsync("guideImage=null;renderGuideImage()");

        const string hostile = "<h2 id='maximizeButton'>安全正文</h2><img src='https://patchwiki.biligame.com/images/stardewvalley/3/3f/lk72p0f9mg1yjx4tfiky8comhj5m2fa.png' onerror='window.__guideInjected=1'><img src='https://untrusted.invalid/track.png'><script>window.__guideInjected=1</script><svg onload='window.__guideInjected=1'><foreignObject>bad</foreignObject></svg><iframe srcdoc='<script>window.__guideInjected=1</script>'></iframe><a href='javascript:window.__guideInjected=1' data-action='removeMod' data-feature='removeMod'>坏链接</a><form action='https://untrusted.invalid'><input autofocus onfocus='window.__guideInjected=1'></form><table><tr><td>表格</td></tr></table>";
        await browser.CoreWebView2.ExecuteScriptAsync("window.__guideInjected=0;guideArticle={...guideArticle,html:" + JsonConvert.SerializeObject(hostile) + "};render()");
        await Task.Delay(250);
        var safe = await Read(browser, "(()=>{const body=document.getElementById('guideArticleBody');return {executed:window.__guideInjected,forbidden:!!body.querySelector('script,svg,iframe,form,input,object,style,link'),attrs:[...body.querySelectorAll('*')].some(el=>[...el.attributes].some(attr=>attr.name.startsWith('on')||['data-action','data-feature','srcdoc','style'].includes(attr.name))),foreignImage:[...body.querySelectorAll('img')].some(img=>!img.src.startsWith('https://guides.starsprout.local/image?url=')),table:!!body.querySelector('table'),duplicateId:document.querySelectorAll('#maximizeButton').length!==1};})()");
        Check(safe.Value<int>("executed") == 0 && !safe.Value<bool>("forbidden") && !safe.Value<bool>("attrs"), "hostile article markup cannot run code or inject launcher actions");
        Check(!safe.Value<bool>("foreignImage") && !safe.Value<bool>("duplicateId") && safe.Value<bool>("table"), "reader keeps tables while rejecting foreign images and DOM ID collisions");

        await RunTask(browser, "accept(await rpc('setOffline',{offline:true}));guideArticle=null;await loadGuideArticle('农场');if(!guideArticle?.fromCache)throw Error('Offline article cache missed');const img=document.querySelector('.guide-body img');await img.decode();");
        Check(true, "offline reader restores cached article and previously loaded image");
        await Capture(browser, output, "guide-offline");
        var offline = await Read(browser, "({favorite:state.guides.favorites.some(item=>item.title==='农场'),recent:state.guides.recent.some(item=>item.title==='农场')})");
        Check(offline.Value<bool>("favorite") && offline.Value<bool>("recent"), "favorites and reading history survive offline refresh");
        await RunTask(browser, "accept(await rpc('setLaunchPlatform',{platform:'xbox'}));navigate('home');if(state.launchProfile.platform!=='xbox'||state.installation.hasGame)throw Error('Xbox profile not isolated');");
        Check(true, "Xbox selection uses its own empty installation profile");
        await RunTask(browser, "accept(await rpc('setLaunchPlatform',{platform:'steam'}));navigate('home');if(state.launchProfile.platform!=='steam')throw Error('Steam selection failed');");
        Check(true, "Steam selection is saved without launching a client or game");
        var layout = await Read(browser, "(()=>{const calendar=document.querySelector('.home-calendar').getBoundingClientRect();const playtime=document.querySelector('.home-playtime').getBoundingClientRect();return {adjacent:Math.abs(calendar.top-playtime.top)<3&&calendar.right<playtime.left,compact:calendar.width<600,selector:!!document.getElementById('launchPlatformSelect'),total:document.getElementById('playtimeTotal').textContent,zero:state.playtime.totalSeconds===0};})()");
        Check(layout.Value<bool>("adjacent") && layout.Value<bool>("compact"), "compact calendar and playtime panel sit next to each other");
        Check(layout.Value<bool>("selector") && layout.Value<bool>("zero"), "homepage account selector and honest zero-time initial state render");
        await Capture(browser, output, "home-account");
        await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('.home-activity').scrollIntoView({block:'center'})");
        await Capture(browser, output, "home-playtime");
        await RunTask(browser, "accept(await rpc('setLaunchPlatform',{platform:'offline'}));if(state.launchProfile.platform!=='offline'||!state.content.offline)throw Error('Offline profile selection failed');");
        Check(true, "offline selection activates the independent local game directory and cache-only mode");
        await browser.CoreWebView2.ExecuteScriptAsync("navigate('multiplayer')");
        var multiplayer = await Read(browser, "({sidebar:!!document.querySelector('[data-page=multiplayer]'),body:document.getElementById('content').textContent,uninstalled:!state.multiplayer.coreInstalled,stopped:!state.multiplayer.running})");
        Check(multiplayer.Value<bool>("sidebar") && multiplayer.Value<string>("body")!.Contains("联机"), "native multiplayer sidebar and lobby render");
        Check(multiplayer.Value<bool>("uninstalled") && multiplayer.Value<bool>("stopped"), "opening multiplayer never downloads a core or starts networking");
        await Capture(browser, output, "multiplayer-home");
        await File.WriteAllTextAsync(Path.Combine(output, "guide-result.json"), JsonConvert.SerializeObject(new { passed = true, checks = results.Count, results, article }, Formatting.Indented));
        await File.WriteAllTextAsync(Path.Combine(output, "passed.txt"), $"{results.Count} real BWIKI GUI, image, offline and HTML safety checks passed. No game was accessed or launched.");
    }

    private static async Task RunTask(WebView2 browser, string body)
    {
        await browser.CoreWebView2.ExecuteScriptAsync("window.__guideTask={done:false};(async()=>{" + body + "})().then(()=>window.__guideTask={done:true},error=>window.__guideTask={done:true,error:String(error.message||error)});");
        for (var attempt = 0; attempt < 600; attempt++)
        {
            var result = await Read(browser, "window.__guideTask");
            if (result.Value<bool>("done"))
            {
                if (result.Value<string>("error") is { } error) throw new InvalidOperationException(error);
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("攻略界面检查超时。");
    }

    private static async Task<JObject> Read(WebView2 browser, string expression) => JObject.Parse(await browser.CoreWebView2.ExecuteScriptAsync(expression));
    private static async Task Capture(WebView2 browser, string output, string name)
    {
        using var stream = File.Create(Path.Combine(output, name + ".png"));
        await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
    }
}
