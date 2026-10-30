using LightGatewayScanner.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LightGatewayScanner;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new AppShell());
		var scanViewModel = IPlatformApplication.Current?.Services.GetService<ScanViewModel>();

		// 启动蓝牙检测：内部用 Task.Run + 回 UI 线程更新提示条，
		// 这里不 await，确保首帧渲染与扫码页进入不被系统蓝牙查询拖慢。
		scanViewModel?.BeginStartupBluetoothCheck();

		// 用户从系统蓝牙设置返回时，App 只是 resume、页面未必重新 OnAppearing，
		// 这里主动刷新一次，开启蓝牙后提示条才能及时消失。
		window.Resumed += (_, _) => scanViewModel?.RefreshBluetoothBanner();

		return window;
	}
}