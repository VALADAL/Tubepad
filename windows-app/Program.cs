using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace YTConsole;

static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_POWER_THROTTLING_STATE { public uint Version; public uint ControlMask; public uint StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr hProcess, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [DllImport("ntdll.dll")]
    static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("kernel32.dll")]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    // Ayudante: vigila a YTConsole y, si alguien lo congela (suspende), lo reanuda.
    // Corre como un proceso aparte, así que no se congela junto con la app.
    static void RunGuardian(int pid, bool debug)
    {
        Process target;
        try { target = Process.GetProcessById(pid); } catch { return; }

        // Para que a este ayudante tampoco le bajen la velocidad en segundo plano
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = 0 };
            SetProcessInformation(Process.GetCurrentProcess().Handle, 4, ref state, Marshal.SizeOf(state));
        }
        catch { }

        IntPtr handle = OpenProcess(0x0800, false, pid);   // 0x0800 = permiso de suspender/reanudar
        if (handle == IntPtr.Zero) { return; }
        string logFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YTConsole", "log.txt");
        int resumed = 0;

        while (!target.HasExited)
        {
            try
            {
                target.Refresh();
                bool suspended = false;
                foreach (ProcessThread t in target.Threads)
                {
                    if (t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended)
                    {
                        suspended = true;
                        break;
                    }
                }
                if (suspended)
                {
                    NtResumeProcess(handle);
                    resumed++;
                    if (debug)
                    {
                        try { File.AppendAllText(logFile, DateTime.Now.ToString("HH:mm:ss.fff") + "  GUARDIAN: YTConsole estaba suspendido, lo reanude (" + resumed + ")" + Environment.NewLine); }
                        catch { }
                    }
                }
            }
            catch { }
            Thread.Sleep(250);
        }
    }

    [STAThread]
    static void Main()
    {
        // Modo ayudante: "YTConsole.exe --guardian <pid>"
        var argv = Environment.GetCommandLineArgs();
        int gi = Array.IndexOf(argv, "--guardian");
        if (gi >= 0 && gi + 1 < argv.Length && int.TryParse(argv[gi + 1], out int guardPid))
        {
            RunGuardian(guardPid, argv.Contains("--debug"));
            return;
        }

        // Una sola copia a la vez
        using var mutex = new Mutex(true, "YTConsole.SingleInstance", out bool first);
        if (!first) { return; }

        // Pide a Windows que no baje la velocidad de este proceso cuando no está al frente
        try
        {
            var me = Process.GetCurrentProcess();
            var state = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = 0 };
            SetProcessInformation(me.Handle, 4, ref state, Marshal.SizeOf(state));   // 4 = ProcessPowerThrottling
            me.PriorityClass = ProcessPriorityClass.High;
        }
        catch { }

        // Lanza el ayudante (a menos que pongas --no-guardian)
        if (!argv.Contains("--no-guardian"))
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--guardian " + Environment.ProcessId + (argv.Contains("--debug") ? " --debug" : ""))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch { }
        }

        ApplicationConfiguration.Initialize();
        var args = argv;
        // --windowed: ventana normal maximizada | --local: carga el index.html de esta carpeta (para probar)
        // --debug: escribe un log en %LocalAppData%\YTConsole\log.txt | --no-smtc: sin SMTC nativo | --no-guardian: sin ayudante
        Application.Run(new MainForm(args.Contains("--windowed"), args.Contains("--local"), args.Contains("--debug"), args.Contains("--no-smtc")));
    }
}

class MainForm : Form
{
    // Dirección de GitHub Pages (el repositorio se llama Tubepad, con T mayúscula)
    const string AppUrl = "https://valadal.github.io/Tubepad/";

    readonly WebView2 web = new();
    readonly bool local;

    public MainForm(bool windowed, bool local, bool debug, bool noSmtc)
    {
        this.local = local;
        this.debug = debug;
        this.noSmtc = noSmtc;
        Text = "YTConsole";
        BackColor = Color.FromArgb(22, 17, 14);

        if (windowed)
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.None;   // sin bordes, como un juego
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen!.Bounds;
        }

        web.Dock = DockStyle.Fill;
        web.DefaultBackgroundColor = Color.FromArgb(22, 17, 14);
        Controls.Add(web);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (noSmtc) { Log("SMTC nativo desactivado (--no-smtc)"); } else { InitSmtc(); }

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YTConsole", "WebView2");

