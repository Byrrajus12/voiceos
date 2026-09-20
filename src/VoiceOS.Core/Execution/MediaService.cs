using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using VoiceOS.Core.Decision;
using Windows.Media.Control;

namespace VoiceOS.Core.Execution;

/// <summary>
/// Controls media playback via SMTC (Global System Media Transport Controls) for deterministic
/// Play/Pause/Toggle, with SendInput fallback when no session is active.
/// Next/Previous always use SendInput virtual keys because SMTC SkipNext/Previous require
/// a session that supports skip, while media keys work universally.
/// </summary>
public sealed class MediaService : IMediaService
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
    private const ushort VK_MEDIA_PREV_TRACK = 0xB1;
    private const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;

    private readonly ILogger<MediaService> _logger;

    public MediaService(ILogger<MediaService> logger) => _logger = logger;

    public ExecutionResult Send(MediaOperation op)
    {
        try
        {
            return op switch
            {
                MediaOperation.Play => SmtcPlay(),
                MediaOperation.Pause => SmtcPause(),
                MediaOperation.Toggle => SmtcToggle(),
                MediaOperation.Next => SendKey(VK_MEDIA_NEXT_TRACK, op),
                MediaOperation.Previous => SendKey(VK_MEDIA_PREV_TRACK, op),
                _ => ExecutionResult.Fail(ExecutionStatus.PlatformError, $"Unmapped media operation: {op}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Media operation failed: {Op}", op);
            return ExecutionResult.Fail(ExecutionStatus.PlatformError, ex.Message);
        }
    }

    private ExecutionResult SmtcPlay()
    {
        var session = TryGetCurrentSession();
        if (session != null)
        {
            bool ok = session.TryPlayAsync().AsTask().GetAwaiter().GetResult();
            if (ok)
            {
                _logger.LogInformation("Media: Play via SMTC");
                return ExecutionResult.Ok("Play");
            }
        }
        _logger.LogInformation("Media: Play via SendInput (no active SMTC session)");
        SendMediaKey(VK_MEDIA_PLAY_PAUSE);
        return ExecutionResult.Ok("Play");
    }

    private ExecutionResult SmtcPause()
    {
        var session = TryGetCurrentSession();
        if (session != null)
        {
            bool ok = session.TryPauseAsync().AsTask().GetAwaiter().GetResult();
            if (ok)
            {
                _logger.LogInformation("Media: Pause via SMTC");
                return ExecutionResult.Ok("Pause");
            }
        }
        _logger.LogInformation("Media: Pause via SendInput (no active SMTC session)");
        SendMediaKey(VK_MEDIA_PLAY_PAUSE);
        return ExecutionResult.Ok("Pause");
    }

    private ExecutionResult SmtcToggle()
    {
        var session = TryGetCurrentSession();
        if (session != null)
        {
            bool ok = session.TryTogglePlayPauseAsync().AsTask().GetAwaiter().GetResult();
            if (ok)
            {
                _logger.LogInformation("Media: Toggle via SMTC");
                return ExecutionResult.Ok("Toggle");
            }
        }
        _logger.LogInformation("Media: Toggle via SendInput (no active SMTC session)");
        SendMediaKey(VK_MEDIA_PLAY_PAUSE);
        return ExecutionResult.Ok("Toggle");
    }

    private ExecutionResult SendKey(ushort vk, MediaOperation op)
    {
        SendMediaKey(vk);
        _logger.LogInformation("Media: {Op} (VK=0x{Vk:X2})", op, vk);
        return ExecutionResult.Ok(op.ToString());
    }

    private static GlobalSystemMediaTransportControlsSession? TryGetCurrentSession()
    {
        try
        {
            var manager = GlobalSystemMediaTransportControlsSessionManager
                .RequestAsync().AsTask().GetAwaiter().GetResult();
            return manager?.GetCurrentSession();
        }
        catch
        {
            return null;
        }
    }

    private static void SendMediaKey(ushort vk)
    {
        var inputs = new INPUT[2];
        inputs[0] = KeyInput(vk, 0);
        inputs[1] = KeyInput(vk, KEYEVENTF_KEYUP);
        uint sent = SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        if (sent != 2) throw new InvalidOperationException($"SendInput sent {sent}/2 events");
    }

    private static INPUT KeyInput(ushort vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = flags }
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
