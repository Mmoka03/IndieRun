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
    guides = {
        "program": """IndieRun 테스트 버전 · Windows x64

1. 압축을 풀고 IndieRun 폴더의 IndieRun.exe를 실행하세요.
2. 탐색 위치를 선택하고 게임을 등록하거나 보관함으로 옮기세요.

처음에는 게임 복사본으로 테스트하세요.
업데이트할 때는 프로그램을 종료하고 IndieRun.exe만 교체하세요.
기존 data 폴더와 보관함·백업은 유지하세요.
""",
        "testkit": """IndieRun 테스트 묶음 · Windows x64

1. 전체를 새 폴더에 압축 해제하세요.
2. StartDemo.cmd를 실행하세요.
3. 예제 폴더로 정리·이동·백업·복원을 확인하세요.

예제 실행 파일은 안내창만 표시합니다. 실제 게임은 포함하지 않습니다.
처음부터 다시 테스트하려면 ZIP을 새 폴더에 풀어 실행하세요.
""",
        "mockdata": """IndieRun 테스트용 예제

압축을 풀고 IndieRun의 탐색 위치로 MockData 폴더를 선택하세요.
예제 실행 파일은 안내창만 표시합니다. 실제 게임은 포함하지 않습니다.
처음부터 다시 테스트하려면 ZIP을 새 폴더에 풀어 사용하세요.
""",
    }
    readmes = {}
    for key, content in guides.items():
        readmes[key] = output / ("readme-" + key + ".txt")
        readmes[key].write_text(content, encoding="utf-8-sig")
    launcher = output / "StartDemo.cmd"
    launcher.write_bytes(b'@echo off\r\ncd /d "%~dp0"\r\nstart "" "%~dp0IndieRun\\IndieRun.exe" --demo\r\n')
    def guide(key): return [(readmes[key], "읽어주세요.txt")]
    app_files = [(app / "IndieRun.exe", "IndieRun/IndieRun.exe")]
    mock_files = [(p, p.relative_to(output).as_posix()) for p in mock.rglob("*") if p.is_file()]
    bundles = {
        "IndieRun-win-x64.zip": app_files + guide("program"),
        "IndieRun-TestKit-win-x64.zip": app_files + mock_files + guide("testkit") + [(launcher, "StartDemo.cmd")],
        "IndieRun-MockData.zip": mock_files + guide("mockdata"),
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
