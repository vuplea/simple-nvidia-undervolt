using Microsoft.Playwright;

namespace SimpleNvidiaUndervolt.E2E;

/// <summary>
/// A sustained GPU load for the tests that assert where the boost algorithm settles: a
/// Playwright-driven browser window rendering a heavy WebGL2 fragment shader. The browser is headed
/// (headless Chromium renders WebGL in software) and launched with the background-throttling
/// features disabled so the frame loop keeps running while the window sits unfocused. Lazy: nothing
/// launches until a test asks for the load, and a host with no usable browser skips those tests
/// rather than failing them. On a hybrid-GPU machine the browser may render on the other adapter -
/// <see cref="SkipUnlessLoading"/> checks the NVIDIA telemetry, so that case skips too.
/// </summary>
public sealed class GpuLoadFixture : IDisposable
{
    /// <summary>The shader page. The canvas attributes set the framebuffer to 1440p regardless of
    /// the window size, and per-pixel loop iterations (window.iters, adjustable at runtime) set the
    /// per-frame cost. window.glOk reports the pipeline came up; window.fps starts reporting after
    /// the first two seconds of frames.</summary>
    private const string LoadPage = """
        <!doctype html>
        <title>gpu load</title>
        <canvas id="c" width="2560" height="1440"></canvas>
        <script>
        const gl = document.getElementById('c').getContext('webgl2', {powerPreference: 'high-performance'});
        if (gl) {
          const sh = (type, src) => {
            const s = gl.createShader(type);
            gl.shaderSource(s, src); gl.compileShader(s);
            if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
            return s;
          };
          const prog = gl.createProgram();
          gl.attachShader(prog, sh(gl.VERTEX_SHADER,
            '#version 300 es\nin vec2 p; void main(){ gl_Position = vec4(p,0.,1.); }'));
          gl.attachShader(prog, sh(gl.FRAGMENT_SHADER, `#version 300 es
            precision highp float;
            uniform float t; uniform int iters;
            out vec4 o;
            void main(){
              vec3 a = vec3(gl_FragCoord.xy / 1440.0, t);
              for (int i = 0; i < iters; i++) {
                a = vec3(sin(a.y*3.1 + t) + cos(a.z*1.7),
                         sin(a.z*2.3) + cos(a.x*2.9 - t),
                         sin(a.x*1.3) + cos(a.y*2.1));
              }
              o = vec4(0.5 + 0.5*sin(a), 1.0);
            }`));
          gl.linkProgram(prog); gl.useProgram(prog);
          gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
          gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1,-1, 3,-1, -1,3]), gl.STATIC_DRAW);
          const loc = gl.getAttribLocation(prog, 'p');
          gl.enableVertexAttribArray(loc);
          gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
          const tLoc = gl.getUniformLocation(prog, 't'), iLoc = gl.getUniformLocation(prog, 'iters');
          window.iters = 800;
          window.fps = -1;
          let frames = 0, t0 = performance.now();
          const frame = () => {
            gl.uniform1f(tLoc, performance.now()/1000);
            gl.uniform1i(iLoc, window.iters);
            gl.drawArrays(gl.TRIANGLES, 0, 3);
            frames++;
            const now = performance.now();
            if (now - t0 > 2000) { window.fps = frames * 1000 / (now - t0); frames = 0; t0 = now; }
            requestAnimationFrame(frame);
          };
          frame();
          window.glOk = true;
        }
        </script>
        """;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private string? _failure;
    private bool _started;

    /// <summary>Ensures the shader is rendering and the NVIDIA GPU is actually the one doing the
    /// work, or skips the calling test: no usable browser, no WebGL2, a frame loop that never came
    /// up, and a load that lands on another adapter are all environment limitations, not failures.</summary>
    public void SkipUnlessLoading(IntPtr gpu)
    {
        string? failure = Start();
        Skip.If(failure is not null, failure);

        // The frame loop is running; now the NVIDIA telemetry must show it - a boost-range clock and
        // real power draw - within a few seconds of warm-up.
        for (int i = 0; i < 20; i++)
        {
            var t = Telemetry.Sample(gpu);
            if (t.CoreMhz >= NvApi.MinBoostClockKhz / 1000 && t.PowerPercent >= 15)
            {
                return;
            }

            Thread.Sleep(500);
        }

        Skip.If(true, "the WebGL load did not register on the NVIDIA GPU (another adapter may be "
                      + "rendering it) - these tests need the load to land on the card under test.");
    }