        // Audio sin pedir clic y que el navegador no duerma la página en segundo plano
        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments =
                "--autoplay-policy=no-user-gesture-required " +
                "--disable-renderer-backgrounding --disable-background-timer-throttling " +
                "--disable-backgrounding-occluded-windows " +
                "--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling,UseEcoQoSForBackgroundProcess"
        };

        var env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
        await web.EnsureCoreWebView2Async(env);

        web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        web.CoreWebView2.WebMessageReceived += OnWebMessage;

        if (local)
        {
            // --local: sirve el index.html de la carpeta de la app (https local, sin subir nada a GitHub)
            web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "tubepad.local", AppContext.BaseDirectory, CoreWebView2HostResourceAccessKind.Allow);
            web.CoreWebView2.Navigate("https://tubepad.local/index.html");
        }
        else
        {
            web.CoreWebView2.Navigate(AppUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());   // evita ver una copia vieja guardada
        }
        Log("WebView2 listo");
        if (debug) { StartDebug(); }
    }

    // La página nos manda textos sencillos: "quit", "login", "logout" o "status"
    async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg = e.TryGetWebMessageAsString();
        // Con un video en pantalla, la pantalla de atrás no manda nada de música ni de videos nuevos
        if (videoActive && (msg.StartsWith("mc|") || msg.StartsWith("vplay|") || msg.StartsWith("mplay|"))) { Log("ignorado con video activo: " + msg); return; }
        if (msg == "quit") { Close(); }
        else if (msg == "login") { OpenLogin(); }
        else if (msg.StartsWith("browse|") || msg.StartsWith("search|")) { _ = Browse(msg); }
        else if (msg.StartsWith("vplay|")) { _ = PlayVideo(msg[6..]); }
        else if (msg == "vclose") { CloseVideo(); }
        else if (msg.StartsWith("mplay|")) { _ = PlayMusic(msg[6..]); }
        else if (msg.StartsWith("mc|")) { _ = MusicCmd(msg[3..]); }
        else if (msg == "logout")
        {
            web.CoreWebView2.CookieManager.DeleteAllCookies();   // borra la sesión de esta app
            _ = ReloadApis();
            web.CoreWebView2.PostWebMessageAsString("login:no");
        }
        else if (msg == "status")
        {
            web.CoreWebView2.PostWebMessageAsString(await IsLoggedIn() ? "login:ok" : "login:no");
        }
    }

    // Revisa si ya hay una sesión de YouTube Music guardada en esta app
    async Task<bool> IsLoggedIn()
    {
        var cookies = await web.CoreWebView2.CookieManager.GetCookiesAsync("https://music.youtube.com");
        return cookies.Any(c => c.Name == "SAPISID" || c.Name == "__Secure-3PAPISID");
    }

    // ---- Datos de YouTube ----
    // Dos páginas ocultas (una de YouTube, otra de YouTube Music) hacen las consultas con tu sesión
    // y le devuelven a la pantalla principal una lista simple de {id, título, subtítulo, carátula}.
    readonly Dictionary<string, Task<WebView2>> apis = new();

    Task<WebView2> GetApi(string mode)
    {
        if (!apis.TryGetValue(mode, out var t)) { apis[mode] = t = CreateApi(mode); }
        return t;
    }

    async Task<WebView2> CreateApi(string mode)
    {
        var w = new WebView2 { Width = 1, Height = 1, Left = 0, Top = 0 };
        Controls.Add(w);
        w.SendToBack();   // queda detrás de la pantalla principal
        await w.EnsureCoreWebView2Async(web.CoreWebView2.Environment);
        w.CoreWebView2.ScriptDialogOpening += (s, e) => e.Accept();
        w.CoreWebView2.WebMessageReceived += (s, e) => web.CoreWebView2.PostWebMessageAsString("data:" + e.TryGetWebMessageAsString());
        var loaded = new TaskCompletionSource();
        w.CoreWebView2.NavigationCompleted += (s, e) => loaded.TrySetResult();
        w.CoreWebView2.Navigate(mode == "music" ? "https://music.youtube.com/" : "https://www.youtube.com/");
        await loaded.Task;
        return w;
    }

    async Task ReloadApis()
    {
        foreach (var t in apis.Values.ToList())
        {
            var w = await t;
            var done = new TaskCompletionSource();
            void h(object? s, CoreWebView2NavigationCompletedEventArgs a) { w.CoreWebView2.NavigationCompleted -= h; done.TrySetResult(); }
            w.CoreWebView2.NavigationCompleted += h;
            w.CoreWebView2.Reload();
            await done.Task;
        }
    }

    async Task Browse(string msg)   // "browse|modo|idPantalla|nombre"
    {
        var p = msg.Split('|', 4);
        try
        {
            var api = await GetApi(p[1]);
            bool search = p[0] == "search";
            var reqs = search
                ? new List<object[]> { new object[] { "Results", new { query = p[2] }, false } }
                : p[2].Split(';').Select(x =>
                  {
                      // "ID#banderas": c = categorías (chips) de YouTube, v = pestaña Videos de un canal,
                      // d = agrupar por fecha, m = leer más páginas, u = sacar creadores de los videos si no hay lista
                      string fl = "";
                      int h = x.IndexOf('#');
                      if (h >= 0) { fl = x[(h + 1)..]; x = x[..h]; }
                      bool f = x.Contains('=');   // "Título=ID" fuerza el título; "ID" usa el de YouTube
                      var z = f ? x.Split('=', 2) : new[] { p[3], x };
                      var body = new Dictionary<string, string> { ["browseId"] = z[1] };
                      if (fl.Contains('v')) { body["params"] = "EgZ2aWRlb3PyBgQKAjoA"; }   // pestaña "Videos" del canal (más nuevos primero)
                      return new object[] { z[0], body, f, fl };
                  }).ToList();
            var js = BrowseJs.Replace("__KEY__", p[1] + "|" + p[2]).Replace("__EP__", search ? "search" : "browse")
                .Replace("__REQS__", System.Text.Json.JsonSerializer.Serialize(reqs)).Replace("__LABEL__", search ? "Results" : p[3]);
            await api.CoreWebView2.ExecuteScriptAsync(js);
        }
        catch
        {
            web.CoreWebView2.PostWebMessageAsString("data:{\"key\":\"" + p[1] + "|" + p[2] + "\",\"error\":true}");
        }
    }

    // Se ejecuta dentro de la página de YouTube: pide la pantalla y saca las canciones/videos que encuentre
    const string BrowseJs = """
        (async () => {
          const key = "__KEY__", post = o => chrome.webview.postMessage(JSON.stringify(o));
          try {
            for (let i = 0; i < 40 && !(window.ytcfg && ytcfg.get && ytcfg.get("INNERTUBE_CONTEXT")); i++) await new Promise(r => setTimeout(r, 250));
            const h = { "Content-Type": "application/json", "X-Goog-AuthUser": "0", "X-Origin": location.origin };
            const sid = (document.cookie.match(/SAPISID=([^;]+)/) || [])[1];
            if (sid) {
              const ts = Math.floor(Date.now() / 1000);
              const d = await crypto.subtle.digest("SHA-1", new TextEncoder().encode(ts + " " + sid + " " + location.origin));
              h.Authorization = "SAPISIDHASH " + ts + "_" + [...new Uint8Array(d)].map(b => b.toString(16).padStart(2, "0")).join("");
            }
            const ctx = ytcfg.get("INNERTUBE_CONTEXT"); ctx.client.hl = "en";
            const call = async (body, t) => (await fetch("/youtubei/v1/__EP__?prettyPrint=false&key=" + ytcfg.get("INNERTUBE_API_KEY") + (t ? "&ctoken=" + t + "&continuation=" + t : ""),
              { method: "POST", headers: h, body: JSON.stringify(t ? { context: ctx, continuation: t } : { context: ctx, ...body }) })).json();
            const REQS = __REQS__;
            const isYT = location.host == "www.youtube.com";   // lo nuevo de abajo solo aplica a YouTube (Music queda igual)
            const fk = (o, k) => { if (!o || typeof o != "object") return; if (k in o) return o[k]; for (const x in o) { const v = fk(o[x], k); if (v) return v; } };
            const tok = j => {
              if (isYT) { const ci = fk(j, "continuationItemRenderer"), c = ci && fk(ci, "continuationCommand"); if (c && c.token) return c.token; }   // el token del final de la lista, no el de las categorías
              const a = fk(j, "continuationCommand"), b = fk(j, "nextContinuationData"); return (a && a.token) || (b && b.continuation) || "";
            };
            let FORCE = false, DATE = false;
            const OK = /^(musicTwoRowItem|musicResponsiveListItem|video|compactVideo|gridVideo|playlistVideo|videoWithContext|channel|gridChannel)Renderer$|^lockupViewModel$/;
            const tx = o => { if (!o || typeof o != "object") return ""; if (typeof o.simpleText == "string") return o.simpleText;
              if (Array.isArray(o.runs)) return o.runs.map(r => r.text).join(""); if (typeof o.content == "string") return o.content;
              for (const k in o) { const v = tx(o[k]); if (v) return v; } return ""; };
            const find = (o, k) => { if (!o || typeof o != "object") return; if (typeof o[k] == "string") return o[k];
              for (const x in o) { const v = find(o[x], k); if (v) return v; } };
            const thumb = o => { if (!o || typeof o != "object") return ""; const a = o.thumbnails || o.sources;
              if (Array.isArray(a) && a.length) { const u = a[a.length - 1].url || ""; return u.startsWith("//") ? "https:" + u : u; }
              for (const k in o) { const v = thumb(o[k]); if (v) return v; } return ""; };
            // Textos chiquitos de un video en el diseño nuevo de YouTube ("Canal", "1.2M views", "3 days ago")
            const mrows = v => { const o = [], go = n => { if (!n || typeof n != "object") return; if (Array.isArray(n)) { n.forEach(go); return; }
              if (Array.isArray(n.metadataParts)) { n.metadataParts.forEach(p => { const t = tx(p.text); if (t) o.push(t); }); return; }
              for (const k in n) go(n[k]); }; go(v.metadata); return o; };
            // "3 days ago" -> en qué grupo de fecha cae
            const bucket = w => {
              const m = /(\d+)\s*(second|minute|hour|day|week|month|year)s?\s+ago/i.exec(w || "");
              if (!m) return /watching|live|premiere|scheduled|waiting/i.test(w || "") ? "Live & upcoming" : "Older";
              const n = +m[1], u = m[2].toLowerCase();
              if (u == "second" || u == "minute" || u == "hour") return "Today";
              if (u == "day") return n == 1 ? "Yesterday" : n < 7 ? "This week" : "This month";
              if (u == "week") return n <= 4 ? "This month" : "Earlier this year";
              if (u == "month") return n < 12 ? "Earlier this year" : "Older";
              return "Older";
            };
            // Las categorías (chips) de arriba de la pantalla Home: [nombre, token]
            const chipList = j => { const out = [], go = o => { if (!o || typeof o != "object") return; if (Array.isArray(o)) { o.forEach(go); return; }
              for (const k in o) { const v = o[k];
                if (k == "chipCloudChipRenderer" && v) { const t = tx(v.text), c = fk(v.navigationEndpoint, "continuationCommand"); if (t && c && c.token && !v.isSelected) out.push([t, c.token]); }
                else if (k == "chipViewModel" && v) { const c = fk(v.tapCommand, "continuationCommand"); if (v.text && c && c.token && !v.selected) out.push([String(v.text), c.token]); }
                else go(v); } }; go(j); return out; };
            // Canal de un video (plan B para la lista de creadores)
            const chan = (v, parts) => {
              const src = v.ownerText || v.shortBylineText || v.longBylineText || v.metadata;
              const be = fk(src, "browseEndpoint"), bid = be && be.browseId;
              if (!/^UC[\w-]{22}$/.test(bid || "")) return null;
              return { id: "", bid, t: tx(v.ownerText || v.shortBylineText || v.longBylineText) || parts[0] || "", s: "", img: thumb(v.channelThumbnailSupportedRenderers) || thumb(fk(v.metadata, "avatarViewModel")), k: "ch" };
            };
            const rows = new Map(), fb = new Map();
            const walk = (n, l) => {
              if (!n || typeof n != "object") return;
              if (Array.isArray(n)) { n.forEach(x => walk(x, l)); return; }
              if (n.header && !FORCE) l = tx(n.header) || l;
              for (const k in n) {
                const v = n[k];
                if (OK.test(k) && v && typeof v == "object") {
                  let id = find(v, "videoId") || find(v, "contentId"); if (!/^[\w-]{11}$/.test(id || "")) id = "";
                  const lk = k == "lockupViewModel", ne = v.navigationEndpoint;
                  let bid = id ? "" : (ne && ne.browseEndpoint && ne.browseEndpoint.browseId) || "";
                  if (!id && !bid && lk) { const oc = v.rendererContext && v.rendererContext.commandContext && v.rendererContext.commandContext.onTap && v.rendererContext.commandContext.onTap.innertubeCommand; bid = (oc && oc.browseEndpoint && oc.browseEndpoint.browseId) || ""; }
                  const t = tx(v.title) || tx(v.flexColumns && v.flexColumns[0]) || tx(v.metadata);
                  if ((id || bid) && t) {
                    const parts = lk ? mrows(v) : [];
                    let s = tx(v.subtitle) || tx(v.shortBylineText) || tx(v.longBylineText) || tx(v.ownerText) || tx(v.flexColumns && v.flexColumns[1]);
                    if (!s && lk) s = parts.join(" · ");
                    else if (/^(video|compactVideo|gridVideo|videoWithContext)Renderer$/.test(k)) s = [s, tx(v.viewCountText), tx(v.publishedTimeText)].filter(Boolean).join(" · ");
                    const when = tx(v.publishedTimeText) || parts.find(p => / ago|watching|premiere|scheduled/i.test(p)) || "";
                    const g = DATE ? bucket(when) : l, cap = g == "Creators" ? 150 : 40; const a = rows.get(g) || [];
                    if (a.length < cap) a.push({ id, bid, t, s, img: thumb(v), k: id && k.startsWith("musicResponsive") ? "l" : /hannelRenderer$/.test(k) ? "ch" : "c" });
                    rows.set(g, a);
                    if (DATE && id) { try { const c = chan(v, parts); if (c && !fb.has(c.bid)) fb.set(c.bid, c); } catch (e) { } }
                  }
                } else walk(v, l);
              }
            };
            let backup = false;
            for (const [lab, body, force, fl] of REQS) {
              const F = fl || "";
              FORCE = !!force || F.includes("c"); DATE = F.includes("d"); backup = backup || F.includes("u");
              const more = F.includes("m") ? 4 : 2;
              let j = await call(body, ""), n = 0;
              if (F.includes("c")) {   // Home: la lista principal + una fila por cada categoría de YouTube (Gaming, Music, Live...)
                const cs = chipList(j).filter(c => !/^(all|watched)$/i.test(c[0])).slice(0, 8);
                if (cs.length) {
                  walk(j, lab);
                  const rs = await Promise.all(cs.map(c => call(null, c[1]).catch(() => null)));
                  FORCE = true;
                  cs.forEach((c, i) => { if (rs[i]) walk(rs[i], c[0]); });
                  continue;
                }
              }
              while (true) {
                walk(j, lab);
                const t = tok(j);
                if (!t || "__EP__" == "search" || (force && !F.includes("m") && !F.includes("c")) || ++n > more) break;
                j = await call(null, t);
              }
            }
            let out = [...rows];
            if (backup && fb.size && !rows.has("Creators")) out = [["Creators", [...fb.values()].slice(0, 60)], ...out];
            post({ key, rows: out.filter(x => x[1].length).slice(0, 30) });
          } catch (e) { post({ key, error: String(e) }); }
        })();
        """;

    // ---- Reproductores ----
    WebView2? vid, mus;

    static bool ValidId(string id) => System.Text.RegularExpressions.Regex.IsMatch(id, "^[\\w-]{11}$");

    static async Task Key(WebView2 w, string key, string code, int vk)   // tecla "de verdad" (cuenta como gesto del usuario)
    {
        foreach (var t in new[] { "keyDown", "keyUp" })
            await w.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                $$"""{"type":"{{t}}","key":"{{key}}","code":"{{code}}","text":"{{key}}","windowsVirtualKeyCode":{{vk}}}""");
    }

    // YouTube: la página real de YouTube (con tu sesión), pero con su interfaz de escritorio escondida:
    // se ve solo el video y los controles propios (VideoJs). Carga detrás de la pantalla principal y sale cuando ya está lista.
    int vidToken;

    async Task PlayVideo(string id)
    {
        if (!ValidId(id)) { return; }
        int token = ++vidToken;
        if (mus != null) { _ = mus.CoreWebView2.ExecuteScriptAsync("window.__hold=1;document.querySelector('video')?.pause()"); }   // pausa y candado: la música no se reanuda sola
        if (vid == null)
        {
            vid = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(vid);
            await vid.EnsureCoreWebView2Async(web.CoreWebView2.Environment);
            vid.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            vid.CoreWebView2.ScriptDialogOpening += (s, e) => e.Accept();   // sin avisos de "Leave site?"
            await vid.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(VideoJs);
            vid.CoreWebView2.WebMessageReceived += async (s, e) =>
            {
                var m = e.TryGetWebMessageAsString();
                if (m == "player:close") { CloseVideo(); }
                else if (m.StartsWith("player:state|")) { OnVideoState(m[13..]); }
                else if (m.StartsWith("player:play|")) { _ = PlayVideoInPlace(m[12..]); }
                else if (m.StartsWith("player:skip|"))   // clic real en "Omitir anuncio"
                {
                    var p = m.Split('|');
                    if (int.TryParse(p[1], out var x) && int.TryParse(p[2], out var y)) { await MouseClick(vid!, x, y); }
                }
            };
        }
        vid.SendToBack();
        vid.CoreWebView2.Navigate("https://www.youtube.com/watch?v=" + id);
        await EnterVideo(token);
    }

    // Abre otro video desde el propio reproductor (recomendados): sin mandarlo al fondo, así no se ve el menú de atrás
    async Task PlayVideoInPlace(string id)
    {
        var w = vid;
        if (!ValidId(id) || w?.CoreWebView2 == null || !videoActive) { return; }
        int token = ++vidToken;
        var done = new TaskCompletionSource();
        void h(object? s, CoreWebView2NavigationCompletedEventArgs a) { w.CoreWebView2.NavigationCompleted -= h; done.TrySetResult(); }
        w.CoreWebView2.NavigationCompleted += h;
        w.CoreWebView2.Navigate("https://www.youtube.com/watch?v=" + id);
        await Task.WhenAny(done.Task, Task.Delay(15000));
        await EnterVideo(token);
    }

    // Espera a que el video esté cargado, lo pone en pantalla completa y entonces lo muestra
    async Task EnterVideo(int token)
    {
        var w = vid!;
        try
        {
            for (int i = 0; i < 40 && token == vidToken; i++)
            {
                await Task.Delay(400);
                var ok = await w.CoreWebView2.ExecuteScriptAsync("(()=>{const v=document.querySelector('video');return !!v&&v.readyState>=2&&!!document.getElementById('movie_player')})()");
                if (ok == "true") { break; }
            }
            if (token != vidToken) { return; }
            w.Focus();
            for (int k = 0; k < 4 && token == vidToken; k++)
            {
                if (await w.CoreWebView2.ExecuteScriptAsync("!!document.fullscreenElement") == "true") { break; }
                await Key(w, "f", "KeyF", 70);
                await Task.Delay(700);
            }
        }
        catch { }
        if (token != vidToken) { return; }
        w.BringToFront();
        w.Focus();
        videoActive = true;   // desde aquí el SMTC sigue al video (no a la música)
        lastKey = "";
        _ = w.CoreWebView2.ExecuteScriptAsync("window.__tpShow && window.__tpShow(8000)");   // al empezar, se ven los controles y atajos un momento
        web.CoreWebView2.PostWebMessageAsString("video:ready");
    }

    void CloseVideo()
    {
        vidToken++;
        if (vid == null) { return; }
        vid.CoreWebView2.Navigate("about:blank");
        vid.SendToBack();
        web.Focus();
        web.CoreWebView2.PostWebMessageAsString("video:closed");
        // El SMTC vuelve a la música (o se cierra si no hay ninguna)
        videoActive = false;
        lastKey = "";
        if (smtc != null)
        {
            if (lastMusicJson != "") { OnMusicState(lastMusicJson); }
            else { smtc.PlaybackStatus = MediaPlaybackStatus.Closed; }
        }
    }

    // YouTube Music: suena en una página oculta; la pantalla principal sigue libre y muestra carátula y progreso
    async Task PlayMusic(string id)
    {
        if (!ValidId(id)) { return; }
        if (mus == null)
        {
            // Tamaño normal (queda tapada por la pantalla principal): con 1x1 los clics no caen donde está el botón de "Omitir"
            mus = new WebView2 { Width = 1280, Height = 720, Left = 0, Top = 0 };
            Controls.Add(mus);
            mus.SendToBack();
            await mus.EnsureCoreWebView2Async(web.CoreWebView2.Environment);
            mus.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            mus.CoreWebView2.ScriptDialogOpening += (s, e) => e.Accept();   // sin avisos de "Leave site?"
            await mus.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(MusicJs);
            mus.CoreWebView2.WebMessageReceived += (s, e) =>
            {
                var m = e.TryGetWebMessageAsString();
                web.CoreWebView2.PostWebMessageAsString("music:" + m);
                OnMusicState(m);   // lo mismo que ve la pantalla, se lo pasamos al SMTC de Windows
            };
        }
        mus.CoreWebView2.Navigate("https://music.youtube.com/watch?v=" + id);
    }

    // Clic "de verdad" (YouTube ignora los clics falsos hechos desde JavaScript en el botón de omitir anuncio)
    static async Task MouseClick(WebView2 w, int x, int y)
    {
        foreach (var t in new[] { "mouseMoved", "mousePressed", "mouseReleased" })
        {
            int buttons = t == "mousePressed" ? 1 : 0, count = t == "mouseMoved" ? 0 : 1;
            await w.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                $$"""{"type":"{{t}}","x":{{x}},"y":{{y}},"button":"left","buttons":{{buttons}},"clickCount":{{count}}}""");
            await Task.Delay(40);
        }
    }

    // Devuelve el centro del botón "Omitir anuncio" si está visible, o null
    const string SkipRectJs = """
        (() => { const s = document.querySelector('.ytp-skip-ad-button,.ytp-ad-skip-button,.ytp-ad-skip-button-modern');
          if (!s || !s.offsetParent) return null; const r = s.getBoundingClientRect();
          return [Math.round(r.left + r.width / 2), Math.round(r.top + r.height / 2)]; })()
        """;

    async Task MusicCmd(string c)
    {
        if (mus == null) { return; }
        await mus.CoreWebView2.ExecuteScriptAsync("window.__hold=0");   // tú mandas: se quita el candado
        if (c == "toggle")
        {
            // Si hay un anuncio con botón de omitir, X lo presiona; si no, pausa/reproduce
            var res = await mus.CoreWebView2.ExecuteScriptAsync(SkipRectJs);
            var pt = res == "null" ? null : System.Text.Json.JsonSerializer.Deserialize<int[]>(res);
            if (pt is { Length: 2 }) { await MouseClick(mus, pt[0], pt[1]); return; }
            await mus.CoreWebView2.ExecuteScriptAsync("(()=>{const v=document.querySelector('video');if(v)v.paused?v.play():v.pause()})()");
            return;
        }
        var js = c switch
        {
            "back" => "(()=>{const v=document.querySelector('video');if(v)v.currentTime-=5})()",
            "fwd" => "(()=>{const v=document.querySelector('video');if(v)v.currentTime+=5})()",
            "next" => "document.querySelector('ytmusic-player-bar .next-button')?.click()",
            "prev" => "document.querySelector('ytmusic-player-bar .previous-button')?.click()",
            _ => ""
        };
        if (js != "") { await mus.CoreWebView2.ExecuteScriptAsync(js); }
    }

    // Se inyecta en la página de música: cada segundo avisa qué suena
    const string MusicJs = """
        addEventListener("beforeunload", e => e.stopImmediatePropagation(), true);   // sin el aviso "Leave site?" al cambiar de canción
        try { Object.defineProperty(window, "onbeforeunload", { get: () => null, set: () => { } }); } catch (e) { }
        // Candado: mientras se ve un video (window.__hold = 1) nada puede darle play a la música (ni YouTube Music, ni teclas multimedia)
        try { const op = HTMLMediaElement.prototype.play; HTMLMediaElement.prototype.play = function () { return window.__hold ? Promise.resolve() : op.apply(this, arguments); }; } catch (e) { }
        addEventListener("play", e => { if (window.__hold && e.target && e.target.pause) e.target.pause(); }, true);
        if (window.top === window) setInterval(() => {
          const v = document.querySelector("video"); if (!v) return;
          const bar = document.querySelector("ytmusic-player-bar"), q = s => bar && bar.querySelector(s);
          const t = q(".title"), by = q(".byline"), im = q("img");
          const sb = document.querySelector(".ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern");
          chrome.webview.postMessage(JSON.stringify({ t: t ? t.textContent : "", a: by ? by.textContent : "", img: im ? im.src : "", pos: v.currentTime, dur: v.duration || 0, p: !v.paused, ad: !!document.querySelector(".ad-showing"), sk: !!(sb && sb.offsetParent) }));
        }, 1000);
        """;

    // Se inyecta en la página de YouTube: esconde su interfaz, dibuja los controles propios y atiende gamepad/teclado
    const string VideoJs = """
        (() => {
          if (window.__tp || window.top !== window) return; window.__tp = 1;
          addEventListener("beforeunload", e => e.stopImmediatePropagation(), true);   // sin avisos de "Leave site?"

          // ---- Estilo: esconde la interfaz de escritorio de YouTube y dibuja la nuestra ----
          const CSS = `
          html,body,*{cursor:none!important}
          .ytp-chrome-bottom,.ytp-chrome-top,.ytp-gradient-bottom,.ytp-gradient-top,.ytp-ce-element,.ytp-ce-covering-overlay,.html5-endscreen,.ytp-pause-overlay,.ytp-cards-teaser,.ytp-paid-content-overlay,.ytp-watermark,.ytp-bezel,.ytp-bezel-text-wrapper,.ytp-popup,.ytp-tooltip,.ytp-impression-link,.ytp-iv-video-content,.annotation,.ytp-autohide-popup,.ytp-player-content{display:none!important}
          .video-ads{opacity:0!important}
          #tp{position:fixed;inset:0;z-index:2147483000;pointer-events:none;overflow:hidden;color:#f1e8dc;font-family:"Segoe UI",system-ui,sans-serif}
          #tp *{box-sizing:border-box;margin:0}
          #tp .tp-top{position:absolute;left:0;right:0;top:0;padding:4vh 5vw 9vh;background:linear-gradient(180deg,rgba(0,0,0,.72),transparent);opacity:0;transform:translateY(-2vh);transition:opacity .4s,transform .55s cubic-bezier(.2,.8,.2,1)}
          #tp .tp-t{font-size:3.8vh;font-weight:700;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:80vw;text-shadow:0 .3vh 2vh rgba(0,0,0,.6)}
          #tp .tp-c{font-size:2.2vh;color:#a8998a;margin-top:.7vh}
          #tp .tp-bot{position:absolute;left:0;right:0;bottom:0;padding:12vh 5vw 4vh;background:linear-gradient(0deg,rgba(0,0,0,.85),transparent);opacity:0;transform:translateY(3vh);transition:opacity .4s,transform .55s cubic-bezier(.2,.8,.2,1)}
          #tp.show .tp-top,#tp.show .tp-bot{opacity:1;transform:none}
          #tp .tp-bar{height:.9vh;border-radius:9px;background:rgba(255,255,255,.25);overflow:hidden}
          #tp .tp-bar i{display:block;height:100%;width:0;border-radius:9px;background:#f1e8dc}
          #tp.isad .tp-bar i{background:#ffd36b}
          #tp .tp-row{display:grid;grid-template-columns:1fr auto 1fr;align-items:center;margin-top:2.2vh}
          #tp .tp-tm{font-size:2.3vh;font-weight:600}
          #tp .tp-vol{justify-self:end;font-size:2vh;color:#a8998a}
          #tp .tp-btns{display:flex;gap:2.4vh;align-items:center}
          #tp .tp-b{position:relative;display:grid;place-items:center;width:7vh;height:7vh;border-radius:50%;background:rgba(255,255,255,.13);-webkit-backdrop-filter:blur(2vh);backdrop-filter:blur(2vh);box-shadow:inset 0 0 0 .2vh rgba(255,255,255,.22);transition:transform .45s cubic-bezier(.34,1.4,.64,1),background .25s}
          #tp .tp-b svg{width:3.4vh;height:3.4vh;fill:#f1e8dc}
          #tp .tp-b em{position:absolute;bottom:.8vh;font-size:1.2vh;font-style:normal;font-weight:700}
          #tp .tp-pp{width:9.6vh;height:9.6vh;background:#f1e8dc}
          #tp .tp-pp svg{width:4.8vh;height:4.8vh;fill:#16110e}
          #tp .tp-b.pop{transform:scale(1.2);background:rgba(255,255,255,.38)}
          #tp .tp-pp.pop{background:#fff}
          #tp .tp-hints{display:flex;flex-wrap:wrap;justify-content:center;gap:2.2vh;width:fit-content;max-width:90vw;margin:2.8vh auto 0;padding:1.1vh 2.4vh;border-radius:99px;background:rgba(14,11,9,.6);-webkit-backdrop-filter:blur(2.4vh);backdrop-filter:blur(2.4vh);box-shadow:inset 0 0 0 .2vh rgba(255,255,255,.18);font-size:1.7vh;font-weight:500;color:#f1e8dc}
          #tp .tp-hints b{display:inline-grid;place-items:center;min-width:2.6vh;height:2.6vh;padding:0 .6vh;margin-right:.7vh;border-radius:99px;background:rgba(255,255,255,.2);color:#f1e8dc;font-size:1.4vh;font-weight:600}
          #tp .tp-ad{position:absolute;left:5vw;top:4vh;display:none;align-items:center;gap:1.4vh;padding:1.1vh 2.6vh 1.1vh 1.2vh;border-radius:99px;background:rgba(0,0,0,.6);-webkit-backdrop-filter:blur(2vh);backdrop-filter:blur(2vh);font-size:2.1vh;font-weight:600}
          #tp .tp-ad b{padding:.3vh 1.4vh;border-radius:99px;background:#ffd36b;color:#16110e;font-size:1.8vh}
          #tp.isad .tp-ad{display:flex}
          #tp.isad .tp-t,#tp.isad .tp-c{opacity:0}
          #tp .tp-skip{position:absolute;right:5vw;bottom:6vh;display:none;align-items:center;gap:1.6vh;padding:1.5vh 3.2vh;border-radius:99px;background:#f1e8dc;color:#16110e;font-size:2.8vh;font-weight:700;box-shadow:0 2vh 6vh rgba(0,0,0,.5);transition:bottom .5s cubic-bezier(.2,.8,.2,1)}
          #tp .tp-skip b{display:grid;place-items:center;width:3.8vh;height:3.8vh;border-radius:50%;background:#16110e;color:#f1e8dc;font-size:2vh}
          #tp.canskip .tp-skip{display:flex;animation:tpin .5s cubic-bezier(.34,1.4,.64,1)}
          #tp.show .tp-skip{bottom:34vh}
          @keyframes tpin{from{opacity:0;transform:translateY(2vh) scale(.9)}}
          #tp .tp-flash{position:absolute;left:50%;top:50%;width:14vh;height:14vh;margin:-7vh 0 0 -7vh;display:grid;place-items:center;border-radius:50%;background:rgba(0,0,0,.55);opacity:0}
          #tp .tp-flash svg{width:7vh;height:7vh;fill:#f1e8dc}
          #tp .tp-flash.on{animation:tpf .7s ease-out}
          @keyframes tpf{0%{opacity:0;transform:scale(.7)}25%{opacity:1;transform:scale(1)}100%{opacity:0;transform:scale(1.15)}}
          #tp .tp-toast{position:absolute;left:50%;top:12vh;transform:translate(-50%,-1vh);padding:1.3vh 3vh;border-radius:99px;background:#f1e8dc;color:#16110e;font-size:2.1vh;font-weight:600;opacity:0;transition:opacity .3s,transform .4s cubic-bezier(.2,.8,.2,1)}
          #tp .tp-toast.on{opacity:1;transform:translate(-50%,0)}

          #tp .tp-q{position:absolute;right:5vw;top:50%;min-width:28vh;padding:2.4vh 2.6vh;border-radius:2vh;background:rgba(14,11,9,.84);-webkit-backdrop-filter:blur(2.4vh);backdrop-filter:blur(2.4vh);box-shadow:inset 0 0 0 .2vh rgba(255,255,255,.2),0 3vh 8vh rgba(0,0,0,.5);opacity:0;visibility:hidden;transform:translateY(-46%) scale(.96);transition:opacity .3s,visibility .3s,transform .5s cubic-bezier(.34,1.4,.64,1)}
          #tp.qopen .tp-q{opacity:1;visibility:visible;transform:translateY(-50%)}
          #tp .tp-qt{font-size:2.6vh;font-weight:700}
          #tp .tp-qs{font-size:1.7vh;color:#a8998a;margin:.5vh 0 1.4vh;min-height:2.2vh}
          #tp .tp-qi{display:flex;justify-content:space-between;gap:3vh;padding:1.1vh 1.8vh;border-radius:1.2vh;font-size:2.1vh;font-weight:500}
          #tp .tp-qi.f{background:#f1e8dc;color:#16110e}
          #tp .tp-end{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;background:rgba(10,8,7,.9);opacity:0;visibility:hidden;transition:opacity .4s,visibility .4s}
          #tp.eopen .tp-end{opacity:1;visibility:visible}
          #tp.eopen .tp-top,#tp.eopen .tp-bot,#tp.eopen .tp-skip,#tp.eopen .tp-flash{opacity:0!important;visibility:hidden}
          #tp .tp-et{font-size:5vh;font-weight:800}
          #tp .tp-es{font-size:2.2vh;color:#a8998a;margin:.8vh 0 3.4vh;max-width:80vw;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
          #tp .tp-eg{display:grid;grid-template-columns:repeat(3,34vh);gap:3.2vh 3vh}
          #tp .tp-ec{min-width:0;transition:transform .45s cubic-bezier(.34,1.4,.64,1)}
          #tp .tp-eimg{display:block;width:34vh;height:19.1vh;object-fit:cover;border-radius:1vh;background:rgba(255,255,255,.08);transition:box-shadow .3s}
          #tp .tp-ec.f{transform:scale(1.06)}
          #tp .tp-ec.f .tp-eimg{box-shadow:0 0 0 .45vh #f1e8dc,0 2.4vh 6vh rgba(0,0,0,.6)}
          #tp .tp-en{margin-top:1.2vh;font-size:1.9vh;font-weight:600;line-height:1.25;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden}
          #tp .tp-ea{margin-top:.4vh;font-size:1.6vh;color:#a8998a;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
          #tp .tp-none{font-size:2.4vh;color:#a8998a;padding:4vh 0}
          #tp .tp-eh{margin-top:4vh}
          `;
          const ICON = {
            play: "M8 5v14l11-7z", pause: "M6 5h4v14H6zM14 5h4v14h-4z",
            prev: "M6 6h2v12H6zM9.5 12L18 6v12z", next: "M16 6h2v12h-2zM6 6l8.5 6L6 18z",
            back: "M11 6l-6 6 6 6zM19 6l-6 6 6 6z", fwd: "M13 6l6 6-6 6zM5 6l6 6-6 6z"
          };
          // YouTube bloquea innerHTML en sus páginas (Trusted Types), así que el overlay se arma pieza por pieza con el DOM
          const NS = "http://www.w3.org/2000/svg";
          const svgEl = p => { const s = document.createElementNS(NS, "svg"), pa = document.createElementNS(NS, "path"); s.setAttribute("viewBox", "0 0 24 24"); pa.setAttribute("d", p); s.append(pa); return s; };
          const E = (tag, cls, kids, at) => { const e = document.createElement(tag); if (cls) e.className = cls; (kids || []).forEach(k => e.append(k)); if (at) for (const a in at) e.setAttribute(a, at[a]); return e; };
          const HINTS = [[["A"], "Play/Pause"], [["X"], "Skip ad"], [["LB", "RB"], "Prev/Next"], [["◀", "▶"], "Seek"], [["▲", "▼"], "Volume"], [["Y"], "Captions"], [["View"], "Quality"], [["B"], "Back"], [["☰"], "Hide/Show"]];
          const btn = (a, icon, extra, cls) => E("div", "tp-b" + (cls || ""), [svgEl(icon)].concat(extra ? [E("em", "", [extra])] : []), { "data-a": a });
          const build = () => E("div", "", [
            E("div", "tp-top", [E("div", "tp-t"), E("div", "tp-c")]),
            E("div", "tp-ad", [E("b", "", ["Ad"]), E("span")]),
            E("div", "tp-skip", [E("span", "", ["Skip ad"]), E("b", "", ["X"])]),
            E("div", "tp-flash"), E("div", "tp-toast"),
            E("div", "tp-bot", [
              E("div", "tp-bar", [E("i")]),
              E("div", "tp-row", [E("span", "tp-tm"),
                E("div", "tp-btns", [btn("prev", ICON.prev), btn("back", ICON.back, "10"), btn("pp", ICON.pause, "", " tp-pp"), btn("fwd", ICON.fwd, "10"), btn("next", ICON.next)]),
                E("span", "tp-vol")]),
              E("div", "tp-hints", HINTS.map(h => E("span", "", h[0].map(k => E("b", "", [k])).concat([h[1]]))))
            ]),
            E("div", "tp-q", [E("div", "tp-qt"), E("div", "tp-qs"), E("div", "tp-ql")]),
            E("div", "tp-end", [E("div", "tp-et"), E("div", "tp-es"), E("div", "tp-eg"), E("div", "tp-eh tp-hints")])
          ]);

          // ---- Utilidades ----
          const Q = s => document.querySelector(s), mp = () => document.getElementById("movie_player"), V = () => Q("video");
          let tp = null, until = 0, toastT = 0, lastPaused = null, lastT = "", lastC = "";
          const q = s => tp && tp.querySelector(s);
          const tm = x => { x = Math.max(0, x | 0); const h = (x / 3600) | 0, m = ((x % 3600) / 60) | 0, s = x % 60; return (h ? h + ":" + String(m).padStart(2, "0") : m) + ":" + String(s).padStart(2, "0"); };
          const isAd = () => { const p = mp(); return !!(p && p.classList.contains("ad-showing")); };
          const skipBtn = () => { const s = Q(".ytp-skip-ad-button,.ytp-ad-skip-button,.ytp-ad-skip-button-modern"); return s && s.offsetParent ? s : null; };
          const usable = b => b && b.style.display != "none" && b.getAttribute("aria-disabled") != "true";
          const poke = () => { until = performance.now() + 3500; };
          window.__tpShow = ms => { until = performance.now() + ms; };   // la app lo llama cuando el video ya se ve: controles y atajos 8 segundos
          const say = t => { const e = q(".tp-toast"); if (!e) return; e.textContent = t; e.classList.add("on"); clearTimeout(toastT); toastT = setTimeout(() => e.classList.remove("on"), 1800); };
          const flash = p => { const f = q(".tp-flash"); if (!f) return; f.replaceChildren(svgEl(p)); f.classList.remove("on"); void f.offsetWidth; f.classList.add("on"); };
          const pop = a => { const b = q('[data-a="' + a + '"]'); if (!b) return; b.classList.add("pop"); setTimeout(() => b.classList.remove("pop"), 180); };

          function ensure() {
            if (!document.documentElement || !document.body) return;
            if (!document.getElementById("tp-css")) { const st = document.createElement("style"); st.id = "tp-css"; st.textContent = CSS; document.documentElement.appendChild(st); }
            if (!tp) { tp = build(); tp.id = "tp"; }
            const host = document.fullscreenElement || document.body;   // en pantalla completa solo se ve lo que está dentro del elemento
            if (tp.parentNode !== host) { host.appendChild(tp); }
          }


          // ---- Calidad (resolución) ----
          const QL = { highres: "4320p", hd2880: "2880p", hd2160: "2160p", hd1440: "1440p", hd1080: "1080p", hd720: "720p", large: "480p", medium: "360p", small: "240p", tiny: "144p", auto: "Auto" };
          const qLabel = x => QL[x] || x;
          const curId = () => new URLSearchParams(location.search).get("v") || "";
          const getPref = () => { try { return localStorage.getItem("tp_quality") || "auto"; } catch (e) { return "auto"; } };
          const setPref = x => { try { localStorage.setItem("tp_quality", x); } catch (e) { } };
          let uiMode = "play", qi = 0, qList = [], qT = 0, qFor = "";
          function applyQ(x) {
            const p = mp(); if (!p) return;
            try { if (p.setPlaybackQualityRange) p.setPlaybackQualityRange(x, x); } catch (e) { }
            try { if (p.setPlaybackQuality) p.setPlaybackQuality(x); } catch (e) { }
          }
          function qLevels() {
            const p = mp(); let lv = [];
            try { lv = (p && p.getAvailableQualityLevels && p.getAvailableQualityLevels()) || []; } catch (e) { }
            lv = lv.filter(x => x != "auto");
            return lv.length ? ["auto"].concat(lv) : [];
          }
          function drawQ() {
            const p = mp(), pref = getPref();
            let now = ""; try { now = p && p.getPlaybackQuality ? p.getPlaybackQuality() : ""; } catch (e) { }
            q(".tp-qt").textContent = "Quality";
            q(".tp-qs").textContent = QL[now] ? "Now playing at " + qLabel(now) : "";
            q(".tp-ql").replaceChildren(...qList.map((l, i) => E("div", "tp-qi" + (i == qi ? " f" : ""), [E("span", "", [qLabel(l)]), E("span", "", [l == pref ? "✓" : ""])])));
          }
          function armQ() { clearTimeout(qT); qT = setTimeout(closeQ, 7000); }
          function closeQ() { uiMode = "play"; if (tp) tp.classList.remove("qopen"); clearTimeout(qT); }
          function openQ() {
            if (!mp() || isAd()) { say("Quality isn't available right now"); return; }
            qList = qLevels();
            if (!qList.length) { say("Quality isn't available right now"); return; }
            qi = Math.max(0, qList.indexOf(getPref()));
            uiMode = "quality"; tp.classList.add("qopen"); drawQ(); poke(); armQ();
          }
          function qKey(k) {
            armQ(); poke();
            if (k == "up") { qi = Math.max(0, qi - 1); }
            else if (k == "down") { qi = Math.min(qList.length - 1, qi + 1); }
            else if (k == "pp") {
              const l = qList[qi]; setPref(l); applyQ(l); closeQ(); say("Quality: " + qLabel(l));
              setTimeout(() => { try { const n = mp().getPlaybackQuality(); if (l != "auto" && n != l && QL[n]) say("YouTube is using " + qLabel(n)); } catch (e) { } }, 2500);
              return;
            }
            else if (k == "close" || k == "qual" || k == "menu") { closeQ(); return; }
            drawQ();
          }
          // Al empezar cada video, vuelve a poner la calidad que elegiste
          setInterval(() => {
            const id = curId(), p = mp(); if (!id || qFor == id || !p || isAd()) return;
            if (!qLevels().length) return;
            qFor = id;
            const pref = getPref(); if (pref != "auto") applyQ(pref);
          }, 800);

          // ---- Al terminar el video: recomendados ----
          let recs = [], ei = 0, endFor = "";
          const clean = s => (s || "").replace(/\s+/g, " ").trim();
          function collectRecs() {
            const seen = new Set([curId()]), out = [];
            const add = (a, title, chan) => {
              if (!a || out.length >= 6) return;
              let id = ""; try { id = new URL(a.href, location.href).searchParams.get("v") || ""; } catch (e) { }
              title = clean(title);
              if (!/^[\w-]{11}$/.test(id) || seen.has(id) || title.length < 4 || /^\d+(:\d{2}){1,2}$/.test(title)) return;
              seen.add(id); out.push({ id, title, chan: clean(chan).split("•")[0].trim(), a });
            };
            const txt = (root, sel) => { const e = root.querySelector(sel); return e ? e.textContent : ""; };
            // 1) las sugerencias que YouTube mismo pone en su pantalla final
            document.querySelectorAll(".ytp-endscreen-content a.ytp-videowall-still").forEach(a =>
              add(a, txt(a, ".ytp-videowall-still-info-title"), txt(a, ".ytp-videowall-still-info-author")));
            // 2) la lista de "siguientes" de la página
            document.querySelectorAll("ytd-watch-next-secondary-results-renderer yt-lockup-view-model, ytd-watch-next-secondary-results-renderer ytd-compact-video-renderer").forEach(c => {
              const ta = c.querySelector("a.yt-lockup-metadata-view-model__title, a#video-title, #video-title");
              add(c.querySelector("a[href*='/watch?v=']"), ta ? (ta.getAttribute("title") || ta.textContent) : "", txt(c, ".yt-content-metadata-view-model__metadata-text, ytd-channel-name, #channel-name"));
            });
            // 3) por si YouTube cambió el diseño: cualquier enlace a un video con título legible
            if (out.length < 3) document.querySelectorAll("ytd-watch-next-secondary-results-renderer a[href*='/watch?v=']").forEach(a =>
              add(a, a.getAttribute("title") || a.getAttribute("aria-label") || a.textContent, ""));
            return out;
          }
          function drawEnd() {
            q(".tp-et").textContent = "Up next";
            q(".tp-es").textContent = lastT ? "Finished: " + lastT : "";
            if (recs.length) {
              q(".tp-eg").replaceChildren(...recs.map((r, i) => E("div", "tp-ec" + (i == ei ? " f" : ""), [
                E("img", "tp-eimg", [], { src: "https://i.ytimg.com/vi/" + r.id + "/mqdefault.jpg" }),
                E("div", "tp-en", [r.title]), E("div", "tp-ea", [r.chan])])));
            } else { q(".tp-eg").replaceChildren(E("div", "tp-none", ["No suggestions found"])); }
            q(".tp-eh").replaceChildren(...[[["A"], "Play"], [["◀", "▶", "▲", "▼"], "Choose"], [["X"], "Replay"], [["B"], "Close"]].map(h => E("span", "", h[0].map(k => E("b", "", [k])).concat([h[1]]))));
          }
          function showEnd() {
            // En una lista de reproducción YouTube sigue solo con el siguiente video
            if (new URLSearchParams(location.search).has("list") && usable(Q(".ytp-next-button"))) return;
            closeQ(); recs = collectRecs(); ei = 0; uiMode = "end"; drawEnd(); tp.classList.add("eopen");
          }
          function hideEnd() { uiMode = "play"; if (tp) tp.classList.remove("eopen"); }
          function playRec(r) {
            const from = curId(); hideEnd();
            try { r.a.click(); } catch (e) { }
            // Si YouTube no cambió de video, la app lo abre por su cuenta
            setTimeout(() => { if (curId() == from) chrome.webview.postMessage("player:play|" + r.id); }, 3500);
          }
          function replay() {
            const v = V(); hideEnd(); endFor = curId(); setTimeout(() => { endFor = ""; }, 3000);
            if (v) { v.currentTime = 0; v.play(); }
          }
          function eKey(k) {
            const n = recs.length;
            if (k == "close") { act.close(); return; }
            if (k == "skip") { replay(); return; }
            if (k == "pp") { if (n) { playRec(recs[ei]); } else { replay(); } return; }
            if (!n) return;
            if (k == "back") { ei = Math.max(0, ei - 1); }
            else if (k == "fwd") { ei = Math.min(n - 1, ei + 1); }
            else if (k == "up") { ei = ei >= 3 ? ei - 3 : ei; }
            else if (k == "down") { ei = ei + 3 < n ? ei + 3 : ei; }
            else return;
            [...q(".tp-eg").children].forEach((c, i) => c.classList.toggle("f", i == ei));
          }

          // ---- Acciones (las usan el gamepad y el teclado) ----
          function seek(d) {
            const v = V();
            if (!v) return;
            if (isAd()) { say("Can't seek during an ad"); return; }
            v.currentTime = Math.max(0, Math.min(v.duration || 1e9, v.currentTime + d));
            pop(d < 0 ? "back" : "fwd");
          }
          function vol(d) {
            const p = mp(), v = V();
            let c = p && p.getVolume ? p.getVolume() : Math.round((v ? v.volume : 1) * 100);
            c = Math.max(0, Math.min(100, c + d));
            if (p && p.setVolume) { if (p.unMute) p.unMute(); p.setVolume(c); } else if (v) { v.volume = c / 100; }
            say("Volume " + c + "%");
          }
          const act = {
            pp() { const v = V(); if (!v) return; if (v.paused) { v.play(); flash(ICON.play); } else { v.pause(); flash(ICON.pause); } pop("pp"); },
            back() { seek(-10); }, fwd() { seek(10); }, sb() { seek(-5); }, sf() { seek(5); },
            up() { vol(10); }, down() { vol(-10); },
            prev() { const b = Q(".ytp-prev-button"); if (usable(b)) { b.click(); say("Previous video"); } else { const v = V(); if (v && !isAd()) { v.currentTime = 0; say("Restart"); } } pop("prev"); },
            next() { const b = Q(".ytp-next-button"); if (usable(b)) { b.click(); say("Next video"); } else { say("No next video"); } pop("next"); },
            skip() {   // YouTube ignora los clics falsos: le pedimos a la app que haga un clic real donde está el botón
              const s = skipBtn();
              if (!s) { say(isAd() ? "This ad can't be skipped yet" : "No ad to skip"); return; }
              const r = s.getBoundingClientRect();
              chrome.webview.postMessage("player:skip|" + Math.round(r.left + r.width / 2) + "|" + Math.round(r.top + r.height / 2));
            },
            cc() { const b = Q(".ytp-subtitles-button"); if (!b || b.style.display == "none") { say("No captions"); return; } b.click(); setTimeout(() => say("Captions " + (b.getAttribute("aria-pressed") == "true" ? "on" : "off")), 150); },
            menu() { },
            qual() { openQ(); },
            close() { chrome.webview.postMessage("player:close"); }
          };
          const fire = k => {
            if (!/(^|\.)youtube\.com$/.test(location.hostname) || !V()) return;   // en about:blank (video cerrado) este reproductor no hace nada
            if (uiMode == "quality") { return qKey(k); }
            if (uiMode == "end") { return eKey(k); }
            if (k == "menu") { until = performance.now() < until ? 0 : performance.now() + 9000; return; }   // ☰: muestra u oculta controles y atajos
            if (act[k]) { poke(); act[k](); }
          };

          addEventListener("keydown", e => {
            const m = { ArrowLeft: "back", ArrowRight: "fwd", ArrowUp: "up", ArrowDown: "down", Enter: "pp", " ": "pp", Escape: "close", Backspace: "close", x: "skip", n: "next", p: "prev", c: "cc", h: "menu", q: "qual" }[e.key];
            if (!m) return;
            e.preventDefault(); e.stopImmediatePropagation(); fire(m);
          }, true);

          const held = {}, REP = { back: 1, fwd: 1, up: 1, down: 1, sb: 1, sf: 1 };
          setInterval(() => {
            const g = [...(navigator.getGamepads ? navigator.getGamepads() : [])].find(x => x); if (!g) return;
            const b = i => g.buttons[i] && g.buttons[i].pressed;
            const now = {
              pp: b(0), close: b(1), skip: b(2), cc: b(3), prev: b(4), next: b(5), menu: b(9), qual: b(8),
              back: b(14) || g.axes[0] < -.6, fwd: b(15) || g.axes[0] > .6, up: b(12) || g.axes[1] < -.6, down: b(13) || g.axes[1] > .6,
              sb: g.axes[2] < -.6, sf: g.axes[2] > .6
            };
            const t = performance.now();
            for (const k in now) {
              if (!now[k]) held[k] = 0;
              else if (!held[k]) { fire(k); held[k] = t + 350; }
              else if (REP[k] && t > held[k]) { fire(k); held[k] = t + 150; }
            }
          }, 60);

          // ---- Refresca la pantalla de controles ----
          setInterval(() => {
            ensure(); if (!tp) return;
            const v = V(), ad = isAd(), sk = skipBtn(), p = mp();
            tp.classList.toggle("isad", ad); tp.classList.toggle("canskip", !!sk);
            if (v) {
              const d = v.duration || 0;
              q(".tp-bar i").style.width = (d ? Math.min(100, v.currentTime / d * 100) : 0) + "%";
              q(".tp-tm").textContent = tm(v.currentTime) + " / " + tm(d);
              if (lastPaused !== v.paused) { lastPaused = v.paused; q(".tp-pp").replaceChildren(svgEl(v.paused ? ICON.play : ICON.pause)); poke(); }
              if (ad) { q(".tp-ad span").textContent = sk ? "Press X to skip" : "Ad · " + tm(Math.max(0, d - v.currentTime)) + " left"; }
            }
            const h1 = Q("ytd-watch-metadata h1") || Q("h1.title") || Q("h1");
            const t = ((h1 && h1.textContent) || document.title.replace(/ - YouTube$/, "")).trim();
            const ch = Q("ytd-watch-metadata ytd-channel-name a") || Q("#owner #channel-name a") || Q("ytd-video-owner-renderer a");
            const c = ch ? ch.textContent.trim() : "";
            if (t != lastT) { lastT = t; q(".tp-t").textContent = t; }
            if (c != lastC) { lastC = c; q(".tp-c").textContent = c; }
            let ql = ""; try { const pq = p && p.getPlaybackQuality ? p.getPlaybackQuality() : ""; if (QL[pq] && pq != "auto") ql = " · " + QL[pq]; } catch (e) { }
            q(".tp-vol").textContent = p && p.getVolume ? "Volume " + Math.round(p.getVolume()) + "%" + ql : "";
            if (uiMode == "end" && curId() != endFor) { hideEnd(); }
            else if (uiMode == "play" && v && !ad && curId() && endFor !== curId()) {
              let st = -1; try { st = p && p.getPlayerState ? p.getPlayerState() : -1; } catch (e) { }
              if (v.ended || st === 0) { endFor = curId(); showEnd(); }
            }
            tp.classList.toggle("show", !!(v && v.paused) || performance.now() < until);
          }, 200);

          // ---- Le cuenta a la app qué video suena (para el SMTC de Windows) ----
          setInterval(() => {
            const v = V(); if (!v) return;
            const id = new URLSearchParams(location.search).get("v") || "";
            chrome.webview.postMessage("player:state|" + JSON.stringify({ id, t: lastT, c: lastC, pos: v.currentTime || 0, dur: isFinite(v.duration) ? v.duration : 0, p: !v.paused, ad: isAd() }));
          }, 1000);
        })();
        """;

    // ---------- Windows media (SMTC), igual que en Controllify ----------
    // La app crea su propio SMTC nativo ligado a esta ventana y le pasa lo que suena (título, carátula en memoria,
    // progreso y botones). Mientras se ve un video el SMTC sigue al video; si no, sigue a la música.
    readonly bool debug;
    readonly bool noSmtc;   // --no-smtc: usa solo las sesiones de los navegadores embebidos
    readonly HttpClient http = new();
    SystemMediaTransportControls? smtc;
    InMemoryRandomAccessStream? thumbStream;   // se mantiene en memoria para que la carátula no desaparezca
    bool videoActive, lastIsVideo;
    string lastKey = "", lastArt = "", lastMusicJson = "", lastDump = "";
    string? lastTitle, lastArtist;
    double tlPos = -1, tlDur = -1;
    bool tlPaused;
    DateTime tlAt = DateTime.UtcNow;
    System.Threading.Timer? freeTimer;   // reloj que NO depende de la ventana (para diagnóstico)
    static readonly object logLock = new();

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YTConsole", "log.txt");

    void Log(string msg)
    {
        if (!debug) { return; }
        try
        {
            lock (logLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                File.AppendAllText(LogFile, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
        }
        catch { }
    }

    void InitSmtc()
    {
        try
        {
            smtc = SystemMediaTransportControlsInterop.GetForWindow(Handle);
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsNextEnabled = true;
            smtc.IsPreviousEnabled = true;
            smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
            Log("SMTC creado");

            smtc.ButtonPressed += (s, a) =>
            {
                string? cmd = a.Button switch
                {
                    SystemMediaTransportControlsButton.Play => "play",
                    SystemMediaTransportControlsButton.Pause => "pause",
                    SystemMediaTransportControlsButton.Next => "next",
                    SystemMediaTransportControlsButton.Previous => "previous",
                    _ => null
                };
                Log("SMTC boton: " + a.Button + " -> " + (cmd ?? "(ignorado)"));
                if (cmd != null) { SmtcCommand(cmd); }
            };

            smtc.PlaybackPositionChangeRequested += (s, a) =>
            {
                Log("SMTC seek: " + a.RequestedPlaybackPosition);
                SmtcCommand("seek:" + (long)a.RequestedPlaybackPosition.TotalMilliseconds);
            };
        }
        catch (Exception ex) { Log("SMTC error: " + ex); }
    }

    // Los botones de Windows llegan en otro hilo: se pasan al hilo de la ventana y de ahí al reproductor que esté activo
    void SmtcCommand(string cmd) => BeginInvoke(new Action(async () =>
    {
        try { await RunSmtcCommand(cmd); }
        catch (Exception ex) { Log("error con boton SMTC: " + ex.Message); }
    }));

    async Task RunSmtcCommand(string cmd)
    {
        bool toVideo = videoActive && vid?.CoreWebView2 != null;
        var w = toVideo ? vid : mus;
        if (w?.CoreWebView2 == null) { return; }
        if (!toVideo) { await w.CoreWebView2.ExecuteScriptAsync("window.__hold=0"); }   // tú mandas: se quita el candado de la música

        string js;
        if (cmd.StartsWith("seek:"))
        {
            if (!long.TryParse(cmd[5..], out long ms)) { return; }
            js = "(()=>{const v=document.querySelector('video');if(v)v.currentTime=" +
                 (ms / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture) + "})()";
        }
        else
        {
            js = (toVideo, cmd) switch
            {
                (_, "play") => "document.querySelector('video')?.play()",
                (_, "pause") => "document.querySelector('video')?.pause()",
                (true, "next") => "(()=>{const b=document.querySelector('.ytp-next-button');if(b&&b.style.display!='none')b.click()})()",
                (true, "previous") => "(()=>{const b=document.querySelector('.ytp-prev-button');if(b&&b.style.display!='none')b.click();else{const v=document.querySelector('video');if(v)v.currentTime=0}})()",
                (false, "next") => "document.querySelector('ytmusic-player-bar .next-button')?.click()",
                (false, "previous") => "document.querySelector('ytmusic-player-bar .previous-button')?.click()",
                _ => ""
            };
        }
        if (js != "") { await w.CoreWebView2.ExecuteScriptAsync(js); }
        Log("enviado al reproductor (" + (toVideo ? "video" : "musica") + "): " + cmd);

        // Respuesta inmediata en Windows; el siguiente aviso del reproductor la confirma
        if (smtc != null)
        {
            if (cmd == "play") { smtc.PlaybackStatus = MediaPlaybackStatus.Playing; }
            else if (cmd == "pause") { smtc.PlaybackStatus = MediaPlaybackStatus.Paused; }
        }
    }

    static string Str(JsonElement m, string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static double Num(JsonElement m, string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    static bool Flag(JsonElement m, string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    // Estado de la música (llega cada segundo desde la página oculta de YouTube Music)
    void OnMusicState(string json)
    {
        lastMusicJson = json;
        if (smtc == null || videoActive) { return; }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            bool ad = Flag(m, "ad");
            string title = Str(m, "t").Trim();
            if (title == "" && !ad) { return; }   // todavía no carga la canción
            string artist = Str(m, "a").Split('•')[0].Trim();   // "Artista • Álbum • Año" -> "Artista"
            string art = System.Text.RegularExpressions.Regex.Replace(Str(m, "img"), @"=w\d+-h\d+\S*$", "=w544-h544");   // carátula grande
            if (ad) { title = "Advertisement"; artist = ""; }
            ApplySmtc(false, ad ? "ad" : title + "|" + artist, title, artist, art, !Flag(m, "p"), Num(m, "pos"), Num(m, "dur"));
        }
        catch (Exception ex) { Log("error estado musica: " + ex.Message); }
    }

    // Estado del video (llega cada segundo desde la página de YouTube)
    void OnVideoState(string json)
    {
        if (smtc == null || !videoActive) { return; }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            string id = Str(m, "id"), title = Str(m, "t").Trim(), chan = Str(m, "c").Trim();
            if (title == "") { title = "YouTube"; }
            if (Flag(m, "ad")) { title = "Advertisement"; chan = ""; }
            string art = System.Text.RegularExpressions.Regex.IsMatch(id, "^[\\w-]{11}$") ? "https://i.ytimg.com/vi/" + id + "/hqdefault.jpg" : "";
            ApplySmtc(true, "v|" + id, title, chan, art, !Flag(m, "p"), Num(m, "pos"), Num(m, "dur"));
        }
        catch (Exception ex) { Log("error estado video: " + ex.Message); }
    }

    // Pone en el SMTC lo que suena. pos y dur vienen en segundos.
    void ApplySmtc(bool video, string key, string title, string artist, string art, bool paused, double pos, double dur)
    {
        if (smtc == null) { return; }
        var u = smtc.DisplayUpdater;

        if (video != lastIsVideo || lastTitle == null)
        {
            lastIsVideo = video;
            u.ClearAll();
            lastTitle = lastArtist = null;
            lastArt = "";   // ClearAll también borra la carátula: hay que ponerla de nuevo
            tlPos = -1;
        }
        if (title != lastTitle || artist != lastArtist)
        {
            lastTitle = title; lastArtist = artist;
            if (video) { u.Type = MediaPlaybackType.Video; u.VideoProperties.Title = title; u.VideoProperties.Subtitle = artist; }
            else { u.Type = MediaPlaybackType.Music; u.MusicProperties.Title = title; u.MusicProperties.Artist = artist; }
            u.Update();
            Log((video ? "video: " : "cancion: ") + title);
        }
        if (key != lastKey) { lastKey = key; tlPos = -1; }

        if (art != "" && art != lastArt && art.StartsWith("http")) { lastArt = art; _ = SetThumbnailAsync(art); }

        smtc.PlaybackStatus = paused ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Playing;

        // La línea de tiempo solo se actualiza si cambió algo (si no, Windows la avanza sola)
        double expected = tlPos < 0 ? -1 : tlPos + (tlPaused ? 0 : (DateTime.UtcNow - tlAt).TotalSeconds);
        if (tlPos < 0 || paused != tlPaused || Math.Abs(dur - tlDur) > 0.5 || Math.Abs(pos - expected) > 1.5)
        {
            tlPos = pos; tlDur = dur; tlPaused = paused; tlAt = DateTime.UtcNow;
            double end = Math.Max(dur, 0);
            smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                EndTime = TimeSpan.FromSeconds(end),
                MaxSeekTime = TimeSpan.FromSeconds(end),
                Position = TimeSpan.FromSeconds(end > 0 ? Math.Min(pos, end) : 0)
            });
        }
        // (si cambió el estado se registra para el log)
        if (debug && (paused != lastLoggedPause)) { lastLoggedPause = paused; Log("estado: " + (paused ? "pausa" : "reproduciendo")); }
    }
    bool? lastLoggedPause;

    // Descarga la carátula y la guarda en memoria: así Windows no depende de una URL que puede caducar.
    async Task SetThumbnailAsync(string url)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (smtc == null || url != lastArt) { return; }   // ya cambió la canción
            try
            {
                byte[] bytes = await http.GetByteArrayAsync(url);
                if (smtc == null || url != lastArt) { return; }
                var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);
                thumbStream?.Dispose();
                thumbStream = stream;
                var u = smtc.DisplayUpdater;
                u.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
                u.Update();
                Log("caratula lista (" + bytes.Length + " bytes)");
                return;
            }
            catch (Exception ex)
            {
                Log("error con la caratula: " + ex.Message);
                await Task.Delay(2000);   // un reintento por si fue la red
            }
        }
    }

    // ---------- Diagnóstico (solo con --debug) ----------
    void StartDebug()
    {
        // Cada segundo anota qué sesiones de medios ve Windows y cuál es la "actual"
        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        var lastTick = DateTime.Now;
        timer.Tick += async (s2, e2) =>
        {
            // Si la ventana se queda "dormida", este reloj tarda más de 1 s en volver a sonar
            double gap = (DateTime.Now - lastTick).TotalSeconds;
            lastTick = DateTime.Now;
            if (gap > 3) { Log("LATIDO nativo tardó " + gap.ToString("0.0") + " s"); }
            await DumpSessionsAsync();
        };
        timer.Start();

        // Segundo reloj en otro hilo: si este también se atrasa, el proceso entero se está congelando.
        // También anota qué programa está al frente.
        var lastFree = DateTime.Now;
        string lastFg = "";
        freeTimer = new System.Threading.Timer(_ =>
        {
            double gap = (DateTime.Now - lastFree).TotalSeconds;
            lastFree = DateTime.Now;
            if (gap > 3) { Log("LATIDO hilo libre tardó " + gap.ToString("0.0") + " s"); }
            try
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                string name = Process.GetProcessById((int)pid).ProcessName;
                if (name != lastFg) { lastFg = name; Log("PRIMER PLANO: " + name); }
            }
            catch { }
        }, null, 1000, 1000);
    }

    async Task DumpSessionsAsync()
    {
        try
        {
            var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            string text = "SESIONES (actual=" + (mgr.GetCurrentSession()?.SourceAppUserModelId ?? "ninguna") + "):";
            foreach (var s in mgr.GetSessions())
            {
                var p = await s.TryGetMediaPropertiesAsync();
                string thumb = "sin";   // tamaño real de la carátula que ve Windows
                try { if (p.Thumbnail != null) { using var ts = await p.Thumbnail.OpenReadAsync(); thumb = ts.Size + "B"; } }
                catch { thumb = "error"; }
                var info = s.GetPlaybackInfo();
                var c = info.Controls;
                // botones: P=play p=pausa N=siguiente B=anterior
                text += " [" + s.SourceAppUserModelId + " | " + info.PlaybackStatus + " | " + p.Title +
                        " | tipo=" + info.PlaybackType + " | caratula=" + thumb +
                        " | botones=" + (c.IsPlayEnabled ? "P" : "-") + (c.IsPauseEnabled ? "p" : "-") +
                        (c.IsNextEnabled ? "N" : "-") + (c.IsPreviousEnabled ? "B" : "-") + "]";
            }
            if (text != lastDump) { lastDump = text; Log(text); }   // solo escribe cuando algo cambia
        }
        catch (Exception ex) { Log("error sesiones: " + ex.Message); }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try
        {
            freeTimer?.Dispose();
            if (smtc != null)
            {
                smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                smtc.IsEnabled = false;
            }
        }
        catch { }
        base.OnFormClosed(e);
    }

    // Ventana emergente para iniciar sesión (se cierra sola al terminar)
    async void OpenLogin()
    {
        var win = new Form { Text = "Iniciar sesión", Size = new Size(520, 760), StartPosition = FormStartPosition.CenterScreen };
        var wv = new WebView2 { Dock = DockStyle.Fill };
        win.Controls.Add(wv);
        win.Show(this);

        // Usa el mismo entorno que la ventana principal, así comparten las cookies
        await wv.EnsureCoreWebView2Async(web.CoreWebView2.Environment);

        wv.CoreWebView2.NavigationCompleted += async (s, a) =>
        {
            // Cuando llega a music.youtube.com y ya hay sesión, terminamos
            if (!wv.CoreWebView2.Source.StartsWith("https://music.youtube.com")) { return; }
            if (!await IsLoggedIn()) { return; }
            win.Close();
            await ReloadApis();   // que los lectores de datos vean la sesión nueva
            web.CoreWebView2.PostWebMessageAsString("login:ok");
        };

        wv.CoreWebView2.Navigate("https://accounts.google.com/ServiceLogin?service=youtube&continue=" +
            Uri.EscapeDataString("https://music.youtube.com/"));
    }
}
