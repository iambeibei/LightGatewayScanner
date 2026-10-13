using Microsoft.Maui.Devices;
#if ANDROID
using Android.Media;
#endif

namespace LightGatewayScanner.Services;

/// <summary>
/// 扫码反馈：震动 + 提示音。可通过配置"声音与震动"开关关闭。
/// </summary>
public sealed class FeedbackService : IFeedbackService
{
    private readonly IAppConfigService _configService;

#if ANDROID
    private static ToneGenerator? _toneGenerator;
#endif

    public FeedbackService(IAppConfigService configService) => _configService = configService;

    public void Success()
    {
        Vibrate(60);
        Beep(1200, 120);
    }

    public void Warning()
    {
        Vibrate(90);
        Beep(800, 120);
    }

    public void Error()
    {
        Vibrate(250);
        Beep(400, 300);
    }

    public void Tap() => Vibrate(30);

    private void Vibrate(int milliseconds)
    {
        if (!_configService.Current.SoundAndVibration) return;

        try
        {
            Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(milliseconds));
        }
        catch
        {
            // 部分设备不支持震动，忽略
        }
    }

    private void Beep(int frequencyHint, int durationMs)
    {
        if (!_configService.Current.SoundAndVibration) return;

#if ANDROID
        try
        {
            _toneGenerator ??= new ToneGenerator(Android.Media.Stream.Music, 70);
            _toneGenerator.StartTone(frequencyHint >= 1000 ? Tone.PropBeep : Tone.PropAck, durationMs);
        }
        catch
        {
            // 提示音失败不影响业务
        }
#endif
    }
}
