using System.Diagnostics;
using System.Globalization;

namespace IptvPlayer.Player.Vlc.Services;

internal readonly record struct NativePlaybackDiagnostic(
    DateTimeOffset CapturedUtc, long TimestampTicks, long SessionId,
    string Kind, string Module, int? Milliseconds,
    int? DecoderThreads = null);

// Only fixed categories, known module names and numeric timings leave this parser.
// Never forward native message text: URLs and provider credentials can appear anywhere in it.
internal static class NativePlaybackDiagnosticClassifier
{
    public static NativePlaybackDiagnostic? Classify(string? message, string? module, long sessionId)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var safeModule = KnownModule(module);
        string? kind = null;
        int? milliseconds = null;
        int? decoderThreads = null;
        bool Has(string value) => message.Contains(value, StringComparison.OrdinalIgnoreCase);

        if (Has("using vout display module") || Has("using opengl module")
            || Has("using video decoder module"))
        {
            kind = "module-selected";
            safeModule = "unrecognized";
            foreach (var candidate in new[] { "direct3d11", "direct3d9", "glwin32", "wingdi", "directdraw", "wgl", "gl", "avcodec" })
            {
                if (Has($"\"{candidate}\"")) { safeModule = candidate; break; }
            }
        }
        else if (Has("no vout display modules matched")) kind = "video-output-unavailable";
        else if (Has("Using D3D11VA")) { kind = "hardware-decoder-selected"; safeModule = "d3d11va"; }
        else if (Has("Using DXVA2")) { kind = "hardware-decoder-selected"; safeModule = "dxva2"; }
        else if (safeModule is "d3d11_filters" or "d3d11_deinterlace")
        {
            foreach (var candidate in new[] { "x", "yadif2x", "bob", "blend", "ivtc" })
            {
                if (Has("using " + candidate + " deinterlacing mode"))
                {
                    kind = "deinterlace-mode-" + candidate;
                    break;
                }
            }
            if (kind is null && Has("trying bob")) kind = "deinterlace-fallback-bob";
        }
        else if (safeModule == "avcodec" && TryMilliseconds(message, "allowing ", " thread(s) for decoding", out var threads)
                 && threads <= 128) { kind = "decoder-thread-limit"; decoderThreads = threads; }
        else if (TryMilliseconds(message, "waited ", " ms for the render fence", out var wait))
        {
            if (wait < 5) return null;
            kind = "render-fence-wait"; milliseconds = wait;
        }
        else if (Has("picture") && Has("late"))
        {
            kind = Has("dropp") ? "late-picture-dropped" : "late-picture";
            if (TryMilliseconds(message, "missing ", " ms", out var late)) milliseconds = late;
        }
        else if (Has("late frames in a row") || Has("seconds of late video")) kind = "decoder-late-frame-escalation";
        else if (Has("trying to reconnect")) kind = "reconnect-attempt";
        else if (Has("reconnection failed") || Has("HTTP connection failure")) kind = "connection-failure";
        else if (Has("SwapChain Present failed")) kind = "present-failure";
        else if ((Has("PCR") || Has("timestamp") || Has("clock"))
                 && (Has("late") || Has("invalid") || Has("reset") || Has("discontinu"))) kind = "clock-discontinuity";
        else if (safeModule == "ts" && (Has("discontinu") || Has("corrupt"))) kind = "transport-discontinuity";
        else if (safeModule is "direct3d11" or "direct3d9" or "gl" or "glwin32"
                 && (Has("failed") || Has("error"))) kind = "video-output-failure";
        else if (safeModule == "avcodec" && (Has("failed") || Has("error") || Has("corrupt"))) kind = "decoder-failure-or-corruption";

        return kind is null ? null : new NativePlaybackDiagnostic(
            DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), sessionId, kind, safeModule, milliseconds, DecoderThreads: decoderThreads);
    }

    private static string KnownModule(string? module) => module?.ToLowerInvariant() switch
    {
        "direct3d11" => "direct3d11", "direct3d9" => "direct3d9", "gl" => "gl",
        "glwin32" => "glwin32", "avcodec" => "avcodec", "ts" => "ts",
        "main" => "main", "http" => "http", "adaptive" => "adaptive",
        "d3d11va" => "d3d11va", "dxva2" => "dxva2",
        "d3d11_filters" => "d3d11_filters", "d3d11_deinterlace" => "d3d11_deinterlace", _ => "other"
    };

    private static bool TryMilliseconds(string message, string prefix, string suffix, out int milliseconds)
    {
        milliseconds = 0;
        var start = message.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return false;
        start += prefix.Length;
        var end = message.IndexOf(suffix, start, StringComparison.OrdinalIgnoreCase);
        return end > start && int.TryParse(message.AsSpan(start, end - start),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out milliseconds) && milliseconds >= 0;
    }
}
