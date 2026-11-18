"""Build the portable app and generated TestKit without external working folders."""
from pathlib import Path
from datetime import datetime
import argparse
import hashlib
import json
import subprocess
import zipfile
from generate_mock_data import generate


def main() -> None:
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=root / "artifacts/releases" / datetime.now().strftime("%Y%m%d-%H%M%S"))
    output = parser.parse_args().output.resolve()
    if output.exists():
        raise FileExistsError("배포 출력은 존재하지 않는 새 폴더를 지정하세요.")
    output.mkdir(parents=True)
    app = output / "IndieRun"
    subprocess.run(["dotnet", "publish", str(root / "src/IndieRun/IndieRun.csproj"),
                    "-p:PublishProfile=Portable", "-o", str(app), "--nologo"], cwd=root, check=True)
    if [p.name for p in app.iterdir()] != ["IndieRun.exe"]:
        raise RuntimeError("배포 파일이 단일 EXE인지 확인하세요.")
    mock = output / "MockData"
    generate(mock)
    readme = output / "읽어주세요.txt"
    readme.write_text("""IndieRun · Windows x64

프로그램 ZIP: 압축을 푼 뒤 IndieRun/IndieRun.exe를 실행하세요.
테스트 묶음: 전체를 새 폴더에 풀고 StartDemo.cmd를 실행하세요.
테스트용 실행 파일은 안내창을 표시하며 예제 데이터가 있는지 확인합니다.
실제 게임·세이브·태그·개인 설정은 포함하지 않습니다.

기존 프로그램을 업데이트할 때는 종료 후 IndieRun.exe만 교체하세요.
실행 후 생성되는 data 폴더와 보관함·백업은 유지하세요.
백업은 직접 삭제할 때까지 유지됩니다.

복원: 작업 기록이나 백업 관리에서 복원을 선택한 뒤 저장 위치를 고릅니다.
기존 파일과 실행 목록 연결은 유지되며 새 폴더에 복원됩니다.
구성 수정: Guide 같은 비필수 폴더를 ‘포함하지 않음’으로 지정할 수 있습니다.
게임 실행에 필요한 하위 데이터는 포함해야 합니다.

목데이터는 생성된 예제입니다. 처음부터 다시 시험하려면 ZIP을 새 폴더에 푸세요.
테스트용 EXE는 Windows .NET Framework 4.x를 사용합니다.
""", encoding="utf-8-sig")
    launcher = output / "StartDemo.cmd"
    launcher.write_bytes(b'@echo off\r\ncd /d "%~dp0"\r\nstart "" "%~dp0IndieRun\\IndieRun.exe" --demo\r\n')
    common = [(readme, "읽어주세요.txt")]
    app_files = [(app / "IndieRun.exe", "IndieRun/IndieRun.exe")]
    mock_files = [(p, p.relative_to(output).as_posix()) for p in mock.rglob("*") if p.is_file()]
    bundles = {
        "IndieRun-win-x64.zip": app_files + common,
        "IndieRun-TestKit-win-x64.zip": app_files + mock_files + common + [(launcher, "StartDemo.cmd")],
        "IndieRun-MockData.zip": mock_files + common,
    }
    manifest = []
    for name, entries in bundles.items():
        archive = output / name
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
            for file, relative in entries:
                z.write(file, relative)
        with zipfile.ZipFile(archive) as z:
            if z.testzip() is not None:
                raise RuntimeError("ZIP 검증 실패: " + name)
        manifest.append({"file": name, "bytes": archive.stat().st_size,
                         "sha256": hashlib.sha256(archive.read_bytes()).hexdigest()})
    (output / "SHA256.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(output)


if __name__ == "__main__":
    main()
