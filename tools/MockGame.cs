using System;
using System.IO;
using System.Windows.Forms;

internal static class MockGame
{
    [STAThread]
    private static int Main(string[] args)
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string requirement = Path.Combine(root, "required-data.txt");
        bool ready = File.Exists(requirement) && File.Exists(Path.Combine(root, File.ReadAllText(requirement).Trim()));
        if (Array.IndexOf(args, "--self-test") >= 0) return ready ? 0 : 2;
        MessageBox.Show(ready
            ? "테스트 게임을 실행했습니다. 필요한 예제 데이터도 같은 구성에 있습니다."
            : "필요한 예제 데이터를 찾지 못했습니다. 이동한 폴더 구성을 확인하세요.",
            "IndieRun 테스트 게임", MessageBoxButtons.OK, ready ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        return ready ? 0 : 2;
    }
}
