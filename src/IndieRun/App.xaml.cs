using System.Windows;
namespace IndieRun;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if(e.Args.Contains("--demo"))try{DemoSetup.Initialize(AppContext.BaseDirectory);}catch(Exception ex) when(ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException){MessageBox.Show(ex.Message,"테스트 데이터 확인",MessageBoxButton.OK,MessageBoxImage.Information);Shutdown();return;}
        base.OnStartup(e);
    }
}
