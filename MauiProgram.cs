using LightGatewayScanner.Services;
using LightGatewayScanner.ViewModels;
using LightGatewayScanner.Views;
using Microsoft.Extensions.Logging;
using ZXing.Net.Maui.Controls;

namespace LightGatewayScanner;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseBarcodeReader()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// 配置与日志
		builder.Services.AddSingleton<IAppConfigService, AppConfigService>();
		builder.Services.AddSingleton<IAppLogService, FileAppLogService>();

		// 业务服务
		builder.Services.AddSingleton<IScanClassifier, ScanClassifier>();
		builder.Services.AddSingleton<IRedisOnlineService, RedisOnlineService>();
		builder.Services.AddSingleton<IMqttSetPnService, MqttSetPnService>();
		builder.Services.AddSingleton<IBleScanner, BleScanner>();
		builder.Services.AddSingleton<IDeviceCallbackApiService, DeviceCallbackApiService>();
		builder.Services.AddSingleton<IScanSessionService, ScanSessionService>();
		builder.Services.AddSingleton<IFeedbackService, FeedbackService>();

		// 页面与 ViewModel（页面单例，保证横竖屏切换时不丢失已扫数据）
		builder.Services.AddSingleton<ScanViewModel>();
		builder.Services.AddSingleton<SettingsViewModel>();
		builder.Services.AddSingleton<ScanPage>();
		builder.Services.AddSingleton<SettingsPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		var app = builder.Build();

		// 启动时载入配置并清理历史日志
		var configService = app.Services.GetRequiredService<IAppConfigService>();
		configService.Load();

		// 后台连一次 MQTT（失败不影响启动，提交时会再尝试）
		app.Services.GetRequiredService<IMqttSetPnService>().Start();

		var logService = app.Services.GetRequiredService<IAppLogService>();
		_ = logService.WriteAsync("APP 启动");

		return app;
	}
}