    /// <summary>Halves the per-frame shader cost — for a card that hits its power limit under the
    /// default intensity, where TGP rather than the voltage cap would pick the operating point.</summary>
    public void HalveIntensity()
        => _page!.EvaluateAsync("() => { window.iters = Math.max(50, window.iters / 2); }")
            .GetAwaiter().GetResult();

    /// <summary>Launches the browser and shader on first use, returning null when the load page is
    /// up or the reason it can't be. The result is cached: one failed launch skips the rest of the
    /// class's tests with the same message instead of retrying per test.</summary>
    private string? Start()
    {
        if (_started)
        {
            return _failure;
        }

        _started = true;
        _failure = TryStart();
        return _failure;
    }

    private string? TryStart()
    {
        try
        {
            _playwright = Playwright.CreateAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return $"Playwright is unavailable ({ex.Message}).";
        }

        // A headed Chromium renders on the real GPU. Prefer the browsers already on the machine
        // (Edge ships with Windows) over the bundled Chromium, which exists only after
        // 'playwright install chromium'. The disabled features keep an unfocused/occluded window
        // rendering at full rate.
        var launchFailures = new List<string>();
        foreach (string channel in new[] { "msedge", "chrome", "" })
        {
            try
            {
                _browser = _playwright.Chromium.LaunchAsync(new()
                {
                    Headless = false,
                    Channel = channel,
                    Args = new[]
                    {
                        "--disable-background-timer-throttling",
                        "--disable-backgrounding-occluded-windows",
                        "--disable-renderer-backgrounding",
                        "--disable-features=CalculateNativeWinOcclusion",
                    },
                }).GetAwaiter().GetResult();
                break;
            }
            catch (Exception ex)
            {
                launchFailures.Add($"{(channel.Length == 0 ? "chromium" : channel)}: {ex.Message.Split('\n')[0]}");
            }
        }

        if (_browser is null)
        {
            return "no usable browser: install Edge/Chrome, or the matching Chromium with "
                   + $"'{ChromiumInstallCommand}' ({string.Join("; ", launchFailures)}).";
        }

        // Everything past the launch is guarded too: a browser that dies mid-setup is the same
        // environment limitation as one that never started, and must skip rather than fail.
        try
        {
            _page = _browser.NewPageAsync().GetAwaiter().GetResult();
            _page.SetContentAsync(LoadPage).GetAwaiter().GetResult();

            if (!_page.EvaluateAsync<bool>("() => window.glOk === true").GetAwaiter().GetResult())
            {
                return "the browser exposes no WebGL2 context, so it cannot generate a GPU load.";
            }

            // Wait for the frame loop's first fps report - proof frames are actually flowing.
            for (int i = 0; i < 20; i++)
            {
                if (_page.EvaluateAsync<double>("() => window.fps").GetAwaiter().GetResult() >= 0)
                {
                    return null;
                }

                Thread.Sleep(500);
            }

            return "the WebGL frame loop never produced frames (rendering may be throttled).";
        }
        catch (Exception ex)
        {
            return $"the browser failed while setting up the load page ({ex.Message.Split('\n')[0]}).";
        }
    }

    /// <summary>How to install a Chromium this project can drive. The bundled .NET Playwright has
    /// its own driver version, so a globally installed <c>playwright</c> CLI (often the Python one,
    /// at a different version) can fetch a browser it won't launch — the script generated next to
    /// these test binaries is the matching one, so the path is taken from the running build rather
    /// than hard-coded to one configuration.</summary>
    private static string ChromiumInstallCommand
        => $"pwsh \"{Path.Combine(AppContext.BaseDirectory, "playwright.ps1")}\" install chromium";

    public void Dispose()
    {
        try
        {
            _browser?.CloseAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Best-effort: an already-dead browser must not fail the suite's teardown.
        }

        _playwright?.Dispose();
    }
}
